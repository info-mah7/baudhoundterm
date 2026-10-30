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

    // The file as last read or written: its time and size. The elevated window the Admin button
    // starts is a second instance on the same file, so a different stamp means it saved there.
    private (DateTime Time, long Length) _stamp;

    public CredentialVault(string? path = null)
    {
        _path = path ?? Path.Combine(Model.AppPaths.DataDirectory, "credentials.dat");
        _stamp = Stamp();
        _entries = LoadEntries();
        if (_readFailed)
        {
            // Not read yet, so the next Get tries the file again.
            _stamp = default;
        }
    }

    /// <summary>Set when the store was unreadable and had to be set aside.</summary>
    public string? LoadProblem { get; private set; }

    private static string Key(Guid sessionId, string name) => $"{sessionId:N}:{name}";

    public string? Get(Guid sessionId, string name = "password")
    {
        lock (_gate)
        {
            // A secret the other instance saved since this one last looked.
            if (Stamp() != _stamp)
            {
                Reload();
            }

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
            var value = Convert.ToBase64String(cipher);
            Change(entries => entries[Key(sessionId, name)] = value);
        }
    }

    public void Remove(Guid sessionId, string? name = null)
    {
        lock (_gate)
        {
            Change(entries =>
            {
                if (name is not null)
                {
                    entries.Remove(Key(sessionId, name));
                }
                else
                {
                    var prefix = $"{sessionId:N}:";
                    foreach (var k in entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    {
                        entries.Remove(k);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Applies one change to the store as it is on disk now, not to this instance's copy of it.
    /// Writing the whole in-memory copy let a normal and an elevated window each wipe out the
    /// secrets the other had saved since it started. The lock file keeps the two from reading
    /// and writing at the same moment. Called under _gate.
    /// </summary>
    private void Change(Action<Dictionary<string, string>> change)
    {
        using var crossProcess = LockFile();
        Reload();
        change(_entries);
        if (_readFailed)
        {
            return;
        }

        Quarterdeck.Core.Model.AtomicFile.Write(_path, JsonSerializer.Serialize(_entries));
        _stamp = Stamp();
    }

    /// <summary>
    /// Picks up the file as it is now. A read that fails (the other instance replacing the file
    /// at that moment, say) keeps the copy already in memory, and the stamp stays old so the next
    /// call tries again. A store unreadable at startup that now reads is writable again.
    /// </summary>
    private void Reload()
    {
        var stamp = Stamp();
        if (!File.Exists(_path))
        {
            if (!_readFailed)
            {
                _entries.Clear();
            }

            _stamp = stamp;
            return;
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            _entries = read is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(read, StringComparer.Ordinal);
            _readFailed = false;
            _stamp = stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private (DateTime Time, long Length) Stamp()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>
    /// The store's lock file, held open with no sharing for the length of one change. A file rather
    /// than a named mutex: a mutex the elevated instance creates carries an administrators-only ACL
    /// that the normal window cannot open. If the lock cannot be had in a few seconds the change goes
    /// ahead without it, as it always did, rather than losing the secret being saved.
    /// </summary>
    private IDisposable? LockFile()
    {
        var path = _path + ".lock";
        for (var i = 0; i < 100; i++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
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
}
