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

        return Task.CompletedTask;
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port is null || !_port.IsOpen)
        {
            return;
        }

        try
        {
            if (_raw is { } raw)
            {
                var count = _port.BytesToRead;
                if (count > 0)
                {
                    var buffer = new byte[count];
                    var read = _port.Read(buffer, 0, count);
                    raw.Push(buffer.AsSpan(0, read));
                }

                return;
            }

            var text = _port.ReadExisting();
            if (text.Length > 0)
            {
                DataReceived?.Invoke(this, new TerminalDataEventArgs(text));
            }
        }
        catch (InvalidOperationException)
        {
            // Port closed underneath us, for instance a USB adapter unplugged.
            Closed?.Invoke(this, new TerminalClosedEventArgs("Serial port closed.", _closing));
        }
    }

    private void OnError(object sender, SerialErrorReceivedEventArgs e) =>
        Closed?.Invoke(this, new TerminalClosedEventArgs($"Serial error: {e.EventType}", false));

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_port?.IsOpen == true)
        {
            _port.Write(text);
        }

        return Task.CompletedTask;
    }

    /// <summary>Serial has no window-size concept; the far end never learns the geometry.</summary>
    public Task ResizeAsync(int columns, int rows) => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        _closing = true;

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
