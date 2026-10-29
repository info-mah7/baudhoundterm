using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Sync;

namespace Quarterdeck.App.Views;

/// <summary>
/// Connects this PC to a git repository that shares shortcuts, folders, agent endpoints,
/// skills and trusted host keys with other PCs, and shows how the last sync went.
/// </summary>
internal sealed class SyncWindow : Window
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));

    private readonly SyncSettings _settings;
    private readonly MainWindow _main;
    private readonly TextBox _url = new();
    private readonly TextBox _branch = new();
    private readonly TextBox _machine = new();
    private readonly TextBox _gitPath = new();
    private readonly CheckBox _auto = new() { Content = "Sync automatically: at start, a few seconds after each change, and every 5 minutes", Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _memory = new()
    {
        Content = "Include the agent's memory (its notes about each host)",
        Margin = new Thickness(0, 6, 0, 0),
        ToolTip = "Per PC. Off, this PC's notes stay here and it receives none from the others; shortcuts still sync."
    };
    private readonly TextBlock _git = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _connect;
    private readonly Button _syncNow;
    private readonly Button _disconnect;

    public SyncWindow(SyncSettings settings, MainWindow main)
    {
        _settings = settings;
        _main = main;
        Title = "Sync between PCs";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = "Shortcuts, folders, agent endpoints, skills and trusted host keys go to a git repository that your " +
                   "other PCs share. Passwords, keys, KeePassXC pairings, serial port names and key file paths never leave " +
                   "this PC. The repository must be private: a public one is refused, because shortcuts map your hosts, " +
                   "users and devices. GitHub works (sign-in happens in the browser), so does a repository on your own server.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(_git);

        Field(root, "Repository (https://github.com/you/bht-config.git, git@github.com:you/bht-config.git, ssh://server/srv/bht.git)", _url);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        Field(left, "Branch", _branch);
        var right = new StackPanel();
        Field(right, "This PC's name (in commits and conflict copies)", _machine);
        Grid.SetColumn(right, 2);
        row.Children.Add(left);
        row.Children.Add(right);
        root.Children.Add(row);
        Field(root, "git.exe (blank finds Git for Windows)", _gitPath);
        root.Children.Add(_auto);
        root.Children.Add(_memory);

        _connect = Btn("Connect", async () => await ConnectAsync(), "Clone the repository and merge this PC's shortcuts into it");
        _syncNow = Btn("Sync now", async () => await SyncAsync());
        _disconnect = Btn("Stop syncing", Disconnect, "This PC keeps its shortcuts; nothing is deleted anywhere");
        _buttons.Children.Add(_connect);
        _buttons.Children.Add(_syncNow);
        _buttons.Children.Add(_disconnect);
        root.Children.Add(_buttons);
        root.Children.Add(_status);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(Btn("Close", Close));
        root.Children.Add(bottom);
        Content = root;

        _url.Text = settings.RepoUrl;
        _branch.Text = settings.Branch;
        _machine.Text = settings.MachineName;
        _gitPath.Text = settings.GitPath;
        _auto.IsChecked = settings.Automatic;
        _memory.IsChecked = settings.SyncAgentMemory;
        _memory.Click += (_, _) =>
        {
            _settings.SyncAgentMemory = _memory.IsChecked == true;
            _settings.Save();
        };
        _auto.Click += (_, _) =>
        {
            _settings.Automatic = _auto.IsChecked == true;
            _settings.Save();
            _main.SyncSettingsChanged();
        };

        ShowGit();
        Refresh();
    }

    private void ShowGit()
    {
        var path = GitRunner.Find(_gitPath.Text.Trim());
        _git.Text = path is null
            ? "Git for Windows was not found. Install it from https://git-scm.com/download/win (it also brings the credential manager that signs in to GitHub), or give the path to git.exe below."
            : "Git: " + path;
        _git.Foreground = path is null ? Bad : Muted;
    }

    private void Refresh()
    {
        var connected = _settings.IsConnected;
        _url.IsReadOnly = connected;
        _branch.IsReadOnly = connected;
        _connect.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        _syncNow.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        _disconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        var failed = _settings.LastResult.StartsWith("Sync failed", StringComparison.Ordinal);
        _status.Text = !connected
            ? (failed ? _settings.LastResult : "Not syncing.")
            : $"Syncing with {_settings.RepoUrl} ({_settings.Branch}). " +
              (_settings.LastSync is { } at ? $"Last sync {at:g}: " : "") + _settings.LastResult;
        _status.Foreground = failed ? Bad : connected ? Good : Muted;
    }

    private void SaveFields()
    {
        _settings.Branch = string.IsNullOrWhiteSpace(_branch.Text) ? "main" : _branch.Text.Trim();
        _settings.MachineName = string.IsNullOrWhiteSpace(_machine.Text) ? Environment.MachineName : _machine.Text.Trim();
        _settings.GitPath = _gitPath.Text.Trim();
        _settings.Save();
        ShowGit();
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_url.Text))
        {
            Status("Enter the repository's address first.", Bad);
            return;
        }

        SaveFields();
        _settings.RepoUrl = _url.Text.Trim();
        _settings.Save();
        _buttons.IsEnabled = false;
        Status("Checking the repository is private, then cloning and merging (a browser may open to sign in)...", Muted);

        try
        {
            var outcome = await _main.SyncNowAsync(connect: true);
            if (outcome is not { Connected: true })
            {
                // Not connected after all (refused, unreachable, no git): back to square one.
                _settings.RepoUrl = "";
                _settings.Save();
            }
        }
        finally
        {
            _buttons.IsEnabled = true;
            _main.SyncSettingsChanged();
            Refresh();
        }
    }

    private async Task SyncAsync()
    {
        SaveFields();
        _buttons.IsEnabled = false;
        Status("Syncing...", Muted);
        try
        {
            await _main.SyncNowAsync();
        }
        finally
        {
            _buttons.IsEnabled = true;
            _main.SyncSettingsChanged();
            Refresh();
        }
    }

    private void Disconnect()
    {
        _settings.RepoUrl = "";
        _settings.LastResult = "";
        _settings.Save();
        _main.SyncSettingsChanged();
        Refresh();
    }

    private void Status(string text, Brush brush)
    {
        _status.Text = text;
        _status.Foreground = brush;
    }

    private static void Field(Panel panel, string label, UIElement input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        panel.Children.Add(input);
    }

    private static Button Btn(string text, Action act, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = tip };
        b.Click += (_, _) => act();
        return b;
    }
}
