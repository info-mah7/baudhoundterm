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
        // -u: UTF-8 whatever the login's locale says. With none set (some sshd setups pass no LANG),
        // tmux drew every box-drawing and symbol character as "_".
        return $" command -v tmux >/dev/null 2>&1 && exec tmux -u new-session -A -s {name}{(folder is null ? "" : $" -c {folder}")}{run} \\; set-option -q mouse on"
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

    /// <summary>
    /// A new shortcut's tmux session: its own name in tmux's alphabet, "Rebex test" -> "rebex-test".
    /// Every shortcut used to start as "bht", and tmux keeps one set of sessions per login on a host,
    /// so all the shortcuts to one host opened the same session.
    /// </summary>
    public static string DefaultSessionName(string shortcutName) =>
        TmuxName(string.Join('-', shortcutName.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>
    /// Other SSH shortcuts that open one of this shortcut's tmux sessions: the same host and login
    /// (tmux has one set of sessions per login, whatever the port) and the same session name.
    /// </summary>
    public static List<(SessionDefinition Shortcut, string Session)> SharingTmux(SessionDefinition d, IEnumerable<SessionDefinition> all)
    {
        if (d is not { Protocol: SessionProtocol.Ssh, Tmux: true })
        {
            return new();
        }

        var mine = d.TmuxSessionList().Select(t => TmuxName(t.Name)).ToHashSet();
        return all.Where(s => s.Id != d.Id && s is { Protocol: SessionProtocol.Ssh, Tmux: true }
                              && string.Equals(s.Host.Trim(), d.Host.Trim(), StringComparison.OrdinalIgnoreCase)
                              && string.Equals(s.Username.Trim(), d.Username.Trim(), StringComparison.Ordinal))
                  .SelectMany(s => s.TmuxSessionList().Select(t => TmuxName(t.Name)).Where(mine.Contains).Distinct().Select(n => (s, n)))
                  .ToList();
    }

    /// <summary>
    /// The folder a shell's title shows, "user@host: ~/work/proj" -> "~/work/proj" (bash and zsh set
    /// such titles); "" when the title is something else or the folder has characters a shell acts on.
    /// </summary>
    public static string FolderFromTitle(string title)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title.Trim(), @"^[^@\s]+@[^:\s]+:\s*(\S.*?)\s*[$#]?$");
        var folder = m.Success ? m.Groups[1].Value.Trim() : "";
        return folder.Length > 0 && (folder[0] is '~' or '/') && TmuxFolderOk(folder) ? folder : "";
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

    /// <summary>A Claude Code process found in a plain session: its id, its folder ("~/..."), its command line.</summary>
    public sealed record ClaudeProcess(int Pid, string Folder, string Args);

    // Run in sh on the host, whatever the login shell: the sshd process serving this connection (every
    // channel of it, the tab's shell included, descends from it), then each claude process of this user
    // with whether it is inside tmux, whether it descends from this connection, its folder and command line.
    private const string FindClaudeScript =
        "me=$$; while [ \"$me\" -gt 1 ] 2>/dev/null; do case \"$(ps -o comm= -p \"$me\")\" in sshd*) break;; esac; " +
        "me=$(ps -o ppid= -p \"$me\" | tr -d \" \"); done; printf \"%s|%s\\n\" \"$HOME\" \"$me\"; " +
        "for p in $(pgrep -u \"$(id -u)\" -x claude); do t=0; o=0; q=$p; while [ \"$q\" -gt 1 ] 2>/dev/null; do " +
        "[ \"$q\" = \"$me\" ] && o=1; case \"$(ps -o comm= -p \"$q\")\" in tmux*) t=1;; esac; " +
        "q=$(ps -o ppid= -p \"$q\" | tr -d \" \"); done; " +
        "echo \"$p|$t|$o|$(readlink /proc/$p/cwd)|$(ps -o args= -p $p)\"; done";

    /// <summary>
    /// The Claude Code running in this tab's own shell, not in tmux, asked on a second channel. Null when
    /// there is none, the host cannot tell (no /proc), or it does not answer.
    /// </summary>
    public async Task<ClaudeProcess?> FindClaudeAsync(CancellationToken token = default)
    {
        if (Ssh?.Client is not { IsConnected: true } client)
        {
            return null;
        }

        try
        {
            using var command = client.CreateCommand($"sh -c '{FindClaudeScript}'");
            command.CommandTimeout = TimeSpan.FromSeconds(15);
            var lines = (await Task.Run(() => command.Execute(), token).ConfigureAwait(false))
                .Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var home = lines[0].Split('|')[0].TrimEnd('/');
            var mine = lines.Skip(1).Select(l => l.Split('|', 5)).Where(f => f.Length == 5 && f[1] == "0" && f[2] == "1")
                            .Select(f => (Pid: int.TryParse(f[0], out var p) ? p : 0, Cwd: f[3], Args: f[4].Trim()))
                            .Where(x => x.Pid > 0).OrderByDescending(x => x.Pid).ToList();
            if (mine.Count == 0)
            {
                return null;
            }

            var (pid, cwd, args) = mine[0];
            var folder = home.Length > 0 && cwd == home ? "~"
                : home.Length > 0 && cwd.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + cwd[home.Length..] : cwd;
            return new ClaudeProcess(pid, TmuxFolderOk(folder) ? folder : "", args);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts tmux session <paramref name="session"/> on the host holding <paramref name="text"/>, what a
    /// plain session printed, so it is kept from then on. The text and a small sh script go up on second
    /// channels of this connection (nothing is typed into the session); the tmux session runs the script,
    /// which prints the text, waits for process <paramref name="waitForPid"/> to end when given (the Claude
    /// in the old tab: two Claudes on one conversation would both write it), runs
    /// <paramref name="startCommand"/> (claude --continue picks the conversation up), then gives a shell.
    /// A running program cannot be moved into tmux; its conversation can. Null when done, else why not;
    /// an existing session of that name is left alone.
    /// </summary>
    public async Task<string?> SeedTmuxSessionAsync(string session, string folder, string text,
        string startCommand = "", int waitForPid = 0, CancellationToken token = default)
    {
        if (Ssh?.Client is not { IsConnected: true } client)
        {
            return "the session is not connected";
        }

        if (startCommand.Length > 0 && !TmuxStart.Ok(startCommand))
        {
            return "the start command has a quote, backslash or line break";
        }

        var name = TmuxName(session);
        var dir = "\"$HOME/.cache/baudhoundterm";

        string Run(string command)
        {
            using var c = client.CreateCommand(command);
            c.CommandTimeout = TimeSpan.FromSeconds(30);
            var output = c.Execute();
            if (c.ExitStatus is { } code and not 0)
            {
                throw new IOException($"\"{command[..Math.Min(command.Length, 40)]}...\" failed ({code}): {(c.Error ?? output).Trim()}");
            }

            return output;
        }

        // Base64 has no character any shell acts on, so nothing needs quoting on its way up.
        void Upload(string file, string content)
        {
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
            Run($"rm -f {dir}/{file}.b64\"");
            for (var i = 0; i < b64.Length; i += 48_000)
            {
                token.ThrowIfCancellationRequested();
                Run($"printf %s {b64.Substring(i, Math.Min(48_000, b64.Length - i))} >> {dir}/{file}.b64\"");
            }

            Run($"base64 -d {dir}/{file}.b64\" > {dir}/{file}\" && rm -f {dir}/{file}.b64\"");
        }

        var script = new System.Text.StringBuilder()
            .Append("#!/bin/sh\n# Written by BaudHoundTerm: how tmux session ").Append(name).Append(" starts. It deletes itself.\n")
            .Append("d=\"$HOME/.cache/baudhoundterm\"\n")
            .Append("cat \"$d/").Append(name).Append(".txt\"; rm -f \"$d/").Append(name).Append(".txt\" \"$d/").Append(name).Append(".sh\"\n");
        if (waitForPid > 0)
        {
            script.Append("if kill -0 ").Append(waitForPid).Append(" 2>/dev/null; then\n")
                  .Append("  printf '\\n[baudhoundterm] Waiting for Claude in the old tab to finish (type /exit there); it then continues here.\\n'\n")
                  .Append("  while kill -0 ").Append(waitForPid).Append(" 2>/dev/null; do sleep 1; done\nfi\n");
        }

        // The start command and the shell after it run in the user's own shell, interactive, so its rc
        // file sets the PATH: from plain sh, "claude" in ~/.local/bin was not found. Not a login shell:
        // a .bashrc that clears the screen on SSH logins wiped the copied text with it. The command has
        // no single quote (TmuxStart.Ok), so it can sit inside one.
        script.Append(startCommand.Length > 0
            ? $"exec \"${{SHELL:-/bin/sh}}\" -i -c '{startCommand}; exec \"$SHELL\" -i'\n"
            : "exec \"${SHELL:-/bin/sh}\" -i\n");

        try
        {
            return await Task.Run(() =>
            {
                if (Run($"tmux has-session -t {name} 2>/dev/null && echo yes || echo no").Trim() == "yes")
                {
                    return $"a tmux session called {name} already runs on the host";
                }

                Run($"mkdir -p {dir}\"");
                Upload(name + ".txt", text);
                Upload(name + ".sh", script.ToString());
                // tmux is started from the user's interactive login shell, through one more script: when
                // this starts the tmux server, the server (and every later session) gets that environment,
                // not the thin one of a command channel.
                var cd = TmuxFolder(folder) is { } f ? $" -c {f}" : "";
                Upload(name + ".start.sh",
                    $"#!/bin/sh\nrm -f \"$HOME/.cache/baudhoundterm/{name}.start.sh\"\n" +
                    $"exec tmux new-session -d -s {name} -x {Math.Max(_columns, 20)} -y {Math.Max(_rows, 5)}{cd} " +
                    $"'sh \"$HOME/.cache/baudhoundterm/{name}.sh\"'\n");
                Run($"\"$SHELL\" -l -i -c 'sh {dir}/{name}.start.sh\"' </dev/null");
                return (string?)null;
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException)
        {
            return ex.Message;
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
