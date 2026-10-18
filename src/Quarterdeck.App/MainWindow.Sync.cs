using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Quarterdeck.App.Views;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Sync;

namespace Quarterdeck.App;

/// <summary>Sharing shortcuts between PCs through a git repository (see <see cref="ConfigSync"/>).</summary>
public partial class MainWindow
{
    private readonly SyncSettings _sync = SyncSettings.Load();
    private readonly DispatcherTimer _syncSoon = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _syncEvery = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _syncing;
    private bool _syncAgain;

    // The instance relaunched through UAC only to host an administrator shell: two instances
    // syncing the same clone would take turns undoing each other's view of the list.
    private static bool IsAdminShellInstance =>
        Environment.GetCommandLineArgs().Contains(AdminShellSwitch, StringComparer.OrdinalIgnoreCase);

    // Not while the shortcut list failed to load: the user should see that first.
    private bool AutoSync => _sync.IsConnected && _sync.Automatic && !IsAdminShellInstance && _store.LoadProblem is null;

    private void InitSync()
    {
        _syncSoon.Tick += async (_, _) =>
        {
            _syncSoon.Stop();
            await SyncNowAsync();
        };
        _syncEvery.Tick += async (_, _) => await SyncNowAsync();

        Loaded += async (_, _) =>
        {
            UpdateSyncButton();
            if (AutoSync)
            {
                _syncEvery.Start();
                await SyncNowAsync();
            }
        };
    }

    /// <summary>A change was saved: sync a few seconds later, once a burst of edits settles.</summary>
    private void ScheduleSync()
    {
        if (AutoSync)
        {
            _syncSoon.Stop();
            _syncSoon.Start();
        }
    }

    private void OnSyncSettings(object sender, RoutedEventArgs e) =>
        new SyncWindow(_sync, this) { Owner = this }.ShowDialog();

    /// <summary>Called by the sync window when the settings changed (connected, disconnected, automatic toggled).</summary>
    internal void SyncSettingsChanged()
    {
        if (AutoSync)
        {
            _syncEvery.Start();
        }
        else
        {
            _syncEvery.Stop();
            _syncSoon.Stop();
        }

        UpdateSyncButton();
    }

    internal ConfigSync? CreateSync(out string? problem)
    {
        var git = GitRunner.Find(_sync.GitPath);
        if (git is null)
        {
            problem = "Git for Windows was not found. Install it from https://git-scm.com/download/win, or set the path to git.exe.";
            return null;
        }

        problem = null;
        return new ConfigSync(AppPaths.DataDirectory, _sync, new GitRunner(git), s => s.Save());
    }

    /// <summary>
    /// Runs one sync (or the first connect) on copies of the list, then applies the result.
    /// Returns null when one was already running (it runs again after) or git is missing.
    /// </summary>
    internal async Task<SyncOutcome?> SyncNowAsync(bool connect = false)
    {
        if (_syncing)
        {
            _syncAgain = true;
            return null;
        }

        var engine = CreateSync(out var problem);
        if (engine is null)
        {
            _sync.LastResult = "Sync failed: " + problem;
            UpdateSyncButton();
            return null;
        }

        _syncing = true;
        UpdateSyncButton();
        try
        {
            // The engine works on a background thread; give it copies, and remember what each
            // shortcut looked like, so edits made meanwhile are told apart from incoming ones.
            var before = _sessions.ToDictionary(s => s.Id, Fingerprint);
            var copies = _sessions.Select(Clone).ToList();
            var foldersBefore = _folders.ToList();

            SyncOutcome outcome;
            try
            {
                outcome = await Task.Run(() => connect
                    ? engine.ConnectAsync(copies, foldersBefore, CancellationToken.None)
                    : engine.SyncAsync(copies, foldersBefore, CancellationToken.None));
            }
            catch (Exception ex)
            {
                // A missing git or an unexpected error used to escape from the timer as an
                // unhandled exception, with an error box every five minutes.
                _sync.LastResult = "Sync failed: " + ex.Message;
                return null;
            }

            ApplySync(outcome, before, foldersBefore);
            return outcome;
        }
        finally
        {
            _syncing = false;
            UpdateSyncButton();
            if (_syncAgain)
            {
                _syncAgain = false;
                ScheduleSync();
            }
        }
    }

