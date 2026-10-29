using System.Text;
using System.Text.RegularExpressions;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// What the agent keeps between runs: a Markdown file per host (every shortcut to the machine
/// shares it: its SSH, tmux and web shortcuts are one machine), or per shortcut when the shortcut
/// says so or has no host. Two kinds of line: notes the agent wrote with its remember tool, and a
/// line recorded after each run. Plain text, so the user can read, edit or clear it, and so Sync
/// can merge two PCs' additions line by line.
/// </summary>
public sealed class AgentMemory
{
    public const string NotesHeading = "## Notes";
    public const string RunsHeading = "## Runs";

    /// <summary>Longest note the remember tool takes: one fact, not a document.</summary>
    public const int MaxNote = 300;

    // Runs kept in the file; the prompt takes fewer. Notes are never dropped by the app.
    private const int MaxRuns = 40;

    private static readonly object Gate = new();

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "agent-memory");

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>"srv01", or a shortcut's name: what the memory is about, for the prompt and the panel.</summary>
    public string Subject { get; }

    /// <summary>Whether every shortcut to the host shares this memory (false: this shortcut's own).</summary>
    public bool PerHost { get; }

    private AgentMemory(string filePath, string subject, bool perHost)
    {
        FilePath = filePath;
        Subject = subject;
        PerHost = perHost;
    }

    /// <summary>The memory a shortcut's agent uses, or null when the shortcut has it off.</summary>
    public static AgentMemory? For(SessionDefinition d, string? directory = null)
    {
        if (!d.AgentMemory)
        {
            return null;
        }

        var dir = directory ?? DefaultDirectory;
        var host = d.Host.Trim().ToLowerInvariant();
        return host.Length > 0 && !d.AgentMemoryOwn
            ? new AgentMemory(Path.Combine(dir, "host-" + SafeName(host) + ".md"), d.Host.Trim(), perHost: true)
            : new AgentMemory(Path.Combine(dir, "shortcut-" + d.Id.ToString("N") + ".md"), d.Name.Trim(), perHost: false);
    }

    /// <summary>A file name from a host: "fe80::1" and "a/b" must not escape the folder or clash.</summary>
    private static string SafeName(string s) => Regex.Replace(s, @"[^a-z0-9._-]", "_");

    public string Read()
    {
        lock (Gate)
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        }
    }

    /// <summary>
    /// Replaces the whole file: the Memory window's Save. Empty leaves the headings and nothing
    /// else, not a deleted file: an emptied memory syncs to other PCs, a missing one does not.
    /// </summary>
    public void Write(string text)
    {
        lock (Gate)
        {
            if (text.Trim().Length == 0)
            {
                File.Delete(FilePath);
                Save(Lines());
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.Write(FilePath, text.Replace("\r\n", "\n"));
        }
    }

    /// <summary>
    /// Keeps a note. Returns null when kept, or why not: the agent is told, so it can say it
    /// without the secret, or leave it.
    /// </summary>
    public string? Remember(string note, string machine, DateTime? now = null)
    {
        note = Regex.Replace(note, @"\s+", " ").Trim();
        if (note.Length == 0)
        {
            return "the note is empty.";
        }

        if (note.Length > MaxNote)
        {
            return $"the note is {note.Length} characters; keep one fact in under {MaxNote}.";
        }

        if (FindSecret(note) is { } secret)
        {
            return $"it looks like it holds a secret (\"{secret}\"). Passwords, keys and tokens are never kept: say the fact without the value.";
        }

        lock (Gate)
        {
            var lines = Lines();
            if (lines.Any(l => l.EndsWith(": " + note, StringComparison.OrdinalIgnoreCase)))
            {
                return null;   // already there, said the same way
            }

            var at = lines.IndexOf(RunsHeading);
            var insert = at < 0 ? lines.Count : at;
            // Before the blank line that ends the notes, if there is one.
            while (insert > 0 && lines[insert - 1].Length == 0)
            {
                insert--;
            }

            lines.Insert(insert, $"- {(now ?? DateTime.Now):yyyy-MM-dd} {machine}: {note}");
            Save(lines);
        }

        return null;
    }

    /// <summary>One line for a finished run: the goal and the first line of its answer.</summary>
    public void RecordRun(string goal, string outcome, string machine, DateTime? now = null)
    {
        static string Short(string s, int max)
        {
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > max ? s[..max] + "..." : s;
        }

        var g = Short(goal, 160);
        var o = Short(outcome.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "", 200);
        if (FindSecret(g) is not null)
        {
            g = "(goal not kept: it looked like it held a secret)";
        }

        if (FindSecret(o) is not null)
        {
            o = "(outcome not kept: it looked like it held a secret)";
        }

        lock (Gate)
        {
            var lines = Lines();
            lines.Add($"- {(now ?? DateTime.Now):yyyy-MM-dd HH:mm} {machine}: \"{g}\" -> {o}");

            // The oldest runs go first; notes stay.
            var runs = lines.Select((l, i) => (l, i)).SkipWhile(x => x.l != RunsHeading).Skip(1)
                .Where(x => x.l.StartsWith("- ", StringComparison.Ordinal)).Select(x => x.i).ToList();
            foreach (var i in runs.Take(Math.Max(0, runs.Count - MaxRuns)).Reverse())
            {
                lines.RemoveAt(i);
            }

            Save(lines);
        }
    }

    /// <summary>
    /// The memory as the agent gets it: every note (the newest when they do not all fit) and the
    /// last few runs, within <paramref name="maxChars"/>. Empty when there is nothing yet.
    /// </summary>
    public string ForPrompt(int maxChars = 6000)
    {
        var lines = Lines();
        var runsAt = lines.IndexOf(RunsHeading);
        var notes = lines.Take(runsAt < 0 ? lines.Count : runsAt).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        var runs = runsAt < 0 ? new List<string>() : lines.Skip(runsAt + 1).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        if (notes.Count == 0 && runs.Count == 0)
        {
            return "";
        }

        var runBudget = Math.Min(1500, maxChars / 4);
        var keptRuns = new List<string>();
        foreach (var r in Enumerable.Reverse(runs).Take(8))
        {
            if (keptRuns.Sum(x => x.Length + 1) + r.Length > runBudget)
            {
                break;
            }

            keptRuns.Insert(0, r);
        }

        var noteBudget = maxChars - keptRuns.Sum(x => x.Length + 1);
        var keptNotes = new List<string>();
        foreach (var n in Enumerable.Reverse(notes))
        {
            if (keptNotes.Sum(x => x.Length + 1) + n.Length > noteBudget)
            {
                break;
            }

            keptNotes.Insert(0, n);
        }

        var sb = new StringBuilder();
        if (keptNotes.Count > 0)
        {
            sb.AppendLine("Notes:");
            if (keptNotes.Count < notes.Count)
            {
                sb.AppendLine($"(the {notes.Count - keptNotes.Count} oldest notes did not fit)");
            }

            keptNotes.ForEach(n => sb.AppendLine(n));
        }

        if (keptRuns.Count > 0)
        {
            sb.AppendLine("Recent runs:");
            keptRuns.ForEach(r => sb.AppendLine(r));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Two copies of one memory (two PCs added to it) as one: every line of <paramref name="mine"/>,
    /// then each note or run line only <paramref name="theirs"/> has, at the end of its section.
    /// Nothing is lost; a line deleted on one PC while the other kept it comes back.
    /// </summary>
    public static string Merge(string mine, string theirs)
    {
        static List<string> Split(string s) => s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        if (mine.Trim().Length == 0)
        {
            return theirs;
        }

        var result = Split(mine);
        if (!result.Contains(NotesHeading))
        {
            result.AddRange(["", NotesHeading]);
        }

        if (!result.Contains(RunsHeading))
        {
            result.AddRange(["", RunsHeading]);
        }

        var have = result.ToHashSet(StringComparer.Ordinal);
        var inRuns = false;
        foreach (var line in Split(theirs))
        {
            if (line == RunsHeading)
            {
                inRuns = true;
                continue;
            }

            if (line == NotesHeading)
            {
                inRuns = false;
                continue;
            }

            if (!line.StartsWith("- ", StringComparison.Ordinal) || !have.Add(line))
            {
                continue;
            }

            if (inRuns)
            {
                result.Add(line);
            }
            else
            {
                var at = result.IndexOf(RunsHeading);
                while (at > 0 && result[at - 1].Length == 0)
                {
                    at--;
                }

                result.Insert(at, line);
            }
        }

        return string.Join("\n", result) + "\n";
    }

    private List<string> Lines()
    {
        var text = Read().Replace("\r\n", "\n");
        if (text.Trim().Length == 0)
        {
            return
            [
                $"# Agent memory: {Subject}",
                "",
                PerHost
                    ? "What the agent keeps about this host, for every shortcut to it. Edit or delete lines as you like."
                    : "What the agent keeps for this shortcut. Edit or delete lines as you like.",
                "",
                NotesHeading,
                "",
                RunsHeading
            ];
        }

        var lines = text.TrimEnd('\n').Split('\n').ToList();
        if (!lines.Contains(NotesHeading))
        {
            lines.Add("");
            lines.Add(NotesHeading);
        }

        if (!lines.Contains(RunsHeading))
        {
            lines.Add("");
            lines.Add(RunsHeading);
        }

        return lines;
    }

    private void Save(List<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        AtomicFile.Write(FilePath, string.Join("\n", lines) + "\n");
    }

    // A secret said in words: a keyword, then a value with a digit or symbol in it ("password is
    // hunter2", "api key: sk-..."). "the password expires on Friday" is not one. Also the forms
    // that are secrets whatever surrounds them: private keys, FortiOS ENC values, crypt hashes.
    private static readonly Regex[] SecretPatterns =
    [
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, TimeSpan.FromMilliseconds(200)),
        new(@"\bENC\s+[A-Za-z0-9+/=]{8,}", RegexOptions.None, TimeSpan.FromMilliseconds(200)),
        new(@"\$\d[a-z]?\$[^\s""']{8,}", RegexOptions.None, TimeSpan.FromMilliseconds(200)),
        new(@"\b(password|passwd|pwd|passphrase|secret|psk|psksecret|pre-?shared[- ]key|api[ _-]?key|token|community|key-string)s?\b" +
            @"[""']?\s*(?:(?:is|was|to|as|=|:|of)\s*)?[""']?(?<value>(?=[^\s""',;)]*[\d!@#$%^&*+=~<>|])[^\s""',;)]{6,})",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))
    ];

    /// <summary>What in the text looks like a secret, or null.</summary>
    public static string? FindSecret(string text)
    {
        foreach (var p in SecretPatterns)
        {
            try
            {
                var m = p.Match(text);
                if (m.Success)
                {
                    var hit = m.Value;
                    return hit.Length > 40 ? hit[..40] + "..." : hit;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return "text too complex to check";
            }
        }

        return null;
    }
}
