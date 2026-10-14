using System.IO.Ports;
using System.Text;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Transfer;

namespace Quarterdeck.Core.Terminal;

/// <summary>A serial console, for console servers and embedded boards.</summary>
public sealed class SerialTerminalSession : ITerminalSession
{
    private readonly SessionDefinition _definition;
    private SerialPort? _port;
    private volatile bool _closing;

    // While a binary transfer runs, received bytes go here instead of to the screen, and
    // they stay bytes: the text path decodes UTF-8 and would mangle a firmware image.
    private volatile QueuedByteLink? _raw;

    /// <summary>Borrows the port for a binary transfer until <see cref="EndRaw"/>.</summary>
    public IByteLink BeginRaw()
    {
        var raw = new QueuedByteLink((data, _) =>
        {
            var port = _port ?? throw new IOException("The serial port is closed.");
            port.Write(data.ToArray(), 0, data.Length);
            return Task.CompletedTask;
        });
        _raw = raw;
        return raw;
    }

    public void EndRaw() => _raw = null;

    public SerialTerminalSession(SessionDefinition definition) => _definition = definition;

    public bool IsConnected => _port?.IsOpen == true;

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<TerminalClosedEventArgs>? Closed;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _closing = false;

        _port = new SerialPort(
            _definition.SerialPort,
            _definition.BaudRate,
            Enum.Parse<Parity>(_definition.Parity, ignoreCase: true),
            _definition.DataBits,
            Enum.Parse<StopBits>(_definition.StopBits, ignoreCase: true))
        {
            Encoding = Encoding.UTF8,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true
        };

        _port.DataReceived += OnData;
        _port.ErrorReceived += OnError;
        _port.Open();

        _closedRaised = 0;
        var watched = _port;
        _watchdog = new Timer(_ => Watch(watched), null, 1000, 1000);

        return Task.CompletedTask;
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        // Captured once: DisconnectAsync nulls the field from another thread.
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            return;
        }

        try
        {
            if (_raw is { } raw)
            {
                var count = port.BytesToRead;
                if (count > 0)
                {
                    var buffer = new byte[count];
                    var read = port.Read(buffer, 0, count);
                    raw.Push(buffer.AsSpan(0, read));
                }

                return;
            }

            var text = port.ReadExisting();
            if (text.Length > 0)
            {
                DataReceived?.Invoke(this, new TerminalDataEventArgs(text));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Port closed underneath us, for instance a USB adapter unplugged.
            RaiseClosed("Serial port closed.");
        }
    }

    private Timer? _watchdog;
    private int _closedRaised;

    /// <summary>
    /// An unplugged USB adapter often raises no event at all and no more data arrives, so the
    /// tab would sit "connected" forever. Check once a second that the port still exists.
    /// </summary>
    private void Watch(SerialPort port)
    {
        if (!ReferenceEquals(port, _port))
        {
            return;
        }

        bool present;
        try
        {
            present = port.IsOpen && SerialPort.GetPortNames().Contains(port.PortName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            present = false;
        }

        if (!present)
        {
            RaiseClosed($"{port.PortName} is gone (adapter unplugged?).");
        }
    }

    private void RaiseClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            _watchdog?.Dispose();
            Closed?.Invoke(this, new TerminalClosedEventArgs(reason, _closing));
        }
    }

    private long _lastErrorNotice;

    /// <summary>
    /// Frame, parity and overrun errors are line noise (a baud mismatch while a device boots,
    /// say); the port is still open. Note them, at most once a second, and keep going.
    /// </summary>
    private void OnError(object sender, SerialErrorReceivedEventArgs e)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastErrorNotice) < 1000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastErrorNotice, now);
        DataReceived?.Invoke(this, new TerminalDataEventArgs($"\r\n\u001b[2m[baudhoundterm] serial line error: {e.EventType}\u001b[0m\r\n"));
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        var port = _port;
        try
        {
            if (port?.IsOpen == true)
            {
                port.Write(text);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            RaiseClosed("Serial port closed: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Serial has no window-size concept; the far end never learns the geometry.</summary>
    public Task ResizeAsync(int columns, int rows) => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        _closing = true;
        _watchdog?.Dispose();
        _watchdog = null;

        if (_port is not null)
        {
            _port.DataReceived -= OnData;
            _port.ErrorReceived -= OnError;

            if (_port.IsOpen)
            {
                _port.Close();
            }

            _port.Dispose();
            _port = null;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
