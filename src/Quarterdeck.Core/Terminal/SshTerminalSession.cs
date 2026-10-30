using System.Net.Sockets;
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
    private readonly Func<string, string, string?>? _promptForSecret;

    private SshClient? _client;
    private ShellStream? _stream;
    private SshJump? _jump;
    private volatile bool _closing;

    // Set once the shell is up. Before that, a failure surfaces as ConnectAsync's exception;
    // SSH.NET ALSO raises ErrorOccurred for it (a refused host key, say), and treating that as
    // a dropped connection started a reconnect, and a fresh password prompt, behind the error.
    private volatile bool _established;
    private int _closedRaised;

    // Decoding state is per session: a UTF-8 sequence can straddle two DataReceived events.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[64 * 1024];
    private readonly byte[] _bytes = new byte[32 * 1024];
    private readonly object _readGate = new();

    // Keystrokes go out in the order they were typed: one Task.Run per write let the pool
    // reorder two quick writes ("ls" arriving as "sl").
    private readonly object _writeGate = new();
    private Task _writeChain = Task.CompletedTask;

    public SshTerminalSession(
        SessionDefinition definition,
        CredentialVault vault,
        Func<string, string, string?>? promptForSecret = null)
    {
        _definition = definition;
        _vault = vault;
        _promptForSecret = promptForSecret;
    }

    // Set when this session logs in to a jump host for another shortcut: secrets it has to ask
    // for are kept under that shortcut's id as "jump:<name>", and the prompt says which host.
    private Guid? _jumpFor;
    private string _promptLabel = "";

    // The owner's key passphrase, when the jump logs in with the owner's own key file.
    private bool _sharesKey;

    internal static SshTerminalSession ForJump(SessionDefinition hop, SessionDefinition owner, CredentialVault vault, Func<string, string, string?>? promptForSecret) =>
        new(hop, vault, promptForSecret)
        {
            _jumpFor = owner.Id,
            _promptLabel = $"Jump host {hop.Username}@{hop.Host}: ",
            _sharesKey = hop.KeyPath.Length > 0 && string.Equals(hop.KeyPath, owner.KeyPath, StringComparison.OrdinalIgnoreCase)
        };

    // Set by RunCommandAsync: a login with no shell, for one command.
    private bool _commandOnly;

    /// <summary>
    /// Logs in with no shell, runs <paramref name="command"/> and logs out: for asking a host something
    /// while no tab is open on it. It never asks for anything: a login that needs a typed password fails
    /// instead, so the caller decides beforehand whether one is saved.
    /// </summary>
    public static async Task<(int Exit, string Output, string Error)> RunCommandAsync(
        SessionDefinition definition, CredentialVault vault, string command, CancellationToken token = default)
    {
        var session = new SshTerminalSession(definition, vault) { _commandOnly = true };
        try
        {
            await session.ConnectAsync(token).ConfigureAwait(false);
            using var c = session._client!.CreateCommand(command);
            c.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = await Task.Run(() => c.Execute(), token).ConfigureAwait(false);
            return (c.ExitStatus ?? -1, output, c.Error);
        }
        finally
        {
            await session.DisconnectAsync().ConfigureAwait(false);
        }
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
        _closedRaised = 0;

        // Off the UI thread: a KeePassXC lookup can wait minutes for the user to unlock or allow
        // access, and agent calls are IPC. Prompts marshal to the UI themselves.
        _jump?.Dispose();
        _jump = null;
        var host = _definition.Host;
        var port = _definition.Port;
        if (_definition.JumpHost.Trim().Length > 0)
        {
            // The host key is still checked against the real name below, not 127.0.0.1.
            _jump = await SshJump.OpenAsync(_definition, host, port, _vault, _promptForSecret, cancellationToken).ConfigureAwait(false);
            (host, port) = ("127.0.0.1", _jump.LocalPort);
        }

        var info = await Task.Run(() => BuildConnectionInfoAsync(host, port, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        _client = new SshClient(info);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _client.ErrorOccurred += OnClientError;

        var client = _client;
        var jump = _jump;
        try
        {
            await Task.Run(() => KnownHosts.Connect(client, _definition.Host, _definition.Port), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (jump is not null && ex is SshException or SocketException or IOException)
        {
            // Through a jump the connection to 127.0.0.1 only sees the channel close; the jump
            // host's refusal ("connect failed", say) is the real reason, and arrives a moment later.
            for (var i = 0; i < 20 && jump.Failure is null; i++)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }

            throw new SshConnectionException($"{jump.Route} could not reach {_definition.Host}:{_definition.Port}: {jump.Failure ?? "the jump host closed the connection (nothing listening there, or it is not allowed to connect)"}.");
        }

        // Disconnected (or the tab closed) while the connect was still running: the token cannot
        // stop a connect in progress, so the session it made is dropped here, not left open.
        if (!ReferenceEquals(_client, client))
        {
            try { client.Disconnect(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            client.Dispose();
            jump?.Dispose();
            throw new OperationCanceledException("Disconnected while connecting.");
        }

        if (_commandOnly)
        {
            return;
        }

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

        // The banner and first prompt can land between CreateShellStream and the subscription
        // above; ShellStream kept them in its buffer, so hand them over now.
        Drain(_stream);
    }

    /// <summary>
    /// The event is only the doorbell. ShellStream also keeps every byte in its own read buffer
    /// until something reads it, so taking the bytes from the event left that buffer growing for
    /// the life of the session. Reading drains it, and also picks up anything that arrived
    /// before the subscription.
    /// </summary>
    private void OnStreamData(object? sender, ShellDataEventArgs e)
    {
        if (sender is ShellStream stream)
        {
            Drain(stream);
        }
    }

    private void Drain(ShellStream stream)
    {
        lock (_readGate)
        {
            try
            {
                // Read blocks only when the buffer is empty, which DataAvailable rules out;
                // this lock makes us its only reader.
                while (stream.DataAvailable)
                {
                    var n = stream.Read(_bytes, 0, _bytes.Length);
                    if (n <= 0)
                    {
                        break;
                    }

                    var count = _decoder.GetChars(_bytes, 0, n, _chars, 0);
                    if (count > 0)
                    {
                        DataReceived?.Invoke(this, new TerminalDataEventArgs(new string(_chars, 0, count)));
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
            {
                // Closed while draining: nothing left to deliver.
            }
        }
    }

    private void OnStreamError(object? sender, ExceptionEventArgs e) => RaiseClosed(e.Exception.Message);

    internal async Task<ConnectionInfo> BuildConnectionInfoAsync(string host, int port, CancellationToken token)
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

        return new ConnectionInfo(host, port, username, methods.ToArray());
    }

    /// <summary>
    /// A prompt for the account password, not for a one-time code: "Passcode", "OTP",
    /// "Verification code" or a token must not be answered with the password, which would burn
    /// a login attempt and hand the password to a prompt that did not ask for it.
    /// </summary>
    internal static bool IsPasswordPrompt(string request)
    {
        if (!request.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var otp in new[] { "passcode", "otp", "one-time", "one time", "verification", "token" })
        {
            if (request.Contains(otp, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private PrivateKeyFile LoadKey()
    {
        if (string.IsNullOrWhiteSpace(_definition.KeyPath) || !File.Exists(_definition.KeyPath))
        {
            throw new FileNotFoundException($"Private key not found: {_definition.KeyPath}");
        }

        var passphrase = _vault.Get(_definition.Id, "passphrase") ?? (_sharesKey && _jumpFor is { } owner ? _vault.Get(owner, "passphrase") : null);
        return string.IsNullOrEmpty(passphrase)
            ? new PrivateKeyFile(_definition.KeyPath)
            : new PrivateKeyFile(_definition.KeyPath, passphrase);
    }

    private string? ResolveSecret(string name)
    {
        // The server writes the prompt text, so it must not pick the vault entry: a prompt of
        // "passphrase" was answered with the key passphrase saved for this shortcut. Answers to
        // other prompts are kept under their own names.
        var key = IsPasswordPrompt(name) ? "password" : "kbd:" + name.Trim();
        if (_jumpFor is { } owner)
        {
            return _vault.Get(_definition.Id, key) ?? _vault.Get(owner, "jump:" + key) ?? _promptForSecret?.Invoke(_promptLabel + name, "jump:" + key);
        }

        return _vault.Get(_definition.Id, key) ?? _promptForSecret?.Invoke(name, key);
    }

    private void OnClientError(object? sender, ExceptionEventArgs e) => RaiseClosed(e.Exception.Message);

    private void RaiseClosed(string reason)
    {
        // A dropped link raises ErrorOccurred on both the client and the shell stream; report
        // it once, or the controller starts two reconnects.
        if (!_established || Interlocked.Exchange(ref _closedRaised, 1) == 1)
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
        lock (_writeGate)
        {
            // No token on the continuation itself: a cancelled link would let the next write start
            // while this one still runs. Each write checks its own token instead.
            _writeChain = _writeChain.ContinueWith(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(text);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _writeChain;
        }
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

        _jump?.Dispose();
        _jump = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
