using System.Text.Json;

namespace Quarterdeck.Core.Model;

/// <summary>
/// Reads and writes the session tree as a single JSON file under %APPDATA%\Quarterdeck.
/// Writes go to a temp file and are moved into place, so a crash mid-save cannot truncate the store.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly string _foldersPath;
    private readonly object _gate = new();

    public SessionStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "sessions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _foldersPath = Path.Combine(Path.GetDirectoryName(_path)!, "folders.json");
    }

    public List<SessionDefinition> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new List<SessionDefinition>();
            }

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<SessionDefinition>();
            }

            return JsonSerializer.Deserialize<List<SessionDefinition>>(json, JsonOptions)
                   ?? new List<SessionDefinition>();
        }
    }

    /// <summary>
    /// Folder paths that exist in their own right. A folder derived only from the sessions in
    /// it would vanish the moment it was empty, so an empty one could never be made first.
    /// </summary>
    public List<string> LoadFolders()
    {
        lock (_gate)
        {
            if (!File.Exists(_foldersPath))
            {
                return new List<string>();
            }

            var json = File.ReadAllText(_foldersPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
        }
    }

    public void SaveFolders(IEnumerable<string> folders)
    {
        lock (_gate)
        {
            var clean = folders
                .Select(f => f.Trim().Trim('/'))
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var temp = _foldersPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(clean, JsonOptions));
            File.Move(temp, _foldersPath, overwrite: true);
        }
    }

    public void Save(IEnumerable<SessionDefinition> sessions)
    {
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(sessions.ToList(), JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }
}

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Quarterdeck");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultLogDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
