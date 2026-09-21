using System.IO.Ports;
using System.Text;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Terminal;

/// <summary>A serial console, for console servers and embedded boards.</summary>
public sealed class SerialTerminalSession : ITerminalSession
{
    private readonly SessionDefinition _definition;
    private SerialPort? _port;
    private volatile bool _closing;

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
