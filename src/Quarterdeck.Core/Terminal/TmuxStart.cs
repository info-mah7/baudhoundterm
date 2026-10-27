using System.Text.RegularExpressions;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// What a tmux session runs when tmux creates it: any program, typed by the user or picked from
/// <see cref="Presets"/>. It goes to the host inside single quotes, so a command may not hold the
/// characters that would end or change that quoting in bash, zsh or fish.
/// </summary>
public static class TmuxStart
{
    /// <summary>No single quote (ends the quoting), backslash (fish reads it inside quotes) or line break.</summary>
    public static bool Ok(string command) => command.IndexOfAny(['\'', '\\', '\n', '\r']) < 0;

    /// <summary>Common programs, filled into the start command and editable there. Empty: a plain shell.</summary>
    public static readonly IReadOnlyList<(string Label, Func<string> Command)> Presets =
    [
        ("A plain shell", () => ""),
        ("Claude Code: the folder's latest conversation", () => "claude --continue || claude"),
        ("Claude Code: always one conversation", () => { var id = Guid.NewGuid(); return $"claude --resume {id} || claude --session-id {id}"; }),
        ("Claude Code, no permission prompts", () => "claude --continue --permission-mode bypassPermissions || claude --permission-mode bypassPermissions"),
        ("Codex", () => "codex"),
        ("Aider", () => "aider"),
        ("htop", () => "htop"),
        ("Follow the system log", () => "journalctl -f"),
    ];

    // Flags with which a coding agent acts without asking first: Claude Code, Codex, Gemini CLI, Aider.
    private static readonly Regex SkipFlags = new(
        @"--permission-mode[\s=]+bypassPermissions|--dangerously-skip-permissions|--dangerously-bypass-approvals-and-sandbox|--yolo\b|--yes-always\b",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    /// <summary>The command makes an agent act without asking first.</summary>
    public static bool SkipsPrompts(string command) => SkipFlags.IsMatch(command);

    /// <summary>The program's name, for the tab mark: "claude" for "claude --continue || claude".</summary>
    public static string Program(string command)
    {
        var first = command.Trim().Split(' ', 2)[0];
        return first[(first.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// A start command from what runs in a session now (its command line, from ps): Claude Code
    /// becomes the folder's-latest form with the same skip flag, since the conversation id it was
    /// started with may be long gone; anything else is taken as it is when it can be quoted.
    /// </summary>
    public static string FromRunning(string args)
    {
        var a = args.Trim();
        if (a.Length == 0)
        {
            return "";
        }

        if (Program(a) == "claude")
        {
            var skip = Regex.Match(a, @"--permission-mode[\s=]+bypassPermissions|--dangerously-skip-permissions", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            var flag = skip.Success ? " " + skip.Value : "";
            return $"claude --continue{flag} || claude{flag}";
        }

        return Ok(a) ? a : "";
    }
}
