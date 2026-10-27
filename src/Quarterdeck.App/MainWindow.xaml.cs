using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Quarterdeck.App.Views;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;
using Quarterdeck.Core.Transfer;

namespace Quarterdeck.App;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new();
    private readonly CredentialVault _vault = new();
    private List<SessionDefinition> _sessions;
    private List<string> _folders;
    private readonly UiPreferences _ui = UiPreferences.Load();

    public MainWindow()
    {
        InitializeComponent();

        Title = $"BaudHoundTerm {Version}";

        _sessions = _store.Load();
        _folders = _store.LoadFolders();

        var problems = new[] { _store.LoadProblem, _vault.LoadProblem }.OfType<string>().ToList();
        if (problems.Count > 0)
        {
            Loaded += (_, _) => MessageBox.Show(this, string.Join("\n\n", problems), "BaudHoundTerm",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Unknown or changed SSH host keys are put to the user; nothing is trusted silently.
        KnownHosts.Ask = q => Dispatcher.Invoke(() => AskHostKey(q));
        GroupToggle.IsChecked = _ui.GroupByHost;   // Click, not Checked: setting it here saves nothing
        Privacy.HideAll = _ui.HideAddresses;
        HideMenu.IsChecked = _ui.HideAddresses;
        DetailsMenu.IsChecked = _ui.ShowAddressDetails;
        TerminalView.AgentsAllowed = _ui.AgentEnabled;
        AgentFeatureMenu.IsChecked = _ui.AgentEnabled;
        SyncFeatureMenu.IsChecked = _ui.SyncEnabled;
        Activated += (_, _) => MarkFocusedRow();   // back from a popped-out window
        RebuildTree();

        // Relaunched through UAC by the Admin button: open the shell the user asked for.
        var args = Environment.GetCommandLineArgs();
        if (args.Contains(AdminShellSwitch, StringComparer.OrdinalIgnoreCase))
        {
            var shell = AdminShellFromCommandLine(args);
            Loaded += (_, _) => LaunchShell(shell, elevated: true);
        }

        InitSync();
        UpdateConnectionToggle();
        UpdateSelectionCommands();

        // Tabs are not kept across restarts: the app starts with none open. A connection that
        // drops while the app runs still reconnects, per the shortcut's reconnect settings.
        Closing += async (_, e) =>
        {
            // Tabs are not restored, so closing ends every live session for good. Say so first.
            var live = AllTabItems.Select(ViewOf).OfType<TerminalView>().Count(v => v.IsConnected);
            if (live > 0 && !_closeConfirmed)
            {
                var answer = MessageBox.Show(this,
                    live == 1
                        ? "One tab is still connected. Close BaudHoundTerm and end that session?"
                        : $"{live} tabs are still connected. Close BaudHoundTerm and end those sessions?",
                    "Close BaudHoundTerm", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                _closeConfirmed = true;
            }

            // An MP4 is unreadable until its encoder finishes, and nothing waits once closing goes
            // on: hold the close, finish every recording, then close again.
            var recording = _live.Select(l => l.View).Where(v => v.IsRecording).ToList();
            if (recording.Count > 0)
            {
                e.Cancel = true;
                foreach (var view in recording)
                {
                    // An audit's log closes with the connection; its SHA-256 list needs the whole file.
                    if (view.RecordingIsAudit)
                    {
                        await view.DisconnectAsync();
                    }

                    await view.FinishRecordingAsync();
                }

                Close();
                return;
            }

            // Popped-out windows would otherwise keep the app running with the main window gone.
            foreach (var popped in _live.Select(l => l.PoppedOut).OfType<PopOutWindow>().ToList())
            {
                popped.Returned = true;
                popped.Content = null;   // the view leaves first, or closing the window destroys its native child
                popped.Close();
            }

            foreach (var view in AllTabItems.Select(ViewOf).OfType<TerminalView>())
            {
                await view.DisconnectAsync();
            }
        };

        _fullScreen = new FullScreenToggle(this);
        PreviewKeyDown += OnWindowKey;
        WireSides();
    }

    // ---------- tab navigation, full screen, pop-out ----------

    private readonly FullScreenToggle _fullScreen;
    private GridLength _listWidth;
    private double _listMinWidth;

    /// <summary>
    /// Ctrl+Tab and Ctrl+PgDn go to the next tab, Ctrl+Shift+Tab and Ctrl+PgUp to the previous;
    /// F11 is full screen; Ctrl+Shift+R and Ctrl+Shift+P record. Seen here first (Preview), so a terminal or remote screen with the
    /// keyboard does not swallow them. Ctrl+Left/Right stay with the session: shells use them
    /// to move by word.
    /// </summary>
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        int step = (e.Key, mods) switch
        {
            (Key.Tab, ModifierKeys.Control) or (Key.PageDown, ModifierKeys.Control) => 1,
            (Key.Tab, ModifierKeys.Control | ModifierKeys.Shift) or (Key.PageUp, ModifierKeys.Control) => -1,
            _ => 0
        };

        if (step != 0)
        {
            CycleTab(step);
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && mods == ModifierKeys.None)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.H && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            SetHideAddresses(!Privacy.HideAll);
            e.Handled = true;
        }
        else if (e.Key is Key.Oem5 or Key.OemBackslash && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OnMoveToOtherSide(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F6 && mods == ModifierKeys.None && IsSplit)
        {
            SwitchSide();
            e.Handled = true;
        }
        else if (e.Key is Key.R or Key.P && mods == (ModifierKeys.Control | ModifierKeys.Shift) && FocusedLive is not null)
        {
            e.Handled = true;
            _ = RecordKeyAsync(e.Key);
        }
    }

    private void OnNextTab(object sender, RoutedEventArgs e) => CycleTab(1);

    private void OnPreviousTab(object sender, RoutedEventArgs e) => CycleTab(-1);

    private void CycleTab(int step)
    {
        var count = ActiveTabs.Items.Count;
        if (count < 2)
        {
            return;
        }

        ActiveTabs.SelectedIndex = ((ActiveTabs.SelectedIndex < 0 ? 0 : ActiveTabs.SelectedIndex) + step + count) % count;
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>Only the tabs: no menu, toolbar, quick connect or shortcut list, and no window frame.</summary>
    private void ToggleFullScreen()
    {
        var entering = !_fullScreen.IsOn;
        var chrome = entering ? Visibility.Collapsed : Visibility.Visible;
        foreach (var part in new UIElement[] { MainMenu, ToolStrip, QuickBar, ListPanel, ListSplitter })
        {
            part.Visibility = chrome;
        }

        if (entering)
        {
            _listWidth = ListColumn.Width;
            _listMinWidth = ListColumn.MinWidth;
            ListColumn.MinWidth = 0;
            ListColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
        }
        else
        {
            ListColumn.Width = _listWidth;
            ListColumn.MinWidth = _listMinWidth;
            SplitterColumn.Width = new GridLength(4);
        }

        _fullScreen.Toggle();
        ActiveView?.FocusTerminal();
    }

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        if ((ActiveTabs.SelectedItem as TabItem) is { } tab && LiveFor(tab) is { } live)
        {
            TogglePopOut(live);
        }
    }

    private void TogglePopOut(LiveTab live)
    {
        if (live.PoppedOut is null)
        {
            PopOut(live);
        }
        else
        {
            BringBack(live);
        }
    }

    /// <summary>
    /// Moves the live view into its own window. The tab stays, holding a note and the way back,
    /// so the tab order, the list and the toolbar commands still reach the session.
    /// </summary>
    private void PopOut(LiveTab live)
    {
        StopRecordingForMove(live);
        live.Tab.Content = PoppedPlaceholder(live);
        var window = new PopOutWindow(live.View, Privacy.MaskName(live.Title, live.Definition), this);
        window.ReturnRequested += (_, _) => BringBack(live, closing: true);
        window.Activated += (_, _) => MarkFocusedRow();
        live.PoppedOut = window;
        window.Show();
        RefreshTab(live);
        UpdateConnectionToggle();
    }

    /// <summary>
    /// A recording follows the window it started in; a tab moving to another window would leave
    /// it filming the empty place. It is finished here and the status line says where it went.
    /// </summary>
    private async void StopRecordingForMove(LiveTab live)
    {
        if (live.View.IsRecording)
        {
            ShowRecordingResult(await live.View.RecordingMovedAsync());
        }
    }

    private void BringBack(LiveTab live, bool closing = false)
    {
        if (live.PoppedOut is not { } window)
        {
            return;
        }

        StopRecordingForMove(live);
        live.PoppedOut = null;
        window.Returned = true;
        window.Content = null;
        if (!closing)
        {
            window.Close();
        }

        live.Tab.Content = live.View;
        SelectTab(live.Tab);
        Activate();
        live.View.FocusTerminal();
        RefreshTab(live);
        UpdateConnectionToggle();
    }

    private UIElement PoppedPlaceholder(LiveTab live)
    {
        var grey = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "This tab is in its own window, still connected.",
            Foreground = grey,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var show = new Button { Content = "Show window", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        show.Click += (_, _) =>
        {
            if (live.PoppedOut is { } w)
            {
                if (w.WindowState == WindowState.Minimized)
                {
                    w.WindowState = WindowState.Normal;
                }

                w.Activate();
            }
        };
        var back = new Button { Content = "Bring back", Padding = new Thickness(10, 4, 10, 4) };
        back.Click += (_, _) => BringBack(live);
        buttons.Children.Add(show);
        buttons.Children.Add(back);
        panel.Children.Add(buttons);
        return panel;
    }

    private bool AskHostKey(HostKeyQuestion q)
    {
        var tls = q.Algorithm.StartsWith("TLS", StringComparison.Ordinal);
        var what = tls ? "TLS certificate" : "host key";
        var check = tls
            ? "If you can, compare it with the TLS line on the device's Settings page (BaudHound) or its own fingerprint."
            : "If you can, compare this with the server's own fingerprint (ssh-keygen -lf on the host key).";

        var text = q.Changed
            ? $"WARNING: the {what} for {q.Host}:{q.Port} has CHANGED.\n\n" +
              $"Trusted before:  SHA256:{q.PreviousFingerprint}\nPresented now:   SHA256:{q.Fingerprint} ({q.Algorithm})\n\n" +
              "Either the device or server was reset or re-keyed, or something is intercepting the connection " +
              "and would see everything typed. Only accept if you know it changed.\n\nTrust the new one?"
            : $"First connection to {q.Host}:{q.Port}.\n\n{(tls ? "TLS certificate" : $"Host key ({q.Algorithm})")}:\nSHA256:{q.Fingerprint}\n\n" +
              check + " Trust it and remember it for next time?";

        return MessageBox.Show(this, text, tls ? "BaudHoundTerm: TLS certificate" : "BaudHoundTerm: SSH host key", MessageBoxButton.YesNo,
                   q.Changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
                   MessageBoxResult.No) == MessageBoxResult.Yes;   // Enter never trusts a key by accident
    }

    // ---------- session tree ----------

    private void RebuildTree()
    {
        SessionTree.Items.Clear();
        _liveRows.Clear();
        var placed = new HashSet<LiveTab>();

        var visible = _sessions.Where(MatchesFilter(FilterBox?.Text ?? "")).ToList();

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
            // ItemContainerStyle reaches only the tree's own children; nested rows have to be
            // handed it, or they start collapsed and select in the system's black-on-blue.
            var node = new TreeViewItem
            {
                Header = $"📁  {name}",
                Tag = new FolderNode(path),
                ItemContainerStyle = SessionTree.ItemContainerStyle,
                IsExpanded = true
            };
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

        ItemsControl ContainerFor(string folder) =>
            folder.Length > 0 && nodes.TryGetValue(folder, out var parent) ? parent : SessionTree;

        // Grouping is worked out here, from Host, and never stored. With it on, every host gets a
        // row, even with one shortcut, so a machine looks the same whether it has one protocol
        // set up or three. A host row never shows a protocol that is not configured.
        var groups = visible
            .GroupBy(s => (Folder: s.Folder.Trim().Trim('/'), Host: _ui.GroupByHost ? SessionPresentation.HostKey(s) : null))
            .OrderBy(g => g.Key.Host is null ? g.First().Name : GroupTitle(g.ToList()), StringComparer.OrdinalIgnoreCase);

        var loose = new List<(string Folder, SessionDefinition Session)>();

        foreach (var group in groups)
        {
            var members = group.ToList();

            if (group.Key.Host is null)
            {
                loose.AddRange(members.Select(m => (group.Key.Folder, m)));
                continue;
            }

            var title = GroupTitle(members);
            var hostNode = new TreeViewItem
            {
                Header = SessionPresentation.HostRow(title, members[0].Host.Trim(),
                    members.Any(m => m.HideAddress), _ui.ShowAddressDetails),
                Tag = new HostNode(members[0].Host.Trim(), group.Key.Folder, members),
                ItemContainerStyle = SessionTree.ItemContainerStyle,
                IsExpanded = true
            };

            foreach (var session in members
                         .OrderBy(s => s.Protocol)
                         .ThenBy(SessionPresentation.EffectivePort)
                         .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                // The name says nothing new when it is the group's title; the badge and target do.
                var name = string.Equals(session.Name, title, StringComparison.OrdinalIgnoreCase) ? null : session.Name;
                hostNode.Items.Add(WithLiveRows(new TreeViewItem
                {
                    Header = SessionPresentation.Row(session, name, hostShown: true, _ui.ShowAddressDetails),
                    Tag = session
                }, session, placed));
            }

            ContainerFor(group.Key.Folder).Items.Add(hostNode);
        }

        foreach (var (folder, session) in loose.OrderBy(l => l.Session.Name, StringComparer.OrdinalIgnoreCase))
        {
            ContainerFor(folder).Items.Add(WithLiveRows(new TreeViewItem
            {
                Header = SessionPresentation.Row(session, session.Name, hostShown: false, _ui.ShowAddressDetails),
                Tag = session
            }, session, placed));
        }

        // Quick connections with no shortcut to sit under, and open tabs whose session the
        // filter is hiding, still need a row: a connection that is open is never invisible.
        var strays = _live.Where(l => !placed.Contains(l)).ToList();
        if (strays.Count > 0)
        {
            var open = new TreeViewItem
            {
                Header = "⚡  Open, not in the list above",
                ItemContainerStyle = SessionTree.ItemContainerStyle,
                IsExpanded = true
            };

            foreach (var live in strays)
            {
                var badge = SessionPresentation.BadgeElement(live.Definition);
                var header = LiveRowHeader(live);
                header.Children.Insert(1, badge);
                var child = new TreeViewItem { Header = header, Tag = live };
                _liveRows[live] = child;
                open.Items.Add(child);
            }

            SessionTree.Items.Add(open);
        }

        MarkFocusedRow();
    }

    /// <summary>
    /// A host group is called by the name most of its sessions share; on a tie, by the SSH
    /// session's name, since that is usually the machine's own; with no names, by the host.
    /// </summary>
    private static string GroupTitle(List<SessionDefinition> members)
    {
        var best = members
            .Where(m => m.Name.Trim().Length > 0)
            .GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Any(m => m.Protocol == SessionProtocol.Ssh))
            .FirstOrDefault();

        return best?.Key ?? members[0].Host.Trim();
    }

    /// <summary>
    /// The filter box: protocol words ("vnc", "ssh", "serial") narrow by protocol, every other
    /// word has to appear in the name, host, target or a tag. "vnc atlas" is atlas over VNC.
    /// </summary>
    private static Func<SessionDefinition, bool> MatchesFilter(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return _ => true;
        }

        var protocols = words.Select(SessionPresentation.ProtocolWord).OfType<SessionProtocol>().ToHashSet();
        var terms = words.Where(w => SessionPresentation.ProtocolWord(w) is null).ToList();

        return s =>
            (protocols.Count == 0 || protocols.Contains(s.Protocol)) &&
            terms.All(t =>
                s.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                s.Host.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                SessionPresentation.Target(s).Contains(t, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record HostNode(string Host, string Folder, List<SessionDefinition> Sessions);

    private HostNode? SelectedHost => (SessionTree.SelectedItem as TreeViewItem)?.Tag as HostNode;

    private void OnGroupToggle(object sender, RoutedEventArgs e)
    {
        _ui.GroupByHost = GroupToggle.IsChecked == true;

        try
        {
            _ui.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The view still changes; it just will not be remembered.
        }

        RebuildTree();
    }

    private void SaveUi()
    {
        try
        {
            _ui.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The view still changes; it just will not be remembered.
        }
    }

    private void OnHideAddresses(object sender, RoutedEventArgs e) => SetHideAddresses(!Privacy.HideAll);

    /// <summary>
    /// Hide addresses on or off, everywhere at once: the list, every tab's header, tooltip and
    /// status line, and popped-out windows. Remembered, so a restart mid-demo does not uncover them.
    /// </summary>
    // Hide addresses was off when the first recording started, and goes back off with the last.
    private bool _hiddenForRecording;

    /// <summary>Addresses hidden while any tab records; the user's own setting back afterwards.</summary>
    private void OnRecordingChanged()
    {
        // An audit keeps the addresses: what was touched is the point of it.
        var any = _live.Any(l => l.View.IsRecording && !l.View.RecordingIsAudit);
        if (any && !Privacy.HideAll)
        {
            _hiddenForRecording = true;
            SetHideAddresses(true);
        }
        else if (!any && _hiddenForRecording)
        {
            _hiddenForRecording = false;
            SetHideAddresses(false);
        }
    }

    private void ShowRecordingResult(string result)
    {
        if (result.StartsWith("error", StringComparison.Ordinal))
        {
            MessageBox.Show(this, result, "Recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetHideAddresses(bool hide)
    {
        Privacy.HideAll = hide;
        _ui.HideAddresses = hide;
        SaveUi();
        HideMenu.IsChecked = hide;
        RebuildTree();
        foreach (var live in _live)
        {
            RefreshTab(live);
        }
    }

    private void OnAddressDetails(object sender, RoutedEventArgs e)
    {
        _ui.ShowAddressDetails = DetailsMenu.IsChecked;
        SaveUi();
        RebuildTree();
    }

    /// <summary>
    /// "Add protocol" on a host row or a session with a host: a new session for the same host,
    /// with the port that protocol normally uses. What Clone was good for, without copying
    /// every setting of the old one.
    /// </summary>
    private void OnAddProtocol(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<SessionProtocol>(tag, out var protocol))
        {
            return;
        }

        var siblings = (SelectedHost?.Sessions
                        ?? (Selected is { } one && SessionPresentation.HostKey(one) is not null
                            ? new List<SessionDefinition> { one }
                            : null))
                       ?.Where(m => m.Protocol != SessionProtocol.Kvm)   // a KVM's host is its board
                       .ToList();
        if (siblings is not { Count: > 0 })
        {
            return;
        }

        var template = siblings[0];
        var definition = new SessionDefinition
        {
            Protocol = protocol,
            Host = template.Host.Trim(),
            Folder = template.Folder,
            Name = GroupTitle(siblings),
            Port = protocol switch
            {
                SessionProtocol.Vnc => 5900,
                SessionProtocol.Rtsp => 554,
                SessionProtocol.Rdp => 3389,
                SessionProtocol.Telnet => 23,
                SessionProtocol.Web => 443,
                _ => 22
            },
            Username = siblings.FirstOrDefault(s => s.Protocol == SessionProtocol.Ssh)?.Username ?? "",
            WebUrl = protocol == SessionProtocol.Web ? "https://" + template.Host.Trim() : ""
        };

        var editor = new SessionEditorWindow(definition, _vault) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _sessions.Add(definition);
            Save();
        }
    }

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
            "Renaming moves every shortcut and subfolder underneath it.") { Owner = this };

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
                : $"\"{folder.Path}\" holds {contents} shortcut(s).\n\n" +
                  "Delete the folder and move them to the top level? The shortcuts are kept.",
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
            MessageBox.Show(this, "Select a shortcut first.", "BaudHoundTerm");
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
        ScheduleSync();
    }

    private SessionDefinition? Selected => (SessionTree.SelectedItem as TreeViewItem)?.Tag as SessionDefinition;

    private bool _closeConfirmed;

    /// <summary>Shortcut commands work on the selected shortcut; without one they are dimmed.</summary>
    private void UpdateSelectionCommands()
    {
        var any = Selected is not null;
        OpenButton.IsEnabled = any;
        ConnectButton.IsEnabled = any;
        ConnectMenu.IsEnabled = any;
        PropertiesMenu.IsEnabled = any;
        DeleteMenu.IsEnabled = any;
    }

    private TerminalView? ActiveView => (ActiveTabs.SelectedItem as TabItem) is { } tab ? ViewOf(tab) : null;

    /// <summary>A tab's view, also while it is popped out and the tab only holds a placeholder.</summary>
    private TerminalView? ViewOf(TabItem tab) => LiveFor(tab)?.View ?? tab.Content as TerminalView;

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => RebuildTree();

    private void OnTreeActivated(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null)
        {
            e.Handled = true;   // or the tree item takes the click, and the keyboard, back
            Connect(Selected);
        }
        else if (SelectedHost is { Sessions.Count: 1 } host)
        {
            // A host with one shortcut: there is nothing to choose, so its row connects too.
            e.Handled = true;
            Connect(host.Sessions[0]);
        }
    }

    private void Save()
    {
        _store.Save(_sessions);
        RebuildTree();
        ScheduleSync();
    }

    // ---------- session commands ----------

    private void OnNewSession(object sender, RoutedEventArgs e) => NewSessionIn("");

    private void NewSessionIn(string folder)
    {
        var definition = new SessionDefinition { Folder = folder };
        var editor = new SessionEditorWindow(definition, _vault) { Owner = this };

        if (editor.ShowDialog() == true)
        {
            _sessions.Add(definition);
            Save();
            WarnSharedTmux(definition);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        // Capture the selection once: Selected is recomputed from the tree on every read, and a
        // tab reconnecting or retitling while the editor is open rebuilds the tree and clears the
        // selection, so reading it again after the dialog returned null and crashed RefreshOpenTabsOf.
        if (Selected is not { } definition)
        {
            return;
        }

        var editor = new SessionEditorWindow(definition, _vault) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            RefreshOpenTabsOf(definition);
            Save();
            WarnSharedTmux(definition);
        }
    }

    /// <summary>
    /// Says so when another shortcut to the same host and login uses one of this shortcut's tmux
    /// session names: their tabs then open the same session. Meant sometimes (Create shortcut does it
    /// on purpose), so it is a note, not a refusal.
    /// </summary>
    private void WarnSharedTmux(SessionDefinition definition)
    {
        var shared = SessionController.SharingTmux(definition, _sessions);
        if (shared.Count == 0)
        {
            return;
        }

        var lines = string.Join("\n", shared.Select(x => $"  {x.Shortcut.Name}: tmux session \"{x.Session}\""));
        MessageBox.Show(this,
            $"These shortcuts reach the same host and login and use the same tmux session name, so their tabs open " +
            $"the same session:\n\n{lines}\n\nIf {definition.Name} should have a session of its own, give it another name " +
            "in Properties > tmux sessions.", "Shared tmux session", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// After a shortcut is edited: every open tab of it takes the new name, colour, badge,
    /// highlights and command list. The page runs commands by index, so a stale list would
    /// run a different command than the one clicked.
    /// </summary>
    private void RefreshOpenTabsOf(SessionDefinition definition)
    {
        if (definition is null)
        {
            return;
        }

        // A Shell menu tab matches by object only: its normal and administrator tabs share an
        // Id, and editing one must not retitle or recolour the other.
        var byId = !_shellTabs.ContainsKey(definition);
        foreach (var live in _live.Where(l => l.Definition is { } d && (ReferenceEquals(d, definition) || (byId && d.Id == definition.Id))))
        {
            live.Title = TabTitle(definition, live.View);
            if (definition.TabColor.Length > 0)
            {
                live.Colour = definition.TabColor;
            }

            RefreshTab(live);
            live.View.ApplyHighlights();
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        // Captured once: the selection can be cleared while the confirm dialog is open (a tab
        // state change rebuilds the tree), so reading Selected again afterwards could be null or
        // a different shortcut.
        if (Selected is not { } session)
        {
            return;
        }

        var answer = MessageBox.Show(
            this, $"Delete the shortcut \"{session.Name}\"? Open tabs from it stay open.", "BaudHoundTerm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _vault.Remove(session.Id);
        _sessions.Remove(session);
        Save();
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Selected is not null)
        {
            Connect(Selected);
        }
    }

    /// <summary>
    /// Opens a shortcut. One with several named tmux sessions (build 72) opens a tab for each one
    /// ticked "open on connect"; the rest wait on the shortcut's Open tmux session menu.
    /// </summary>
    private async void Connect(SessionDefinition definition)
    {
        if (definition is { Protocol: SessionProtocol.Ssh, Tmux: true } && definition.TmuxSessions.Count > 1)
        {
            var open = definition.TmuxSessions.Where(t => t.OpenOnConnect).ToList();
            if (open.Count == 0)
            {
                open.Add(definition.TmuxSessions[0]);
            }

            foreach (var session in open)
            {
                await OpenTabAsync(definition, tmuxSession: session.Name);
            }

            return;
        }

        await OpenTabAsync(definition);
    }

    /// <summary>
    /// The tab title: the shortcut's name, plus the tmux session when the shortcut has several
    /// or this tab runs one other than the main session.
    /// </summary>
    private static string TabTitle(SessionDefinition definition, TerminalView view)
    {
        if (definition is not { Protocol: SessionProtocol.Ssh, Tmux: true } || string.IsNullOrWhiteSpace(view.TmuxSessionName))
        {
            return definition.Name;
        }

        var sessions = definition.TmuxSessionList();
        var name = SessionController.TmuxName(view.TmuxSessionName);
        return sessions.Count > 1 || name != SessionController.TmuxName(sessions[0].Name)
            ? $"{definition.Name} · {name}"
            : definition.Name;
    }

    /// <summary>"Open tmux session": each named session of the shortcut, then New tmux session.</summary>
    private MenuItem TmuxSessionsMenu(SessionDefinition definition)
    {
        var menu = new MenuItem { Header = "Open t_mux session" };
        foreach (var session in definition.TmuxSessionList())
        {
            var name = session.Name;
            var item = new MenuItem
            {
                Header = SessionController.TmuxName(name) + (session.OpenOnConnect ? "" : "   (on demand)"),
                ToolTip = $"A tab in the tmux session {SessionController.TmuxName(name)} on {definition.Host}: attaches if it is running, starts it if not"
            };
            item.Click += async (_, _) => await OpenTabAsync(definition, tmuxSession: name);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "_New tmux session...", ToolTip = "Name a new session on this host; it is added to the shortcut and opens in a tab" };
        add.Click += async (_, _) => await NewTmuxSessionAsync(definition);
        menu.Items.Add(add);
        return menu;
    }

    private MenuItem CreateShortcutItem(TerminalView view, SessionDefinition definition)
    {
        var item = new MenuItem
        {
            Header = "Create _shortcut for this tmux session...",
            ToolTip = "A shortcut of its own that opens this tmux session, with its folder and the program running in it"
        };
        item.Click += async (_, _) => await CreateShortcutFromTmuxAsync(view, definition);
        return item;
    }

    /// <summary>
    /// A new shortcut for one running tmux session: this connection, named after the session,
    /// with only that session. Its folder, and the program running in it, are read from the host;
    /// the properties window opens so they can be checked before it is saved.
    /// </summary>
    private async Task CreateShortcutFromTmuxAsync(TerminalView view, SessionDefinition definition)
    {
        var name = SessionController.TmuxName(string.IsNullOrWhiteSpace(view.TmuxSessionName) ? definition.TmuxSession : view.TmuxSessionName);
        var pane = view.Controller is { } controller ? await controller.TmuxPaneAsync() : null;

        var copy = definition.Clone(name);
        var known = definition.TmuxSessionList().FirstOrDefault(t => SessionController.TmuxName(t.Name) == name);
        var entry = new TmuxSessionEntry { Name = name, Folder = known?.Folder ?? "", Command = known?.StartCommand ?? "" };
        if (pane is { } p)
        {
            entry.Folder = entry.Folder.Length > 0 ? entry.Folder : p.Folder;
            entry.Command = entry.Command.Length > 0 ? entry.Command : TmuxStart.FromRunning(p.Running);
        }

        copy.Tmux = true;
        copy.TmuxSessions = [entry];
        copy.TmuxSession = name;

        // The stored secrets come along, so the new shortcut logs in the same way.
        foreach (var key in new[] { "password", "passphrase" })
        {
            if (_vault.Get(definition.Id, key) is { } secret)
            {
                _vault.Set(copy.Id, secret, key);
            }
        }

        var editor = new SessionEditorWindow(copy, _vault) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _sessions.Add(copy);
            Save();
        }
        else
        {
            _vault.Remove(copy.Id);
        }
    }

    /// <summary>Asks for a name, adds it to the shortcut's sessions (on demand), and opens it.</summary>
    private async Task NewTmuxSessionAsync(SessionDefinition definition)
    {
        var dialog = new InputPromptWindow("New tmux session", "",
            $"A name for another session on {definition.Host}, for example logs. Letters, digits, - and _.") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        var name = SessionController.TmuxName(dialog.Value.Trim());
        if (definition.TmuxSessions.Count == 0)
        {
            // An older shortcut: its single session becomes the first entry, opened as before.
            definition.TmuxSessions.Add(new TmuxSessionEntry { Name = definition.TmuxSession, OpenOnConnect = true });
        }

        if (!definition.TmuxSessions.Any(t => SessionController.TmuxName(t.Name) == name))
        {
            definition.TmuxSessions.Add(new TmuxSessionEntry { Name = name, OpenOnConnect = false });
            PersistIfSaved(definition);
            RefreshOpenTabsOf(definition);   // a single-session tab now shows its session in the title
        }

        await OpenTabAsync(definition, tmuxSession: name);
    }

    private async Task OpenTabAsync(SessionDefinition definition, string? colour = null, string? tmuxSession = null, string? localWebFolder = null)
    {
        if (definition.Protocol == SessionProtocol.Sql)
        {
            MessageBox.Show(this, $"\"{definition.Name}\" is a SQL database shortcut. SQL tabs were removed in build 70, " +
                "so it can no longer open. Delete it, or keep it as a note.", "SQL tabs removed",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var view = new TerminalView(definition, _vault) { TmuxSessionName = tmuxSession ?? "", LocalWebFolder = localWebFolder };
        var tab = new TabItem { Content = view };
        var live = new LiveTab(tab, view, definition)
        {
            Title = TabTitle(definition, view),
            Colour = string.IsNullOrEmpty(colour) ? ColourForNewTab(definition) : colour
        };
        _live.Add(live);

        RefreshTab(live);
        tab.ContextMenu = BuildTabMenu(tab, definition);
        // The tab keeps the shortcut's name: PowerShell titles itself with its full path. What
        // the shell says goes in the tab's tooltip and on its row in the list.
        view.TitleChanged += (_, title) =>
        {
            live.ShellTitle = string.IsNullOrWhiteSpace(title) ? "" : title;
            RefreshTab(live);
        };
        view.AgentAttachedChanged += (_, _) => RefreshTab(live);
        view.AuditProblem += (_, problem) =>
            MessageBox.Show(this, problem, "Audit recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        view.RecordingChanged += (_, _) =>
        {
            OnRecordingChanged();
            RefreshTab(live);
            tab.ContextMenu = BuildTabMenu(tab, definition);
        };
        view.TmuxChanged += (_, _) =>
        {
            RefreshTab(live);
            tab.ContextMenu = BuildTabMenu(tab, definition);   // the end-session item turns on with it
        };
        view.StateChanged += (_, e) =>
        {
            live.State = e.State;
            RefreshLiveRow(live);

            if (ReferenceEquals(view, ActiveView))
            {
                UpdateConnectionToggle();
            }
        };

        view.EditCommandsRequested += (_, _) =>
        {
            var editor = new SessionEditorWindow(definition, _vault) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                RefreshOpenTabsOf(definition);
                PersistIfSaved(definition);
            }
        };

        ActiveTabs.Items.Add(tab);
        UpdateConnectionToggle();
        SelectTab(tab);
        view.FocusTerminal();
        RebuildTree();   // the new connection shows under its shortcut straight away

        await view.ConnectAsync();

        // Records LastConnected. An elevated instance skips it: it loaded the list when it
        // started, and writing that snapshot back would drop shortcuts the main window has
        // added since.
        if (!Elevation.IsCurrentProcessElevated)
        {
            Save();
        }
    }

    // ---------- open connections ----------

    /// <summary>
    /// An open tab as the window tracks it: its own colour (two tabs of one session can differ)
    /// and what the session list shows for it.
    /// </summary>
    private sealed class LiveTab(TabItem tab, TerminalView view, SessionDefinition definition)
    {
        public TabItem Tab { get; } = tab;
        public TerminalView View { get; } = view;
        public SessionDefinition Definition { get; } = definition;
        public string Title { get; set; } = "";
        public string ShellTitle { get; set; } = "";
        public string Colour { get; set; } = "";
        public SessionState State { get; set; } = SessionState.Connecting;

        /// <summary>The window the view lives in while popped out; null while it is in its tab.</summary>
        public PopOutWindow? PoppedOut { get; set; }
    }

    private readonly List<LiveTab> _live = new();

    /// <summary>The list row each open tab has, rebuilt with the tree, so a title or state
    /// change updates one row instead of redrawing the list under the pointer.</summary>
    private readonly Dictionary<LiveTab, TreeViewItem> _liveRows = new();

    private LiveTab? LiveFor(TabItem tab) => _live.FirstOrDefault(l => ReferenceEquals(l.Tab, tab));

    /// <summary>Tabs that go to the same machine: same host for network sessions, the same
    /// saved session for a COM port or a local shell.</summary>
    private static bool SameTarget(SessionDefinition a, SessionDefinition b)
    {
        // A KVM's host is the board, not the machine it drives: a KVM tab and an SSH tab to the
        // board's address show two different machines, so they do not share a colour.
        if ((a.Protocol == SessionProtocol.Kvm) != (b.Protocol == SessionProtocol.Kvm))
        {
            return false;
        }

        var ka = SessionPresentation.HostKey(a);
        return ka is not null ? ka == SessionPresentation.HostKey(b) : a.Id == b.Id;
    }

    /// <summary>
    /// The first tab to a machine wears the session's own colour, or none. Once a second one
    /// opens, every tab on that machine gets a colour of its own from the palette, so the
    /// strip and the session list both say which is which. Assigned colours are not saved;
    /// one picked from the tab menu still is.
    /// </summary>
    private string ColourForNewTab(SessionDefinition definition)
    {
        var siblings = _live.Where(l => SameTarget(l.Definition, definition)).ToList();
        if (siblings.Count == 0)
        {
            return definition.TabColor;
        }

        var palette = TabPalette.Select(p => p.Hex).Where(h => h.Length > 0).ToList();
        var used = new HashSet<string>(siblings.Select(s => s.Colour).Where(c => c.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        string Next()
        {
            var free = palette.FirstOrDefault(c => !used.Contains(c)) ?? palette[used.Count % palette.Count];
            used.Add(free);
            return free;
        }

        foreach (var sibling in siblings.Where(s => s.Colour.Length == 0))
        {
            sibling.Colour = Next();
            RefreshTab(sibling);
        }

        return definition.TabColor.Length > 0 && !used.Contains(definition.TabColor)
            ? definition.TabColor
            : Next();
    }

    private void RefreshTab(LiveTab live)
    {
        SetTabHeader(live.Tab, live.Definition, live.Title, live.Colour, live.View);
        // What the shell calls itself (often user@host: path) goes in the tooltip, masked like the rest.
        live.Tab.ToolTip = live.ShellTitle.Length > 0 ? Privacy.Mask(live.ShellTitle, live.Definition) : null;
        live.View.RefreshPrivacy();
        RefreshLiveRow(live);
        if (live.PoppedOut is { } window)
        {
            window.Title = Privacy.MaskName(live.Title, live.Definition) + " - BaudHoundTerm";
        }
    }

    private void RefreshLiveRow(LiveTab live)
    {
        if (_liveRows.TryGetValue(live, out var row))
        {
            row.Header = LiveRowHeader(live);
            MarkFocusedRow();
        }
    }

    private static readonly Brush FocusedRowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0x5A, 0x80)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The session in focus: a popped-out window that has the keyboard, else the selected tab.
    /// With several tabs on one host the list rows read alike, so this one is marked.
    /// </summary>
    private LiveTab? FocusedLive =>
        _live.FirstOrDefault(l => l.PoppedOut is { IsActive: true })
        ?? ((ActiveTabs.SelectedItem as TabItem) is { } tab ? LiveFor(tab) : null);

    /// <summary>Highlights the focused session's row in the list and clears the others.</summary>
    private void MarkFocusedRow()
    {
        var focused = FocusedLive;
        foreach (var (live, row) in _liveRows)
        {
            if (row.Header is not Panel header)
            {
                continue;
            }

            var on = ReferenceEquals(live, focused);
            header.Background = on ? FocusedRowBrush : null;
            if (header.Children.OfType<TextBlock>().FirstOrDefault() is { } title)
            {
                title.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    private static StackPanel LiveRowHeader(LiveTab live)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(ColourStripe(live.Colour, hollow: true));
        panel.Children.Add(new TextBlock { Text = ShortTitle(live.ShellTitle.Length > 0 ? Privacy.Mask(live.ShellTitle, live.Definition) : Privacy.MaskName(live.Title, live.Definition)), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = live.State switch
            {
                SessionState.Connected => "",
                SessionState.Connecting => "connecting",
                SessionState.Reconnecting => "reconnecting",
                SessionState.Failed => "failed",
                _ => "disconnected"
            },
            Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x8A, 0xA3)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (LiveRowRecMark(live.View) is { } rec)
        {
            panel.Children.Add(rec);
        }

        return panel;
    }

    /// <summary>
    /// A shell's title is often the full path of its executable, which says nothing in a
    /// 260-pixel sidebar that the badge does not: keep the last segment, and cap the rest.
    /// </summary>
    private static string ShortTitle(string title)
    {
        var t = title.Trim();
        var cut = t.LastIndexOfAny(new[] { '\\', '/' });
        // Only an executable's path: "user@server: ~/work" is a directory worth keeping whole.
        if (cut >= 0 && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            t = t[(cut + 1)..];
        }

        return t.Length > 40 ? t[..39] + "…" : t;
    }

    /// <summary>The tab's colour as a stripe. With no colour, an outline on the list (so the
    /// row still reads as an open connection) and nothing on the tab.</summary>
    private static FrameworkElement ColourStripe(string colour, bool hollow)
    {
        Brush? brush = null;
        if (!string.IsNullOrWhiteSpace(colour))
        {
            try
            {
                brush = (Brush)new BrushConverter().ConvertFromString(colour)!;
            }
            catch (FormatException)
            {
                // A colour the user mistyped should not cost them the tab.
            }
        }

        return new System.Windows.Shapes.Rectangle
        {
            Width = 4,
            Height = 14,
            RadiusX = 2,
            RadiusY = 2,
            Fill = brush ?? Brushes.Transparent,
            Stroke = brush is null && hollow ? new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x7A)) : null,
            StrokeThickness = 1,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// The open tabs a saved session should carry in the list: its own, plus quick connections
    /// that went to exactly the same place, which belong to this shortcut in all but name.
    /// </summary>
    private IEnumerable<LiveTab> LiveFor(SessionDefinition session) => _live.Where(l =>
        l.Definition.Id == session.Id ||
        (!_sessions.Any(s => s.Id == l.Definition.Id) && SameEndpoint(l.Definition, session)));

    private static bool SameEndpoint(SessionDefinition a, SessionDefinition b) =>
        a.Protocol == b.Protocol && a.Protocol switch
        {
            SessionProtocol.Serial => string.Equals(a.SerialPort, b.SerialPort, StringComparison.OrdinalIgnoreCase),
            SessionProtocol.LocalShell => false,
            _ => SessionPresentation.HostKey(a) is { } k && k == SessionPresentation.HostKey(b)
                 && SessionPresentation.EffectivePort(a) == SessionPresentation.EffectivePort(b)
        };

    private TreeViewItem WithLiveRows(TreeViewItem row, SessionDefinition session, HashSet<LiveTab> placed)
    {
        // A quick connection matching several shortcuts goes under the first only: a second row
        // replaced the first in _liveRows, and the orphaned one kept saying "connecting".
        foreach (var live in LiveFor(session).Where(l => l.Definition.Id == session.Id || !placed.Contains(l)))
        {
            var child = new TreeViewItem { Header = LiveRowHeader(live), Tag = live };
            _liveRows[live] = child;
            placed.Add(live);
            row.Items.Add(child);
        }

        row.ItemContainerStyle = SessionTree.ItemContainerStyle;
        row.IsExpanded = true;
        return row;
    }

    /// <summary>Clicking an open connection in the list brings its tab forward.</summary>
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        UpdateSelectionCommands();
        if ((e.NewValue as TreeViewItem)?.Tag is LiveTab live && AllTabItems.Contains(live.Tab))
        {
            SelectTab(live.Tab);
        }
    }

    /// <summary>Switching tabs puts the keyboard back on the terminal, not on whatever was
    /// clicked to get here.</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl side || (side != Tabs && side != TabsRight))
        {
            return;   // a ComboBox inside a tab, not a tab strip
        }

        // Picking a tab on a side makes that side the active one.
        if (side.Items.Count > 0)
        {
            SetActiveSide(side);
        }

        // A popped-out tab's view is in its own window; focusing it would pull that window forward.
        if ((side.SelectedItem as TabItem) is { } tab && LiveFor(tab)?.PoppedOut is null)
        {
            ViewOf(tab)?.FocusTerminal();
        }

        UpdateConnectionToggle();
        MarkFocusedRow();
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
                RefreshOpenTabsOf(definition);
                PersistIfSaved(definition);
            }
        };
        menu.Items.Add(properties);

        if (definition.Protocol == SessionProtocol.Vnc)
        {
            var cad = new MenuItem { Header = "Send Ctrl+Alt+_Del", ToolTip = "Ctrl+Alt+Del on the remote desktop, which Windows keeps for itself if you press it here" };
            cad.Click += (_, _) => ViewOf(tab)?.SendCtrlAltDel();
            menu.Items.Add(new Separator());
            menu.Items.Add(cad);
        }

        if (definition is { Protocol: SessionProtocol.Ssh, Tmux: true })
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(TmuxSessionsMenu(definition));
        }

        if (ViewOf(tab) is { Tmux: TmuxState.Live } tmuxView)
        {
            var end = new MenuItem
            {
                Header = "End the tmux _session on the host",
                ToolTip = "Kills the tmux session and everything running in it, on the host. Closing the tab only leaves it running."
            };
            end.Click += async (_, _) => await EndTmuxAsync(tmuxView, definition);
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateShortcutItem(tmuxView, definition));
            menu.Items.Add(end);
        }

        if (ViewOf(tab) is { CanAttachAgent: true })
        {
            var agent = new MenuItem { Header = "_Agent on this tab" };
            agent.Click += (_, _) =>
            {
                if (ViewOf(tab) is { } v)
                {
                    ToggleAgent(v);
                }
            };
            menu.Items.Add(new Separator());
            menu.Items.Add(agent);
        }

        if (ViewOf(tab) is { } recView)
        {
            menu.Items.Add(new Separator());
            if (!recView.IsRecording)
            {
                var rec = new MenuItem { Header = "_Record" };
                foreach (var item in RecordStartItems(recView))
                {
                    rec.Items.Add(item);
                }

                menu.Items.Add(rec);
            }
            else
            {
                foreach (var item in RecordRunningItems(recView))
                {
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(RecordingsFolderItem());
        }

        var popOut = new MenuItem();
        popOut.Click += (_, _) =>
        {
            if (LiveFor(tab) is { } live)
            {
                TogglePopOut(live);
            }
        };
        var side = new MenuItem { InputGestureText = "Ctrl+Shift+\\" };
        side.Click += (_, _) => MoveToOtherSide(tab);
        menu.Opened += (_, _) =>
        {
            popOut.Header = LiveFor(tab)?.PoppedOut is null ? "_Pop out" : "_Bring back";
            side.Header = SideOf(tab) == TabsRight ? "Move to the _left side" : "Move to the _right side";
            side.IsEnabled = AllTabItems.Count() > 1;
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(popOut);
        menu.Items.Add(side);

        return menu;
    }

    /// <summary>
    /// Ends the tmux session on the host after asking. Everything running in it goes with it,
    /// and that cannot be undone from here, so the name of the session is in the question.
    /// </summary>
    private async Task EndTmuxAsync(TerminalView view, SessionDefinition definition)
    {
        var name = SessionController.TmuxName(string.IsNullOrWhiteSpace(view.TmuxSessionName) ? definition.TmuxSession : view.TmuxSessionName);
        var answer = MessageBox.Show(this,
            $"End the tmux session \"{name}\" on {definition.Host}?\n\n" +
            "Everything running in it stops, and this connection ends with it. " +
            "Closing the tab instead leaves the session running on the host.",
            "End tmux session", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (await view.EndTmuxAsync() is { } why)
        {
            MessageBox.Show(this, $"The tmux session was not ended: {why}", "End tmux session",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTabColour(TabItem tab, SessionDefinition definition, string colour)
    {
        definition.TabColor = colour.Trim();

        if (LiveFor(tab) is { } live)
        {
            live.Colour = definition.TabColor;   // the title stays whatever the far end last set
            RefreshTab(live);
        }

        PersistIfSaved(definition);
    }

    /// <summary>
    /// A saved shortcut goes to the store, a Shell menu tab to its own file; a quick-connect
    /// session has nothing to write to.
    /// </summary>
    private void PersistIfSaved(SessionDefinition definition)
    {
        if (_sessions.Any(s => s.Id == definition.Id))
        {
            Save();
        }
        else if (_shellTabs.TryGetValue(definition, out var shell))
        {
            // One entry per shell, shared by its normal and administrator tabs. An administrator
            // tab writes back only the look (CopyLook), so neither its red tab nor its empty
            // login steps and shortcuts reach the normal tab's entry.
            SessionDefinition copy;
            if (shell.Elevated)
            {
                copy = LocalShellStore.Load(shell.Key) ?? new SessionDefinition { Id = definition.Id };
                CopyLook(definition, copy);
            }
            else
            {
                copy = definition.Clone(definition.Name);
                copy.Id = definition.Id;
            }

            try
            {
                LocalShellStore.Save(shell.Key, copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, "Could not save this shell's settings: " + ex.Message, "BaudHoundTerm",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// A coloured stripe beside the tab title. The stripe is part of the header content rather
    /// than TabItem.Background, which the default template paints over when the tab is selected.
    /// </summary>
    private void SetTabHeader(TabItem tab, SessionDefinition definition, string title, string colour, TerminalView view)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };   // hit-testable, for the record dot on hover

        if (!string.IsNullOrWhiteSpace(colour))
        {
            panel.Children.Add(ColourStripe(colour, hollow: false));
        }

        // Serial and telnet tabs carry a state dot that blinks with traffic. The view owns it, so
        // it leaves the previous header (rebuilt on every rename) before joining this one.
        if (view.TabLamp is { } lamp)
        {
            (lamp.Parent as Panel)?.Children.Remove(lamp);
            panel.Children.Add(lamp);
        }

        // Two tabs on one host differ only by protocol; the badge says which is which.
        panel.Children.Add(SessionPresentation.BadgeElement(definition));

        // Whether the shell is really being kept in tmux, which decides what closing the tab costs.
        if (SessionPresentation.TmuxChip(view.Tmux) is { } tmux)
        {
            panel.Children.Add(tmux);
        }

        if (SessionPresentation.ProgramChip(definition, view.TmuxSessionName) is { } program)
        {
            panel.Children.Add(program);
        }

        var text = new TextBlock { Text = Privacy.MaskName(title, definition), VerticalAlignment = VerticalAlignment.Center };

        // The app-wide TextBlock style would otherwise pin one colour here and the tab would
        // not brighten when selected or dim when it is not.
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(TabItem), 1)
        });

        panel.Children.Add(text);
        panel.Children.Add(RecordControls(panel, view));

        if (view.CanAttachAgent)
        {
            panel.Children.Add(AgentToggle(tab, view));
        }

        panel.Children.Add(TabCloseButton(tab));

        tab.Header = panel;
    }

    /// <summary>
    /// The bot on the tab: dim when no agent is attached, lit (and outlined) when one is and can
    /// type into the session. Clicking it attaches or detaches the agent for this tab only.
    /// </summary>
    /// <summary>The × on a tab that closes it, like every browser's. Closing still disconnects
    /// and tidies up through the one path (CloseTabAsync), so the menu and the × behave alike.</summary>
    private Button TabCloseButton(TabItem tab)
    {
        var grey = Color.FromRgb(0x8B, 0x94, 0xA7);
        var button = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 11, Foreground = new SolidColorBrush(grey) },
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 0,
            MinHeight = 0,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Focusable = false,   // closing must not pull the keyboard off the terminal first
            ToolTip = "Close this tab. A connected session ends."
        };

        System.Windows.Automation.AutomationProperties.SetName(button, "Close this tab");

        button.Click += async (_, e) =>
        {
            e.Handled = true;
            await CloseTabAsync(tab);
        };

        return button;
    }

    private static Button AgentToggle(TabItem tab, TerminalView view)
    {
        var on = view.IsAgentAttached;
        var tripped = on && view.AgentTripped;
        var colour = tripped ? Color.FromRgb(0xE5, 0xC0, 0x7B) : on ? Color.FromRgb(0x56, 0xB6, 0xC2) : Color.FromRgb(0x6B, 0x75, 0x8A);

        // Tripped: amber with a warning sign, so it shows from any tab, the agent pane hidden or not.
        object content = SessionPresentation.BotIcon(14, new SolidColorBrush(colour));
        if (tripped)
        {
            var both = new StackPanel { Orientation = Orientation.Horizontal };
            both.Children.Add((UIElement)content);
            both.Children.Add(new TextBlock { Text = "⚠", Foreground = new SolidColorBrush(colour), FontWeight = FontWeights.Bold,
                                              Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            content = both;
        }

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(3, 1, 3, 1),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 0,
            MinHeight = 0,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Background = on ? new SolidColorBrush(Color.FromArgb(0x33, 0x56, 0xB6, 0xC2)) : Brushes.Transparent,
            BorderBrush = on ? new SolidColorBrush(colour) : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Focusable = false,   // a click here must not steal the keyboard from the terminal
            ToolTip = tripped
                ? "The agent read text that tried to give it instructions. Every action it takes now waits for your yes; " +
                  "New in the agent pane starts a clean conversation. Click to detach it."
                : on
                ? "An agent is attached and can type into this session. Click to detach it."
                : "Attach an agent to this session (Ctrl+Shift+A)"
        };

        System.Windows.Automation.AutomationProperties.SetName(button, on ? "Detach the agent from this tab" : "Attach an agent to this tab");

        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (tab.Parent is TabControl tabs)
            {
                tabs.SelectedItem = tab;
            }

            ToggleAgent(view);
        };

        return button;
    }

    // ---------- terminal commands ----------

    private void OnFind(object sender, RoutedEventArgs e) => ActiveView?.Find();

    private void OnClear(object sender, RoutedEventArgs e) => ActiveView?.ClearScreen();

    private void OnToggleAgent(object sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view)
        {
            ToggleAgent(view);
        }
    }

    /// <summary>Sharing is per tab: the agent docks into this one and can touch nothing else.</summary>
    private static void ToggleAgent(TerminalView view)
    {
        if (view.IsAgentAttached)
        {
            view.DetachAgent();
        }
        else
        {
            view.AttachAgent();
        }
    }

    private void OnKeePassXc(object sender, RoutedEventArgs e) =>
        new KeePassXcWindow(_vault) { Owner = this }.ShowDialog();

    /// <summary>Help > Features > Agent. Off closes every open agent and takes away its way back in.</summary>
    private void OnAgentFeature(object sender, RoutedEventArgs e)
    {
        _ui.AgentEnabled = AgentFeatureMenu.IsChecked;
        SaveUi();
        TerminalView.AgentsAllowed = _ui.AgentEnabled;
        foreach (var live in _live)
        {
            if (!_ui.AgentEnabled && live.View.IsAgentAttached)
            {
                live.View.DetachAgent();
            }

            live.Tab.ContextMenu = BuildTabMenu(live.Tab, live.Definition);
            RefreshTab(live);
        }

        UpdateConnectionToggle();
    }

    private void OnAgentSettings(object sender, RoutedEventArgs e) =>
        new AgentSettingsWindow(_vault) { Owner = this }.ShowDialog();

    /// <summary>One button: Disconnect a live tab (it stays open with its scrollback), or
    /// Reconnect a dead one in place.</summary>
    private async void OnToggleConnection(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view)
        {
            return;
        }

        if (view.IsConnected)
        {
            await view.DisconnectAsync();
        }
        else
        {
            await view.ReconnectAsync();
            view.FocusTerminal();
        }

        UpdateConnectionToggle();
    }

    private void UpdateConnectionToggle()
    {
        var view = ActiveView;
        var reconnect = view is not null && !view.IsConnected;

        ConnToggle.Content = reconnect ? "Reconnect" : "Disconnect";
        ConnToggle.ToolTip = reconnect
            ? "Connect the active tab again, keeping its scrollback (or press Enter in it)"
            : "Disconnect the active tab, leaving it open";
        ConnToggleMenu.Header = reconnect ? "_Reconnect tab" : "_Disconnect tab";

        // Dimmed with no tab: the buttons' labels follow their Foreground since the button
        // template stopped the app-wide TextBlock style from pinning it.
        var tab = view is not null;
        foreach (var control in new UIElement[] { ConnToggle, CloseButton, FindButton, ClearButton, FilesButton,
                                                  ConnToggleMenu, CloseTabMenu, FindMenu, ClearMenu })
        {
            control.IsEnabled = tab;
        }

        var popped = (ActiveTabs.SelectedItem as TabItem) is { } selected && LiveFor(selected)?.PoppedOut is not null;
        PopOutMenu.Header = popped ? "_Bring tab back" : "_Pop out tab";
        PopOutMenu.IsEnabled = tab;

        AgentMenu.IsEnabled = view is { CanAttachAgent: true };
        AgentSeparator.Visibility = AgentMenu.Visibility = AgentSettingsMenu.Visibility =
            _ui.AgentEnabled ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = Tabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoveSideMenu.IsEnabled = tab && AllTabItems.Count() > 1;
        MoveSideMenu.Header = ActiveTabs == TabsRight ? "_Move tab to the left side" : "_Move tab to the right side";
        SwitchSideMenu.IsEnabled = IsSplit;
    }

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (ActiveTabs.SelectedItem is TabItem tab)
        {
            await CloseTabAsync(tab);
        }
    }

    /// <summary>Closes one tab, whichever it is: the X on its header and the menus all come here.</summary>
    private async Task CloseTabAsync(TabItem tab)
    {
        if (ViewOf(tab) is { } view)
        {
            view.DetachAgent();
            await view.DisconnectAsync();

            // While its window is still up: a popped-out tab's recording would otherwise end on
            // its last frame held for as long as the disconnect took.
            await view.FinishRecordingAsync();

            // A quick-connect session was never saved, so its stored password has nothing to
            // belong to once the tab is gone. A Shell menu tab is not one: its login steps
            // persist in local-shells.json and need their secret next time.
            var shellTab = _shellTabs.Remove(view.Definition);
            if (!shellTab && !_sessions.Any(s => s.Id == view.Definition.Id))
            {
                _vault.Remove(view.Definition.Id);
            }
        }

        if (LiveFor(tab)?.PoppedOut is { } window)
        {
            window.Returned = true;
            window.Content = null;
            window.Close();
        }

        SideOf(tab).Items.Remove(tab);
        LayoutSides();
        UpdateConnectionToggle();
        ViewOf(tab)?.Release();

        if (LiveFor(tab) is { } live)
        {
            _live.Remove(live);
            RebuildTree();
        }
    }

    // ---------- quick connect ----------

    /// <summary>Speeds worth offering, including the odd ones BaudHound scans for.</summary>
    private static readonly string[] BaudRates =
    {
        "9600", "14400", "19200", "28800", "38400", "57600", "74880",
        "115200", "230400", "460800", "921600"
    };

    /// <summary>Serial needs a port and a speed, not a string to memorise.</summary>
    /// <summary>
    /// The quick-connect protocol, read from the item's Tag: the list is not in enum order (it
    /// has RDP but no camera), so its position cannot be cast.
    /// </summary>
    private SessionProtocol QuickProtocolValue =>
        QuickProtocol.SelectedItem is ComboBoxItem { Tag: string name } && Enum.TryParse<SessionProtocol>(name, out var p)
            ? p
            : SessionProtocol.Ssh;

    private void OnQuickProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuickSerialPanel is null)
        {
            return;
        }

        var serial = QuickProtocolValue == SessionProtocol.Serial;
        QuickSerialPanel.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        QuickAddress.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;

        if (serial)
        {
            FillBaudRates(QuickBaud, "115200");
            FillSerialPorts(QuickSerialPort);
        }
    }

    /// <summary>
    /// Ports come and go with USB adapters, so the list is rebuilt whenever it is opened
    /// rather than once at startup.
    /// </summary>
    private void OnSerialPortsDropDown(object sender, EventArgs e)
    {
        if (sender is ComboBox box)
        {
            FillSerialPorts(box);
        }
    }

    private static void FillSerialPorts(ComboBox box)
    {
        var typed = box.Text;
        box.Items.Clear();

        foreach (var port in System.IO.Ports.SerialPort.GetPortNames()
                     .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            box.Items.Add(port);
        }

        // Keep whatever was typed, even when the adapter is unplugged at the moment.
        if (!string.IsNullOrWhiteSpace(typed))
        {
            box.SelectedItem = box.Items.Cast<string>().FirstOrDefault(p => p == typed);
            box.Text = typed;
        }
        else if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static void FillBaudRates(ComboBox box, string fallback)
    {
        if (box.Items.Count > 0)
        {
            return;
        }

        var wanted = string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text;

        foreach (var rate in BaudRates)
        {
            box.Items.Add(rate);
        }

        // Text on an editable ComboBox is dropped while the control is still collapsed and its
        // template unrealised. SelectedItem is a logical property and survives, and it sets the
        // text for us once the box is shown.
        box.SelectedItem = box.Items.Cast<string>().FirstOrDefault(r => r == wanted) ?? BaudRates[7];
    }

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
        else if (SessionTree.SelectedItem is TreeViewItem selected)
        {
            selected.IsSelected = false;   // empty space: the menu is about the list, not the last row
        }

        SessionTree.ContextMenu = BuildTreeMenu((source as TreeViewItem)?.Tag);
    }

    /// <summary>
    /// What the right-click offers depends on what was clicked: a session gets session actions,
    /// a folder folder actions, a host its protocols, an open connection its tab, and empty space
    /// the ways to add something. Nothing offers an action on a thing that was not clicked.
    /// </summary>
    private ContextMenu BuildTreeMenu(object? tag)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE))
        };

        MenuItem Item(string header, Action act)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => act();
            return item;
        }

        MenuItem AddProtocolMenu()
        {
            var add = new MenuItem { Header = "_Add protocol for this host" };
            foreach (var (label, protocol) in new[] { ("SSH...", "Ssh"), ("RDP...", "Rdp"), ("VNC...", "Vnc"), ("Telnet...", "Telnet"), ("RTSP camera...", "Rtsp"), ("Web page...", "Web") })
            {
                var item = new MenuItem { Header = label, Tag = protocol };
                item.Click += OnAddProtocol;
                add.Items.Add(item);
            }

            return add;
        }

        var none = new RoutedEventArgs();

        switch (tag)
        {
            case LiveTab { View.Tmux: TmuxState.Live } live:
                menu.Items.Add(CreateShortcutItem(live.View, live.Definition));
                break;

            case SessionDefinition session:
                menu.Items.Add(Item("_Connect", () => Connect(session)));
                if (session is { Protocol: SessionProtocol.Ssh, Tmux: true })
                {
                    menu.Items.Add(TmuxSessionsMenu(session));
                }

                menu.Items.Add(new Separator());
                menu.Items.Add(Item("_Properties...", () => OnEdit(this, none)));
                menu.Items.Add(Item("_Move to folder...", () => OnMoveToFolder(this, none)));
                // Not on a KVM: its host is the board, so "another protocol for this host" would
                // point at the board rather than the machine behind it.
                if (session.Protocol != SessionProtocol.Kvm && SessionPresentation.HostKey(session) is not null)
                {
                    menu.Items.Add(AddProtocolMenu());
                }

                menu.Items.Add(new Separator());
                menu.Items.Add(Item("_Delete shortcut", () => OnDelete(this, none)));
                break;

            case HostNode host:
                var connect = new MenuItem { Header = "_Connect" };
                foreach (var session in host.Sessions.OrderBy(s => s.Protocol).ThenBy(SessionPresentation.EffectivePort))
                {
                    var (badge, _) = SessionPresentation.Badge(session);
                    var target = session;
                    connect.Items.Add(Item($"{badge}  {SessionPresentation.Target(session, hostShown: true)}",
                        () => Connect(target)));
                }

                menu.Items.Add(connect);
                if (host.Sessions.Any(m => m.Protocol != SessionProtocol.Kvm))
                {
                    menu.Items.Add(AddProtocolMenu());
                }
                break;

            case FolderNode folder:
                menu.Items.Add(Item("New _shortcut here...", () => NewSessionIn(folder.Path)));
                menu.Items.Add(Item("New _folder inside...", () => OnNewFolder(this, none)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("_Rename folder...", () => OnRenameFolder(this, none)));
                menu.Items.Add(Item("_Delete folder...", () => OnDeleteFolder(this, none)));
                break;

            case LiveTab live:
                menu.Items.Add(Item("_Show tab", () =>
                {
                    SelectTab(live.Tab);
                    live.PoppedOut?.Activate();
                }));
                if (live.View.CanAttachAgent)
                {
                    menu.Items.Add(Item(live.View.IsAgentAttached ? "Detach _agent" : "Attach _agent...", () =>
                    {
                        SelectTab(live.Tab);
                        ToggleAgent(live.View);
                    }));
                }

                menu.Items.Add(Item(live.View.IsConnected ? "_Disconnect" : "_Reconnect", async () =>
                {
                    SelectTab(live.Tab);
                    if (live.View.IsConnected)
                    {
                        await live.View.DisconnectAsync();
                    }
                    else
                    {
                        await live.View.ReconnectAsync();
                    }

                    UpdateConnectionToggle();
                }));
                if (live.View.Tmux == TmuxState.Live)
                {
                    menu.Items.Add(Item("End the tmux _session on the host", async () =>
                        await EndTmuxAsync(live.View, live.Definition)));
                }

                menu.Items.Add(Item("Close _tab", () =>
                {
                    SelectTab(live.Tab);
                    OnCloseTab(this, none);
                }));
                break;

            default:
                menu.Items.Add(Item("New _shortcut...", () => OnNewSession(this, none)));
                menu.Items.Add(Item("New _folder...", () => OnNewFolder(this, none)));
                break;
        }

        return menu;
    }

    private void OnQuickAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Handled, or the Connect button (IsDefault) fired as well: two tabs per Enter.
            e.Handled = true;
            OnQuickConnect(sender, e);
        }
    }

    /// <summary>
    /// Connects to whatever was typed without creating a saved session. Accepts
    /// "user@host:port" for SSH, "COM3@115200" for serial, or a command line for a local shell.
    /// </summary>
    private void OnQuickConnect(object sender, RoutedEventArgs e)
    {
        var protocol = QuickProtocolValue;

        if (protocol == SessionProtocol.Serial)
        {
            ConnectSerialFromQuickBar();
            return;
        }

        var address = QuickAddress.Text.Trim();
        if (string.IsNullOrEmpty(address))
        {
            QuickAddress.Focus();
            return;
        }
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

                // user@host: the user name fills the prompt. The last @ splits, so a user name may contain one.
                var givenUser = Environment.UserName;
                var at = rest.LastIndexOf('@');
                if (at > 0)
                {
                    givenUser = rest[..at];
                    rest = rest[(at + 1)..];
                }

                definition.Host = rest;

                // Who is connecting is asked here, once, before a tab exists: a cancelled prompt
                // should not leave one behind.
                var credentials = new CredentialPromptWindow(
                    $"{definition.Host}:{definition.Port}", givenUser) { Owner = this };

                if (credentials.ShowDialog() != true)
                {
                    return;
                }

                definition.Username = credentials.Username;
                definition.Name = $"{definition.Username}@{definition.Host}";
                _vault.Set(definition.Id, credentials.Password);
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

            case SessionProtocol.Rdp:
                var desk = address;
                var mark3 = desk.LastIndexOf(':');
                definition.Port = 3389;
                if (mark3 > 0 && int.TryParse(desk[(mark3 + 1)..], out var rdpPort))
                {
                    definition.Port = rdpPort;
                    desk = desk[..mark3];
                }

                // user@host: the user name is used; without one, the tab asks.
                var at3 = desk.LastIndexOf('@');
                if (at3 > 0)
                {
                    definition.Username = desk[..at3];
                    desk = desk[(at3 + 1)..];
                }

                definition.Host = desk;
                definition.Name = definition.Port == 3389 ? definition.Host : $"{definition.Host}:{definition.Port}";
                break;

            case SessionProtocol.Vnc:
                var screen = address;
                var colon2 = screen.LastIndexOf(':');

                if (colon2 > 0 && int.TryParse(screen[(colon2 + 1)..], out var vncPort))
                {
                    // ":1" is the display number, not a port, the way every VNC tool writes it.
                    definition.Port = vncPort < 100 ? 5900 + vncPort : vncPort;
                    screen = screen[..colon2];
                }
                else
                {
                    definition.Port = 5900;
                }

                definition.Host = screen;
                definition.Name = $"{definition.Host}:{definition.Port}";
                break;

            default:
                definition.ShellCommand = address;
                break;
        }

        Connect(definition);
    }

    private void ConnectSerialFromQuickBar()
    {
        var port = QuickSerialPort.Text.Trim();
        if (port.Length == 0)
        {
            QuickSerialPort.Focus();
            return;
        }

        var definition = new SessionDefinition
        {
            Protocol = SessionProtocol.Serial,
            SerialPort = port,
            BaudRate = int.TryParse(QuickBaud.Text.Trim(), out var rate) ? rate : 115200
        };

        // "8-N-1" and friends: data bits, parity, stop bits.
        var framing = (QuickFraming.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "8-N-1";
        var fields = framing.Split('-');
        if (fields.Length == 3)
        {
            definition.DataBits = int.TryParse(fields[0], out var bits) ? bits : 8;
            definition.Parity = fields[1] switch { "E" => "Even", "O" => "Odd", _ => "None" };
            definition.StopBits = fields[2] == "2" ? "Two" : "One";
        }

        definition.Name = $"{port} @ {definition.BaudRate}";
        Connect(definition);
    }

    /// <summary>
    /// Find and connect to a BaudHound wireless serial console. It serves the UART it is clipped
    /// to on telnet port 23, at 192.168.4.1 on its own access point or at its LAN address once
    /// joined to a network; firmware 1.4 and later also answer a discovery broadcast.
    /// </summary>
    private void OnBaudHound(object sender, RoutedEventArgs e)
    {
        new BaudHoundWindow(Connect, SaveBaudHound) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// A device keeps its name (BaudHound-XXXX) but not its DHCP address, so saving one that is
    /// already a shortcut moves that shortcut to the new address rather than adding a second.
    /// </summary>
    private void SaveBaudHound(SessionDefinition found)
    {
        var existing = _sessions.FirstOrDefault(s =>
            s.Protocol == SessionProtocol.Telnet
            && string.Equals(s.Name, found.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _sessions.Add(found);
        }
        else
        {
            existing.Host = found.Host;
            existing.Port = found.Port;
            existing.UseTls = found.UseTls;
            existing.LineMode = true;
            RefreshOpenTabsOf(existing);
        }

        Save();
    }

    // ---------- quick shells ----------

    // Tabs opened from the Shell menu, by definition id: which shell, and whether elevated.
    // Keyed by the tab's own definition object: the normal and administrator tabs of one shell
    // share the stored Id, and keyed by Id the second opened overwrote the first's Elevated flag.
    private readonly Dictionary<SessionDefinition, (string Key, bool Elevated)> _shellTabs =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What an administrator shell takes from, and gives back to, local-shells.json: how the tab
    /// looks, nothing that runs. The file is writable by any program running as the user, so
    /// login steps or command shortcuts planted there must never reach a high-integrity shell.
    /// </summary>
    private static void CopyLook(SessionDefinition from, SessionDefinition to)
    {
        to.FontFamily = from.FontFamily;
        to.FontSize = from.FontSize;
        to.ScrollbackLines = from.ScrollbackLines;
        to.WheelSendsArrows = from.WheelSendsArrows;
        to.Highlights = from.Highlights.Select(h => h.Copy()).ToList();
    }

    /// <summary>
    /// The shells the Local and Admin buttons offer. A fixed list on purpose: the elevated path
    /// passes the key on our own command line, and accepting an arbitrary executable there would
    /// turn our UAC prompt into a way for any caller to elevate anything.
    /// </summary>
    private static readonly (string Key, string Label, string Command)[] Shells =
    [
        // Absolute paths: CreateProcess searches the app's own folder before System32, so a bare
        // "cmd.exe" planted next to BaudHoundTerm would run elevated from the Admin entries.
        ("powershell", "Windows PowerShell", System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
        ("cmd",        "Command Prompt",     System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe")),
        ("pwsh",       "PowerShell 7",       System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"))
    ];

    /// <summary>
    /// A throwaway local shell tab, as you or as administrator; nothing is written to the session
    /// store. The list drops under the button. PowerShell 7 only appears when it is actually
    /// installed, since it ships with nothing and is a separate download.
    /// </summary>
    private void OnShellMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var present = Shells.Where(s => IsShellPresent(s.Command)).ToList();

        foreach (var elevated in new[] { false, true })
        {
            if (elevated)
            {
                menu.Items.Add(new Separator());
            }

            foreach (var (key, label, _) in present)
            {
                var item = new MenuItem { Header = elevated ? $"{label} (administrator)" : label };
                var chosen = key;
                var admin = elevated;
                item.Click += (_, _) => LaunchShell(chosen, admin);
                if (elevated && !IsElevated && Elevation.UnsafeInstall is { } where)
                {
                    item.IsEnabled = false;
                    item.ToolTip = "Not from this folder: programs without administrator rights can change\n" + where +
                                   "\nMove BaudHoundTerm into C:\\Program Files to open shells as administrator.";
                    ToolTipService.SetShowOnDisabled(item, true);
                }

                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Opens one of <see cref="Shells"/>, elevated or not. A medium-integrity process cannot
    /// attach a pseudoconsole to a high-integrity child, so when elevation is asked for and we do
    /// not have it, the only honest option is to relaunch through UAC and open the shell there.
    /// </summary>
    private void LaunchShell(string key, bool elevated)
    {
        var shell = Shells.FirstOrDefault(s => s.Key == key);
        if (shell.Command is null)
        {
            return;
        }

        if (!elevated || IsElevated)
        {
            // Settings kept from an earlier run (commands, font...); what the shell is stays fixed.
            // An administrator tab takes only the look: the file is user-writable (see CopyLook).
            var stored = LocalShellStore.Load(shell.Key);
            var definition = stored is null ? new SessionDefinition() : elevated ? new SessionDefinition { Id = stored.Id } : stored;
            if (elevated && stored is not null)
            {
                CopyLook(stored, definition);
            }

            definition.Name = elevated ? $"Administrator: {shell.Label}" : shell.Label;
            definition.Folder = "";
            definition.Protocol = SessionProtocol.LocalShell;
            definition.ShellCommand = shell.Command.Contains(' ') ? $"\"{shell.Command}\"" : shell.Command;
            if (elevated)
            {
                definition.TabColor = "#e06c75";
            }

            _shellTabs[definition] = (shell.Key, elevated);
            Connect(definition);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"An elevated {shell.Label} cannot run inside this window, because BaudHoundTerm is " +
            "not itself elevated.\n\n" +
            "Relaunch BaudHoundTerm as administrator and open the shell there?\n" +
            "Open sessions in this window stay open.",
            "BaudHoundTerm", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (Elevation.UnsafeInstall is { } unsafeFolder)
        {
            MessageBox.Show(this, "BaudHoundTerm does not run as administrator from a folder that programs without " +
                                  $"administrator rights can change:\n\n{unsafeFolder}", "BaudHoundTerm",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "BaudHoundTerm.exe",
                Arguments = $"{AdminShellSwitch} {shell.Key}",
                UseShellExecute = true,
                Verb = "runas"           // triggers the UAC prompt
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user dismissed the UAC prompt. That is an answer, not a failure to report.
        }
    }

    private static bool IsShellPresent(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command);
        }

        // cmd and powershell.exe live in System32, which is always on PATH; pwsh.exe may not be
        // installed at all.
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir =>
            {
                try
                {
                    return File.Exists(Path.Combine(dir.Trim(), command));
                }
                catch (ArgumentException)
                {
                    return false;   // a malformed PATH entry is not a reason to fail the menu
                }
            });
    }

    /// <summary>
    /// Reads the shell key that the elevated relaunch was asked for. Unrecognised values fall back
    /// to PowerShell rather than being run, which is what keeps the switch from naming anything.
    /// </summary>
    private static string AdminShellFromCommandLine(string[] args)
    {
        var at = Array.FindIndex(args, a =>
            string.Equals(a, AdminShellSwitch, StringComparison.OrdinalIgnoreCase));

        if (at >= 0 && at + 1 < args.Length)
        {
            var asked = args[at + 1];
            if (Shells.Any(s => string.Equals(s.Key, asked, StringComparison.OrdinalIgnoreCase)))
            {
                return asked.ToLowerInvariant();
            }
        }

        return "powershell";
    }

    internal const string AdminShellSwitch = "--admin-shell";

    private static bool IsElevated => Elevation.IsCurrentProcessElevated;

    // ---------- files ----------

    /// <summary>
    /// The file actions that make sense for the active tab. Each protocol moves files its own
    /// way; an action that does not apply is shown disabled with the reason, not hidden, so the
    /// menu also says what exists.
    /// </summary>
    private List<Control> FileMenuItems()
    {
        var view = ActiveView;
        var protocol = view?.Definition.Protocol;
        var items = new List<Control>();

        MenuItem Item(string header, bool enabled, string tip, Action act)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) => act();
            return item;
        }

        items.Add(Item("_Browse and transfer (SFTP)...", protocol == SessionProtocol.Ssh,
            protocol == SessionProtocol.Ssh ? "Upload, download and browse files on this SSH server"
                                            : "Needs an SSH tab",
            () => OnTransfer(this, new RoutedEventArgs())));

        var vnc = protocol == SessionProtocol.Vnc;
        items.Add(Item("Browse and transfer (_TightVNC)...", vnc,
            vnc ? "Upload, download and browse files on this VNC host. Needs TightVNC Server; others have no compatible file transfer."
                : "Needs a VNC tab",
            async () => await OpenVncFilesAsync(view!.Definition)));

        var terminal = view is not null && protocol is not (SessionProtocol.Vnc or SessionProtocol.Rtsp or SessionProtocol.Rdp or SessionProtocol.Kvm or SessionProtocol.Web);
        items.Add(Item("Send _text file...", terminal && view!.IsConnected && !view.IsSending,
            !terminal ? "Needs a terminal tab"
            : !view!.IsConnected ? "Connect the tab first"
            : "Type a text file into this session, paced so the far end keeps up. Works on serial, telnet, SSH and local shells.",
            async () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Send a text file into " + view!.Definition.Name,
                    Filter = "Text files|*.txt;*.cfg;*.conf;*.ini;*.sh;*.ps1;*.py;*.json;*.yaml;*.yml|All files|*.*"
                };

                if (dialog.ShowDialog(this) == true)
                {
                    view.FocusTerminal();
                    await view.SendTextFileAsync(dialog.FileName);
                }
            }));

        // Binary transfers over the serial line: boot loaders and BaudHound targets.
        var serial = protocol == SessionProtocol.Serial;
        var modemReady = serial && view!.CanModem;
        var modemTip = !serial ? "Needs a serial tab"
            : !view!.IsConnected ? "Connect the tab first"
            : view.IsSending ? "A transfer is already running"
            : "";

        var send = new MenuItem { Header = "Send _binary file", IsEnabled = modemReady, ToolTip = modemTip.Length > 0 ? modemTip : "Start the device's receive first (U-Boot: loady / loadx, Linux: rb / rx), then pick the file" };
        ToolTipService.SetShowOnDisabled(send, true);
        foreach (var (label, proto, multi) in new[]
                 {
                     ("_YMODEM (name and size travel with it; several files)...", ModemProtocol.Ymodem, true),
                     ("XMODEM-_1K (U-Boot loadx)...", ModemProtocol.Xmodem1K, false),
                     ("_XMODEM 128-byte...", ModemProtocol.Xmodem, false)
                 })
        {
            var p = proto;
            var allowMany = multi;
            send.Items.Add(Item(label, true, "", async () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Send over {label.Replace("_", "").Split(' ')[0]}", Multiselect = allowMany };
                if (dialog.ShowDialog(this) == true)
                {
                    await view!.ModemSendAsync(p, dialog.FileNames);
                }
            }));
        }

        var receive = new MenuItem { Header = "_Receive binary file", IsEnabled = modemReady, ToolTip = modemTip.Length > 0 ? modemTip : "Pick a folder, then start the device's send (Linux: sb / sx, U-Boot: loadb is Kermit and not supported)" };
        ToolTipService.SetShowOnDisabled(receive, true);
        foreach (var (label, proto) in new[] { ("_YMODEM...", ModemProtocol.Ymodem), ("_XMODEM / XMODEM-1K...", ModemProtocol.Xmodem1K) })
        {
            var p = proto;
            receive.Items.Add(Item(label, true, "", async () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save received files in" };
                if (dialog.ShowDialog(this) == true)
                {
                    await view!.ModemReceiveAsync(p, dialog.FolderName);
                }
            }));
        }

        if (!modemReady)
        {
            // The theme's submenu-header template has no disabled look of its own.
            send.Foreground = receive.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x75, 0x8A));
        }

        items.Add(send);
        items.Add(receive);

        items.Add(new Separator());
        var tftpRunning = _tftp?.IsRunning == true;
        items.Add(Item(tftpRunning ? "TFTP _server (firmware) - running" : "TFTP _server (firmware)...", true,
            "Serve a folder over TFTP so routers, switches and boot loaders can pull firmware from this PC",
            OpenTftp));

        return items;
    }

    private TftpWindow? _tftp;

    /// <summary>One TFTP window: a second would only fight the first for port 69.</summary>
    private void OpenTftp()
    {
        if (_tftp is null)
        {
            _tftp = new TftpWindow(_ui) { Owner = this };
            _tftp.Closed += (_, _) => _tftp = null;
            _tftp.Show();
        }
        else
        {
            _tftp.Activate();
        }
    }

    /// <summary>
    /// A second connection to the VNC server, for files only; the screen view is not touched.
    /// </summary>
    private async Task OpenVncFilesAsync(SessionDefinition definition)
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var client = await TightVncFileClient.ConnectAsync(definition.Host, definition.Port, _vault.Get(definition.Id));
            new TransferWindow(client, $"Files - {definition.Name} (TightVNC)") { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "BaudHoundTerm: VNC files", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            Cursor = null;
        }
    }

    // ---------- recording: the Record menu and the tab's menu offer the same items ----------

    private void OnRecordMenuOpened(object sender, RoutedEventArgs e)
    {
        RecordMenu.Items.Clear();
        if (ActiveView is not { } view)
        {
            RecordMenu.Items.Add(new MenuItem { Header = "(no tab)", IsEnabled = false });
        }
        else
        {
            foreach (var item in view.IsRecording ? RecordRunningItems(view) : RecordStartItems(view))
            {
                RecordMenu.Items.Add(item);
            }
        }

        RecordMenu.Items.Add(new Separator());
        RecordMenu.Items.Add(RecordingsFolderItem());
    }

    private IEnumerable<MenuItem> RecordStartItems(TerminalView view)
    {
        foreach (var (header, scope, tip) in new[]
        {
            ("This _tab", "tab", "The terminal and, when it is open, the agent pane beside it"),
            ("Whole _window", "window", "All of BaudHoundTerm, to show the app itself"),
        })
        {
            var item = new MenuItem { Header = header, ToolTip = tip + ". Addresses are hidden while it records.",
                                      InputGestureText = scope == "tab" ? "Ctrl+Shift+R" : "" };
            item.Click += async (_, _) => ShowRecordingResult(await view.RecordingAsync("start", scope));
            yield return item;
        }

        var audit = new MenuItem
        {
            Header = "_Audit recording...",
            ToolTip = "Records this tab until it closes, with no pause or stop, keeps a timed log, and audits every later session of this shortcut"
        };
        audit.Click += async (_, _) => await StartAuditFromMenuAsync(view);
        yield return audit;
    }

    /// <summary>
    /// The Record menu's way into an audit: the same setting as the Properties checkbox, with
    /// the copy folder asked here, and the recording started on this tab now.
    /// </summary>
    private async Task StartAuditFromMenuAsync(TerminalView view)
    {
        var definition = view.Definition;
        var dialog = new InputPromptWindow(
            $"Audit {definition.Name}: copy the recordings to",
            definition.Audit.CopyFolder,
            "A share the person at this PC cannot change, for example \\\\nas\\audit; blank keeps them on this PC only. "
            + "The recording runs until the tab closes and cannot be paused or stopped. Every later session of this shortcut "
            + "is audited too, until Audit is unticked in its Properties.")
        {
            Owner = this,
            SizeToContent = SizeToContent.Height
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var d in _sessions.Where(s => s.Id == definition.Id).Append(definition).Distinct())
        {
            d.Audit.Enabled = true;
            d.Audit.CopyFolder = dialog.Value;
        }

        Save();
        await view.StartAuditNowAsync();
    }

    private IEnumerable<MenuItem> RecordRunningItems(TerminalView view)
    {
        if (view.RecordingIsAudit)
        {
            yield return new MenuItem { Header = "Audit recording: runs until the tab closes", IsEnabled = false };
            yield break;
        }

        var pause = new MenuItem { Header = view.RecordingPaused ? "Res_ume recording" : "_Pause recording", InputGestureText = "Ctrl+Shift+P" };
        pause.Click += async (_, _) => await view.RecordingAsync(view.RecordingPaused ? "resume" : "pause");
        yield return pause;
        var stop = new MenuItem { Header = "_Stop recording", InputGestureText = "Ctrl+Shift+R" };
        stop.Click += async (_, _) => ShowRecordingResult(await view.RecordingAsync("stop"));
        yield return stop;
    }

    private MenuItem RecordingsFolderItem()
    {
        var folder = new MenuItem { Header = "Open recordings _folder" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(TerminalView.RecordingsFolder);
            OpenInBrowser(TerminalView.RecordingsFolder);
        };
        return folder;
    }

    private void OnFilesMenuOpened(object sender, RoutedEventArgs e)
    {
        FilesMenu.Items.Clear();
        foreach (var item in FileMenuItems())
        {
            FilesMenu.Items.Add(item);
        }
    }

    private void OnFilesButton(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var item in FileMenuItems())
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
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

    /// <summary>
    /// The version for the title bar, so a build can be identified from it. A published build
    /// carries its stamp ("0.8.0-dev.20261014.22", shown as "0.8.0 (build 20261014.22)"), the
    /// same one as in its zip's name; a plain build shows the version alone.
    /// </summary>
    private static string Version
    {
        get
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var info = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

            if (!string.IsNullOrEmpty(info))
            {
                info = info.Split('+')[0];   // the SDK appends "+<commit>"
                var dev = info.IndexOf("-dev.", StringComparison.Ordinal);
                return dev > 0 ? $"{info[..dev]} (build {info[(dev + 5)..]})" : info;
            }

            var v = assembly.GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // The guide and manual ship as HTML in the docs folder beside the exe, so Help works offline and
    // does not depend on the source repository being reachable. They open in the Home tab.
    private async void OnHome(object sender, RoutedEventArgs e) => await OpenHomeAsync("home.html");

    private async void OnGettingStarted(object sender, RoutedEventArgs e) => await OpenHomeAsync("HELP.html");

    private async void OnManual(object sender, RoutedEventArgs e) => await OpenHomeAsync("MANUAL.html");

    /// <summary>The Home tab on a page: brought to the front when it is open, opened when not.</summary>
    private async Task OpenHomeAsync(string page)
    {
        if (_live.FirstOrDefault(l => l.Definition.Id == HomePage.Id) is { } open)
        {
            SelectTab(open.Tab);
            await open.View.NavigateAsync(HomePage.Url(page), CancellationToken.None);
            return;
        }

        string folder;
        try
        {
            folder = HomePage.Prepare(Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The Home pages could not be written: " + ex.Message, "BaudHoundTerm");
            return;
        }

        var definition = new SessionDefinition
        {
            Id = HomePage.Id, Name = "Home", Protocol = SessionProtocol.Web, WebUrl = HomePage.Url(page)
        };
        await OpenTabAsync(definition, localWebFolder: folder);
    }

    // Credits and licences are the text files the zip carries beside the exe, so what Help shows is
    // exactly what this build ships, offline.
    private void OnCredits(object sender, RoutedEventArgs e) => OpenBesideExe("NOTICE.txt");

    private void OnThirdPartyLicenses(object sender, RoutedEventArgs e) => OpenBesideExe("THIRD-PARTY-NOTICES.txt");

    private void OnLicense(object sender, RoutedEventArgs e) => OpenBesideExe("LICENSE.txt");

    private void OpenBesideExe(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"{name} was not found beside BaudHoundTerm.exe:\n{path}\n\n" +
                "Extract the whole zip, not just the executable.", "BaudHoundTerm");
            return;
        }

        OpenInBrowser(path);
    }

    private void OnReportProblem(object sender, RoutedEventArgs e)
    {
        var open = MessageBox.Show(this,
            "Problems and questions go to the MAH7 Discord (mah7.com/discord).\n\n" +
            "Say what you did, what you expected and what happened, and the build number from Help > About. " +
            "Help > Open log folder has the logs; they can contain host names and addresses, so check " +
            "them before you share them.\n\nOpen the MAH7 Discord?",
            "Report a problem", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes)
        {
            OpenInBrowser("https://mah7.com/discord");
        }
    }

    // Via mah7.com, so a replaced invite does not need a new build.
    private void OnDiscord(object sender, RoutedEventArgs e) => OpenInBrowser("https://mah7.com/discord");

    /// <summary>Opens a web link or a local documentation file in the user's own browser.</summary>
    private void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"The page could not be opened: {ex.Message}\n\n{url}", "BaudHoundTerm");
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"BaudHoundTerm {Version}\n" +
            "Terminal and session manager for Windows: SSH, telnet, serial and local shells.\n" +
            "The desktop console for BaudHound (github.com/mahseven/BaudHound).\n\n" +
            "Apache-2.0. Terminal rendering by xterm.js (MIT), SSH by SSH.NET (MIT), VNC by noVNC\n" +
            "(MPL-2.0), cameras by LibVLC (LGPL-2.1), and more. Every component and its licence is\n" +
            "listed under Help > Third-party licenses.",
            "About BaudHoundTerm");

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
