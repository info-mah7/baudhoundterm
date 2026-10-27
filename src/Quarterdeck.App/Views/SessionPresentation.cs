using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// How a session reads in the list and on its tab: a protocol badge, a name, and where it goes.
/// The badge is what makes "atlas" over SSH and "atlas" over VNC tell themselves apart.
/// </summary>
internal static class SessionPresentation
{
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0x8A, 0xA3)));

    public static (string Text, Color Color) Badge(SessionDefinition s) => s.Protocol switch
    {
        SessionProtocol.Ssh => ("SSH", Color.FromRgb(0x61, 0xAF, 0xEF)),
        SessionProtocol.Vnc => ("VNC", Color.FromRgb(0xC6, 0x78, 0xDD)),
        SessionProtocol.Rtsp => ("CAM", Color.FromRgb(0xE0, 0x6C, 0x75)),
        SessionProtocol.Rdp => ("RDP", Color.FromRgb(0x56, 0xB6, 0xC2)),
        SessionProtocol.Telnet => ("TEL", Color.FromRgb(0xE5, 0xC0, 0x7B)),
        SessionProtocol.Serial => ("COM", Color.FromRgb(0x98, 0xC3, 0x79)),
        SessionProtocol.Kvm => ("KVM", Color.FromRgb(0xB4, 0x8E, 0xAD)),
        SessionProtocol.Web => ("WEB", Color.FromRgb(0x8F, 0xBC, 0xBB)),
        _ => (ShellBadge(s.ShellCommand), Color.FromRgb(0x56, 0xB6, 0xC2))
    };

    private static string ShellBadge(string command)
    {
        var c = command.ToLowerInvariant();
        if (c.Contains("pwsh") || c.Contains("powershell"))
        {
            return "PS";
        }

        return c.Contains("cmd") ? "CMD" : "SH";
    }

    /// <summary>
    /// Where the session connects. Inside a host group the host is already on the row above,
    /// so only what differs (user, port) is shown.
    /// </summary>
    public static string Target(SessionDefinition s, bool hostShown = false)
    {
        var host = hostShown ? "" : s.Host;

        return s.Protocol switch
        {
            SessionProtocol.Ssh =>
                (string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@") + host + PortSuffix(s.Port, 22, hostShown)
                + (s.Tmux ? "  · tmux " + string.Join(", ", s.TmuxSessionList().Select(t => SessionController.TmuxName(t.Name))) : ""),
            SessionProtocol.Telnet => host + PortSuffix(s.Port, 23, hostShown),
            SessionProtocol.Vnc => host + PortSuffix(s.Port, 5900, hostShown) + (s.ViewOnly ? "  · view only" : ""),
            SessionProtocol.Rtsp => host + PortSuffix(s.Port, 554, hostShown) + (s.RtspPath.Length > 0 ? s.RtspPath : "  · ONVIF"),
            SessionProtocol.Serial => $"{s.SerialPort} · {s.BaudRate} {Framing(s)}",
            SessionProtocol.Sql => "SQL tabs were removed in build 70",
            SessionProtocol.Kvm => host + PortSuffix(s.KvmPort, 9200, hostShown),
            SessionProtocol.Web => WebTarget(s, hostShown),
            SessionProtocol.Rdp =>
                (string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@") + host + PortSuffix(s.Port, 3389, hostShown)
                + (s.RdpGateway.Length > 0 ? "  · via " + s.RdpGateway : ""),
            _ => s.ShellCommand
        };
    }

    /// <summary>The page's address: in a host group just its path (the group shows the host).</summary>
    private static string WebTarget(SessionDefinition s, bool hostShown)
    {
        if (BrowserView.Home(s.WebUrl) is not { } u)
        {
            return s.WebUrl;
        }

        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        return hostShown ? $"{u.Scheme}{port} {u.AbsolutePath}".TrimEnd('/') : $"{u.Scheme}://{u.Host}{port}{u.AbsolutePath}".TrimEnd('/');
    }

    // Inside a group the port is the only thing telling two rows apart, so it always shows;
    // on a lone row a default port is noise.
    private static string PortSuffix(int port, int standard, bool always) =>
        always || port != standard ? $":{port}" : "";

    private static string Framing(SessionDefinition s)
    {
        var parity = string.IsNullOrEmpty(s.Parity) ? "N" : s.Parity[..1].ToUpperInvariant();
        var stop = s.StopBits switch { "Two" => "2", "OnePointFive" => "1.5", _ => "1" };
        return $"{s.DataBits}{parity}{stop}";
    }

    /// <summary>
    /// The key sessions are grouped on. Only network protocols share a host meaningfully; a COM
    /// port or a local shell never groups.
    /// </summary>
    public static string? HostKey(SessionDefinition s) =>
        (s.Protocol is SessionProtocol.Ssh or SessionProtocol.Telnet or SessionProtocol.Vnc or SessionProtocol.Rtsp or SessionProtocol.Rdp or SessionProtocol.Kvm or SessionProtocol.Web)
        && !string.IsNullOrWhiteSpace(s.Host)
            ? s.Host.Trim().ToLowerInvariant()
            : null;

    /// <summary>
    /// The port a session actually connects to. A KVM keeps its daemon port in KvmPort and
    /// leaves Port unused, so comparing or sorting KVM sessions by Port compares nothing.
    /// </summary>
    public static int EffectivePort(SessionDefinition s) => s.Protocol == SessionProtocol.Kvm ? s.KvmPort : s.Port;

    /// <summary>Words in the filter box that mean a protocol rather than a name.</summary>
    public static SessionProtocol? ProtocolWord(string word) => word.ToLowerInvariant() switch
    {
        "ssh" => SessionProtocol.Ssh,
        "kvm" => SessionProtocol.Kvm,
        "web" => SessionProtocol.Web,
        "vnc" => SessionProtocol.Vnc,
        "rdp" => SessionProtocol.Rdp,
        "rtsp" or "cam" or "camera" => SessionProtocol.Rtsp,
        "telnet" or "tel" => SessionProtocol.Telnet,
        "serial" or "com" => SessionProtocol.Serial,
        "local" or "shell" or "ps" or "cmd" => SessionProtocol.LocalShell,
        _ => null
    };

    public static Border BadgeElement(SessionDefinition s)
    {
        var (text, color) = Badge(s);

        return new Border
        {
            Width = 34,
            CornerRadius = new CornerRadius(3),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B))),
            BorderBrush = Freeze(new SolidColorBrush(color)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Freeze(new SolidColorBrush(color)),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
    }

    /// <summary>
    /// The tmux mark for a tab: "tmux" while the session is really there on the host, so closing
    /// the tab leaves the work running, and "no tmux" when it was asked for and is not, which is
    /// the case where closing the tab loses everything.
    /// </summary>
    public static Border? TmuxChip(TmuxState state)
    {
        if (state == TmuxState.Off)
        {
            return null;
        }

        var live = state == TmuxState.Live;
        var colour = live ? Color.FromRgb(0x98, 0xC3, 0x79) : Color.FromRgb(0xE0, 0x6C, 0x75);

        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, colour.R, colour.G, colour.B))),
            BorderBrush = Freeze(new SolidColorBrush(colour)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = live
                ? "The shell runs in tmux on the host: closing this tab leaves it running, and reconnecting attaches to it again."
                : "tmux was asked for but is not running on the host: this is a plain shell, and closing the tab ends it.",
            Child = new TextBlock
            {
                Text = live ? "tmux" : "no tmux",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Freeze(new SolidColorBrush(colour))
            }
        };
    }

    /// <summary>
    /// The "set up to use tmux" mark for a saved SSH shortcut, or null. Muted, because it says
    /// how the shortcut is configured, not that a session is running (that is the live tab's
    /// green chip); it answers "will this one survive a dropped link" at a glance in the list.
    /// </summary>
    public static Border? ConfiguredTmuxChip(SessionDefinition s)
    {
        if (!(s.Protocol == SessionProtocol.Ssh && s.Tmux))
        {
            return null;
        }

        var colour = Color.FromRgb(0x7F, 0x9A, 0x7B);   // a muted green: configured, not live
        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0x22, colour.R, colour.G, colour.B))),
            BorderBrush = Freeze(new SolidColorBrush(colour)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(6, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Runs in tmux ({string.Join(", ", s.TmuxSessionList().Select(t => Quarterdeck.Core.Terminal.SessionController.TmuxName(t.Name)))}): a dropped link leaves the shell running on the host, and reconnecting attaches to it.",
            Child = new TextBlock { Text = "tmux", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Freeze(new SolidColorBrush(colour)) }
        };
    }

    /// <summary>
    /// A list row: badge, then the name (if it says anything), then the target, dimmed. With
    /// details off the target is left out; with addresses hidden it shows as dots.
    /// </summary>
    public static StackPanel Row(SessionDefinition s, string? name, bool hostShown, bool details = true)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(BadgeElement(s));

        if (!string.IsNullOrWhiteSpace(name))
        {
            panel.Children.Add(new TextBlock { Text = Privacy.MaskName(name, s), VerticalAlignment = VerticalAlignment.Center });
        }

        // Before the address, which a narrow list cuts off first.
        if (AuditChip(s) is { } audit)
        {
            panel.Children.Add(audit);
        }

        // The tmux mark is a chip, so its text comes out of the dimmed target line here (Target
        // itself keeps it, for the filter and the agent's description of the session).
        var target = details ? Privacy.Mask(Target(s, hostShown), s) : "";
        var mark = target.IndexOf("  · tmux ", StringComparison.Ordinal);
        if (mark >= 0)
        {
            target = target[..mark];
        }

        if (target.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = target,
                Foreground = Dim,
                Margin = new Thickness(string.IsNullOrWhiteSpace(name) ? 0 : 8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (ConfiguredTmuxChip(s) is { } chip)
        {
            panel.Children.Add(chip);
        }

        return panel;
    }

    /// <summary>
    /// A tab whose tmux session has a start command: the program's name. Amber with a warning sign
    /// when the command lets an agent act without asking (Claude Code, Codex, Gemini CLI, Aider),
    /// so a tab like that is never mistaken for one that asks.
    /// </summary>
    public static Border? ProgramChip(SessionDefinition s, string tmuxSession)
    {
        if (s is not { Protocol: SessionProtocol.Ssh, Tmux: true })
        {
            return null;
        }

        var name = Quarterdeck.Core.Terminal.SessionController.TmuxName(string.IsNullOrWhiteSpace(tmuxSession) ? s.TmuxSession : tmuxSession);
        var command = s.TmuxSessionList().FirstOrDefault(t => Quarterdeck.Core.Terminal.SessionController.TmuxName(t.Name) == name)?.StartCommand ?? "";
        if (command.Length == 0)
        {
            return null;
        }

        var skip = Quarterdeck.Core.Terminal.TmuxStart.SkipsPrompts(command);
        var colour = skip ? Color.FromRgb(0xE5, 0xC0, 0x7B) : Color.FromRgb(0xC6, 0x9C, 0x6D);
        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0x22, colour.R, colour.G, colour.B))),
            BorderBrush = Freeze(new SolidColorBrush(colour)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Started with: " + command + (skip ? "\nIt acts without asking first: nothing it does waits for a yes." : ""),
            Child = new TextBlock
            {
                Text = Quarterdeck.Core.Terminal.TmuxStart.Program(command) + (skip ? " ⚠" : ""),
                FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Freeze(new SolidColorBrush(colour))
            }
        };
    }

    /// <summary>An audited shortcut says so before it is opened: every session of it is recorded.</summary>
    public static Border? AuditChip(SessionDefinition s)
    {
        if (!s.Audit.Enabled)
        {
            return null;
        }

        var colour = Color.FromRgb(0xE0, 0x6C, 0x75);
        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0x22, colour.R, colour.G, colour.B))),
            BorderBrush = Freeze(new SolidColorBrush(colour)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(6, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Audited: every session of this shortcut is recorded from connect until its tab closes.",
            Child = new TextBlock { Text = "● audit", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Freeze(new SolidColorBrush(colour)) }
        };
    }

    /// <summary>
    /// The row above a host's sessions: the machine's name first, then its address in brackets,
    /// smaller and dimmed, when the name is not the address already. The address is left out
    /// with details off, and shown as dots when hidden.
    /// </summary>
    public static StackPanel HostRow(string title, string host, bool hide, bool details = true)
    {
        // The address is shared by the group, so one member marked "always hide" hides it for all.
        var first = new SessionDefinition { Host = host, HideAddress = hide };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var isAddress = string.Equals(title, host, StringComparison.OrdinalIgnoreCase);
        var shownTitle = isAddress ? Privacy.Mask(title, first) : Privacy.MaskName(title, first);
        panel.Children.Add(new TextBlock { Text = "🖥  " + shownTitle, VerticalAlignment = VerticalAlignment.Center });

        if (details && !isAddress)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "(" + Privacy.Mask(host, first) + ")",
                Foreground = Dim,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        return panel;
    }

    /// <summary>
    /// A small robot head, drawn: WPF cannot render colour emoji and Windows substitutes a
    /// monochrome glyph for 🤖 that reads as anything but a bot.
    /// </summary>
    public static FrameworkElement BotIcon(double size, Brush brush)
    {
        var canvas = new Canvas { Width = 14, Height = 14 };

        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            // Antenna, rounded head, mouth; ears as two short strokes.
            Data = Geometry.Parse("M7,1.6 L7,3.2 M2.8,3.2 H11.2 A1.6,1.6 0 0 1 12.8,4.8 V10.4 A1.6,1.6 0 0 1 11.2,12 " +
                                  "H2.8 A1.6,1.6 0 0 1 1.2,10.4 V4.8 A1.6,1.6 0 0 1 2.8,3.2 Z M5,9.6 H9 M0.3,6.4 V8.6 M13.7,6.4 V8.6"),
            Stroke = brush,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        void Dot(double x, double y, double r)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = brush };
            Canvas.SetLeft(dot, x - r);
            Canvas.SetTop(dot, y - r);
            canvas.Children.Add(dot);
        }

        Dot(7, 1.1, 1.1);    // antenna tip
        Dot(4.8, 6.6, 1.25); // eyes
        Dot(9.2, 6.6, 1.25);

        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
