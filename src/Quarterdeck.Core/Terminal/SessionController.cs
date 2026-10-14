using Quarterdeck.Core.Automation;
using Quarterdeck.Core.Logging;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.KeePassXc;

namespace Quarterdeck.Core.Terminal;

public enum SessionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

public sealed class SessionStateEventArgs(SessionState state, string? message) : EventArgs
{
    public SessionState State { get; } = state;
    public string? Message { get; } = message;
}

/// <summary>
/// One tab's worth of behaviour: picks the transport, feeds the logger, runs the login
/// script, and reconnects with backoff when the far end drops. The UI talks only to this.
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private readonly CredentialVault _vault;
    private readonly Func<string, string?>? _promptForSecret;

    private ITerminalSession? _transport;
    private SessionLogger? _logger;
    private ExpectEngine? _expect;
    private CancellationTokenSource? _life;
    private int _attempt;
    private int _columns = 120;
    private int _rows = 30;
    private bool _userInitiatedClose;
    private readonly object _closeGate = new();
    private ITerminalSession? _closeHandled;

    public SessionController(
        SessionDefinition definition,
        CredentialVault vault,
        Func<string, string?>? promptForSecret = null)
    {
        Definition = definition;
        _vault = vault;
        _promptForSecret = promptForSecret;
    }

    public SessionDefinition Definition { get; }
    public SessionState State { get; private set; } = SessionState.Disconnected;

    /// <summary>The live SSH client when this is an SSH session, for the transfer pane.</summary>
    public SshTerminalSession? Ssh => _transport as SshTerminalSession;

    public SerialTerminalSession? Serial => _transport as SerialTerminalSession;

    public string? LogPath => _logger?.CurrentPath;

    /// <summary>Bytes in and out on serial and telnet links, across reconnects. For the activity lamps.</summary>
    public LinkCounters Counters { get; } = new();

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<SessionStateEventArgs>? StateChanged;

    public async Task ConnectAsync()
    {
        _userInitiatedClose = false;
        _life = new CancellationTokenSource();
        _attempt = 0;
        await ConnectOnceAsync(_life.Token).ConfigureAwait(false);
    }

    private async Task ConnectOnceAsync(CancellationToken token)
    {
        SetState(SessionState.Connecting, $"Connecting to {Describe()}...");

        try
        {
            // Never leave a previous transport subscribed: its late events would land here.
            await DetachTransportAsync().ConfigureAwait(false);

            _transport = Definition.Protocol switch
            {
                SessionProtocol.Ssh => new SshTerminalSession(Definition, _vault, _promptForSecret),
                SessionProtocol.Serial => new SerialTerminalSession(Definition, Counters),
                SessionProtocol.LocalShell => new ConPtyTerminalSession(Definition),
                SessionProtocol.Telnet => new TelnetTerminalSession(Definition, Counters),
                _ => throw new NotSupportedException($"Unknown protocol {Definition.Protocol}")
            };

            _transport.DataReceived += OnTransportData;
            _transport.Closed += OnTransportClosed;

            _logger ??= new SessionLogger(Definition);

            // The login script listens from the first byte: a login: or Password: prompt sent
            // during the connect would otherwise be gone before the script started.
            _expect = Definition.AutoLogin.Count > 0 ? new ExpectEngine(_transport, _vault, Definition.Id) : null;

            await _transport.ConnectAsync(token).ConfigureAwait(false);
            await _transport.ResizeAsync(_columns, _rows).ConfigureAwait(false);

            _attempt = 0;
            Definition.LastConnected = DateTimeOffset.UtcNow;
            SetState(SessionState.Connected, $"Connected to {Describe()}.");

            if (_expect is { } expect)
            {
                // A script problem stops the script, never the connection: RunAsync reports
                // bad patterns, missing secrets and failed sends as a stop.
                var stopped = await expect.RunAsync(Definition.AutoLogin, token).ConfigureAwait(false);
                if (stopped is not null)
                {
                    Emit($"\r\n[baudhoundterm] login script stopped at step {stopped.Number}: {stopped.Reason.TrimEnd('.')}.\r\n");
                }

                expect.Detach();
                if (ReferenceEquals(_expect, expect))
                {
                    _expect = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetState(SessionState.Disconnected, "Cancelled.");
        }
        catch (Exception ex)
        {
            Emit($"\r\n[baudhoundterm] {ex.Message}\r\n");

            // Retrying a rejected password just spends the account's lockout budget. Only
            // transient failures earn a reconnect.
            if (IsPermanent(ex))
            {
                Emit("[baudhoundterm] not retrying: this failure will not fix itself.\r\n");
                SetState(SessionState.Failed, ex.Message);
                return;
            }

            await ScheduleReconnectAsync(ex.Message, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wrong credentials, a missing key file, a cancelled prompt: reconnecting cannot help,
    /// and on a domain account it walks straight into a lockout.
    /// </summary>
    private static bool IsPermanent(Exception ex) => ex switch
    {
        Renci.SshNet.Common.SshAuthenticationException => true,
        OperationCanceledException => true,
        FileNotFoundException => true,
        NotSupportedException => true,
        InvalidOperationException => true,   // no credential available for this session
        KeePassXcException => true,          // KeePassXC closed, locked, denied, or no entry: retrying re-prompts forever
        _ => false
    };

    private void OnTransportData(object? sender, TerminalDataEventArgs e)
    {
        _logger?.Append(e.Text);
        DataReceived?.Invoke(this, e);
    }

    private void OnTransportClosed(object? sender, TerminalClosedEventArgs e)
    {
        // Only the current transport's first close counts: a replaced transport, or a second
        // error event for the same drop, would start a second reconnect chain and a second
        // live connection.
        if (sender is not ITerminalSession closed)
        {
            return;
        }

        lock (_closeGate)
        {
            if (!ReferenceEquals(closed, _transport) || ReferenceEquals(closed, _closeHandled))
            {
                return;
            }

            _closeHandled = closed;
        }

        closed.Closed -= OnTransportClosed;

        if (e.WasExpected || _userInitiatedClose)
        {
            SetState(SessionState.Disconnected, e.Reason);
            return;
        }

        Emit($"\r\n[baudhoundterm] {e.Reason}\r\n");

        // Not on this thread: SSH.NET raises the drop on its own receive thread, and the
        // reconnect starts by disposing that client, which waits for the receive thread to
        // finish. Run inline, it waited for itself and the tab sat at "Reconnecting" forever.
        var token = _life?.Token ?? CancellationToken.None;
        _ = Task.Run(() => ScheduleReconnectAsync(e.Reason, token));
    }

    private async Task ScheduleReconnectAsync(string? reason, CancellationToken token)
    {
        var policy = Definition.Reconnect;

        if (!policy.Enabled || _userInitiatedClose)
        {
            SetState(SessionState.Failed, reason);
            return;
        }

        if (policy.MaxAttempts > 0 && _attempt >= policy.MaxAttempts)
        {
            SetState(SessionState.Failed, $"Giving up after {_attempt} attempts: {reason}");
            return;
        }

        _attempt++;

        var delay = Math.Min(
            policy.InitialDelayMs * Math.Pow(policy.BackoffFactor, _attempt - 1),
            policy.MaxDelayMs);

        SetState(SessionState.Reconnecting,
            $"Reconnecting in {delay / 1000:F0}s (attempt {_attempt}"
            + (policy.MaxAttempts > 0 ? $" of {policy.MaxAttempts}" : "") + ")...");

        await DetachTransportAsync().ConfigureAwait(false);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ConnectOnceAsync(token).ConfigureAwait(false);
    }

    public Task WriteAsync(string text) =>
        _transport?.WriteAsync(text) ?? Task.CompletedTask;

    public Task ResizeAsync(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        return _transport?.ResizeAsync(columns, rows) ?? Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _userInitiatedClose = true;
        _life?.Cancel();
        await DetachTransportAsync().ConfigureAwait(false);
        SetState(SessionState.Disconnected, "Disconnected.");
    }

    private async Task DetachTransportAsync()
    {
        if (_transport is null)
        {
            return;
        }

        var transport = _transport;
        _transport = null;
        _expect?.Detach();
        _expect = null;
        transport.DataReceived -= OnTransportData;
        transport.Closed -= OnTransportClosed;
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private string Describe() => Definition.Protocol switch
    {
        // The user name may come from KeePassXC at connect time, so ask the session for it.
        SessionProtocol.Ssh => ((_transport as SshTerminalSession)?.Username ?? Definition.Username) is { Length: > 0 } user
            ? $"{user}@{Definition.Host}:{Definition.Port}"
            : $"{Definition.Host}:{Definition.Port}",
        SessionProtocol.Serial => $"{Definition.SerialPort} @ {Definition.BaudRate}",
        SessionProtocol.Telnet => $"{Definition.Host}:{Definition.Port}",
        _ => Definition.ShellCommand
    };

    private void Emit(string text) => DataReceived?.Invoke(this, new TerminalDataEventArgs(text));

    private void SetState(SessionState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(this, new SessionStateEventArgs(state, message));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _logger?.Dispose();
        _logger = null;
        _life?.Dispose();
    }
}
