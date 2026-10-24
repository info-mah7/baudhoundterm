using System.Text.Json.Serialization;

namespace Quarterdeck.Core.Model;

public enum SessionProtocol
{
    Ssh,
    Serial,
    LocalShell,

    /// <summary>Telnet or a raw TCP console: BaudHound, console servers, network gear.</summary>
    Telnet,

    /// <summary>A VNC desktop, drawn by noVNC through a loopback bridge.</summary>
    Vnc,

    /// <summary>An IP camera's RTSP stream, played by LibVLC; ONVIF for PTZ and stream lookup.</summary>
    Rtsp,

    /// <summary>
    /// A Windows desktop over RDP, drawn by Windows' own Remote Desktop control (mstscax.dll).
    /// Last on purpose: shortcuts store the protocol as its number.
    /// </summary>
    Rdp,

    /// <summary>
    /// Was a SQL query tab; removed in build 70 with its database drivers. Kept so the numbers
    /// stored for later protocols (Kvm) do not shift; a stored SQL shortcut no longer opens.
    /// </summary>
    Sql,

    /// <summary>
    /// A machine driven through an IP-KVM running agentkvm: the picture comes from the board's
    /// video, the typing from a USB gadget the target takes for a keyboard. Nothing is installed
    /// on the target, so a BIOS screen or an installer can be worked like any other screen.
    /// </summary>
    Kvm
}

public enum SshAuthMethod
{
    Password,
    PublicKey,
    Agent,
    KeyboardInteractive
}

/// <summary>Where a password comes from at connect time.</summary>
public enum PasswordSource
{
    /// <summary>The DPAPI vault, or a prompt when nothing is saved.</summary>
    Saved,

    /// <summary>Looked up in KeePassXC each time; never stored here.</summary>
    KeePassXc
}

