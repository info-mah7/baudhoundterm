using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// A helper: a small service the agent calls with JSON for work code does better than a model:
/// reading a chess position exactly, choosing a chess move, OCR of a screen region. Declared by
/// the user in agent.json; a skill names the helpers it uses, and a run gets only helpers that are
/// both enabled here and named by a ticked skill. The model cannot add one.
/// </summary>
public sealed class AgentHelper
{
    /// <summary>The tool name the model calls: lower case letters, digits and underscores.</summary>
    public string Name { get; set; } = "";

    /// <summary>Where the JSON arguments are POSTed. The reply body is what the agent reads.</summary>
    public string Url { get; set; } = "";

    /// <summary>What it does and when to call it, for the model.</summary>
    public string Description { get; set; } = "";

    /// <summary>JSON Schema of its arguments. Empty means none.</summary>
    public string Parameters { get; set; } = "";

    /// <summary>Send the current remote-desktop screenshot with each call (as "screenshot", base64 JPEG).</summary>
    public bool Screenshot { get; set; }

    /// <summary>A helper outside this PC. Off by default: arguments, and screenshots if sent, would leave the machine.</summary>
    public bool AllowRemote { get; set; }

    /// <summary>Ask the user before each call, whatever the endpoint's Approval setting.</summary>
    public bool Ask { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A helper built into BaudHoundTerm runs here instead of at a URL: it gets the arguments and,
    /// when <see cref="Screenshot"/> is set, the current screen, and returns the text the agent reads.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<JsonObject, ScreenShot?, CancellationToken, Task<string>>? Run { get; init; }

    /// <summary>Helpers built into the app (registered at startup); offered on every remote-desktop run.</summary>
    public static List<AgentHelper> BuiltIn { get; } = new();

    private static readonly Regex NameRule = new("^[a-z][a-z0-9_]{1,40}$");

    /// <summary>Why this helper cannot be offered, or null when it can.</summary>
    public string? Problem(IEnumerable<string> builtInTools)
    {
        if (!NameRule.IsMatch(Name))
        {
            return $"name \"{Name}\" must be lower case letters, digits and _";
        }

        if (builtInTools.Contains(Name))
        {
            return $"name \"{Name}\" is a built-in tool";
        }

        if (Run is not null)
        {
            return null;                 // built into the app: no address to check
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return $"url \"{Url}\" is not http(s)";
        }

        if (!AllowRemote && !uri.IsLoopback)
        {
            return $"url \"{Url}\" is not on this PC; set allowRemote to use it";
        }

        if (Parameters.Length > 0 && JsonNode.Parse(Parameters) is not JsonObject)
        {
            return "parameters must be a JSON Schema object";
        }

        return null;
    }

    /// <summary>
    /// The helpers a run gets: the app's built-in ones on a remote desktop (they need no setup and
    /// suit any task there), plus configured ones that are enabled, valid and named by a ticked skill.
    /// </summary>
    public static List<AgentHelper> ForRun(IEnumerable<AgentHelper> configured, IEnumerable<AgentSkill> skills, IEnumerable<string> builtInTools,
                                           bool screen = false)
    {
        var wanted = skills.SelectMany(s => s.Helpers).ToHashSet(StringComparer.Ordinal);
        var builtIn = builtInTools.ToList();
        var mine = screen ? BuiltIn.Where(h => h.Problem(builtIn) is null) : [];
        return mine.Concat(configured.Where(h => h.Enabled && wanted.Contains(h.Name) && h.Problem(builtIn) is null))
            .GroupBy(h => h.Name).Select(g => g.First()).ToList();
    }

    public JsonObject ToolDefinition()
    {
        var parameters = Parameters.Length > 0 ? JsonNode.Parse(Parameters)!.AsObject() : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = Name,
                ["description"] = Description + (Screenshot ? " (The current screen is sent with the call; you do not pass it.)" : ""),
                ["parameters"] = parameters
            }
        };
    }
}
