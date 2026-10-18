using System.Text.Json;
using System.Text.Json.Nodes;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// One OpenAI-compatible chat endpoint: vLLM, llama.cpp, Ollama, OpenRouter, OpenAI itself.
/// The API key is not here; it lives in the DPAPI vault under <see cref="Id"/>.
/// </summary>
public sealed class AgentEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New endpoint";

    /// <summary>Base URL up to and including /v1, e.g. http://192.0.2.10:8000/v1.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000/v1";

    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.2;

    /// <summary>Model turns allowed per goal before the agent stops and says so.</summary>
    public int MaxSteps { get; set; } = 30;

    /// <summary>
    /// Raw JSON merged into every request, for what only some servers take, e.g.
    /// {"chat_template_kwargs":{"enable_thinking":false}} for Qwen on vLLM. Blank for none.
    /// </summary>
    public string ExtraJson { get; set; } = "";

    /// <summary>When the agent must ask before typing a command.</summary>
    public AgentApproval Approval { get; set; } = AgentApproval.Risky;

    /// <summary>
    /// Commands that need a yes under <see cref="AgentApproval.Risky"/>: one regular expression
    /// per line, case-insensitive (a line that is not valid regex matches as plain text).
    /// </summary>
    public string RiskyPatterns { get; set; } = DefaultRiskyPatterns;

    /// <summary>
    /// Refuse to run on local shells. They run as the user, who can decrypt the credential
    /// vault, so a model misled by remote output could read every stored secret.
    /// </summary>
    public bool NeverOnLocalShells { get; set; }

    /// <summary>Remote desktops (VNC): screenshots go to the model at most this wide. The model needs vision.</summary>
    public int ScreenshotWidth { get; set; } = 1280;

    /// <summary>
    /// Remote desktops: before each click, show the model a close-up with a ring on the point and
    /// ask whether it is on the named target. A no means no click.
    /// </summary>
    public bool ConfirmClicks { get; set; } = true;

    public const string DefaultRiskyPatterns = """
        \brm\s+(\S+\s+)*(-\w*[rf]|--recursive|--force)
        \b(reboot|shutdown|poweroff|halt)\b
        \bmkfs|\bfdisk\b|\bparted\b|\bwipefs\b
        \bdd\s+.*\bof=
        >\s*/dev/(sd|nvme|mmcblk|hd)
        \bchmod\s+-R\b|\bchown\s+-R\b
        \bsystemctl\s+(stop|disable|mask|isolate)\b
        \bwrite\s+erase\b|\berase\s+(startup|nvram|flash)|\breload\b|\bformat\s+\w+:
        \bdelete\s+(flash|nvram):
        Remove-Item\b.*-Recurse|\b(del|erase|rd|rmdir)\b.*\s/[sq]
        \b(Stop|Restart)-Computer\b|\bFormat-Volume\b|\bClear-Disk\b|\bdiskpart\b
        \bgit\s+(push\s+.*--force|reset\s+--hard|clean\s+-\w*f)
        :\(\)\s*\{
        """;

    public override string ToString() => string.IsNullOrWhiteSpace(Model) ? Name : $"{Name} ({Model})";
}

public enum AgentApproval
{
    /// <summary>Everything runs without asking.</summary>
    Free,

    /// <summary>Commands matching <see cref="AgentEndpoint.RiskyPatterns"/> need a yes.</summary>
    Risky,

    /// <summary>Every command needs a yes.</summary>
    Always
}

public sealed class AgentConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<AgentEndpoint> Endpoints { get; set; } = new();
    public Guid? DefaultEndpoint { get; set; }

    /// <summary>Helper services skills may use (see <see cref="AgentHelper"/>). Edited in agent.json.</summary>
    public List<AgentHelper> Helpers { get; set; } = new();

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "agent.json");
    public static string SkillsDirectory => Path.Combine(AppPaths.DataDirectory, "skills");
    public static string LogDirectory => Path.Combine(AppPaths.DataDirectory, "agent-logs");

    public static AgentConfig Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            return File.Exists(file)
                ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(file), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        var file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Parses <see cref="AgentEndpoint.ExtraJson"/>; a broken value is reported, not ignored.</summary>
    public static JsonObject? ParseExtra(string extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            return null;
        }

        return JsonNode.Parse(extra) as JsonObject
               ?? throw new FormatException("Extra JSON must be an object, like {\"key\": value}.");
    }
}