/// <summary>
/// One saved connection. Secrets never live here; the vault holds them keyed by <see cref="Id"/>.
/// </summary>
public sealed class SessionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Session";

    /// <summary>Folder path in the session tree, "/" separated. Empty means top level.</summary>
    public string Folder { get; set; } = "";

    public SessionProtocol Protocol { get; set; } = SessionProtocol.Ssh;

    /// <summary>
    /// Always show this session's address and user name as dots, as the global Hide addresses
    /// switch does for every session: for demos and screen recordings. Display only.
    /// </summary>
    public bool HideAddress { get; set; }

    // SSH
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
    public string KeyPath { get; set; } = "";

    /// <summary>Password and keyboard-interactive logins: the vault, or KeePassXC.</summary>
    public PasswordSource PasswordSource { get; set; } = PasswordSource.Saved;

    /// <summary>KeePassXC entry: blank matches ssh://Host, or an entry UUID, or a URL.</summary>
    public string KeePassEntry { get; set; } = "";

    /// <summary>Agent login: offer only keys whose comment contains this, or with this SHA256 fingerprint.</summary>
    public string AgentKeyFilter { get; set; } = "";

    /// <summary>Optional jump host, "user@host:port". Empty means direct.</summary>
    public string JumpHost { get; set; } = "";

    /// <summary>
    /// Run the shell inside tmux: a dropped connection leaves it running on the host, and the
    /// reconnect attaches to it again. Needs tmux on the host; without it the plain shell stays.
    /// </summary>
    public bool Tmux { get; set; }

    /// <summary>
    /// The tmux session to create or attach to. Since build 72 it mirrors the first entry of
    /// <see cref="TmuxSessions"/>, so builds before 72 (and other PCs on them, via Sync) still
    /// open the main session.
    /// </summary>
    public string TmuxSession { get; set; } = "bht";

    /// <summary>
    /// Named tmux sessions on this host, each its own tab (build 72). Empty on shortcuts made
    /// before 72: <see cref="TmuxSessionList"/> then stands in with <see cref="TmuxSession"/>.
    /// </summary>
    public List<TmuxSessionEntry> TmuxSessions { get; set; } = new();

    /// <summary>The sessions to offer: the list, or the single session of an older shortcut.</summary>
    public IReadOnlyList<TmuxSessionEntry> TmuxSessionList() =>
        TmuxSessions.Count > 0 ? TmuxSessions : new[] { new TmuxSessionEntry { Name = TmuxSession, OpenOnConnect = true } };

    // Serial
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";

    // Telnet
    /// <summary>
    /// Edit and echo each line here and send it whole with CR LF, for consoles that read lines
    /// and do not echo. BaudHound's telnet port is one.
    /// </summary>
    public bool LineMode { get; set; }

    /// <summary>Telnet inside TLS (telnets). The certificate is pinned on first use, like an SSH host key.</summary>
    public bool UseTls { get; set; }

    // Local shell
    public string ShellCommand { get; set; } = "powershell.exe";

    /// <summary>VNC only: watch without sending input.</summary>
    public bool ViewOnly { get; set; }

    // RDP: Host, Port (3389), Username and the password source are shared fields.
    /// <summary>Windows domain for the login. Blank for a local account or user@domain in Username.</summary>
    public string RdpDomain { get; set; } = "";

    /// <summary>KVM: the port agentkvmd listens on, and whether to reach it over TLS.</summary>
    public int KvmPort { get; set; } = 9200;
    public bool KvmTls { get; set; }

    /// <summary>
    /// KVM: where the picture comes from. The board's own web interface serves it, with a sign-in
    /// of its own, because agentkvmd only offers a screenshot where the firmware has an
    /// unauthenticated one. Blank uses the same host on port 80.
    /// </summary>
    public string KvmVideoUrl { get; set; } = "";
    public string KvmVideoUser { get; set; } = "admin";

    /// <summary>Remote Desktop Gateway host, for servers behind one. Blank connects directly.</summary>
    public string RdpGateway { get; set; } = "";

    /// <summary>Copy and paste between this PC and the remote desktop.</summary>
    public bool RdpClipboard { get; set; } = true;

    /// <summary>This PC's drives appear on the remote desktop. Off by default: the server can then read them.</summary>
    public bool RdpDrives { get; set; }

    /// <summary>Connect to the server's console/admin session (mstsc /admin).</summary>
    public bool RdpAdmin { get; set; }

    // RTSP camera: Host, Port (554) and Username are shared fields; the password is in the vault.
    /// <summary>Stream path after host:port, e.g. /Streaming/Channels/101. Blank asks the camera over ONVIF.</summary>
    public string RtspPath { get; set; } = "";

    /// <summary>RTP over the RTSP TCP connection: survives firewalls and NAT, costs a little latency.</summary>
    public bool RtspTcp { get; set; } = true;

    /// <summary>A 200 ms network buffer instead of 1 s: closer to live, less tolerant of a bad link.</summary>
    public bool LowLatency { get; set; } = true;

    /// <summary>The camera's ONVIF HTTP port, for PTZ and stream lookup.</summary>
    public int OnvifPort { get; set; } = 80;

    /// <summary>Tab stripe colour as CSS hex, e.g. "#e06c75". Empty means no colour.</summary>
    public string TabColor { get; set; } = "";

    /// <summary>Commands offered on the terminal's right-click menu.</summary>
    public List<CommandShortcut> Commands { get; set; } = CommandShortcut.Defaults();

    /// <summary>The default theme was called "Quarterdeck Dark" until build 70; saved shortcuts get the new name.</summary>
    public string Theme
    {
        get => _theme;
        set => _theme = value == "Quarterdeck Dark" ? DefaultTheme : value;
    }

    private string _theme = DefaultTheme;
    public const string DefaultTheme = "BaudHoundTerm Dark";
    public string FontFamily { get; set; } = "Cascadia Mono";
    public int FontSize { get; set; } = 13;
    public int ScrollbackLines { get; set; } = 10000;

    /// <summary>
    /// In a full-screen program (vim, less, tmux with mouse off) that does not read the mouse,
    /// the wheel is sent as Up/Down keys, as every terminal does. Off, the wheel does nothing
    /// there, so it cannot page through a shell's command history by accident.
    /// </summary>
    public bool WheelSendsArrows { get; set; } = true;

    public LoggingSettings Logging { get; set; } = new();
    public ReconnectSettings Reconnect { get; set; } = new();
    public List<AutoLoginStep> AutoLogin { get; set; } = new();
    public List<HighlightRule> Highlights { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    public DateTimeOffset? LastConnected { get; set; }

    [JsonIgnore]
    public string DisplayPath => string.IsNullOrEmpty(Folder) ? Name : $"{Folder}/{Name}";

    public SessionDefinition Clone(string? newName = null)
    {
        var copy = (SessionDefinition)MemberwiseClone();
        copy.Id = Guid.NewGuid();
        copy.Name = newName ?? $"{Name} (copy)";
        copy.LastConnected = null;
        copy.Logging = Logging.Copy();
        copy.Reconnect = Reconnect.Copy();
        copy.AutoLogin = AutoLogin.Select(s => s.Copy()).ToList();
        copy.Highlights = Highlights.Select(h => h.Copy()).ToList();
        copy.Commands = Commands.Select(c => c.Copy()).ToList();
        copy.TmuxSessions = TmuxSessions.Select(t => t.Copy()).ToList();
        copy.Tags = new List<string>(Tags);
        return copy;
    }
}

public sealed class LoggingSettings
{
    public bool Enabled { get; set; }

