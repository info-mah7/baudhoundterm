using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Quarterdeck.App.Views;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;

namespace Quarterdeck.App;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new();
    private readonly CredentialVault _vault = new();
    private List<SessionDefinition> _sessions;

    public MainWindow()
    {
        InitializeComponent();

        _sessions = _store.Load();
        RebuildTree();

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

        foreach (var group in visible.GroupBy(s => s.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ItemsControl parent = SessionTree;

            if (!string.IsNullOrEmpty(group.Key))
            {
                var folder = new TreeViewItem { Header = group.Key, Tag = null };
                SessionTree.Items.Add(folder);
                parent = folder;
            }

            foreach (var session in group.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                parent.Items.Add(new TreeViewItem
                {
                    Header = session.Protocol == SessionProtocol.Ssh && !string.IsNullOrEmpty(session.Host)
                        ? $"{session.Name}  ({session.Host})"
                        : session.Name,
                    Tag = session
                });
            }
        }
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
            this, $"Delete session \"{Selected.Name}\"?", "Quarterdeck",
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
        var tab = new TabItem { Header = definition.Name, Content = view };

        view.TitleChanged += (_, title) => tab.Header = string.IsNullOrWhiteSpace(title) ? definition.Name : title;

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;

        await view.ConnectAsync();
        Save();
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
        }

        Tabs.Items.Remove(tab);
    }

    private void OnTransfer(object sender, RoutedEventArgs e)
    {
        var definition = ActiveView?.Definition ?? Selected;

        if (definition is null || definition.Protocol != SessionProtocol.Ssh)
        {
            MessageBox.Show(this, "File transfer needs an SSH session.", "Quarterdeck");
            return;
        }

        new TransferWindow(definition, _vault) { Owner = this }.Show();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DefaultLogDirectory) { UseShellExecute = true });

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Quarterdeck\nA session manager and terminal for Windows.\n\n" +
            "Apache-2.0. Terminal rendering by xterm.js (MIT), SSH by SSH.NET (MIT).",
            "About Quarterdeck");

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
