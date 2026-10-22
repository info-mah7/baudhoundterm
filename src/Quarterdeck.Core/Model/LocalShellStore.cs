using System.Text.Json;

namespace Quarterdeck.Core.Model;

/// <summary>
/// What the Shell menu's tabs keep between runs (commands, font, highlights, logging), one entry
/// per shell. They have no shortcut, so edits made from such a tab used to last only until the
/// app closed. A file of its own, read fresh on every save: the elevated copy writes it too, and a
/// snapshot held since startup would undo the other window's edits. Not synced: shells are per PC.
/// </summary>
public static class LocalShellStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly object Gate = new();

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "local-shells.json");

    /// <summary>The saved settings for a shell, or null when there are none (or the file is unreadable).</summary>
    public static SessionDefinition? Load(string key, string? path = null)
    {
        lock (Gate)
        {
            return ReadAll(path ?? DefaultPath).GetValueOrDefault(key);
        }
    }

    public static void Save(string key, SessionDefinition definition, string? path = null)
    {
        var file = path ?? DefaultPath;
        lock (Gate)
        {
            var all = ReadAll(file);
            all[key] = definition;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.Write(file, JsonSerializer.Serialize(all, JsonOptions));
        }
    }

    private static Dictionary<string, SessionDefinition> ReadAll(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, SessionDefinition>>(File.ReadAllText(file), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file costs only these tab settings: the shells open with defaults.
            return new();
        }
    }
}
