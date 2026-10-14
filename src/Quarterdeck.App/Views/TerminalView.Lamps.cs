using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// Lamps for serial and telnet links, the way a modem has them: one for the connection, one for
/// each direction, plus byte counts. A serial port says nothing when the far end is silent or
/// dead, so these are how you tell "no output" from "no link".
/// </summary>
public partial class TerminalView
{
    private static readonly Color Off = Color.FromRgb(0x3A, 0x41, 0x52);
    private static readonly Color Live = Color.FromRgb(0x98, 0xC3, 0x79);
    private static readonly Color Waiting = Color.FromRgb(0xE5, 0xC0, 0x7B);
    private static readonly Color Dead = Color.FromRgb(0xE0, 0x6C, 0x75);
    private static readonly Color TxLit = Color.FromRgb(0xE5, 0xC0, 0x7B);
    private static readonly Color RxLit = Color.FromRgb(0x61, 0xAF, 0xEF);

    /// <summary>How long a lamp stays lit after a byte, so a single keystroke is visible.</summary>
    private const int HoldMs = 120;

    private Ellipse? _linkLamp;
    private Ellipse? _txLamp;
    private Ellipse? _rxLamp;
    private TextBlock? _counts;
    private DispatcherTimer? _lampTimer;
    private LinkCounters? _lampSource;
    private long _lastSent;
    private long _lastReceived;
    private long _txUntil;
    private long _rxUntil;

    /// <summary>Serial, and telnet, which is how BaudHound carries a serial line.</summary>
    public bool HasLinkLamps => Definition.Protocol is SessionProtocol.Serial or SessionProtocol.Telnet;

    /// <summary>
    /// The tab's own dot: connection state, flickering with traffic. Owned here and re-parented
    /// by the tab header, which is rebuilt whenever the tab is renamed or recoloured.
    /// </summary>
    public Ellipse? TabLamp { get; private set; }

    private void BuildLamps()
    {
        if (!HasLinkLamps)
        {
            return;
        }

        _linkLamp = Lamp("Connection");
        _txLamp = Lamp("TX: sent to the device");
        _rxLamp = Lamp("RX: received from the device");
        _counts = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

        LinkLamps.Children.Add(_linkLamp);
        LinkLamps.Children.Add(Label("TX", 8));
        LinkLamps.Children.Add(_txLamp);
        LinkLamps.Children.Add(Label("RX", 6));
        LinkLamps.Children.Add(_rxLamp);
        LinkLamps.Children.Add(_counts);
        LinkLamps.Visibility = Visibility.Visible;

        TabLamp = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Off) };

        _lampTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _lampTimer.Tick += (_, _) => TickLamps();
        _lampTimer.Start();
        ShowLinkState();
    }

    private static Ellipse Lamp(string tip) => new()
    {
        Width = 9,
        Height = 9,
        Fill = new SolidColorBrush(Off),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip
    };

    private static TextBlock Label(string text, double left) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)),
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 3, 0)
    };

    private Color StateColour => _state switch
    {
        SessionState.Connected => Live,
        SessionState.Connecting or SessionState.Reconnecting => Waiting,
        SessionState.Failed => Dead,
        _ => Off
    };

    private void ShowLinkState()
    {
        if (_linkLamp is null)
        {
            return;
        }

        var colour = StateColour;
        _linkLamp.Fill = new SolidColorBrush(colour);
        _linkLamp.ToolTip = _state switch
        {
            SessionState.Connected => "Connected",
            SessionState.Connecting => "Connecting",
            SessionState.Reconnecting => "Reconnecting",
            SessionState.Failed => "Failed",
            _ => "Disconnected"
        };

        if (TabLamp is not null)
        {
            TabLamp.Fill = new SolidColorBrush(colour);
            TabLamp.ToolTip = _linkLamp.ToolTip;
        }
    }

    private void TickLamps()
    {
        var counters = _controller?.Counters;
        if (!ReferenceEquals(counters, _lampSource))
        {
            // A new controller (Reconnect) counts from zero.
            _lampSource = counters;
            _lastSent = 0;
            _lastReceived = 0;
        }

        var now = Environment.TickCount64;
        var sent = counters?.Sent ?? 0;
        var received = counters?.Received ?? 0;

        if (sent != _lastSent)
        {
            _txUntil = now + HoldMs;
        }

        if (received != _lastReceived)
        {
            _rxUntil = now + HoldMs;
        }

        var countsChanged = sent != _lastSent || received != _lastReceived;
        _lastSent = sent;
        _lastReceived = received;

        var tx = now < _txUntil;
        var rx = now < _rxUntil;
        SetFill(_txLamp!, tx ? TxLit : Off);
        SetFill(_rxLamp!, rx ? RxLit : Off);

        // The tab dot blinks dark on traffic, so a busy tab shows from the tab strip.
        if (TabLamp is not null)
        {
            SetFill(TabLamp, (tx || rx) && _state == SessionState.Connected && (now / 120) % 2 == 0 ? Off : StateColour);
        }

        if (countsChanged || _counts!.Text.Length == 0)
        {
            _counts!.Text = $"{Size(sent)} out · {Size(received)} in";
            _counts.ToolTip = $"{sent.ToString("N0", CultureInfo.CurrentCulture)} bytes sent, {received.ToString("N0", CultureInfo.CurrentCulture)} received on this connection";
        }
    }

    /// <summary>Only swaps the brush when the colour changes: no allocation per tick on an idle tab.</summary>
    private static void SetFill(Shape shape, Color colour)
    {
        if (shape.Fill is not SolidColorBrush brush || brush.Color != colour)
        {
            shape.Fill = new SolidColorBrush(colour);
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB"
    };
}
