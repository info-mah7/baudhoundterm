using System.Text.Json;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Sync;

/// <summary>
/// This PC's sync settings (sync.json, never synced itself): where the repository is, what this
/// machine calls itself in commits, and the values that are local to the machine.
/// </summary>
public sealed class SyncSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string RepoUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>Sync at start, a few seconds after each change, and every few minutes.</summary>
    public bool Automatic { get; set; } = true;

    /// <summary>git.exe to use; blank finds it.</summary>
    public string GitPath { get; set; } = "";

    /// <summary>
    /// Serial port names per shortcut on this PC. COM3 here is another device, or nothing, on
    /// the next machine, so the port never goes into the repository.
    /// </summary>
    public Dictionary<Guid, string> SerialPorts { get; set; } = new();

    /// <summary>Private key file paths per shortcut: a path on this PC means nothing on another.</summary>
    public Dictionary<Guid, string> KeyPaths { get; set; } = new();

    public DateTimeOffset? LastSync { get; set; }
    public string LastResult { get; set; } = "";

    public bool IsConnected => RepoUrl.Length > 0;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "sync.json");

    public static SyncSettings Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            return File.Exists(file)
                ? JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(file), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null) => AtomicFile.Write(path ?? DefaultPath, JsonSerializer.Serialize(this, JsonOptions));
}