    /// <summary>
    /// Folds a sync result into the live list. Existing shortcuts are updated in place, so open
    /// tabs keep their definition; anything edited, added or deleted here while the sync ran
    /// wins and goes out with the next sync.
    /// </summary>
    private void ApplySync(SyncOutcome outcome, Dictionary<Guid, string> before, List<string> foldersBefore)
    {
        var current = _sessions.ToDictionary(s => s.Id);
        var editedHere = current.Values.Where(s => !before.TryGetValue(s.Id, out var was) || was != Fingerprint(s))
            .Select(s => s.Id).ToHashSet();
        var deletedHere = before.Keys.Where(id => !current.ContainsKey(id)).ToHashSet();

        var result = new List<SessionDefinition>();
        var updated = new List<SessionDefinition>();
        var incomingIds = outcome.Sessions.Select(s => s.Id).ToHashSet();

        foreach (var incoming in outcome.Sessions)
        {
            if (deletedHere.Contains(incoming.Id))
            {
                continue;
            }

            if (current.TryGetValue(incoming.Id, out var mine))
            {
                if (!editedHere.Contains(mine.Id) && Fingerprint(mine) != Fingerprint(incoming))
                {
                    CopyInto(incoming, mine);
                    updated.Add(mine);
                }

                result.Add(mine);
            }
            else
            {
                result.Add(incoming);
            }
        }

        foreach (var mine in current.Values)
        {
            // Added here during the sync, or edited here while another PC deleted it.
            if (!incomingIds.Contains(mine.Id) && (!before.ContainsKey(mine.Id) || editedHere.Contains(mine.Id)))
            {
                result.Add(mine);
            }
        }

        var foldersChangedHere = !_folders.SequenceEqual(foldersBefore);
        var folders = foldersChangedHere ? outcome.Folders.Union(_folders).ToList() : outcome.Folders;

        _sessions.Clear();
        _sessions.AddRange(result);
        _folders = folders;
        _store.Save(_sessions);          // straight to the store: Save() would schedule another sync
        _store.SaveFolders(_folders);
        RebuildTree();

        foreach (var definition in updated)
        {
            RefreshOpenTabsOf(definition);
        }

        if (editedHere.Count > 0 || deletedHere.Count > 0 || foldersChangedHere)
        {
            _syncAgain = true;
        }
    }

    private static readonly JsonSerializerOptions CloneOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Fingerprint(SessionDefinition d) => JsonSerializer.Serialize(d, CloneOptions);

    private static SessionDefinition Clone(SessionDefinition d) =>
        JsonSerializer.Deserialize<SessionDefinition>(JsonSerializer.Serialize(d, CloneOptions), CloneOptions)!;

    private static readonly PropertyInfo[] DefinitionProperties = typeof(SessionDefinition)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToArray();

    private static void CopyInto(SessionDefinition from, SessionDefinition to)
    {
        foreach (var p in DefinitionProperties)
        {
            p.SetValue(to, p.GetValue(from));
        }
    }

    private void UpdateSyncButton()
    {
        if (SyncButton is null)
        {
            return;
        }

        var failed = _sync.LastResult.StartsWith("Sync failed", StringComparison.Ordinal);
        SyncButton.Content = !_sync.IsConnected ? "Sync"
            : _syncing ? "Syncing..."
            : failed ? "Sync !"
            : "Sync ✓";
        SyncButton.Foreground = failed && _sync.IsConnected
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B))
            : new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
        SyncButton.ToolTip = !_sync.IsConnected
            ? "Share shortcuts between PCs through a private git repository"
            : $"{_sync.RepoUrl}\n{(_sync.LastSync is { } at ? $"Last sync {at:g}: " : "")}{_sync.LastResult}";
    }
}
