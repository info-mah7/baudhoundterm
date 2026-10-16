using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Quarterdeck.Core.BaudHound;
using Quarterdeck.Core.Model;

namespace Quarterdeck.App.Views;

/// <summary>
/// Finds BaudHounds on this PC's networks and opens one: a telnet tab in line mode, with the
/// device's ~ commands on the right-click menu. An address can still be typed for a device
/// that does not answer discovery.
/// </summary>
internal sealed class BaudHoundWindow : Window
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B));

    private readonly Action<SessionDefinition> _connect;
    private readonly Action<SessionDefinition> _save;
    private readonly ListBox _list = new() { Height = 190, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _address = new() { Text = "192.168.4.1", Padding = new Thickness(4) };
    private readonly Button _search;
    private readonly Button _open;
    private readonly Button _keep;
    private readonly Button _web;

    public BaudHoundWindow(Action<SessionDefinition> connect, Action<SessionDefinition> save)
    {
        _connect = connect;
        _save = save;
        Title = "BaudHound";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = "BaudHounds on the networks this PC is on. To reach one that has not joined your Wi-Fi, join its own " +
                   "network (BaudHound-XXXX) first. Firmware 1.4 and later answer discovery; older firmware is found only " +
                   "on its own network.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted
        });

        System.Windows.Automation.AutomationProperties.SetName(_list, "BaudHounds found");
        System.Windows.Automation.AutomationProperties.SetName(_address, "Address to connect to");
        _list.SelectionChanged += (_, _) => Refresh();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (Selected is not null && e.ChangedButton == MouseButton.Left)
            {
                Open(Selected);
            }
        };
        root.Children.Add(_list);
        root.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _search = Btn("Search again", async () => await SearchAsync(), "Ask again; takes about two seconds");
        _open = Btn("Connect to device", () => Open(Selected!), "Open the device's serial console in a new tab");
        _open.Style = (Style)Application.Current.FindResource("QdPrimary");
        _keep = Btn("Save shortcut", () => Keep(Selected!), "Add it to the BaudHound folder, or update the address of the one already there");
        _web = Btn("Web page", () => Browse(Selected!), "Open the device's own console and settings page in the browser");
        actions.Children.Add(_search);
        actions.Children.Add(_open);
        actions.Children.Add(_keep);
        actions.Children.Add(_web);
        root.Children.Add(actions);

        root.Children.Add(new TextBlock { Text = "Or connect to an address (port 23 unless given; :992 for TLS):", Margin = new Thickness(0, 14, 0, 2) });
        var manual = new DockPanel();
        var go = Btn("Connect to address", ConnectTyped);
        go.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(go, Dock.Right);
        manual.Children.Add(go);
        manual.Children.Add(_address);
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ConnectTyped();
            }
        };
        root.Children.Add(manual);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(Btn("Close", Close));
        root.Children.Add(bottom);
        Content = root;

        Refresh();
        Loaded += async (_, _) => await SearchAsync();
    }

    private BaudHoundDevice? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as BaudHoundDevice;

    private async Task SearchAsync()
    {
        _search.IsEnabled = false;
        Status("Searching...", Muted);
        Placeholder("Searching...");

        IReadOnlyList<BaudHoundDevice> devices;
        try
        {
            devices = await BaudHoundDiscovery.FindAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Status("Could not search: " + ex.Message, Bad);
            _search.IsEnabled = true;
            return;
        }

        var previous = Selected?.Name;
        _list.Items.Clear();
        if (devices.Count == 0)
        {
            Placeholder("No BaudHounds found yet. The note below says what to check.");
        }

        foreach (var device in devices)
        {
            var item = new ListBoxItem { Content = Row(device), Tag = device, Padding = new Thickness(4) };
            _list.Items.Add(item);
            if (device.Name == previous || devices.Count == 1)
            {
                item.IsSelected = true;
            }
        }

        Status(devices.Count switch
        {
            0 => "None found. Is it powered, and is this PC on its network or the network it joined? " +
                 "Windows can block the replies on a network marked Public.",
            1 => "Found one.",
            _ => $"Found {devices.Count}."
        }, devices.Count == 0 ? Bad : Good);

        _search.IsEnabled = true;
        Refresh();
    }

    /// <summary>A line inside the empty list, so it never reads as a blank box.</summary>
    private void Placeholder(string text)
    {
        _list.Items.Clear();
        _list.Items.Add(new ListBoxItem
        {
            Content = new TextBlock { Text = text, Foreground = Muted, Margin = new Thickness(6, 8, 6, 8) },
            IsEnabled = false,
            Focusable = false
        });
    }

    private static StackPanel Row(BaudHoundDevice d)
    {
        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, Foreground = Accent });
        title.Children.Add(new TextBlock { Text = "   " + d.Address + (d.TlsPort > 0 ? $"  TLS :{d.TlsPort}" : $"  telnet :{d.TelnetPort}") });
        if (d.Locked)
        {
            title.Children.Add(new TextBlock { Text = "   in use: a TLS session is open", Foreground = Bad });
        }

        panel.Children.Add(title);

        var speed = d.Baud == 0
            ? (d.Rate > 0 ? $"baud auto, now {d.Rate}" : "baud auto")
            : $"baud {d.Baud}";
        var network = d.Ssid.Length > 0
            ? $"joined {d.Ssid}" + (d.LanAddress.Length > 0 && d.LanAddress != d.Address ? $" as {d.LanAddress}" : "")
            : "own network only";
        var firmware = d.Firmware.Length > 0 ? "firmware " + d.Firmware : "firmware before 1.4";

        panel.Children.Add(new TextBlock
        {
            Text = $"{firmware} · {speed} · {network}",
            Foreground = Muted,
            FontSize = 11
        });

        if (d.TlsFingerprint.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = "certificate " + d.TlsFingerprint, Foreground = Muted, FontSize = 11 });
        }

        return panel;
    }

    private void Refresh()
    {
        var any = Selected is not null;
        _open.IsEnabled = any;
        _keep.IsEnabled = any;
        _web.IsEnabled = any;
    }

    /// <summary>TLS whenever the device has it: plain telnet shows the line to anyone on the network.</summary>
    private static Quarterdeck.Core.Model.SessionDefinition SessionFor(BaudHoundDevice d) =>
        d.TlsPort > 0
            ? BaudHoundSessions.Create(d.Name, d.Address, d.TlsPort, tls: true)
            : BaudHoundSessions.Create(d.Name, d.Address, d.TelnetPort);

    private void Open(BaudHoundDevice device)
    {
        if (device.Locked)
        {
            Status($"{device.Name} has a TLS session open and accepts no other connection until it ends.", Bad);
            return;
        }

        _connect(SessionFor(device));
        Close();
    }

    private void Keep(BaudHoundDevice device)
    {
        _save(SessionFor(device));
        Status($"Saved {device.Name} in the BaudHound folder.", Good);
    }

    private void Browse(BaudHoundDevice device)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://{device.Address}/") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status("Could not open the browser: " + ex.Message, Bad);
        }
    }

    private void ConnectTyped()
    {
        var address = _address.Text.Trim();
        if (address.Length == 0)
        {
            return;
        }

        var port = 23;
        var mark = address.LastIndexOf(':');
        if (mark > 0 && int.TryParse(address[(mark + 1)..], out var given))
        {
            port = given;
            address = address[..mark];
        }

        // 992 is telnets: BaudHound's TLS console.
        _connect(BaudHoundSessions.Create($"BaudHound {address}", address, port, tls: port == 992));
        Close();
    }

    private void Status(string text, Brush brush)
    {
        _status.Text = text;
        _status.Foreground = brush;
    }

    private static Button Btn(string text, Action act, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = tip };
        b.Click += (_, _) => act();
        return b;
    }
}
