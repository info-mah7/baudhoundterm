using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Sync;

public sealed class SyncOutcome
{
    /// <summary>The shortcut list after the sync, to replace the one in memory.</summary>
    public List<SessionDefinition> Sessions { get; init; } = new();
    public List<string> Folders { get; init; } = new();

    /// <summary>Files that came in from other PCs.</summary>
    public int Received { get; set; }
    public bool Sent { get; set; }

    /// <summary>Set by a connect once the repository was cloned: later errors (a failed push) leave it connected.</summary>
    public bool Connected { get; set; }
    public List<string> Notes { get; } = new();
    public string? Error { get; set; }

    public string Summary =>
        Error is not null ? "Sync failed: " + Error
        : (Received, Sent) switch
        {
            (0, false) => "Up to date.",
            (0, true) => "Sent this PC's changes.",
            (_, false) => $"Received {Received} change(s).",
            _ => $"Received {Received} change(s) and sent this PC's."
        } + (Notes.Count > 0 ? " " + string.Join(" ", Notes) : "");
}

/// <summary>
/// Keeps shortcuts, folders, agent endpoints (without keys), skills and trusted host keys in a
/// git repository so several PCs share them. The clone lives in the data directory; each
/// shortcut is its own file, so edits on two PCs merge unless they touch the same shortcut.
///
/// A sync exports this PC's state into the clone, commits if anything changed, merges the
/// remote, pushes, and imports the result. Conflicts never stop it: the same shortcut edited
/// on two PCs keeps the remote version plus a "(conflict, this PC)" copy; an edit beats a
/// delete; folders, host keys and endpoints are merged as sets.
///
/// Never synced: the credential vault (DPAPI ties it to this user on this PC), KeePassXC
/// pairings, and per-PC values (serial port, key file path, last connected). A scan refuses to
/// commit anything that looks like a password or a private key.
/// </summary>
public sealed class ConfigSync
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NewLine = "\n"
    };

    // Not "secretKey" (the name of a vault entry, which is fine): actual values only.
    private static readonly Regex SecretPattern = new(
        "-----BEGIN [A-Z ]*PRIVATE KEY-----|\"(password|passphrase|apiKey|api_key|secret|token)\"\\s*:\\s*\"[^\"]+\"",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private readonly string _dataDir;
    private readonly SyncSettings _settings;
    private readonly GitRunner _git;
    private readonly Action<SyncSettings> _saveSettings;

    public ConfigSync(string dataDir, SyncSettings settings, GitRunner git, Action<SyncSettings> saveSettings)
    {
        _dataDir = dataDir;
        _settings = settings;
        _git = git;
        _saveSettings = saveSettings;
    }

    public string RepoDir => Path.Combine(_dataDir, "sync");
    private string ShortcutsDir => Path.Combine(RepoDir, "shortcuts");
    private string RepoSkills => Path.Combine(RepoDir, "skills");
    private string LocalSkills => Path.Combine(_dataDir, "skills");
    private string Machine => string.IsNullOrWhiteSpace(_settings.MachineName) ? Environment.MachineName : _settings.MachineName.Trim();

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    // ---------------------------------------------------------------- entry points

    /// <summary>
    /// Clones the repository and merges this PC's shortcuts into it. An empty repository simply
    /// receives them; one with content keeps both (a shortcut that differs on both sides keeps
    /// the repository's version plus this PC's as a conflict copy).
    /// </summary>
    public async Task<SyncOutcome> ConnectAsync(IReadOnlyList<SessionDefinition> local, IReadOnlyList<string> folders, CancellationToken token)
    {
        using var held = await LockAsync(token);

        if (await CheckPublicAsync(_settings.RepoUrl, token) is { } refusal)
        {
            return Failed(local, folders, refusal);
        }

        if (Directory.Exists(RepoDir))
        {
            // A clone of another repository (or a broken one): set it aside, never delete.
            Directory.Move(RepoDir, RepoDir + $".old-{DateTime.UtcNow:yyyyMMddHHmmss}");
        }

        var clone = await _git.RunAsync(_dataDir, NetworkTimeout, token, "clone", "--quiet", _settings.RepoUrl, RepoDir);
        if (!clone.Ok)
        {
            return Failed(local, folders, "could not clone " + _settings.RepoUrl + ": " + clone.Message);
        }

        await ConfigureAsync(token);

        // Work on the configured branch. A repository that has commits but not that branch
        // keeps its own default branch rather than gaining an unrelated one; an empty one
        // starts the configured branch.
        var branch = _settings.Branch;
        if ((await GitAsync(token, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}")).Ok)
        {
            await GitAsync(token, "checkout", "--quiet", "-B", branch, $"origin/{branch}");
        }
        else if ((await GitAsync(token, "rev-parse", "--verify", "--quiet", "HEAD")).Ok)
        {
            _settings.Branch = (await GitAsync(token, "rev-parse", "--abbrev-ref", "HEAD")).Output.Trim();
        }
        else
        {
            await GitAsync(token, "symbolic-ref", "HEAD", $"refs/heads/{branch}");
        }

        WriteIfChanged(Path.Combine(RepoDir, ".gitattributes"), "* -text\n");
        WriteIfChanged(Path.Combine(RepoDir, "README.md"),
            "# BaudHoundTerm shared configuration\n\nWritten by BaudHoundTerm's sync. One file per shortcut in `shortcuts/`.\n" +
            "No passwords or keys are stored here. Keep this repository private: it still maps hosts, users and devices.\n");

        // Merge by content before the first export, so nothing in the repository is lost.
        var notes = new List<string>();
        var remote = ReadShortcuts(notes).ToDictionary(d => d.Id);
        var merged = new List<SessionDefinition>(remote.Values);
        foreach (var mine in local)
        {
            if (!remote.TryGetValue(mine.Id, out var theirs))
            {
                merged.Add(mine);
            }
            else if (ExportText(theirs) != ExportText(mine))
            {
                merged.Add(ConflictCopy(mine));
                notes.Add($"\"{mine.Name}\" differed; kept the repository's and this PC's copy.");
            }
        }

        var allFolders = ReadFolders().Union(folders, StringComparer.Ordinal).ToList();

        MergeLocalFileInto("known_hosts.json", MergeObjects);
        MergeLocalFileInto("agent.json", MergeAgent);
        foreach (var file in Directory.Exists(RepoSkills) ? Directory.GetFiles(RepoSkills, "*.md") : Array.Empty<string>())
        {
            var mine = Path.Combine(LocalSkills, Path.GetFileName(file));
            if (File.Exists(mine) && File.ReadAllText(mine) != File.ReadAllText(file))
            {
                File.Copy(mine, ConflictSkillName(mine), overwrite: true);
            }
        }

        // From here it is an ordinary sync, with the merged list as this PC's state. Remote
        // skills not here yet must not look like deletions, so bring them over first.
        CopyMissingSkills();
        var outcome = await SyncCoreAsync(local: merged, folders: allFolders, token);
        outcome.Notes.InsertRange(0, notes);
        outcome.Connected = true;
        return outcome;
    }

    public async Task<SyncOutcome> SyncAsync(IReadOnlyList<SessionDefinition> local, IReadOnlyList<string> folders, CancellationToken token)
    {
        using var held = await LockAsync(token);
        if (!Directory.Exists(Path.Combine(RepoDir, ".git")))
        {
            return Failed(local, folders, "not connected to a repository yet.");
        }

        return await SyncCoreAsync(local, folders, token);
    }

    // ---------------------------------------------------------------- the sync itself

    private async Task<SyncOutcome> SyncCoreAsync(IReadOnlyList<SessionDefinition> local, IReadOnlyList<string> folders, CancellationToken token)
    {
        var outcome = new SyncOutcome();
        try
        {
            Export(local, folders, outcome.Notes);

            await GitAsync(token, "add", "-A");
            var status = await GitAsync(token, "status", "--porcelain");
            if (status.Output.Trim().Length > 0)
            {
                var secret = await FindSecretAsync(token);
                if (secret is not null)
                {
                    await GitAsync(token, "reset", "--quiet");
                    return Failed(local, folders, $"{secret} looks like it holds a password or key, so nothing was committed. Remove it and sync again.");
                }

                var changed = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                var commit = await GitAsync(token, "commit", "--quiet", "-m", $"{Machine}: {changed} change(s)");
                if (!commit.Ok)
                {
                    return Failed(local, folders, "commit failed: " + commit.Message);
                }

                outcome.Sent = true;
            }

            var before = (await GitAsync(token, "rev-parse", "--verify", "--quiet", "HEAD")).Output.Trim();
            var pushed = false;

            for (var attempt = 0; attempt < 3 && !pushed; attempt++)
            {
                var fetch = await _git.RunAsync(RepoDir, NetworkTimeout, token, "fetch", "--quiet", "origin");
                if (!fetch.Ok)
                {
                    // Offline or refused: keep working locally, the commit waits for next time.
                    outcome.Error = "could not reach the repository: " + fetch.Message;
                    break;
                }

                var remoteRef = $"refs/remotes/origin/{_settings.Branch}";
                if ((await GitAsync(token, "rev-parse", "--verify", "--quiet", remoteRef)).Ok)
                {
                    var merge = await GitAsync(token, "merge", "--no-edit", "--quiet", "-m", $"{Machine}: merge", remoteRef);
                    if (!merge.Ok)
                    {
                        var conflicts = (await GitAsync(token, "diff", "--name-only", "--diff-filter=U")).Output
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (conflicts.Length == 0)
                        {
                            await GitAsync(token, "merge", "--abort");
                            outcome.Error = "merge failed: " + merge.Message;
                            break;
                        }

                        await ResolveAsync(conflicts, outcome.Notes, token);
                        var done = await GitAsync(token, "commit", "--no-edit", "--quiet");
                        if (!done.Ok)
                        {
                            outcome.Error = "could not finish the merge: " + done.Message;
                            break;
                        }
                    }
                }

                // Visibility can be switched after connecting: look again before every send.
                if (await CheckPublicAsync(_settings.RepoUrl, token) is { } refusal)
                {
                    outcome.Error = refusal;
                    break;
                }

                // Nothing to push on a fresh, empty repository with nothing local either.
                if (!(await GitAsync(token, "rev-parse", "--verify", "--quiet", "HEAD")).Ok)
                {
                    pushed = true;
                    break;
                }

                var push = await _git.RunAsync(RepoDir, NetworkTimeout, token, "push", "--quiet", "origin", $"HEAD:refs/heads/{_settings.Branch}");
                if (push.Ok)
                {
                    pushed = true;
                }
                else if (!push.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase) &&
                         !push.Message.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Error = "could not send: " + push.Message;
                    break;
                }

                // Rejected: another PC pushed in between. Fetch and merge again.
            }

            if (before.Length > 0)
            {
                var diff = await GitAsync(token, "diff", "--name-only", before, "HEAD");
                outcome.Received = diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            else
            {
                outcome.Received = Directory.Exists(ShortcutsDir) ? Directory.GetFiles(ShortcutsDir, "*.json").Length : 0;
            }

            var result = Import(local, outcome.Notes);
            var final = new SyncOutcome
            {
                Sessions = result.Sessions,
                Folders = result.Folders,
                Received = outcome.Received,
                Sent = outcome.Sent && pushed,
                Error = outcome.Error
            };
            final.Notes.AddRange(outcome.Notes);

            _settings.LastSync = DateTimeOffset.Now;
            _settings.LastResult = final.Summary;
            _saveSettings(_settings);
            return final;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // The clone may already hold merged changes from other PCs. Hand those back too:
            // a caller left with the old list would export it next time and undo them.
            try
            {
                var partial = Import(local, outcome.Notes);
                var failed = new SyncOutcome { Sessions = partial.Sessions, Folders = partial.Folders, Error = ex.Message };
                _settings.LastResult = failed.Summary;
                _saveSettings(_settings);
                return failed;
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException or JsonException)
            {
                return Failed(local, folders, ex.Message);
            }
        }
    }

    private SyncOutcome Failed(IReadOnlyList<SessionDefinition> local, IReadOnlyList<string> folders, string error)
    {
        var outcome = new SyncOutcome { Sessions = local.ToList(), Folders = folders.ToList(), Error = error };
        _settings.LastResult = outcome.Summary;
        _saveSettings(_settings);
        return outcome;
    }

    private Task<GitResult> GitAsync(CancellationToken token, params string[] args) => _git.RunAsync(RepoDir, token, args);

    /// <summary>
    /// Refuses a repository anyone can read. The test is to read it as a stranger: list its
    /// branches over https with every credential source switched off (no helper, no prompt,
    /// no user in the URL). If that works, the repository is public. ssh addresses are probed
    /// at the same host and path over https, which is where GitHub, GitLab, Codeberg, Gitea and
    /// Forgejo serve anonymous reads; a bare repository reached only over ssh has no anonymous
    /// route at all. Local paths are not checked. Returns the refusal, or null to go ahead.
    /// </summary>
    public async Task<string?> CheckPublicAsync(string url, CancellationToken token)
    {
        var anonymous = AnonymousUrl(url);
        if (anonymous is null)
        {
            return null;
        }

        // A stranger has no git config, no stored logins and no .netrc: give git an empty home
        // and skip the system and global config, so nothing can sign the probe in.
        var stranger = Path.Combine(Path.GetTempPath(), "bht-anon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stranger);
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["HOME"] = stranger,
                ["USERPROFILE"] = stranger,
                ["XDG_CONFIG_HOME"] = stranger,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(stranger, "none"),
                ["GIT_ASKPASS"] = "",
                ["SSH_ASKPASS"] = "",
                ["GCM_INTERACTIVE"] = "never"
            };

            var probe = await _git.RunAsync(stranger, TimeSpan.FromSeconds(30), environment, token,
                "-c", "credential.helper=", "-c", "core.askPass=", "ls-remote", "--heads", anonymous);
            return probe.Ok
                ? $"{anonymous} can be read without signing in, so it is public. Shortcuts map hosts, users and devices: " +
                  "use a private repository. Nothing was sent."
                : null;
        }
        finally
        {
            try
            {
                Directory.Delete(stranger, recursive: true);
            }
            catch (IOException)
            {
                // Temp clutter at worst.
            }
        }
    }

    /// <summary>The https form of a remote address, without any user name or token in it; null for local paths.</summary>
    public static string? AnonymousUrl(string url)
    {
        url = url.Trim();

        // scp-like ssh: git@github.com:owner/repo.git
        var scp = Regex.Match(url, @"^(?:[^@/]+@)?(?<host>[^:/]+):(?!//)(?<path>.+)$");
        if (scp.Success && !url.Contains("://") && !Regex.IsMatch(url, @"^[A-Za-z]:[\\/]"))
        {
            return $"https://{scp.Groups["host"].Value}/{scp.Groups["path"].Value.TrimStart('/')}";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile || uri.IsUnc)
        {
            return null;
        }

        return uri.Scheme is "http" or "https" or "ssh" or "git"
            ? $"https://{uri.Host}{uri.AbsolutePath}"
            : null;
    }

    private async Task ConfigureAsync(CancellationToken token)
    {
        // Commits name the PC, whatever the global git identity says (or if there is none).
        await GitAsync(token, "config", "user.name", $"BaudHoundTerm on {Machine}");
        await GitAsync(token, "config", "user.email", $"baudhoundterm@{Regex.Replace(Machine, "[^A-Za-z0-9.-]", "-")}.local");
        await GitAsync(token, "config", "core.autocrlf", "false");
        await GitAsync(token, "config", "commit.gpgsign", "false");
    }

    /// <summary>One sync at a time across processes (the elevated window is a second instance).</summary>
    private async Task<IDisposable> LockAsync(CancellationToken token)
    {
        var path = Path.Combine(_dataDir, "sync.lock");
        for (var i = 0; ; i++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (i < 600)
            {
                await Task.Delay(500, token);
            }
        }
    }

    private async Task<string?> FindSecretAsync(CancellationToken token)
    {
        var staged = (await GitAsync(token, "diff", "--cached", "--name-only", "--diff-filter=AM")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in staged)
        {
            var path = Path.Combine(RepoDir, name);
            if (File.Exists(path) && SecretPattern.IsMatch(File.ReadAllText(path)))
            {
                return name;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- export and import

    /// <summary>A shortcut as stored in the repository: without the values local to this PC.</summary>
    private static string ExportText(SessionDefinition definition)
    {
        var node = JsonSerializer.SerializeToNode(definition, JsonOptions)!.AsObject();
        node.Remove("lastConnected");
        node.Remove("serialPort");
        node.Remove("keyPath");
        return node.ToJsonString(JsonOptions) + "\n";
    }

    private void Export(IReadOnlyList<SessionDefinition> local, IReadOnlyList<string> folders, List<string> notes)
    {
        Directory.CreateDirectory(ShortcutsDir);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // No shortcuts here but some in the repository is a damaged or lost sessions.json, or a
        // reinstall, far more often than a deliberate "delete them all": exporting it removed
        // every shortcut from the repository and then from every other PC. Keep the repository's.
        if (local.Count == 0 && Directory.GetFiles(ShortcutsDir, "*.json").Length is var inRepo and > 0)
        {
            foreach (var file in Directory.GetFiles(ShortcutsDir, "*.json"))
            {
                keep.Add(Path.GetFileName(file));
            }

            notes.Add($"This PC had no shortcuts, so the {inRepo} in the repository were kept, not deleted.");
        }

        foreach (var definition in local)
        {
            var name = definition.Id + ".json";
            keep.Add(name);
            WriteIfChanged(Path.Combine(ShortcutsDir, name), ExportText(definition));
            _settings.SerialPorts[definition.Id] = definition.SerialPort;
            _settings.KeyPaths[definition.Id] = definition.KeyPath;
        }

        foreach (var file in Directory.GetFiles(ShortcutsDir, "*.json"))
        {
            if (!keep.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }

        WriteIfChanged(Path.Combine(RepoDir, "folders.json"),
            JsonSerializer.Serialize(folders.Distinct().Order(StringComparer.Ordinal).ToList(), JsonOptions) + "\n");

        ExportFile("known_hosts.json", sortKeys: true);
        ExportFile("agent.json", sortKeys: false);

        Directory.CreateDirectory(RepoSkills);
        var skills = Directory.Exists(LocalSkills) ? Directory.GetFiles(LocalSkills, "*.md") : Array.Empty<string>();
        foreach (var skill in skills)
        {
            WriteIfChanged(Path.Combine(RepoSkills, Path.GetFileName(skill)), File.ReadAllText(skill));
        }

        var names = skills.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(RepoSkills, "*.md"))
        {
            if (!names.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>A local JSON file, normalised so formatting alone never makes a change.</summary>
    private void ExportFile(string name, bool sortKeys)
    {
        var local = Path.Combine(_dataDir, name);
        if (!File.Exists(local))
        {
            return;   // absent here: leave the repository's copy, the import brings it over
        }

        var node = JsonNode.Parse(File.ReadAllText(local));
        if (sortKeys && node is JsonObject obj)
        {
            node = new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        }

        WriteIfChanged(Path.Combine(RepoDir, name), node!.ToJsonString(JsonOptions) + "\n");
    }

    private (List<SessionDefinition> Sessions, List<string> Folders) Import(IReadOnlyList<SessionDefinition> local, List<string> notes)
    {
        var mine = local.ToDictionary(d => d.Id);
        var sessions = new List<SessionDefinition>();
        foreach (var definition in ReadShortcuts(notes))
        {
            mine.TryGetValue(definition.Id, out var here);
            definition.SerialPort = _settings.SerialPorts.GetValueOrDefault(definition.Id) ?? here?.SerialPort ?? "";
            definition.KeyPath = _settings.KeyPaths.GetValueOrDefault(definition.Id) ?? here?.KeyPath ?? "";
            definition.LastConnected = here?.LastConnected;
            sessions.Add(definition);
        }

        foreach (var name in new[] { "known_hosts.json", "agent.json" })
        {
            var source = Path.Combine(RepoDir, name);
            if (File.Exists(source))
            {
                WriteIfChanged(Path.Combine(_dataDir, name), File.ReadAllText(source));
            }
        }

        if (Directory.Exists(RepoSkills))
        {
            Directory.CreateDirectory(LocalSkills);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(RepoSkills, "*.md"))
            {
                names.Add(Path.GetFileName(file));
                WriteIfChanged(Path.Combine(LocalSkills, Path.GetFileName(file)), File.ReadAllText(file));
            }

            foreach (var file in Directory.GetFiles(LocalSkills, "*.md"))
            {
                if (!names.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);   // deleted on another PC
                }
            }
        }

        return (sessions, ReadFolders());
    }

    private List<SessionDefinition> ReadShortcuts(List<string> notes)
    {
        var list = new List<SessionDefinition>();
        if (!Directory.Exists(ShortcutsDir))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(ShortcutsDir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<SessionDefinition>(File.ReadAllText(file), JsonOptions) is { } d)
                {
                    // Two files with one Id (a copy made by hand) used to stop every later sync.
                    if (list.Any(x => x.Id == d.Id))
                    {
                        notes.Add($"Skipped {Path.GetFileName(file)}: it has the same Id as another shortcut.");
                        continue;
                    }

                    // The repository holds no per-PC values; the class defaults (COM1) must not
                    // pass for this PC's own when the next export records them.
                    d.SerialPort = "";
                    d.KeyPath = "";
                    list.Add(d);
                }
            }
            catch (JsonException ex)
            {
                notes.Add($"Skipped {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return list;
    }

    private List<string> ReadFolders()
    {
        var path = Path.Combine(RepoDir, "folders.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), JsonOptions) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private void CopyMissingSkills()
    {
        if (!Directory.Exists(RepoSkills))
        {
            return;
        }

        Directory.CreateDirectory(LocalSkills);
        foreach (var file in Directory.GetFiles(RepoSkills, "*.md"))
        {
            var target = Path.Combine(LocalSkills, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Copy(file, target);
            }
        }
    }

    /// <summary>At first connect: fold the repository's copy of a data-directory file into the local one.</summary>
    private void MergeLocalFileInto(string name, Func<string, string, string> merge)
    {
        var repo = Path.Combine(RepoDir, name);
        var local = Path.Combine(_dataDir, name);
        if (File.Exists(repo) && File.Exists(local))
        {
            // Repository first as "theirs", so its value wins where both have one.
            AtomicFile.Write(local, merge(File.ReadAllText(local), File.ReadAllText(repo)));
        }
    }

    // ---------------------------------------------------------------- conflicts

    private async Task ResolveAsync(IEnumerable<string> paths, List<string> notes, CancellationToken token)
    {
        foreach (var path in paths)
        {
            var ours = await ShowAsync(":2:" + path, token);
            var theirs = await ShowAsync(":3:" + path, token);
            var full = Path.Combine(RepoDir, path);
            string? result;

            if (path.StartsWith("shortcuts/", StringComparison.Ordinal))
            {
                result = theirs ?? ours;   // an edit beats a delete, on either side
                if (ours is not null && theirs is not null && ours != theirs &&
                    JsonSerializer.Deserialize<SessionDefinition>(ours, JsonOptions) is { } mine)
                {
                    var copy = ConflictCopy(mine);
                    WriteIfChanged(Path.Combine(ShortcutsDir, copy.Id + ".json"), ExportText(copy));
                    await GitAsync(token, "add", "--", $"shortcuts/{copy.Id}.json");
                    notes.Add($"\"{mine.Name}\" was changed here and on another PC; kept both.");
                }
            }
            else if (path == "folders.json")
            {
                result = JsonSerializer.Serialize(Parse<List<string>>(ours).Union(Parse<List<string>>(theirs))
                    .Distinct().Order(StringComparer.Ordinal).ToList(), JsonOptions) + "\n";
            }
            else if (path == "known_hosts.json")
            {
                result = ours is null || theirs is null ? theirs ?? ours : MergeObjects(ours, theirs);
            }
            else if (path == "agent.json")
            {
                result = ours is null || theirs is null ? theirs ?? ours : MergeAgent(ours, theirs);
            }
            else if (path.StartsWith("skills/", StringComparison.Ordinal))
            {
                result = theirs ?? ours;
                if (ours is not null && theirs is not null && ours != theirs)
                {
                    var copy = ConflictSkillName(full);
                    WriteIfChanged(copy, ours);
                    await GitAsync(token, "add", "--", Path.GetRelativePath(RepoDir, copy).Replace('\\', '/'));
                    notes.Add($"Skill {Path.GetFileName(path)} was changed on both PCs; this PC's is kept as {Path.GetFileName(copy)}.");
                }
            }
            else
            {
                result = theirs ?? ours;
            }

            if (result is null)
            {
                await GitAsync(token, "rm", "--quiet", "--cached", "--ignore-unmatch", "--", path);
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, result);
                await GitAsync(token, "add", "--", path);
            }
        }
    }

    private async Task<string?> ShowAsync(string spec, CancellationToken token)
    {
        var r = await GitAsync(token, "show", spec);
        return r.Ok ? r.Output : null;
    }

    private SessionDefinition ConflictCopy(SessionDefinition original)
    {
        var copy = JsonSerializer.Deserialize<SessionDefinition>(JsonSerializer.Serialize(original, JsonOptions), JsonOptions)!;
        copy.Id = Guid.NewGuid();
        copy.Name = $"{original.Name} (conflict, {Machine})";
        _settings.SerialPorts[copy.Id] = original.SerialPort;
        _settings.KeyPaths[copy.Id] = original.KeyPath;
        return copy;
    }

    private string ConflictSkillName(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.conflict-{Regex.Replace(Machine, "[^A-Za-z0-9-]", "-")}.md");

    /// <summary>Two JSON objects as one: every key from both, "theirs" winning where both have it.</summary>
    private static string MergeObjects(string ours, string theirs)
    {
        var mine = JsonNode.Parse(ours) as JsonObject ?? new JsonObject();
        var other = JsonNode.Parse(theirs) as JsonObject ?? new JsonObject();
        var merged = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (k, v) in mine)
        {
            merged[k] = v?.DeepClone();
        }

        foreach (var (k, v) in other)
        {
            merged[k] = v?.DeepClone();
        }

        return new JsonObject(merged).ToJsonString(JsonOptions) + "\n";
    }

    /// <summary>Endpoints from both sides by id ("theirs" winning on the same id); the default from theirs.</summary>
    private static string MergeAgent(string ours, string theirs)
    {
        var mine = JsonNode.Parse(ours) as JsonObject ?? new JsonObject();
        var other = JsonNode.Parse(theirs) as JsonObject ?? new JsonObject();
        var byId = new Dictionary<string, JsonNode>();
        var order = new List<string>();

        foreach (var side in new[] { mine, other })
        {
            foreach (var ep in side["endpoints"] as JsonArray ?? new JsonArray())
            {
                var id = ep?["id"]?.ToString() ?? Guid.NewGuid().ToString();
                if (!byId.ContainsKey(id))
                {
                    order.Add(id);
                }

                byId[id] = ep!.DeepClone();
            }
        }

        var result = (JsonObject)other.DeepClone();
        result["endpoints"] = new JsonArray(order.Select(id => byId[id]).ToArray());
        result["defaultEndpoint"] = other["defaultEndpoint"]?.DeepClone() ?? mine["defaultEndpoint"]?.DeepClone();
        return result.ToJsonString(JsonOptions) + "\n";
    }

    private static T Parse<T>(string? json) where T : new()
    {
        try
        {
            return json is null ? new T() : JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
