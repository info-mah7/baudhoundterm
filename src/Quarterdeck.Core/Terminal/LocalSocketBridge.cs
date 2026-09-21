using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// A WebSocket-to-TCP pump on the loopback interface.
///
/// noVNC speaks RFB over a WebSocket because a page cannot open a TCP socket, so something has
/// to sit in between. This is the same two-loop bridge the Nagatha portal uses for AMI, minus
/// the multi-user gate: bytes are copied both ways and never parsed, so RFB stays opaque.
///
/// It listens on 127.0.0.1 only, on a port the OS picks, behind a per-session token. Nothing
/// off this machine can reach it, and one session's page cannot drive another's target.
/// </summary>
public sealed class LocalSocketBridge : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _life = new();

    public LocalSocketBridge(string targetHost, int targetPort)
    {
        _host = targetHost;
        _port = targetPort;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // Port 0 lets the OS choose a free one, but HttpListener needs the number in its
        // prefix: take a socket, read the port back, release it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        LocalPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{LocalPort}/");
        _listener.Start();

        _ = Task.Run(AcceptAsync);
    }

    public int LocalPort { get; }

    /// <summary>Shared secret in the URL path; a request without it is refused.</summary>
    public string Token { get; }

    public string Url => $"ws://127.0.0.1:{LocalPort}/{Token}";

    /// <summary>Raised when the far end drops or the pump fails, so the tab can say so.</summary>
    public event EventHandler<string>? Closed;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                if (!context.Request.Url!.AbsolutePath.Trim('/').Equals(Token, StringComparison.Ordinal)
                    || !context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                // noVNC offers a subprotocol and refuses the connection unless the server
                // agrees to one of them, so echo back whatever it asked for.
                var offered = context.Request.Headers["Sec-WebSocket-Protocol"];
                var chosen = offered?.Split(',').FirstOrDefault()?.Trim();

                var ws = await context.AcceptWebSocketAsync(
                    string.IsNullOrEmpty(chosen) ? null : chosen).ConfigureAwait(false);

                _ = Task.Run(() => PumpAsync(ws.WebSocket));
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // Listener stopped with the session.
        }
    }

    private async Task PumpAsync(WebSocket socket)
    {
        using var tcp = new TcpClient { NoDelay = true };

        try
        {
            await tcp.ConnectAsync(_host, _port, _life.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Closed?.Invoke(this, ex.Message);
            return;
        }

        var stream = tcp.GetStream();

        var toTcp = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), _life.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, result.Count), _life.Token).ConfigureAwait(false);
            }
        });

        var toWs = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), _life.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary,
                    true, _life.Token).ConfigureAwait(false);
            }
        });

        try
        {
            // Whichever direction ends first takes the session down with it.
            await Task.WhenAny(toTcp, toWs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Closed?.Invoke(this, ex.Message);
        }
        finally
        {
            Closed?.Invoke(this, "Disconnected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _life.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
