using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Renci.SshNet;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// A shortcut's jump host: an SSH login to it and a port on 127.0.0.1 forwarded from there to
/// the target, as ssh -J does. SSH tabs, their SFTP pane and VNC tabs connect to that port.
///
/// The jump is the name of an SSH shortcut, which brings its own login, saved password and
/// jump host (so chains work), or "user@host:port", which logs in the way the shortcut itself
/// does and asks for a password it does not have.
/// </summary>
public sealed class SshJump : IDisposable
{
    private const int MaxHops = 4;

    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;
    private readonly SshJump? _inner;

    /// <summary>Finds an SSH shortcut by name or folder path; the app sets it. Returns a copy with the same id.</summary>
    public static Func<string, SessionDefinition?>? FindShortcut { get; set; }

    private SshJump(SshClient client, ForwardedPortLocal forward, SshJump? inner, string route)
    {
        _client = client;
        _forward = forward;
        _inner = inner;
        Route = route;
        _forward.Exception += (_, e) => Failure = e.Exception.Message;
    }

    /// <summary>The forwarded port on 127.0.0.1.</summary>
    public int LocalPort => (int)_forward.BoundPort;

    /// <summary>"Jump host fw-a" or "Jump host admin@192.0.2.1:22", for messages.</summary>
    public string Route { get; }

    /// <summary>The last reason the jump host refused to open the forward, if any.</summary>
    public string? Failure { get; private set; }

    public bool IsConnected => _client.IsConnected && (_inner?.IsConnected ?? true);

    public static Task<SshJump> OpenAsync(SessionDefinition owner, string targetHost, int targetPort, CredentialVault vault,
        Func<string, string, string?>? promptForSecret, CancellationToken token) =>
        OpenAsync(owner, targetHost, targetPort, vault, promptForSecret, [owner.Id], token);

    private static async Task<SshJump> OpenAsync(SessionDefinition owner, string targetHost, int targetPort, CredentialVault vault,
        Func<string, string, string?>? promptForSecret, List<Guid> seen, CancellationToken token)
    {
        var (hop, route) = Resolve(owner);
        if (seen.Contains(hop.Id) || seen.Count > MaxHops)
        {
            throw new InvalidOperationException($"{owner.Name}: the jump hosts go round in a loop or more than {MaxHops} deep.");
        }

        seen.Add(hop.Id);

        // A jump shortcut with its own jump host: reach that first, then this one through it.
        SshJump? inner = null;
        if (hop.JumpHost.Trim().Length > 0)
        {
            inner = await OpenAsync(hop, hop.Host, hop.Port, vault, promptForSecret, seen, token).ConfigureAwait(false);
        }

        SshClient? client = null;
        try
        {
            var login = SshTerminalSession.ForJump(hop, owner, vault, promptForSecret);
            var info = await Task.Run(() => login.BuildConnectionInfoAsync(
                inner is null ? hop.Host : "127.0.0.1", inner?.LocalPort ?? hop.Port, token), token).ConfigureAwait(false);
            client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            var connecting = client;
            try
            {
                await Task.Run(() => KnownHosts.Connect(connecting, hop.Host, hop.Port), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (HostKeyRejectedException or OperationCanceledException))
            {
                throw new InvalidOperationException($"{route}: {ex.Message}", ex);
            }

            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshJump(client, forward, inner, route);
        }
        catch
        {
            client?.Dispose();
            inner?.Dispose();
            throw;
        }
    }

    /// <summary>The jump field as a login: an SSH shortcut by name, else user@host:port with the owner's way of logging in.</summary>
    internal static (SessionDefinition Hop, string Route) Resolve(SessionDefinition owner)
    {
        var text = owner.JumpHost.Trim();
        if (FindShortcut?.Invoke(text) is { Protocol: SessionProtocol.Ssh } shortcut)
        {
            return (shortcut, "Jump host " + shortcut.Name);
        }

        var at = text.LastIndexOf('@');
        var user = at > 0 ? text[..at] : owner.Username;
        var host = at >= 0 ? text[(at + 1)..] : text;
        var port = Address.SplitPort(ref host) ?? 22;
        if (host.Length == 0 || user.Length == 0 || host.Contains(' '))
        {
            throw new InvalidOperationException(
                $"{owner.Name}: jump host \"{text}\" is neither an SSH shortcut nor user@host:port.");
        }

        // A fresh id, so the owner's own saved password is never offered to the jump host;
        // what it is asked for is kept under the owner as "jump:password".
        var hop = owner.Clone(text);
        hop.Id = Guid.NewGuid();
        hop.Protocol = SessionProtocol.Ssh;
        hop.Host = host;
        hop.Port = port;
        hop.Username = user;
        hop.JumpHost = "";
        hop.KeePassEntry = "";
        if (owner.Protocol != SessionProtocol.Ssh)
        {
            // A VNC shortcut has no SSH login of its own to borrow.
            hop.AuthMethod = SshAuthMethod.Password;
            hop.PasswordSource = PasswordSource.Saved;
        }

        return (hop, $"Jump host {user}@{host}:{port}");
    }

    public void Dispose()
    {
        try
        {
            if (_forward.IsStarted)
            {
                _forward.Stop();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        _forward.Dispose();
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        _client.Dispose();
        _inner?.Dispose();
    }
}
