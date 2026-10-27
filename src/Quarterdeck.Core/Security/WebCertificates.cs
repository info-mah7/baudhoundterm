using System.Text.Json;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Security;

/// <summary>
/// Certificates a Web tab was told to trust, by host and port: device admin pages nearly always
/// have a self-signed one. Like SSH host keys: the first is shown and kept on a yes; the same one
/// later passes; a different one is refused, since that is what an intercepted connection looks like.
/// </summary>
public static class WebCertificates
{
    public static string StorePath { get; set; } = Path.Combine(AppPaths.DataDirectory, "web_certs.json");

    public enum Verdict { New, Known, Changed }

    private static readonly object Gate = new();

    private static Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public static Verdict Check(string hostPort, string sha256)
    {
        lock (Gate)
        {
            return Load().TryGetValue(hostPort.ToLowerInvariant(), out var known)
                ? string.Equals(known, sha256, StringComparison.OrdinalIgnoreCase) ? Verdict.Known : Verdict.Changed
                : Verdict.New;
        }
    }

    public static void Remember(string hostPort, string sha256)
    {
        lock (Gate)
        {
            var all = Load();
            all[hostPort.ToLowerInvariant()] = sha256;
            AtomicFile.Write(StorePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static void Forget(string hostPort)
    {
        lock (Gate)
        {
            var all = Load();
            if (all.Remove(hostPort.ToLowerInvariant()))
            {
                AtomicFile.Write(StorePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    public static string? Fingerprint(string hostPort)
    {
        lock (Gate)
        {
            return Load().TryGetValue(hostPort.ToLowerInvariant(), out var known) ? known : null;
        }
    }
}