    /// <summary>Directory for transcripts. Empty means %APPDATA%\Quarterdeck\logs.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Supports %NAME%, %HOST%, %Y%, %M%, %D%, %H%, %m%, %S%.</summary>
    public string FileNamePattern { get; set; } = "%NAME%_%Y%-%M%-%D%_%H%%m%%S%.log";

    public bool PrefixTimestamps { get; set; }
    public bool StripAnsi { get; set; } = true;

    /// <summary>Roll to a new file past this size. Zero disables rolling.</summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    public LoggingSettings Copy() => (LoggingSettings)MemberwiseClone();
}

public sealed class ReconnectSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Zero means retry until the user cancels.</summary>
    public int MaxAttempts { get; set; } = 10;

    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;

    /// <summary>Exponential backoff multiplier applied to the delay after each failure.</summary>
    public double BackoffFactor { get; set; } = 1.8;

    public ReconnectSettings Copy() => (ReconnectSettings)MemberwiseClone();
}

/// <summary>One step of an expect-style login script: wait for <see cref="Expect"/>, then send <see cref="Send"/>.</summary>
public sealed class AutoLoginStep
{
    public string Expect { get; set; } = "";
    public string Send { get; set; } = "";

    /// <summary>When true, <see cref="Send"/> is ignored and the secret named by <see cref="SecretKey"/> is sent.</summary>
    public bool IsSecret { get; set; }

    /// <summary>Vault key for the secret, used when <see cref="IsSecret"/> is set. "password" is the session password.</summary>
    public string SecretKey { get; set; } = "password";

    public bool AppendNewline { get; set; } = true;
    public int TimeoutMs { get; set; } = 15000;

    /// <summary>Treat <see cref="Expect"/> as a regular expression rather than a literal substring.</summary>
    public bool IsRegex { get; set; }

    public AutoLoginStep Copy() => (AutoLoginStep)MemberwiseClone();
}

/// <summary>
/// One entry on the terminal's right-click menu: a label and the text it sends.
/// </summary>
/// <summary>One named tmux session of an SSH shortcut.</summary>
public sealed class TmuxSessionEntry
{
    public string Name { get; set; } = "bht";

    /// <summary>Opened with the shortcut; when unticked, only on demand from the shortcut's menu.</summary>
    public bool OpenOnConnect { get; set; } = true;

    public TmuxSessionEntry Copy() => (TmuxSessionEntry)MemberwiseClone();
}

public sealed class CommandShortcut
{
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Press Enter after sending. Clear it to leave the line for the user to finish.</summary>
    public bool AppendNewline { get; set; } = true;

    /// <summary>Ask before sending. For anything that reboots or deletes.</summary>
    public bool Confirm { get; set; }

    public CommandShortcut Copy() => (CommandShortcut)MemberwiseClone();

    /// <summary>
    /// What a new session starts with. Destructive entries are marked Confirm so a stray
    /// click on a menu cannot reboot a machine.
    /// </summary>
    /// <summary>
    /// BaudHound's own commands. The device acts on lines starting with ~ and passes every
    /// other line to the target, so these never reach the device under test.
    /// </summary>
    public static List<CommandShortcut> BaudHound() => new()
    {
        new CommandShortcut { Label = "Device info (~info)", Text = "~info" },
        new CommandShortcut { Label = "Detect baud now (~baud)", Text = "~baud" },
        new CommandShortcut { Label = "Auto-detect baud (~baud auto)", Text = "~baud auto" },
        new CommandShortcut { Label = "Pin 115200 (~baud 115200)", Text = "~baud 115200" },
        new CommandShortcut { Label = "Pin baud...", Text = "~baud ", AppendNewline = false },
        new CommandShortcut { Label = "Wake probe on (~probe on)", Text = "~probe on" },
        new CommandShortcut { Label = "Wake probe off (~probe off)", Text = "~probe off" },
        new CommandShortcut { Label = "Swap RX/TX (~swap)", Text = "~swap" },
        new CommandShortcut { Label = "LED test (~ledtest)", Text = "~ledtest" }
    };

    public static List<CommandShortcut> Defaults() => new()
    {
        new CommandShortcut { Label = "btop", Text = "btop" },
        new CommandShortcut { Label = "nvitop", Text = "nvitop" },
        new CommandShortcut { Label = "Disk usage", Text = "df -h" },
        new CommandShortcut { Label = "Follow syslog", Text = "sudo journalctl -f" },
        new CommandShortcut { Label = "Reboot", Text = "sudo reboot", Confirm = true }
    };
}

public sealed class HighlightRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; } = true;
    public bool CaseSensitive { get; set; }

    /// <summary>CSS colour applied to matching text in the terminal view.</summary>
    public string Color { get; set; } = "#ff6b6b";

    public bool Bold { get; set; }
    public bool Enabled { get; set; } = true;

    public HighlightRule Copy() => (HighlightRule)MemberwiseClone();
}
