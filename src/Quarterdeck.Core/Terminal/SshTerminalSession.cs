using System.Text;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.Agent;
using Quarterdeck.Core.Security.KeePassXc;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// An interactive SSH shell. The same <see cref="SshClient"/> backs SFTP/SCP transfers and
/// port forwards for this tab, so one authentication serves the whole session.
/// </summary>
public sealed class SshTerminalSession : ITerminalSession
{
    private readonly SessionDefinition _definition;
    private readonly CredentialVault _vault;
    private readonly Func<string, string?>? _promptForSecret;

    private SshClient? _client;
    private ShellStream? _stream;
    private volatile bool _closing;

    // Set once the shell is up. Before that, a failure surfaces as ConnectAsync's exception;
    // SSH.NET ALSO raises ErrorOccurred for it (a refused host key, say), and treating that as
    // a dropped connection started a reconnect, and a fresh password prompt, behind the error.
    private volatile bool _established;

    // Decoding state is per session: a UTF-8 sequence can straddle two DataReceived events.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[64 * 1024];

    public SshTerminalSession(
        SessionDefinition definition,
        CredentialVault vault,
        Func<string, string?>? promptForSecret = null)
    {
        _definition = definition;
        _vault = vault;
        _promptForSecret = promptForSecret;
    }

    /// <summary>The user name the login used: the shortcut's, or the KeePassXC entry's when the shortcut has none.</summary>
    public string Username { get; private set; } = "";

    public bool IsConnected => _client?.IsConnected == true && _stream is not null;

    /// <summary>Exposed so the transfer pane and port forwards can reuse the authenticated connection.</summary>
    public SshClient? Client => _client;

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<TerminalClosedEventArgs>? Closed;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _closing = false;
        _established = false;

        // Off the UI thread: a KeePassXC lookup can wait minutes for the user to unlock or allow
        // access, and agent calls are IPC. Prompts marshal to the UI themselves.
        var info = await Task.Run(() => BuildConnectionInfoAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        _client = new SshClient(info);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _client.ErrorOccurred += OnClientError;

        var client = _client;
        await Task.Run(() => KnownHosts.Connect(client, _definition.Host, _definition.Port), cancellationToken)
            .ConfigureAwait(false);

        _stream = _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: 120,
            rows: 30,
            width: 0,
            height: 0,
            bufferSize: 64 * 1024);

        // Read through the event, never a blocking Read loop. ShellStream does not override
        // ReadAsync, so Stream's fallback runs the blocking Read on a pool thread and holds the
        // stream's internal lock while it waits for data, which starves Write/Flush: typing
        // reached the app, was accepted without error, and never left the machine.
        _stream.DataReceived += OnStreamData;
        _stream.ErrorOccurred += OnStreamError;
        _established = true;
    }

    private void OnStreamData(object? sender, ShellDataEventArgs e)
    {
        var count = _decoder.GetChars(e.Data, 0, e.Data.Length, _chars, 0);
        if (count > 0)
        {
            DataReceived?.Invoke(this, new TerminalDataEventArgs(new string(_chars, 0, count)));
        }
    }

    private void OnStreamError(object? sender, ExceptionEventArgs e) => RaiseClosed(e.Exception.Message);

    private async Task<ConnectionInfo> BuildConnectionInfoAsync(CancellationToken token)
    {
        var methods = new List<AuthenticationMethod>();
        var username = _definition.Username;
        string? fetched = null;

        if (_definition.PasswordSource == PasswordSource.KeePassXc
            && _definition.AuthMethod is SshAuthMethod.Password or SshAuthMethod.KeyboardInteractive)
        {
            var login = await KeePassXcPasswords.LookupAsync(
                _vault, _definition.KeePassEntry, _definition.Host, _definition.Port, username, token);
            fetched = login.Password;
            if (string.IsNullOrEmpty(username))
            {
                username = login.Login;
            }
        }

        if (string.IsNullOrEmpty(username))
        {
            throw new InvalidOperationException($"{_definition.Name} has no user name.");
        }

        Username = username;

        switch (_definition.AuthMethod)
        {
            case SshAuthMethod.PublicKey:
                methods.Add(new PrivateKeyAuthenticationMethod(username, LoadKey()));
                break;

            case SshAuthMethod.Agent:
                methods.Add(new PrivateKeyAuthenticationMethod(username, AgentKeySource.FromAgents(_definition.AgentKeyFilter)));
                break;

            case SshAuthMethod.KeyboardInteractive:
                var interactive = new KeyboardInteractiveAuthenticationMethod(username);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                    {
                        // KeePassXC holds one secret: give it to the password prompt, ask for the rest (an OTP, say).
                        prompt.Response = (fetched is not null && IsPasswordPrompt(prompt.Request) ? fetched : ResolveSecret(prompt.Request))
                                          ?? string.Empty;
                    }
                };
                methods.Add(interactive);
                break;

            default:
                var password = fetched ?? ResolveSecret("password")
                               ?? throw new InvalidOperationException(
                                   $"No password available for {_definition.Name}.");
                methods.Add(new PasswordAuthenticationMethod(username, password));
                break;
        }

        return new ConnectionInfo(_definition.Host, _definition.Port, username, methods.ToArray());
    }

    private static bool IsPasswordPrompt(string request) => request.Contains("pass", StringComparison.OrdinalIgnoreCase);

    private PrivateKeyFile LoadKey()
    {
        if (string.IsNullOrWhiteSpace(_definition.KeyPath) || !File.Exists(_definition.KeyPath))
        {
            throw new FileNotFoundException($"Private key not found: {_definition.KeyPath}");
        }

        var passphrase = _vault.Get(_definition.Id, "passphrase");
        return string.IsNullOrEmpty(passphrase)
            ? new PrivateKeyFile(_definition.KeyPath)
            : new PrivateKeyFile(_definition.KeyPath, passphrase);
    }

    private string? ResolveSecret(string name)
    {
        var key = IsPasswordPrompt(name) ? "password" : name;
        return _vault.Get(_definition.Id, key) ?? _promptForSecret?.Invoke(name);
    }

    private void OnClientError(object? sender, ExceptionEventArgs e) => RaiseClosed(e.Exception.Message);

    private void RaiseClosed(string reason)
    {
        if (!_established)
        {
            return;
        }

        Closed?.Invoke(this, new TerminalClosedEventArgs(reason, _closing));
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null)
        {
            return Task.CompletedTask;
        }

        // ShellStream.Write(string) is SSH.NET's own path for shell input: it encodes and
        // flushes in one call. The Stream overloads leave the bytes in ShellStream's outgoing
        // buffer, and FlushAsync is not overridden, so the default posts the blocking Flush to
        // the pool, where it contends with the channel. It blocks, so keep it off the UI thread.
        return Task.Run(() => stream.Write(text), cancellationToken);
    }

    public Task ResizeAsync(int columns, int rows)
    {
        // SSH.NET sends the window-change request; failure here is cosmetic, never fatal.
        try
        {
            _stream?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch (SshException)
        {
        }

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _closing = true;

        if (_stream is not null)
        {
            _stream.DataReceived -= OnStreamData;
            _stream.ErrorOccurred -= OnStreamError;
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            RaiseClosed("Disconnected.");
        }

        if (_client is not null)
        {
            _client.ErrorOccurred -= OnClientError;
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
