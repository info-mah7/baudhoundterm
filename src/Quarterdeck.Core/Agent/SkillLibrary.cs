using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// A skill is a Markdown file of instructions the agent is given for a kind of session: how to
/// drive a Cisco console, what a BaudHound expects on serial, house rules for a server. The
/// header says which protocols it applies to:
/// <code>
/// ---
/// name: Cisco IOS console
/// description: Navigating exec/config modes safely
/// protocols: serial, ssh, telnet
/// ---
/// (instructions)
/// </code>
/// No protocols line means every protocol. "group: Office" files it under that heading in the
/// agent pane, and "default: off" leaves it unticked until the user ticks it.
/// </summary>
public sealed class AgentSkill
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public HashSet<SessionProtocol> Protocols { get; init; } = new();
    public string Body { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Header "group: Office": its heading in the agent pane. Without one, Desktop, Terminal or General.</summary>
    public string Group { get; init; } = "General";

    /// <summary>Header "default: off": unticked until the user ticks it (a game, or a whole suite of apps).</summary>
    public bool DefaultOn { get; init; } = true;

    /// <summary>Header "steps: 300": a task this long needs at least that many steps, whatever the endpoint allows.</summary>
    public int MinSteps { get; init; }

    /// <summary>Header "helpers: chess_read, chess_best": helpers (agent.json) this skill uses.</summary>
    public List<string> Helpers { get; init; } = new();

    /// <summary>
    /// True when the header has a protocols line. A line naming only unknown protocols (a typo
    /// such as "telnett") then applies nowhere instead of everywhere.
    /// </summary>
    public bool Restricted { get; init; }

    /// <summary>A desktop skill ("vnc") is for any remote desktop: RDP and a KVM too.</summary>
    public bool AppliesTo(SessionProtocol protocol) =>
        !Restricted || Protocols.Contains(protocol)
        || (protocol is SessionProtocol.Rdp or SessionProtocol.Kvm) && Protocols.Contains(SessionProtocol.Vnc);
}

public static class SkillLibrary
{
    public static List<AgentSkill> Load(string? directory = null)
    {
        var dir = directory ?? AgentConfig.SkillsDirectory;
        Directory.CreateDirectory(dir);

        if (!Directory.EnumerateFiles(dir, "*.md").Any())
        {
            SeedDefaults(dir);
        }

        OfferBuiltIns(dir);

        var skills = new List<AgentSkill>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                skills.Add(Parse(File.ReadAllText(file), file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A skill being edited right now is skipped this time, not fatal.
            }
        }

        return skills;
    }

