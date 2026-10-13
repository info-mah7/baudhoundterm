using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Model;

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
        SessionProtocol.Telnet => ("TEL", Color.FromRgb(0xE5, 0xC0, 0x7B)),
        SessionProtocol.Serial => ("COM", Color.FromRgb(0x98, 0xC3, 0x79)),
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
                (string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@") + host + PortSuffix(s.Port, 22, hostShown),
            SessionProtocol.Telnet => host + PortSuffix(s.Port, 23, hostShown),
            SessionProtocol.Vnc => host + PortSuffix(s.Port, 5900, hostShown) + (s.ViewOnly ? "  · view only" : ""),
            SessionProtocol.Rtsp => host + PortSuffix(s.Port, 554, hostShown) + (s.RtspPath.Length > 0 ? s.RtspPath : "  · ONVIF"),
            SessionProtocol.Serial => $"{s.SerialPort} · {s.BaudRate} {Framing(s)}",
            _ => s.ShellCommand
        };
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
        s.Protocol is SessionProtocol.Ssh or SessionProtocol.Telnet or SessionProtocol.Vnc or SessionProtocol.Rtsp
        && !string.IsNullOrWhiteSpace(s.Host)
            ? s.Host.Trim().ToLowerInvariant()
            : null;

    /// <summary>Words in the filter box that mean a protocol rather than a name.</summary>
    public static SessionProtocol? ProtocolWord(string word) => word.ToLowerInvariant() switch
    {
        "ssh" => SessionProtocol.Ssh,
        "vnc" => SessionProtocol.Vnc,
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

    /// <summary>A list row: badge, then the name (if it says anything), then the target, dimmed.</summary>
    public static StackPanel Row(SessionDefinition s, string? name, bool hostShown)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(BadgeElement(s));

        if (!string.IsNullOrWhiteSpace(name))
        {
            panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        }

        var target = Target(s, hostShown);
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

        return panel;
    }

    /// <summary>The row above a host's sessions: its title, and the host itself if the title is a name.</summary>
    public static StackPanel HostRow(string title, string host)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = "🖥  " + title, VerticalAlignment = VerticalAlignment.Center });

        if (!string.Equals(title, host, StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(new TextBlock
            {
                Text = host,
                Foreground = Dim,
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
