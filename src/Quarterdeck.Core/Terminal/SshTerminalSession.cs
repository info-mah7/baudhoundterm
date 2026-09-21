using System.Text;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
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
    private CancellationTokenSource? _pump;
    private volatile bool _closing;

    public SshTerminalSession(
        SessionDefinition definition,
        CredentialVault vault,
        Func<string, string?>? promptForSecret = null)
    {
        _definition = definition;
        _vault = vault;
        _promptForSecret = promptForSecret;
    }

    public bool IsConnected => _client?.IsConnected == true && _stream is not null;

    /// <summary>Exposed so the transfer pane and port forwards can reuse the authenticated connection.</summary>
    public SshClient? Client => _client;

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<TerminalClosedEventArgs>? Closed;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _closing = false;
        var info = BuildConnectionInfo();

        _client = new SshClient(info);
        _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _client.ErrorOccurred += OnClientError;

        await Task.Run(() => _client.Connect(), cancellationToken).ConfigureAwait(false);

        _stream = _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: 120,
            rows: 30,
            width: 0,
            height: 0,
            bufferSize: 64 * 1024);

        _pump = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_pump.Token), _pump.Token);
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();

        switch (_definition.AuthMethod)
        {
            case SshAuthMethod.PublicKey:
                methods.Add(new PrivateKeyAuthenticationMethod(_definition.Username, LoadKey()));
                break;

            case SshAuthMethod.KeyboardInteractive:
                var interactive = new KeyboardInteractiveAuthenticationMethod(_definition.Username);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                    {
                        prompt.Response = ResolveSecret(prompt.Request) ?? string.Empty;
                    }
                };
                methods.Add(interactive);
                break;

            default:
                var password = ResolveSecret("password")
                               ?? throw new InvalidOperationException(
                                   $"No password available for {_definition.Name}.");
                methods.Add(new PasswordAuthenticationMethod(_definition.Username, password));
                break;
        }

        return new ConnectionInfo(_definition.Host, _definition.Port, _definition.Username, methods.ToArray());
    }

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
        var key = name.Contains("pass", StringComparison.OrdinalIgnoreCase) ? "password" : name;
        return _vault.Get(_definition.Id, key) ?? _promptForSecret?.Invoke(name);
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[buffer.Length * 2];

        try
        {
            while (!token.IsCancellationRequested && _stream is not null)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // Decode incrementally: a UTF-8 sequence can straddle two reads.
                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0)
                {
                    DataReceived?.Invoke(this, new TerminalDataEventArgs(new string(chars, 0, count)));
                }
            }

            RaiseClosed(_closing ? "Disconnected." : "Connection closed by remote host.");
        }
        catch (OperationCanceledException)
        {
            RaiseClosed("Disconnected.");
        }
        catch (Exception ex)
        {
            RaiseClosed(ex.Message);
        }
    }

    private void OnClientError(object? sender, ExceptionEventArgs e) => RaiseClosed(e.Exception.Message);

    private void RaiseClosed(string reason) =>
        Closed?.Invoke(this, new TerminalClosedEventArgs(reason, _closing));

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            return Task.CompletedTask;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        return _stream.WriteAsync(bytes.AsMemory(), cancellationToken).AsTask();
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
        _pump?.Cancel();

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
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
