using System.Text.Json;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// Every agent action, one JSON object per line, one file per day under agent-logs. What was
/// asked, what the model said, every tool call with its arguments (input typed into the session
/// verbatim) and what came back. Append-only; nothing in the app rewrites it.
/// </summary>
public sealed class AgentLog
{
    private static readonly object Gate = new();
    // Relaxed escaping: the log is read by people, and \u0027 for every quote in a shell
    // command makes it unreadable. It is never embedded in HTML.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory;

    public AgentLog(string? directory = null)
    {
        _directory = directory ?? AgentConfig.LogDirectory;
    }

    public string CurrentFile => Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.jsonl");

    public void Write(AgentLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (Gate)
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk must not stop the session; the panel still shows every step.
        }
    }
}

public sealed record AgentLogEntry(
    DateTimeOffset Ts,
    string Run,
    string Session,
    string Target,
    string Endpoint,
    string Model,
    string Event,
    string? Tool = null,
    string? Arguments = null,
    string? Text = null);
