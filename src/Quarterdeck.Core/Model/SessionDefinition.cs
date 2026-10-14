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
    Rtsp
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

    // Serial
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";

    // Local shell
    public string ShellCommand { get; set; } = "powershell.exe";

    /// <summary>VNC only: watch without sending input.</summary>
    public bool ViewOnly { get; set; }

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

    public string Theme { get; set; } = "Quarterdeck Dark";
    public string FontFamily { get; set; } = "Cascadia Mono";
    public int FontSize { get; set; } = 13;
    public int ScrollbackLines { get; set; } = 10000;

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
    public static List<CommandShortcut> Defaults() => new()
    {
        new CommandShortcut { Label = "btop", Text = "btop" },
        new CommandShortcut { Label = "nvitop", Text = "nvitop" },
        new CommandShortcut { Label = "Claude (bypass permissions)", Text = "claude --permission-mode bypassPermissions" },
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
