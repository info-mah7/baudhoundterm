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

    public CredentialVault(string? path = null)
    {
        _path = path ?? Path.Combine(Model.AppPaths.DataDirectory, "credentials.dat");
        _entries = LoadEntries();
    }

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
            catch (CryptographicException)
            {
                // Written by a different user or machine. Treat as absent rather than crashing,
                // but do not silently delete it: the owner may still be able to read it.
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

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt vault must not take the app down, but it must be visible.
            var quarantine = _path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_path, quarantine);
            throw new InvalidOperationException(
                $"Credential store was unreadable and has been moved to {quarantine}. " +
                "Saved passwords must be re-entered.");
        }
    }

    private void Flush()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
        File.Move(temp, _path, overwrite: true);
    }
}
