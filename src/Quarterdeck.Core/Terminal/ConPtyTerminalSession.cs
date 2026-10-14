using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// A local shell (cmd, PowerShell, wsl, anything) running inside a pseudoconsole we own.
/// </summary>
public sealed class ConPtyTerminalSession : ITerminalSession
{
    private readonly SessionDefinition _definition;

    private IntPtr _pty = IntPtr.Zero;
    private SafeFileHandle? _ptyIn;      // we write here, the child reads
    private SafeFileHandle? _ptyOut;     // the child writes here, we read
    private FileStream? _writer;
    private FileStream? _reader;
    private ConPty.PROCESS_INFORMATION _process;

    // The exit waiter owns the process handles and closes them once the wait returns:
    // closing them under a thread still waiting on them let a reused handle value stand in
    // for an unrelated object. Each connection has a generation, so a waiter left over from
    // an earlier connection never reports this one closed.
    private readonly object _processLock = new();
    private int _generation;
    private CancellationTokenSource? _pump;
    private volatile bool _closing;

    public ConPtyTerminalSession(SessionDefinition definition) => _definition = definition;

    public bool IsConnected => _pty != IntPtr.Zero;

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<TerminalClosedEventArgs>? Closed;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _closing = false;

        if (!ConPty.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) ||
            !ConPty.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
        {
            throw new InvalidOperationException(
                $"CreatePipe failed: {Marshal.GetLastWin32Error()}");
        }

        var size = new ConPty.COORD { X = 120, Y = 30 };
        var hr = ConPty.CreatePseudoConsole(size, inRead, outWrite, 0, out _pty);
        if (hr != 0)
        {
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }

        // The child owns these ends now; holding them open would keep the pipe alive
        // after the shell exits and the reader would never see EOF.
        inRead.Dispose();
        outWrite.Dispose();

        _ptyIn = inWrite;
        _ptyOut = outRead;
        _writer = new FileStream(_ptyIn, FileAccess.Write);
        _reader = new FileStream(_ptyOut, FileAccess.Read);

        StartChild();

        int generation;
        ConPty.PROCESS_INFORMATION process;
        lock (_processLock)
        {
            generation = ++_generation;
            process = _process;
        }

        _pump = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_pump.Token), _pump.Token);
        _ = Task.Run(() => WaitForExitAsync(process, generation));

        return Task.CompletedTask;
    }

    private void StartChild()
    {
        var attrSize = IntPtr.Zero;
        ConPty.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);

        try
        {
            if (!ConPty.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
            {
                throw new InvalidOperationException(
                    $"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
            }

            if (!ConPty.UpdateProcThreadAttribute(
                    attrList, 0, ConPty.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pty,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    $"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
            }

            var startup = new ConPty.STARTUPINFOEX
            {
                StartupInfo = new ConPty.STARTUPINFO(),
                lpAttributeList = attrList
            };
            startup.StartupInfo.cb = Marshal.SizeOf<ConPty.STARTUPINFOEX>();

            var command = string.IsNullOrWhiteSpace(_definition.ShellCommand)
                ? "powershell.exe"
                : _definition.ShellCommand;

            if (!ConPty.CreateProcessW(
                    null, command, IntPtr.Zero, IntPtr.Zero, false,
                    ConPty.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null,
                    ref startup, out _process))
            {
                throw new InvalidOperationException(
                    $"Could not start '{command}': {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            ConPty.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[buffer.Length * 2];

        try
        {
            while (!token.IsCancellationRequested && _reader is not null)
            {
                var read = await _reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0 && !_closing)
                {
                    DataReceived?.Invoke(this, new TerminalDataEventArgs(new string(chars, 0, count)));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Pipe torn down with the child. Exit reporting is handled by WaitForExitAsync.
        }
    }

    private async Task WaitForExitAsync(ConPty.PROCESS_INFORMATION process, int generation)
    {
        if (process.hProcess == IntPtr.Zero)
        {
            return;
        }

        await Task.Run(() => ConPty.WaitForSingleObject(process.hProcess, 0xFFFFFFFF)).ConfigureAwait(false);

        bool current;
        lock (_processLock)
        {
            if (_process.hProcess == process.hProcess)
            {
                _process = default;
            }

            current = generation == _generation;
        }

        ConPty.CloseHandle(process.hThread);
        ConPty.CloseHandle(process.hProcess);

        if (!current)
        {
            return;
        }

        // A shell that exits on its own ("exit", Ctrl+D) was asked to by the user. Reporting it
        // as unexpected made the reconnect policy start another one, for ever, elevated in an
        // Admin tab. The tab offers Reconnect instead.
        Closed?.Invoke(this, new TerminalClosedEventArgs(_closing ? "Disconnected." : "Shell exited.", true));
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_writer is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await _writer.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ResizeAsync(int columns, int rows)
    {
        if (_pty != IntPtr.Zero)
        {
            ConPty.ResizePseudoConsole(_pty, new ConPty.COORD { X = (short)columns, Y = (short)rows });
        }

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _closing = true;

        // Only end the process here; its exit waiter closes the handles.
        lock (_processLock)
        {
            if (_process.hProcess != IntPtr.Zero)
            {
                ConPty.TerminateProcess(_process.hProcess, 0);
            }
        }

        // Before Windows 11 24H2, ClosePseudoConsole waits until its output pipe is drained.
        // The pump used to be cancelled first, so nothing drained it and the window hung on
        // Close. Now the pump keeps reading (and discarding, since _closing is set) until the
        // close returns, and the close runs off the caller's thread.
        if (_pty != IntPtr.Zero)
        {
            var pty = _pty;
            _pty = IntPtr.Zero;
            var close = Task.Run(() => ConPty.ClosePseudoConsole(pty));
            await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        _pump?.Cancel();
        _writer?.Dispose();
        _reader?.Dispose();
        _writer = null;
        _reader = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