    public static AgentSkill Parse(string text, string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var description = "";
        var protocols = new HashSet<SessionProtocol>();
        var restricted = false;
        var minSteps = 0;
        var helpers = new List<string>();
        var group = "";
        var defaultOn = true;
        var body = text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 1 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end > 0)
            {
                foreach (var line in lines[1..end])
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim();

                    switch (key)
                    {
                        case "name": name = value; break;
                        case "description": description = value; break;
                        case "group": group = value; break;
                        case "default": defaultOn = value.ToLowerInvariant() is not ("off" or "no" or "false"); break;
                        case "steps": minSteps = int.TryParse(value, out var n) ? Math.Clamp(n, 0, 1000) : 0; break;
                        // Split(',', ' ', ...) would read ' ' as a count (32), not a second separator.
                        case "helpers": helpers.AddRange(value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                        case "protocols":
                            restricted |= value.Length > 0;
                            foreach (var word in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                if (ProtocolWord(word) is { } p)
                                {
                                    protocols.Add(p);
                                }
                            }

                            break;
                    }
                }

                body = string.Join('\n', lines[(end + 1)..]).Trim();
            }
        }

        if (group.Length == 0)
        {
            group = protocols.Contains(SessionProtocol.Vnc) || protocols.Contains(SessionProtocol.Rdp) || protocols.Contains(SessionProtocol.Kvm) ? "Desktop"
                : protocols.Contains(SessionProtocol.Web) ? "Web"
                : restricted ? "Terminal"
                : "General";
        }

        return new AgentSkill
        {
            Name = name, Description = description, Protocols = protocols, Restricted = restricted, Body = body, Path = path,
            MinSteps = minSteps, Helpers = helpers, Group = group, DefaultOn = defaultOn
        };
    }

    private static SessionProtocol? ProtocolWord(string word) => word.Trim().ToLowerInvariant() switch
    {
        "ssh" => SessionProtocol.Ssh,
        "serial" or "com" => SessionProtocol.Serial,
        "local" or "localshell" or "shell" => SessionProtocol.LocalShell,
        "telnet" => SessionProtocol.Telnet,
        "vnc" or "desktop" => SessionProtocol.Vnc,
        "rdp" => SessionProtocol.Rdp,
        "web" => SessionProtocol.Web,
        "kvm" => SessionProtocol.Kvm,
        _ => null
    };

    /// <summary>
    /// Earlier shipped texts of each built-in skill (SHA-256 of the text with CR removed). A copy
    /// on disk that matches one is untouched by the user and is brought up to date; anything else
    /// is the user's own and is left alone.
    /// </summary>
    private static readonly Dictionary<string, string[]> ShippedBefore = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chess-com.md"] =
        [
            "5482e27a46c2212373f9d8e17ebcd5c86c9ac23afab4fb6051eb5010351a5aed",   // build 40
            "bf2b69b9ea5463f03641eb0249f1cdfe7538e39eb0a223aad4ec8d58953cb933",   // build 41
            "9369f6fc4af92c70d77d37c88a15af7750b5d40df374248c8de053616c0107be",   // build 41, later asset, and 43
            "ea5dafc11bbfab3ae7232f396ae824e87b8085ac3589d83da30d7ab596dfcb61",   // build 44
            "5d920d82f138afa085e7f2619922d8c48d2927ff13204f173d8d0b63fb30289e",   // builds 45 and 46
            "559bf8fd3f5b77d0a3186113c0e8103cab0bd0b31eeb7cdaf114fa18b36fe9a7",   // build 47
            "62d02a3c940c9e932a8e4aca3924a06f2d4e30d48b410a1809a65d377d3c63f1",   // build 48
            "559bf8fd3f5b77d0a3186113c0e8103cab0bd0b31eeb7cdaf114fa18b36fe9a7"    // builds 49 to 54
        ],
        ["windows-desktop.md"] =
        [
            "0f34a1f019122c0cdad203aeaf102be48efacc38d4efd6d9dbee89bde10988cb",   // build 49
            "f36359cbffde750d78acb7c88aab867ea1435076f9bd50b3cebeb4a951c25a36"    // builds 50 to 54
        ]
    };

    /// <summary>
    /// Built-in skills that shipped once and were then withdrawn, by file name and the SHA-256 of
    /// each text shipped. An unedited copy on disk is removed; one the user changed is left alone.
    /// </summary>
    private static readonly Dictionary<string, string[]> RetiredBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["security-review.md"] = ["53348c5092ed19c15755713a99c87ce1aca33aea4056123af069bc9b11ea4ba5"],   // build 56
        ["fortigate-review.md"] = ["3776224ad5dbdaea357d364cc2bbd3980d0bd50b3370e110ea9ba684405071f1"]    // build 56
    };

    private static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r", ""))));

    /// <summary>
    /// Skills shipped in the app (docs/skills) reach an existing skills folder too, and stay up to
    /// date. The ledger .builtin lists each one offered with the hash of the text written: one the
    /// user deleted stays deleted, one the user edited is never overwritten, and an unedited older
    /// copy (written by the app, or copied in from a release) is replaced by the current text.
    /// </summary>
    /// <summary>
    /// Puts back every built-in skill that is missing, even ones the ledger says were deleted (a
    /// user who deleted chess-com.md on advice had no way to get it back from inside the app).
    /// Edited copies stay as they are. Returns the names written.
    /// </summary>
    public static List<string> RestoreBuiltIns(string? directory = null)
    {
        var dir = directory ?? AgentConfig.SkillsDirectory;
        Directory.CreateDirectory(dir);
        var missing = typeof(SkillLibrary).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith("skills/", StringComparison.Ordinal))
            .Select(r => r["skills/".Length..])
            .Where(f => !File.Exists(System.IO.Path.Combine(dir, f)))
            .ToList();
        OfferBuiltIns(dir, restore: true);
        return missing.Where(f => File.Exists(System.IO.Path.Combine(dir, f))).ToList();
    }

    private static void OfferBuiltIns(string dir, bool restore = false)
    {
        var ledgerPath = System.IO.Path.Combine(dir, ".builtin");
        try
        {
            // "name hash" per line; build 43 wrote names only.
            // Built line by line: a name listed twice (an edit by hand) made ToDictionary throw and the
            // agent pane fail to open.
            var ledger = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in (File.Exists(ledgerPath) ? File.ReadAllLines(ledgerPath) : [])
                         .Select(l => l.Trim().Split(' ', 2)).Where(p => p[0].Length > 0))
            {
                ledger[p[0]] = p.Length > 1 ? p[1].Trim() : "";
            }
            var assembly = typeof(SkillLibrary).Assembly;
            var changed = false;
            foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith("skills/", StringComparison.Ordinal)))
            {
                var file = resource["skills/".Length..];
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var hash = Hash(text);
                var path = System.IO.Path.Combine(dir, file);
                var offered = ledger.TryGetValue(file, out var written);

                if (!File.Exists(path))
                {
                    if (offered && !restore)
                    {
                        continue;   // deleted by the user
                    }

                    File.WriteAllText(path, text);
                }
                else
                {
                    var onDisk = Hash(File.ReadAllText(path));
                    var untouched = onDisk == written || ShippedBefore.GetValueOrDefault(file, []).Contains(onDisk);
                    if (onDisk != hash)
                    {
                        if (!untouched)
                        {
                            continue;   // the user's own version
                        }

                        File.WriteAllText(path, text);
                    }
                }

                if (written != hash)
                {
                    ledger[file] = hash;
                    changed = true;
                }
            }

            // Remove skills that shipped once and were withdrawn, if the copy on disk is unedited.
            foreach (var (file, hashes) in RetiredBuiltIns)
            {
                var path = System.IO.Path.Combine(dir, file);
                if (File.Exists(path) && hashes.Contains(Hash(File.ReadAllText(path))))
                {
                    File.Delete(path);
                    if (ledger.Remove(file))
                    {
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                File.WriteAllLines(ledgerPath, ledger.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key} {p.Value}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked folder: the user's own skills still load.
        }
    }

    private static void SeedDefaults(string dir)
    {
        File.WriteAllText(System.IO.Path.Combine(dir, "shell-basics.md"), """
            ---
            name: Shell basics
            description: Careful habits for any command line
            protocols: ssh, telnet, local
            ---
            - Look before acting: read the terminal first, and check where you are (prompt, host,
              current directory) before running anything that changes state.
            - Prefer read-only commands to find things out. Before anything destructive (rm, format,
              reboot, overwriting config) say what it will do in your final answer instead of
              running it, unless the goal explicitly asks for it.
            - Disable pagers so output does not stall: `git --no-pager`, `| cat`, `systemctl --no-pager`,
              `terminal length 0` on network gear.
            - Long-running commands: send them, then use `wait` with an `until` pattern for the prompt.
            - If a command prompts for a password, stop and say so; do not guess credentials.
            """);

        File.WriteAllText(System.IO.Path.Combine(dir, "serial-console.md"), """
            ---
            name: Serial console
            description: Talking to a device over a COM port
            protocols: serial
            ---
            - A serial console may show nothing until you press Enter; send an empty line first.
            - Output can be slow; wait for the prompt rather than assuming a command finished.
            - Boot loaders (U-Boot, ROMMON, ESP-IDF) read single keys; use `send_key` for them.
            - Never send break sequences or interrupt a firmware update in progress.
            """);
    }
}
