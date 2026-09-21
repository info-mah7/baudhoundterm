using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Quarterdeck.App.Views;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;

namespace Quarterdeck.App;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new();
    private readonly CredentialVault _vault = new();
    private List<SessionDefinition> _sessions;
    private List<string> _folders;

    public MainWindow()
    {
        InitializeComponent();

        Title = $"BaudHoundTerm {Version}";

        _sessions = _store.Load();
        _folders = _store.LoadFolders();
        RebuildTree();

        // Relaunched through UAC by the Admin button: open the shell the user asked for.
        if (Environment.GetCommandLineArgs().Contains(AdminShellSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Loaded += (_, _) => OnAdminShell(this, new RoutedEventArgs());
        }

        Closing += async (_, _) =>
        {
            foreach (var view in Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<TerminalView>())
            {
                await view.DisconnectAsync();
            }
        };
    }

    // ---------- session tree ----------

    private void RebuildTree()
    {
        SessionTree.Items.Clear();

        var filter = FilterBox?.Text?.Trim() ?? "";
        var visible = string.IsNullOrEmpty(filter)
            ? _sessions
            : _sessions.Where(s =>
                s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();

        // Every folder that must appear: the ones declared in their own right, plus any implied
        // by a session's path, plus every ancestor of both.
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _folders.Concat(visible.Select(s => s.Folder)))
        {
            var trimmed = path.Trim().Trim('/');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= segments.Length; i++)
            {
                paths.Add(string.Join('/', segments.Take(i)));
            }
        }

        // path -> node, so a child can find its parent without walking the tree again.
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            var node = new TreeViewItem { Header = $"📁  {name}", Tag = new FolderNode(path) };
            nodes[path] = node;

            var slash = path.LastIndexOf('/');
            if (slash > 0 && nodes.TryGetValue(path[..slash], out var parent))
            {
                parent.Items.Add(node);
            }
            else
            {
                SessionTree.Items.Add(node);
            }
        }

        foreach (var session in visible.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var header = session.Protocol == SessionProtocol.Ssh && !string.IsNullOrEmpty(session.Host)
                ? $"{session.Name}  ({session.Host})"
                : session.Name;

            var item = new TreeViewItem { Header = header, Tag = session };
            var folder = session.Folder.Trim().Trim('/');

            if (folder.Length > 0 && nodes.TryGetValue(folder, out var parent))
            {
                parent.Items.Add(item);
            }
            else
            {
                SessionTree.Items.Add(item);
            }
        }
    }

    /// <summary>Marks a tree node as a folder and carries its full path.</summary>
    private sealed record FolderNode(string Path);

    private FolderNode? SelectedFolder => (SessionTree.SelectedItem as TreeViewItem)?.Tag as FolderNode;

    // ---------- folders ----------

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        // A folder created while one is selected lands inside it.
        var parent = SelectedFolder?.Path ?? Selected?.Folder ?? "";

        var dialog = new InputPromptWindow(
            "Name for the new folder",
            parent.Length > 0 ? parent + "/" : "",
            "Use / to nest, for example Customers/Acme/Edge")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Value.Length == 0)
        {
            return;
        }

        _folders.Add(dialog.Value.Trim().Trim('/'));
        SaveFolders();
    }

    private void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not { } folder)
        {
            MessageBox.Show(this, "Select a folder first.", "BaudHoundTerm");
            return;
        }

        var dialog = new InputPromptWindow("Rename folder", folder.Path,
            "Renaming moves every session and subfolder underneath it.") { Owner = this };

        if (dialog.ShowDialog() != true || dialog.Value.Length == 0)
        {
            return;
        }

        var from = folder.Path;
        var to = dialog.Value.Trim().Trim('/');

        // Rewrite the prefix on the folder itself and on everything nested below it.
        _folders = _folders
            .Select(f => Rebase(f, from, to))
            .ToList();

        foreach (var session in _sessions.Where(s => IsUnder(s.Folder, from)))
        {
            session.Folder = Rebase(session.Folder, from, to);
        }

        SaveFolders();
        Save();
    }

    private void OnDeleteFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not { } folder)
        {
            MessageBox.Show(this, "Select a folder first.", "BaudHoundTerm");
            return;
        }

        var contents = _sessions.Count(s => IsUnder(s.Folder, folder.Path));

        var answer = MessageBox.Show(
            this,
            contents == 0
                ? $"Delete the empty folder \"{folder.Path}\"?"
                : $"\"{folder.Path}\" holds {contents} session(s).\n\n" +
                  "Delete the folder and move them to the top level? The sessions are kept.",
            "BaudHoundTerm", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var session in _sessions.Where(s => IsUnder(s.Folder, folder.Path)))
        {
            session.Folder = "";
        }

        _folders = _folders.Where(f => !IsUnder(f, folder.Path)).ToList();
        SaveFolders();
        Save();
    }

    private void OnMoveToFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } session)
        {
            MessageBox.Show(this, "Select a session first.", "BaudHoundTerm");
            return;
        }

        var dialog = new InputPromptWindow(
            $"Move \"{session.Name}\" to which folder?",
            session.Folder,
            "Blank moves it to the top level. Use / to nest.") { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        session.Folder = dialog.Value.Trim().Trim('/');

        if (session.Folder.Length > 0)
        {
            _folders.Add(session.Folder);
            SaveFolders();
        }

        Save();
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static string Rebase(string path, string from, string to) =>
        IsUnder(path, from) ? to + path[from.Length..] : path;

    private void SaveFolders()
    {
        _store.SaveFolders(_folders);
        _folders = _store.LoadFolders();
        RebuildTree();
    }

    private SessionDefinition? Selected => (SessionTree.SelectedItem as TreeViewItem)?.Tag as SessionDefinition;

    private TerminalView? ActiveView => (Tabs.SelectedItem as TabItem)?.Content as TerminalView;

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => RebuildTree();

    private void OnTreeActivated(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null)
        {
            Connect(Selected);
        }
    }

    private void Save()
    {
        _store.Save(_sessions);
        RebuildTree();
    }

    // ---------- session commands ----------

    private void OnNewSession(object sender, RoutedEventArgs e)
    {
        var definition = new SessionDefinition();
        var editor = new SessionEditorWindow(definition, _vault) { Owner = this };

        if (editor.ShowDialog() == true)
        {
            _sessions.Add(definition);
            Save();
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        var editor = new SessionEditorWindow(Selected, _vault) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            Save();
        }
    }

    private void OnClone(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        var copy = Selected.Clone();

        // Carry the saved secret across so a cloned session connects without re-entry.
        if (_vault.Get(Selected.Id) is { } password)
        {
            _vault.Set(copy.Id, password);
        }

        if (_vault.Get(Selected.Id, "passphrase") is { } passphrase)
        {
            _vault.Set(copy.Id, passphrase, "passphrase");
        }

        _sessions.Add(copy);
        Save();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this, $"Delete session \"{Selected.Name}\"?", "BaudHoundTerm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _vault.Remove(Selected.Id);
        _sessions.Remove(Selected);
        Save();
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Selected is not null)
        {
            Connect(Selected);
        }
    }

    private async void Connect(SessionDefinition definition)
    {
        var view = new TerminalView(definition, _vault);
        var tab = new TabItem { Content = view };

        SetTabHeader(tab, definition, definition.Name);
        tab.ContextMenu = BuildTabMenu(tab, definition);
        view.TitleChanged += (_, title) =>
            SetTabHeader(tab, definition, string.IsNullOrWhiteSpace(title) ? definition.Name : title);

        view.EditCommandsRequested += (_, _) =>
        {
            var editor = new SessionEditorWindow(definition, _vault) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                SetTabHeader(tab, definition, tab.Header is StackPanel ? definition.Name : definition.Name);
                view.ApplyHighlights();

                if (_sessions.Any(s => s.Id == definition.Id))
                {
                    Save();
                }
            }
        };

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;

        await view.ConnectAsync();
        Save();
    }

    /// <summary>The palette offered when right-clicking a tab.</summary>
    private static readonly (string Name, string Hex)[] TabPalette =
    {
        ("No colour", ""),
        ("Red", "#e06c75"),
        ("Amber", "#e5c07b"),
        ("Green", "#98c379"),
        ("Cyan", "#56b6c2"),
        ("Blue", "#61afef"),
        ("Purple", "#c678dd"),
        ("Grey", "#8b94a7")
    };

    /// <summary>
    /// Right-click menu on a tab: pick a colour, or open the session's properties. The colour
    /// sticks to the session, so a saved one keeps it next time it is opened.
    /// </summary>
    private ContextMenu BuildTabMenu(TabItem tab, SessionDefinition definition)
    {
        var menu = new ContextMenu();

        foreach (var (name, hex) in TabPalette)
        {
            var item = new MenuItem { Header = name, IsCheckable = false };

            if (hex.Length > 0)
            {
                try
                {
                    item.Icon = new System.Windows.Shapes.Rectangle
                    {
                        Width = 12,
                        Height = 12,
                        RadiusX = 2,
                        RadiusY = 2,
                        Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                            .ConvertFromString(hex)!
                    };
                }
                catch (FormatException)
                {
                }
            }

            var colour = hex;
            item.Click += (_, _) => ApplyTabColour(tab, definition, colour);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var custom = new MenuItem { Header = "Custom colour..." };
        custom.Click += (_, _) =>
        {
            var dialog = new InputPromptWindow("Tab colour", definition.TabColor,
                "Hex like #ff8800, or a name like Tomato. Blank clears it.") { Owner = this };

            if (dialog.ShowDialog() == true)
            {
                ApplyTabColour(tab, definition, dialog.Value);
            }
        };
        menu.Items.Add(custom);

        var properties = new MenuItem { Header = "Properties..." };
        properties.Click += (_, _) =>
        {
            var editor = new SessionEditorWindow(definition, _vault) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                SetTabHeader(tab, definition, definition.Name);
                PersistIfSaved(definition);
            }
        };
        menu.Items.Add(properties);

        return menu;
    }

    private void ApplyTabColour(TabItem tab, SessionDefinition definition, string colour)
    {
        definition.TabColor = colour.Trim();

        // Keep whatever title the far end last gave us rather than snapping back to the name.
        var title = tab.Header is StackPanel panel && panel.Children.Count > 0
            ? panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? definition.Name
            : definition.Name;

        SetTabHeader(tab, definition, title);
        PersistIfSaved(definition);
    }

    /// <summary>A quick-connect session has nothing to write to; a saved one does.</summary>
    private void PersistIfSaved(SessionDefinition definition)
    {
        if (_sessions.Any(s => s.Id == definition.Id))
        {
            Save();
        }
    }

    /// <summary>
    /// A coloured stripe beside the tab title. The stripe is part of the header content rather
    /// than TabItem.Background, which the default template paints over when the tab is selected.
    /// </summary>
    private static void SetTabHeader(TabItem tab, SessionDefinition definition, string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        if (!string.IsNullOrWhiteSpace(definition.TabColor))
        {
            try
            {
                var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                    .ConvertFromString(definition.TabColor)!;

                panel.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 4,
                    Height = 14,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = brush,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            catch (FormatException)
            {
                // A colour the user mistyped should not cost them the tab.
            }
        }

        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };

        // The app-wide TextBlock style would otherwise pin one colour here and the tab would
        // not brighten when selected or dim when it is not.
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(TabItem), 1)
        });

        panel.Children.Add(text);
        tab.Header = panel;
    }

    // ---------- terminal commands ----------

    private void OnFind(object sender, RoutedEventArgs e) => ActiveView?.Find();

    private void OnClear(object sender, RoutedEventArgs e) => ActiveView?.ClearScreen();

    private async void OnDisconnectTab(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not null)
        {
            await ActiveView.DisconnectAsync();
        }
    }

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (tab.Content is TerminalView view)
        {
            await view.DisconnectAsync();

            // A quick-connect session was never saved, so its stored password has nothing to
            // belong to once the tab is gone.
            if (!_sessions.Any(s => s.Id == view.Definition.Id))
            {
                _vault.Remove(view.Definition.Id);
            }
        }

        Tabs.Items.Remove(tab);
    }

    // ---------- quick connect ----------

    /// <summary>
    /// Right-clicking selects what it points at first, so the context menu acts on the item
    /// under the cursor rather than whatever happened to be selected before.
    /// </summary>
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        while (source is not null and not TreeViewItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is TreeViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void OnQuickAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnQuickConnect(sender, e);
        }
    }

    /// <summary>
    /// Connects to whatever was typed without creating a saved session. Accepts
    /// "user@host:port" for SSH, "COM3@115200" for serial, or a command line for a local shell.
    /// </summary>
    private void OnQuickConnect(object sender, RoutedEventArgs e)
    {
        var address = QuickAddress.Text.Trim();
        if (string.IsNullOrEmpty(address))
        {
            QuickAddress.Focus();
            return;
        }

        var protocol = (SessionProtocol)Math.Max(0, QuickProtocol.SelectedIndex);
        var definition = new SessionDefinition { Protocol = protocol, Name = address };

        switch (protocol)
        {
            case SessionProtocol.Ssh:
                var rest = address;

                // A bare IPv6 literal has colons of its own; only split a trailing :port.
                var colon = rest.LastIndexOf(':');
                if (colon > 0 && int.TryParse(rest[(colon + 1)..], out var port))
                {
                    definition.Port = port;
                    rest = rest[..colon];
                }

                definition.Host = rest;

                // The address box takes a server, nothing else. Who is connecting is asked here,
                // once, before a tab exists: a cancelled prompt should not leave one behind.
                var credentials = new CredentialPromptWindow(
                    $"{definition.Host}:{definition.Port}", Environment.UserName) { Owner = this };

                if (credentials.ShowDialog() != true)
                {
                    return;
                }

                definition.Username = credentials.Username;
                definition.Name = $"{definition.Username}@{definition.Host}";
                _vault.Set(definition.Id, credentials.Password);
                break;

            case SessionProtocol.Serial:
                var parts = address.Split('@', 2);
                definition.SerialPort = parts[0];
                if (parts.Length == 2 && int.TryParse(parts[1], out var baud))
                {
                    definition.BaudRate = baud;
                }

                break;

            case SessionProtocol.Telnet:
                var target = address;
                var mark = target.LastIndexOf(':');

                if (mark > 0 && int.TryParse(target[(mark + 1)..], out var telnetPort))
                {
                    definition.Port = telnetPort;
                    target = target[..mark];
                }
                else
                {
                    definition.Port = 23;
                }

                definition.Host = target;
                definition.Name = $"{definition.Host}:{definition.Port}";
                break;

            default:
                definition.ShellCommand = address;
                break;
        }

        Connect(definition);
    }

    /// <summary>
    /// Connect to a BaudHound wireless serial console. It serves the UART it is clipped to on
    /// telnet port 23, at 192.168.4.1 on its own access point or at its LAN address once joined
    /// to a network, so the address is asked rather than assumed.
    /// </summary>
    private void OnBaudHound(object sender, RoutedEventArgs e)
    {
        var dialog = new InputPromptWindow(
            "BaudHound address",
            "192.168.4.1",
            "192.168.4.1 on the device's own AP, or its LAN IP from the Settings page. Port 23 unless given.")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Value.Length == 0)
        {
            return;
        }

        var address = dialog.Value.Trim();
        var port = 23;
        var mark = address.LastIndexOf(':');

        if (mark > 0 && int.TryParse(address[(mark + 1)..], out var given))
        {
            port = given;
            address = address[..mark];
        }

        Connect(new SessionDefinition
        {
            Name = $"BaudHound {address}",
            Protocol = SessionProtocol.Telnet,
            Host = address,
            Port = port,
            TabColor = "#e5c07b"
        });
    }

    // ---------- quick shells ----------

    /// <summary>A throwaway local shell tab. Nothing is written to the session store.</summary>
    private void OnQuickLocalShell(object sender, RoutedEventArgs e) =>
        Connect(new SessionDefinition
        {
            Name = "Local shell",
            Protocol = SessionProtocol.LocalShell,
            ShellCommand = "powershell.exe"
        });

    /// <summary>
    /// An elevated shell. A medium-integrity process cannot attach a pseudoconsole to a
    /// high-integrity child, so when we are not already elevated the only honest option is to
    /// relaunch Quarterdeck itself through UAC and open the shell on the other side.
    /// </summary>
    private void OnAdminShell(object sender, RoutedEventArgs e)
    {
        if (IsElevated)
        {
            Connect(new SessionDefinition
            {
                Name = "Administrator: shell",
                Protocol = SessionProtocol.LocalShell,
                ShellCommand = "powershell.exe"
            });
            return;
        }

        var answer = MessageBox.Show(
            this,
            "An elevated shell cannot run inside this window because Quarterdeck is not elevated.\n\n" +
            "Relaunch Quarterdeck as administrator and open the shell there?\n" +
            "Open sessions in this window stay open.",
            "BaudHoundTerm", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "Quarterdeck.exe",
                Arguments = AdminShellSwitch,
                UseShellExecute = true,
                Verb = "runas"           // triggers the UAC prompt
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user dismissed the UAC prompt. That is an answer, not a failure to report.
        }
    }

    internal const string AdminShellSwitch = "--admin-shell";

    private static bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    private void OnTransfer(object sender, RoutedEventArgs e)
    {
        var definition = ActiveView?.Definition ?? Selected;

        if (definition is null || definition.Protocol != SessionProtocol.Ssh)
        {
            MessageBox.Show(this, "File transfer needs an SSH session.", "BaudHoundTerm");
            return;
        }

        new TransferWindow(definition, _vault) { Owner = this }.Show();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DefaultLogDirectory) { UseShellExecute = true });

    /// <summary>Assembly version, so a build can be identified from the title bar.</summary>
    private static string Version
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"BaudHoundTerm {Version}\n" +
            "Terminal and session manager for Windows: SSH, telnet, serial and local shells.\n" +
            "The desktop console for BaudHound (github.com/magicsmokepress/BaudHound).\n\n" +
            "Apache-2.0. Terminal rendering by xterm.js (MIT), SSH by SSH.NET (MIT).",
            "About BaudHoundTerm");

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
