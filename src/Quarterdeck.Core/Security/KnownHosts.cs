using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Quarterdeck.Core.Security;

/// <summary>What the user is asked about a server's host key.</summary>
public sealed record HostKeyQuestion(string Host, int Port, string Algorithm, string Fingerprint, string? PreviousFingerprint)
{
    /// <summary>True when the server presents a different key from the one trusted before.</summary>
    public bool Changed => PreviousFingerprint is not null;
}

/// <summary>Thrown when the user declines a host key; never worth retrying.</summary>
public sealed class HostKeyRejectedException(string message) : InvalidOperationException(message);

/// <summary>
/// Trust on first use for SSH host keys, like OpenSSH's known_hosts: the first connection to a
/// host asks once and remembers the key's SHA-256 fingerprint; a later connection whose key
/// differs is refused unless the user, warned, accepts the new one. Without this SSH.NET
/// accepts any key, and a machine in the middle receives the password.
/// </summary>
public static class KnownHosts
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Set by the app to ask the user. Unset (tests, no UI) means unknown keys are refused.</summary>
    public static Func<HostKeyQuestion, bool>? Ask { get; set; }

    public static string DefaultPath => Path.Combine(Model.AppPaths.DataDirectory, "known_hosts.json");

    /// <summary>Overridable for tests.</summary>
    public static string StorePath { get; set; } = DefaultPath;

    private sealed record Entry(string Algorithm, string Sha256, DateTimeOffset Added);

    private static string Key(string host, int port) => $"[{host.Trim().ToLowerInvariant()}]:{port}";

    /// <summary>
    /// Hooks a client so its host key is checked before authentication. The returned flag says
    /// whether the user refused it, so the caller can report that instead of a vague
    /// "connection failed" (and not retry it).
    /// </summary>
    public static Func<bool> Attach(BaseClient client, string host, int port)
    {
        var rejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = Verify(host, port, e.HostKeyName, e.FingerPrintSHA256);
            rejected = !e.CanTrust;
        };
        return () => rejected;
    }

    public static bool Verify(string host, int port, string algorithm, string fingerprint)
    {
        var key = Key(host, port);
        Entry? known;
        lock (Gate)
        {
            Load().TryGetValue(key, out known);
        }

        if (known is not null && string.Equals(known.Sha256, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        // Ask outside the lock: the prompt may run on the UI thread, which could itself be
        // waiting to check another host.
        var question = new HostKeyQuestion(host, port, algorithm, fingerprint, known?.Sha256);
        if (Ask?.Invoke(question) != true)
        {
            return false;
        }

        lock (Gate)
        {
            var all = Load();
            all[key] = new Entry(algorithm, fingerprint, DateTimeOffset.Now);
            Save(all);
        }

        return true;
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            // A damaged store must not silently trust everything: set it aside and ask afresh.
            File.Move(StorePath, StorePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}");
            return new();
        }
    }

    private static void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        Quarterdeck.Core.Model.AtomicFile.Write(StorePath, JsonSerializer.Serialize(all, JsonOptions));
    }

    /// <summary>Connect, turning a refused host key into a clear, non-retryable error.</summary>
    public static void Connect(BaseClient client, string host, int port)
    {
        var rejected = Attach(client, host, port);
        try
        {
            client.Connect();
        }
        catch (SshConnectionException) when (rejected())
        {
            throw new HostKeyRejectedException(
                $"Host key for {host}:{port} was not trusted, so the connection was not made.");
        }
    }
}
