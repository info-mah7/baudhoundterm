using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Transfer;

namespace Quarterdeck.App.Views;

/// <summary>
/// A TFTP server for firmware updates: pick a folder and an interface, start, and point the
/// device at it. The server runs while this window is open; closing it stops the server, so a
/// TFTP port is never left open by accident.
/// </summary>
internal sealed class TftpWindow : Window
{
    private readonly UiPreferences _ui;
    private readonly TextBox _folder = new() { Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _address = new();
    private readonly TextBox _port = new() { Width = 70 };
    private readonly CheckBox _uploads = new()
    {
        Content = "Allow uploads into the folder (never overwrites)",
        ToolTip = "Lets a device send a file to you, e.g. a config backup with \"copy running-config tftp:\"."
    };
    private readonly Button _start = new() { Content = "Start", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
    private readonly TextBlock _how = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5 };
    private readonly ListBox _files = new() { Height = 120 };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11
    };

    private TftpServer? _server;

    public bool IsRunning => _server is not null;

    public TftpWindow(UiPreferences ui)
    {
        _ui = ui;
        Title = "TFTP server";
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var dark = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C));
        var light = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
        foreach (var box in new Control[] { _files, _log })
        {
            box.Background = dark;
            box.Foreground = light;
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x59));
        }

        var root = new DockPanel { Margin = new Thickness(12) };
        var form = new StackPanel();

        form.Children.Add(Label("Folder to serve"));
        var folderRow = new DockPanel();
        var browse = new Button { Content = "Browse...", Padding = new Thickness(10, 3, 10, 3) };
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        form.Children.Add(folderRow);

        form.Children.Add(Label("Listen on"));
        var bindRow = new DockPanel();
        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        portRow.Children.Add(new TextBlock { Text = "Port ", VerticalAlignment = VerticalAlignment.Center });
        portRow.Children.Add(_port);
        DockPanel.SetDock(portRow, Dock.Right);
        bindRow.Children.Add(portRow);
        bindRow.Children.Add(_address);
        form.Children.Add(bindRow);

        _uploads.Margin = new Thickness(0, 8, 0, 0);
        form.Children.Add(_uploads);

        var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        startRow.Children.Add(_start);
        startRow.Children.Add(new TextBlock
        {
            Text = "Runs while this window is open. Windows may ask once to allow it through the firewall.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        form.Children.Add(startRow);
        form.Children.Add(_how);

        form.Children.Add(Label("Files in the folder (double-click to copy a device command)"));
        form.Children.Add(_files);
        form.Children.Add(Label("Log"));
        DockPanel.SetDock(form, Dock.Top);
        root.Children.Add(form);
        root.Children.Add(_log);
        Content = root;

        _folder.Text = string.IsNullOrEmpty(_ui.TftpFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tftp")
            : _ui.TftpFolder;
        _port.Text = _ui.TftpPort.ToString();
        _uploads.IsChecked = _ui.TftpUploads;
        FillAddresses();

        _start.Click += async (_, _) => await ToggleAsync();
        _folder.LostFocus += (_, _) => RefreshFiles();
        _address.SelectionChanged += (_, _) => ShowHowTo();
        _files.MouseDoubleClick += (_, _) => CopyCommand();
        Closing += async (_, _) => await StopAsync();

        RefreshFiles();
        ShowHowTo();
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 3) };

    private sealed record Choice(IPAddress Address, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Every up, non-loopback IPv4 address, plus "all", labelled with its adapter.</summary>
    private void FillAddresses()
    {
        _address.Items.Add(new Choice(IPAddress.Any, "All interfaces (0.0.0.0)"));

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                 n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses
                         .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                _address.Items.Add(new Choice(ua.Address, $"{ua.Address}  ({nic.Name})"));
            }
        }

        _address.SelectedItem = _address.Items.OfType<Choice>().FirstOrDefault(c => c.Address.ToString() == _ui.TftpAddress)
                                ?? _address.Items.OfType<Choice>().Skip(1).FirstOrDefault()
                                ?? _address.Items[0];
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to serve over TFTP", InitialDirectory = _folder.Text };
        if (dialog.ShowDialog(this) == true)
        {
            _folder.Text = dialog.FolderName;
            RefreshFiles();
        }
    }

    private void RefreshFiles()
    {
        _files.Items.Clear();
        try
        {
            if (!Directory.Exists(_folder.Text))
            {
                _files.Items.Add("(folder does not exist yet: Start creates it)");
                return;
            }

            foreach (var f in new DirectoryInfo(_folder.Text).EnumerateFiles().OrderBy(f => f.Name))
            {
                _files.Items.Add(new ListBoxItem { Content = $"{f.Name}    {f.Length:N0} bytes", Tag = f.Name });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _files.Items.Add($"(cannot list: {ex.Message})");
        }
    }

    private string ServerAddress =>
        _address.SelectedItem is Choice c && !c.Address.Equals(IPAddress.Any)
            ? c.Address.ToString()
            : _address.Items.OfType<Choice>().Skip(1).FirstOrDefault()?.Address.ToString() ?? "<this-pc>";

    /// <summary>What to type on the device, for the usual suspects.</summary>
    private void ShowHowTo(string file = "<file>")
    {
        var ip = ServerAddress;
        var port = int.TryParse(_port.Text, out var p) ? p : 69;
        var portNote = port == 69 ? "" : $"   (non-standard port {port}: most devices only use 69)\n";
        _how.Text =
            portNote +
            $"U-Boot:     setenv serverip {ip}; tftpboot ${{loadaddr}} {file}\n" +
            $"Cisco IOS:  copy tftp://{ip}/{file} flash:\n" +
            $"Linux:      tftp -g -r {file} {ip}     (busybox)\n" +
            $"Windows:    tftp -i {ip} GET {file}";
    }

    private void CopyCommand()
    {
        if (_files.SelectedItem is ListBoxItem { Tag: string name })
        {
            ShowHowTo(name);
            try
            {
                Clipboard.SetText($"tftp://{ServerAddress}/{name}");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program holds the clipboard (a remote desktop's sync, a manager): not worth a crash dialog.
            }
            AppendLog($"Copied tftp://{ServerAddress}/{name}");
        }
    }

    private async Task ToggleAsync()
    {
        if (_server is not null)
        {
            await StopAsync();
            return;
        }

        if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
        {
            AppendLog("Port must be 1-65535.");
            return;
        }

        var choice = (Choice)_address.SelectedItem;
        try
        {
            Directory.CreateDirectory(_folder.Text);
            _server = new TftpServer(_folder.Text, new IPEndPoint(choice.Address, port), _uploads.IsChecked == true);
            _server.Log += line => Dispatcher.BeginInvoke(() =>
            {
                AppendLog(line);
                if (line.Contains(": done"))
                {
                    RefreshFiles();   // an upload adds a file
                }
            });
            _server.Start();
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            AppendLog(ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? $"Port {port} is already in use (another TFTP server?)."
                : $"Could not start: {ex.Message}");
            _server = null;
            return;
        }

        _ui.TftpFolder = _folder.Text;
        _ui.TftpAddress = choice.Address.ToString();
        _ui.TftpPort = port;
        _ui.TftpUploads = _uploads.IsChecked == true;
        try
        {
            _ui.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        SetEditable(false);
        _start.Content = "Stop";
        RefreshFiles();
        ShowHowTo();
    }

    private async Task StopAsync()
    {
        if (_server is null)
        {
            return;
        }

        await _server.DisposeAsync();
        _server = null;
        _start.Content = "Start";
        SetEditable(true);
    }

    private void SetEditable(bool editable)
    {
        _folder.IsEnabled = editable;
        _address.IsEnabled = editable;
        _port.IsEnabled = editable;
        _uploads.IsEnabled = editable;
    }

    private void AppendLog(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
    }
}
