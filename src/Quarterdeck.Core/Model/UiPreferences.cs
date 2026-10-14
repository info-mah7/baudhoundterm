using System.Text.Json;

namespace Quarterdeck.Core.Model;

/// <summary>
/// How the window looks, kept apart from the sessions so a view toggle never rewrites the
/// session store. Lives in ui.json next to sessions.json.
/// </summary>
public sealed class UiPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Sessions that share a host sit under one host row; off is a flat list.</summary>
    public bool GroupByHost { get; set; } = true;

    // The TFTP server's last settings, so firmware day starts where it left off.
    public string TftpFolder { get; set; } = "";
    public string TftpAddress { get; set; } = "";
    public int TftpPort { get; set; } = 69;
    public bool TftpUploads { get; set; }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "ui.json");

    /// <summary>Missing or unreadable preferences are not worth failing startup over.</summary>
    public static UiPreferences Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            return File.Exists(file)
                ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(file), JsonOptions) ?? new()
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
        AtomicFile.Write(file, JsonSerializer.Serialize(this, JsonOptions));
    }
}
