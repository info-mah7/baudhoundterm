using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quarterdeck.Core.Security;

/// <summary>
/// Per-user secret storage. Every value is encrypted with DPAPI under
/// <see cref="DataProtectionScope.CurrentUser"/>, so the file is unreadable by
/// another account on the same machine and useless if copied off it.
/// </summary>
public sealed class CredentialVault
{
    // Do not change: this string is entropy for every stored secret.
    private const string Purpose = "Quarterdeck.v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(Purpose);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _entries;

    // Set when the file exists but could not be read (locked, no access). Writing would then
    // replace an intact store with this session's few entries, so changes stay in memory.
    private bool _readFailed;

    public CredentialVault(string? path = null)
    {
        _path = path ?? Path.Combine(Model.AppPaths.DataDirectory, "credentials.dat");
        _entries = LoadEntries();
    }

    /// <summary>Set when the store was unreadable and had to be set aside.</summary>
    public string? LoadProblem { get; private set; }

    private static string Key(Guid sessionId, string name) => $"{sessionId:N}:{name}";

    public string? Get(Guid sessionId, string name = "password")
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(sessionId, name), out var encoded))
            {
                return null;
            }

            try
            {
                var plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Written by a different user or machine, or a damaged entry. Treat as absent
                // rather than failing the connect, but do not silently delete it: the owner may
                // still be able to read it.
                return null;
            }
        }
    }

    public void Set(Guid sessionId, string secret, string name = "password")
    {
        lock (_gate)
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            _entries[Key(sessionId, name)] = Convert.ToBase64String(cipher);
            Flush();
        }
    }

    public void Remove(Guid sessionId, string? name = null)
    {
        lock (_gate)
        {
            if (name is not null)
            {
                _entries.Remove(Key(sessionId, name));
            }
            else
            {
                var prefix = $"{sessionId:N}:";
                foreach (var k in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                {
                    _entries.Remove(k);
                }
            }

            Flush();
        }
    }

    private Dictionary<string, string> LoadEntries()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable, but possibly intact: leave it alone and do not overwrite it.
            _readFailed = true;
            LoadProblem = $"The credential store {_path} could not be read ({ex.Message}). " +
                          "Saved passwords are unavailable, and changes will not be saved until the app is restarted.";
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt vault must not take the app down, but it must be visible.
            // Thrown from the window's constructor, this used to end the app before it could
            // say anything. Set the file aside, start empty, and let the window report it.
            var quarantine = _path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(_path, quarantine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _readFailed = true;
                LoadProblem = $"The credential store {_path} is damaged and could not be set aside ({ex.Message}). " +
                              "Changes will not be saved until it is moved or deleted.";
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            LoadProblem = $"The credential store could not be read and was moved to {quarantine}. " +
                          "Saved passwords and keys must be entered again.";
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Flush()
    {
        if (_readFailed)
        {
            return;
        }

        Quarterdeck.Core.Model.AtomicFile.Write(_path, JsonSerializer.Serialize(_entries));
    }
}
