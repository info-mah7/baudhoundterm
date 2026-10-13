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

    public override string ToString() => string.IsNullOrWhiteSpace(Model) ? Name : $"{Name} ({Model})";
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
