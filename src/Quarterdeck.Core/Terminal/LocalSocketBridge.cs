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

    private readonly bool _tls;
    private readonly bool _hasUsername;

    /// <param name="tls">Bring up TLS (VeNCrypt X509) before handing the connection to the page; see <see cref="VncTls"/>.</param>
    /// <param name="hasUsername">Whether the page can answer a login (the X509Plain subtype), not only a password.</param>
    public LocalSocketBridge(string targetHost, int targetPort, bool tls = false, bool hasUsername = false)
    {
        _host = targetHost;
        _port = targetPort;
        _tls = tls;
        _hasUsername = hasUsername;
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

    /// <summary>
    /// Where each connection really goes, asked for when the page opens the socket: a jump host's
    /// forward on 127.0.0.1 (see <see cref="SshJump"/>) and a name for it. Null connects directly.
    /// </summary>
    public Func<CancellationToken, Task<(string Host, int Port, string Via)>>? Route { get; init; }

    /// <summary>The name the server's TLS certificate is kept under, when it is not the target host itself.</summary>
    public string? TrustHost { get; init; }

    /// <summary>Shared secret in the URL path; a request without it is refused.</summary>
    public string Token { get; }

    public string Url => $"ws://127.0.0.1:{LocalPort}/{Token}";

    /// <summary>Raised when the far end drops or the pump fails, so the tab can say so.</summary>
    public event EventHandler<string>? Closed;

    /// <summary>
    /// Progress through the handshake. A VNC session that stalls gives no clue which half is at
    /// fault; these say whether the page ever opened the socket and whether the target answered.
    /// </summary>
    public event EventHandler<string>? Note;

    private async Task AcceptAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (_life.IsCancellationRequested || !_listener.IsListening)
                {
                    return;   // listener stopped with the session
                }

                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }

            // One bad request (a viewer that aborts mid-handshake, say) must not end the loop:
            // noVNC reconnects, and every later attempt would otherwise hang unanswered.
            try
            {
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

                Note?.Invoke(this, $"Viewer attached (subprotocol {chosen ?? "none"}).");
                _ = Task.Run(() => PumpAsync(ws.WebSocket));
            }
            catch (Exception ex) when (ex is WebSocketException or HttpListenerException or InvalidOperationException
                                           or ObjectDisposedException or UriFormatException)
            {
                Note?.Invoke(this, $"A viewer connection failed: {ex.Message}");
                try
                {
                    context.Response.Abort();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private async Task PumpAsync(WebSocket socket)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var pump = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var token = pump.Token;

        var via = "";
        try
        {
            var (host, port) = (_host, _port);
            if (Route is not null)
            {
                (host, port, via) = await Route(token).ConfigureAwait(false);
            }

            await tcp.ConnectAsync(host, port, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Abort();   // tell the page, or it waits on a socket nothing will answer
            socket.Dispose();
            Closed?.Invoke(this, ex.Message);
            return;
        }

        Note?.Invoke(this, via.Length == 0 ? $"Reached {_host}:{_port}." : $"Opened {_host}:{_port} through {via}.");

        Stream stream = tcp.GetStream();
        if (_tls)
        {
            // The outer handshake and TLS happen here; the page then gets the inner one.
            VncTls.Session session;
            try
            {
                session = await VncTls.NegotiateAsync(stream, TrustHost ?? _host, _port, _hasUsername, token).ConfigureAwait(false);
                Note?.Invoke(this, $"TLS up ({session.SubtypeName}), certificate SHA-256 {session.Fingerprint}.");
                await VncTls.PresentAsync(socket, session.Subtype, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VncTlsException or IOException or WebSocketException)
            {
                await CloseOutputQuietlyAsync(socket).ConfigureAwait(false);
                socket.Abort();
                socket.Dispose();
                Closed?.Invoke(this, ex.Message);
                return;
            }

            stream = session.Stream;
        }

        var toTcp = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, result.Count), token).ConfigureAwait(false);
            }
        });

        var toWs = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary,
                    true, token).ConfigureAwait(false);
            }
        });

        try
        {
            // Whichever direction ends first takes the session down with it. WhenAny hands back
            // the finished task rather than throwing, so await it again to see why it finished:
            // without that second await a failing pump looks exactly like a clean close.
            await (await Task.WhenAny(toTcp, toWs).ConfigureAwait(false)).ConfigureAwait(false);
            Closed?.Invoke(this, "Disconnected.");
        }
        catch (OperationCanceledException)
        {
            // The tab closed.
        }
        catch (Exception ex)
        {
            Closed?.Invoke(this, ex.Message);
        }
        finally
        {
            // End both halves: stop the other pump, and close the page's socket so noVNC shows
            // the disconnect instead of a frozen last frame.
            // The close frame goes first: cancelling a pending WebSocket receive aborts the socket.
            await CloseOutputQuietlyAsync(socket).ConfigureAwait(false);
            pump.Cancel();
            try
            {
                await Task.WhenAll(toTcp, toWs).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Each half's own failure was reported (or was the cancel just above).
            }

            socket.Abort();
            socket.Dispose();
            if (_tls)
            {
                await stream.DisposeAsync().ConfigureAwait(false);   // the TLS stream owns the socket's
            }
        }
    }

    private static async Task CloseOutputQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "target closed", wait.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
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
