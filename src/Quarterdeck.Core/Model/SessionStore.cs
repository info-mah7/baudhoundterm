using System.Text.Json;

namespace Quarterdeck.Core.Model;

/// <summary>
/// Reads and writes the session tree as a single JSON file under %APPDATA%\BaudHoundTerm.
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

    /// <summary>Set when <see cref="Load"/> had to set a damaged file aside.</summary>
    public string? LoadProblem { get; private set; }

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

            try
            {
                return JsonSerializer.Deserialize<List<SessionDefinition>>(json, JsonOptions)
                       ?? new List<SessionDefinition>();
            }
            catch (JsonException ex)
            {
                // A truncated or hand-broken file used to stop the app before its window
                // existed. Keep the file (it may be repairable) and start empty, saying so.
                var aside = _path + $".damaged-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(_path, aside);
                LoadProblem = $"sessions.json could not be read ({ex.Message}) and was moved to {aside}. " +
                              "The list starts empty; the old file is kept for repair.";
                return new List<SessionDefinition>();
            }
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
    private const string FolderName = "BaudHoundTerm";
    private const string FormerFolderName = "Quarterdeck";

    public static string DataDirectory
    {
        get
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(roaming, FolderName);

            // The app was called Quarterdeck before. Saved sessions, folders and the
            // DPAPI-encrypted vault live in that directory: move them once rather than
            // silently starting empty and losing every stored password.
            var former = Path.Combine(roaming, FormerFolderName);
            if (!Directory.Exists(dir) && Directory.Exists(former))
            {
                try
                {
                    Directory.Move(former, dir);
                }
                catch (DirectoryNotFoundException)
                {
                    // Two instances started together and the other one moved it first. The new
                    // directory is the real one; recreating the old name here would leave this
                    // instance running against an empty store with none of the user's sessions.
                }
                catch (IOException)
                {
                    // Another instance still holds a file open in the old directory. Share it,
                    // so the user keeps their sessions rather than losing them to a half-move.
                    Directory.CreateDirectory(former);
                    return former;
                }
            }

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
