using System.Net.Sockets;
using System.Text;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// A TCP console: telnet proper, or the raw sockets that most embedded consoles actually serve.
/// BaudHound exposes its UART this way on port 23, as do console servers and network gear.
/// </summary>
public sealed class TelnetTerminalSession : ITerminalSession
{
    // Telnet control bytes. Enough to stay out of trouble, not a full implementation.
    private const byte IAC = 255;   // interpret as command
    private const byte DONT = 254;
    private const byte DO = 253;
    private const byte WONT = 252;
    private const byte WILL = 251;
    private const byte SB = 250;    // subnegotiation begin
    private const byte SE = 240;    // subnegotiation end

    private readonly SessionDefinition _definition;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[64 * 1024];

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _pump;
    private volatile bool _closing;

    public TelnetTerminalSession(SessionDefinition definition) => _definition = definition;

    public bool IsConnected => _client?.Connected == true;

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<TerminalClosedEventArgs>? Closed;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _closing = false;
        _client = new TcpClient { NoDelay = true };

        await _client.ConnectAsync(_definition.Host, _definition.Port, cancellationToken)
            .ConfigureAwait(false);

        _stream = _client.GetStream();
        _pump = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_pump.Token), _pump.Token);
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var clean = new byte[buffer.Length];

        try
        {
            while (!token.IsCancellationRequested && _stream is not null)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var kept = await StripCommandsAsync(buffer, read, clean, token).ConfigureAwait(false);
                if (kept == 0)
                {
                    continue;
                }

                var count = _decoder.GetChars(clean, 0, kept, _chars, 0);
                if (count > 0)
                {
                    DataReceived?.Invoke(this, new TerminalDataEventArgs(new string(_chars, 0, count)));
                }
            }

            Closed?.Invoke(this, new TerminalClosedEventArgs(
                _closing ? "Disconnected." : "Connection closed by the far end.", _closing));
        }
        catch (OperationCanceledException)
        {
            Closed?.Invoke(this, new TerminalClosedEventArgs("Disconnected.", true));
        }
        catch (Exception ex)
        {
            Closed?.Invoke(this, new TerminalClosedEventArgs(ex.Message, _closing));
        }
    }

    /// <summary>
    /// Takes telnet negotiation out of the data stream and refuses every option, which is what
    /// a plain character terminal wants. A raw socket has no IAC bytes and passes through
    /// untouched, so the same code serves both.
    /// </summary>
    private async Task<int> StripCommandsAsync(byte[] source, int length, byte[] target, CancellationToken token)
    {
        var kept = 0;

        for (var i = 0; i < length; i++)
        {
            if (source[i] != IAC)
            {
                target[kept++] = source[i];
                continue;
            }

            if (i + 1 >= length)
            {
                break;
            }

            var command = source[++i];

            switch (command)
            {
                case IAC:
                    target[kept++] = IAC;      // escaped 0xFF is literal data
                    break;

                case DO:
                case DONT:
                case WILL:
                case WONT:
                    if (i + 1 < length)
                    {
                        var option = source[++i];

                        // Refuse everything: WONT in answer to DO, DONT in answer to WILL.
                        var answer = command is DO or DONT ? WONT : DONT;
                        await SendRawAsync(new[] { IAC, answer, option }, token).ConfigureAwait(false);
                    }

                    break;

                case SB:
                    // Skip to the end of the subnegotiation.
                    while (i + 1 < length && !(source[i] == IAC && source[i + 1] == SE))
                    {
                        i++;
                    }

                    i++;
                    break;
            }
        }

        return kept;
    }

    private async Task SendRawAsync(byte[] bytes, CancellationToken token)
    {
        if (_stream is not null)
        {
            await _stream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
        }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await _stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A telnet console has no window-size channel unless NAWS is negotiated.</summary>
    public Task ResizeAsync(int columns, int rows) => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        _closing = true;
        _pump?.Cancel();

        _stream?.Dispose();
        _stream = null;

        _client?.Dispose();
        _client = null;

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
