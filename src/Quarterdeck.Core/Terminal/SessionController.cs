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

/// <summary>Whether this session's shell is running inside tmux on the host.</summary>
public enum TmuxState
{
    /// <summary>Not asked for, or not an SSH session.</summary>
    Off,

    /// <summary>The shell is in a tmux session that is still there: closing the tab leaves it running.</summary>
    Live,

    /// <summary>Asked for, but the host has no tmux or no longer has that session.</summary>
    Missing
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
    private readonly Func<string, string, string?>? _promptForSecret;

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
        Func<string, string, string?>? promptForSecret = null)
    {
        Definition = definition;
        _vault = vault;
        _promptForSecret = promptForSecret;
    }

    public SessionDefinition Definition { get; }

    /// <summary>
    /// The tmux session this connection runs in: one of the shortcut's named sessions, chosen
    /// per tab (build 72), or the shortcut's main session.
    /// </summary>
    public string TmuxSessionName { get; init; } = "";

    private string TmuxTarget => string.IsNullOrWhiteSpace(TmuxSessionName) ? Definition.TmuxSession : TmuxSessionName;
    public SessionState State { get; private set; } = SessionState.Disconnected;

    /// <summary>The live SSH client when this is an SSH session, for the transfer pane.</summary>
    public SshTerminalSession? Ssh => _transport as SshTerminalSession;

    public SerialTerminalSession? Serial => _transport as SerialTerminalSession;

    public string? LogPath => _logger?.CurrentPath;

    /// <summary>Every log file this connection wrote; still answers after it is disposed.</summary>
    public IReadOnlyList<string> LogFiles => _logFiles ?? (IReadOnlyList<string>?)_logger?.Paths ?? Array.Empty<string>();

    private List<string>? _logFiles;

    /// <summary>Starts the session log now, for an audit turned on mid-session.</summary>
    public void EnsureLog() => _logger?.EnsureOpen();

    /// <summary>Bytes in and out on serial and telnet links, across reconnects. For the activity lamps.</summary>
    public LinkCounters Counters { get; } = new();

    public event EventHandler<TerminalDataEventArgs>? DataReceived;
    public event EventHandler<SessionStateEventArgs>? StateChanged;

    /// <summary>The marker changed: the shell entered tmux, or the session turned out not to be there.</summary>
    public event EventHandler? TmuxChanged;

    /// <summary>Whether this shell is inside tmux, which is what makes closing the tab harmless.</summary>
    public TmuxState Tmux { get; private set; }

    public async Task ConnectAsync()
    {
        _userInitiatedClose = false;
        _life = new CancellationTokenSource();
        _attempt = 0;
        _retry = null;
        await ConnectOnceAsync(_life.Token).ConfigureAwait(false);
        if (_retry is { } reason)
        {
            // The first attempt failed: keep trying in the background, not nested in this call.
            var token = _life.Token;
            _ = Task.Run(() => ScheduleReconnectAsync(reason, token), token);
        }
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
            _expect = Definition.AutoLogin.Count > 0 ? new ExpectEngine(_transport, _vault, Definition.Id, _promptForSecret) : null;

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

            if (Definition is { Protocol: SessionProtocol.Ssh, Tmux: true })
            {
                var entry = Definition.TmuxSessionList().FirstOrDefault(t => TmuxName(t.Name) == TmuxName(TmuxTarget));
                await _transport.WriteAsync(TmuxCommand(TmuxTarget, entry), token).ConfigureAwait(false);

                // Asked for is not the same as running: a host without tmux keeps a plain shell,
                // and the marker must say which of the two this is. The answer comes from the
                // host, on a channel of its own, so nothing is guessed from the terminal's text.
                _ = Task.Run(async () =>
                {
                    // A slow login shell can still be on its way to tmux, so a first "not there"
                    // is asked again before the marker settles on it.
                    foreach (var wait in (int[])[1200, 3000, 6000])
                    {
                        await Task.Delay(wait, token).ConfigureAwait(false);
                        await RefreshTmuxAsync(token).ConfigureAwait(false);
                        if (Tmux != TmuxState.Missing)
                        {
                            return;
                        }
                    }
                }, token);
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

            _retry = ex.Message;   // the loop in ScheduleReconnectAsync tries again
        }
    }

    // Set by a failed connect so the reconnect loop goes round again, instead of the connect
    // awaiting the next attempt: each retry then nested inside the last, and the chain (and its
    // memory) grew for as long as the far end stayed down.
    private string? _retry;
    private int _reconnecting;

    /// <summary>
    /// Typed into the login shell after connect. exec replaces the shell, so leaving tmux ends
    /// the channel; -A attaches when the session already exists. POSIX shells and fish accept
    /// it; the leading space keeps it out of bash history under ignorespace. Mouse goes on for
    /// this session only: with it off, tmux turns the wheel into Up/Down keys, so scrolling
    /// paged through the shell's command history instead of the output.
    /// </summary>
    internal static string TmuxCommand(string session, TmuxSessionEntry? entry = null)
    {
        var name = TmuxName(session);
        // With -A an existing session is attached as it is: the folder and command only apply
        // when tmux creates it, so a running program is never restarted or typed into.
        var folder = entry is null ? null : TmuxFolder(entry.Folder);
        var command = entry?.StartCommand ?? "";
        var run = command.Length > 0 && TmuxStart.Ok(command) ? $" '{command}; exec \"$SHELL\"'" : "";
        return $" command -v tmux >/dev/null 2>&1 && exec tmux new-session -A -s {name}{(folder is null ? "" : $" -c {folder}")}{run} \\; set-option -q mouse on"
            + " || echo '[baudhoundterm] tmux not found on this host: plain shell, not kept on disconnect'\r";
    }

    /// <summary>Characters a folder may use: enough for paths, nothing a shell would act on.</summary>
    public static bool TmuxFolderOk(string folder) =>
        folder.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '/' or '.' or '_' or '-' or '~' or '+' or '@' or ',');

    /// <summary>The folder for tmux -c, double-quoted so the shell expands $HOME (tmux does not expand ~).</summary>
    private static string? TmuxFolder(string folder)
    {
        var f = folder.Trim();
        if (f.Length == 0 || !TmuxFolderOk(f))
        {
            return null;
        }

        return "\"" + (f == "~" ? "$HOME" : f.StartsWith("~/", StringComparison.Ordinal) ? "$HOME/" + f[2..] : f) + "\"";
    }

    /// <summary>tmux rejects '.' and ':' in names, and a safe name needs no quoting in any shell.</summary>
    public static string TmuxName(string session)
    {
        var name = new string(session.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return name.Length == 0 ? "bht" : name;
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
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
        {
            return;   // one chain of attempts at a time
        }

        try
        {
            while (true)
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

                _retry = null;
                await ConnectOnceAsync(token).ConfigureAwait(false);
                if (_retry is not { } again)
                {
                    return;   // connected, or a failure that will not fix itself
                }

                reason = again;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>
    /// Asks the host whether the tmux session is there, on a second channel of the connection
    /// that is already open: no new login, and no dependence on what the terminal is showing.
    /// </summary>
    public async Task RefreshTmuxAsync(CancellationToken token = default)
    {
        var was = Tmux;
        if (Definition is not { Protocol: SessionProtocol.Ssh, Tmux: true })
        {
            Tmux = TmuxState.Off;
        }
        else
        {
            Tmux = await HasTmuxSessionAsync(token).ConfigureAwait(false) ? TmuxState.Live : TmuxState.Missing;
        }

        if (Tmux != was)
        {
            TmuxChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The tmux session's folder ("~/..." under the home folder) and the command line of the program
    /// running in it ("" for a bare shell), asked on a second channel like <see cref="RefreshTmuxAsync"/>.
    /// Null when the host does not answer.
    /// </summary>
    public async Task<(string Folder, string Running)?> TmuxPaneAsync(CancellationToken token = default)
    {
        if (Ssh?.Client is not { IsConnected: true } client)
        {
            return null;
        }

        try
        {
            // One value per line: tmux prints a tab inside a format as "_". A program started by the
            // session runs as a child of the pane's shell, which tmux names as the pane's command, so
            // the command line is read from the shell's first child.
            var name = TmuxName(TmuxTarget);
            using var command = client.CreateCommand(
                $"printf '%s\\n' \"$HOME\"; tmux display-message -p -t {name} '#{{pane_current_path}}'; " +
                $"ps -o args= -p \"$(pgrep -P \"$(tmux display-message -p -t {name} '#{{pane_pid}}')\" | head -n 1)\" 2>/dev/null; true");
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            var text = await Task.Run(() => command.Execute(), token).ConfigureAwait(false);
            var parts = text.Replace("\r", "").TrimEnd('\n').Split('\n');
            if (parts.Length < 2)
            {
                return null;
            }

            var (home, path) = (parts[0].TrimEnd('/'), parts[1]);
            var folder = home.Length > 0 && path == home ? "~"
                : home.Length > 0 && path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..]
                : path;
            return (TmuxFolderOk(folder) ? folder : "", parts.Length > 2 ? parts[2].Trim() : "");
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> HasTmuxSessionAsync(CancellationToken token)
    {
        if (Ssh?.Client is not { IsConnected: true } client)
        {
            return false;
        }

        try
        {
            using var command = client.CreateCommand($"tmux has-session -t {TmuxName(TmuxTarget)}");
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            await Task.Run(() => command.Execute(), token).ConfigureAwait(false);
            return command.ExitStatus == 0;
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The host answered nothing useful; "not there" is the safe reading, and the marker
            // simply does not claim a session is being kept.
            return false;
        }
    }

    /// <summary>
    /// Ends the tmux session on the host, with everything running in it. Sent on its own channel,
    /// so it works whatever the terminal is showing: a full-screen editor would have swallowed a
    /// typed command. The shell's channel then ends by itself, because tmux was its exec.
    /// </summary>
    /// <returns>Null when it is gone, or why it is not.</returns>
    public async Task<string?> EndTmuxAsync(CancellationToken token = default)
    {
        if (Ssh?.Client is not { IsConnected: true } client)
        {
            return "This session has no live SSH connection.";
        }

        var name = TmuxName(TmuxTarget);
        try
        {
            using var command = client.CreateCommand($"tmux kill-session -t {name}");
            command.CommandTimeout = TimeSpan.FromSeconds(15);
            await Task.Run(() => command.Execute(), token).ConfigureAwait(false);
            if (command.ExitStatus != 0)
            {
                var why = (command.Error.Length > 0 ? command.Error : command.Result).Trim();
                return why.Length > 0 ? why : $"tmux refused to end {name} (exit {command.ExitStatus}).";
            }
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException)
        {
            return ex.Message;
        }

        Emit($"\r\n[baudhoundterm] tmux session {name} ended on the host.\r\n");
        await RefreshTmuxAsync(token).ConfigureAwait(false);
        return null;
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
        if (Tmux != TmuxState.Off)
        {
            Tmux = TmuxState.Off;
            TmuxChanged?.Invoke(this, EventArgs.Empty);
        }

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
        _logFiles = _logger?.Paths;
        _logger?.Dispose();
        _logger = null;
        _life?.Dispose();
    }
}
