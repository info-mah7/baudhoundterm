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
/// No protocols line means every protocol.
/// </summary>
public sealed class AgentSkill
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public HashSet<SessionProtocol> Protocols { get; init; } = new();
    public string Body { get; init; } = "";
    public string Path { get; init; } = "";

    public bool AppliesTo(SessionProtocol protocol) => Protocols.Count == 0 || Protocols.Contains(protocol);
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

        var skills = new List<AgentSkill>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                skills.Add(Parse(File.ReadAllText(file), file));
            }
            catch (IOException)
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
                        case "protocols":
                            foreach (var word in value.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries))
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

        return new AgentSkill { Name = name, Description = description, Protocols = protocols, Body = body, Path = path };
    }

    private static SessionProtocol? ProtocolWord(string word) => word.Trim().ToLowerInvariant() switch
    {
        "ssh" => SessionProtocol.Ssh,
        "serial" or "com" => SessionProtocol.Serial,
        "local" or "localshell" or "shell" => SessionProtocol.LocalShell,
        "telnet" => SessionProtocol.Telnet,
        _ => null
    };

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
