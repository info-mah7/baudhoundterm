using System.Text.Json;

namespace Quarterdeck.Core.Security.KeePassXc;

/// <summary>
/// Looks up a shortcut's login in KeePassXC at connect time. Nothing fetched is stored: the
/// password goes straight into the login. The pairing (database id and identification key)
/// lives in the DPAPI vault; with it, anyone running as this user could query KeePassXC, which
/// is why KeePassXC can be told to confirm every access.
/// </summary>
public static class KeePassXcPasswords
{
    /// <summary>Vault slot for the pairings: not a session id, just a fixed key.</summary>
    private static readonly Guid StoreId = new("4b505843-0000-4000-8000-000000000001");

    // One conversation with KeePassXC at a time: two tabs opening together would otherwise
    // raise two pairing dialogs.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static List<KeePassXcAssociation> LoadPairings(CredentialVault vault)
    {
        try
        {
            return JsonSerializer.Deserialize<List<KeePassXcAssociation>>(vault.Get(StoreId, "keepassxc") ?? "[]") ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static void SavePairings(CredentialVault vault, List<KeePassXcAssociation> pairings)
    {
        if (pairings.Count == 0)
        {
            vault.Remove(StoreId);
        }
        else
        {
            vault.Set(StoreId, JsonSerializer.Serialize(pairings), "keepassxc");
        }
    }

    public static void Forget(CredentialVault vault) => SavePairings(vault, new());

    /// <summary>
    /// Connects and proves a pairing for the open database, pairing anew (KeePassXC asks the
    /// user to allow it) when none of the saved ones is known to it.
    /// </summary>
    public static async Task<(KeePassXcClient Client, List<KeePassXcAssociation> Known)> OpenAsync(
        CredentialVault vault, bool pairIfNeeded, CancellationToken token)
    {
        var client = await KeePassXcClient.ConnectAsync(token);
        try
        {
            var pairings = LoadPairings(vault);
            var known = new List<KeePassXcAssociation>();
            foreach (var p in pairings)
            {
                if (await client.TestAssociateAsync(p, token))
                {
                    known.Add(p);
                }
            }

            if (known.Count == 0)
            {
                if (!pairIfNeeded)
                {
                    throw new KeePassXcException("BaudHoundTerm is not paired with the open KeePassXC database.");
                }

                var fresh = await client.AssociateAsync(token);
                pairings.Add(fresh);
                SavePairings(vault, pairings);
                known.Add(fresh);
            }

            return (client, known);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What to ask KeePassXC for. Blank: entries whose URL is ssh://host (the port too, unless
    /// it is 22). A UUID (as KeePassXC shows it, dashes optional): that one entry. Anything with
    /// "://": used as given. Anything else: a host name to match instead of the shortcut's.
    /// </summary>
    public static string QueryFor(string entry, string host, int port)
    {
        var e = entry.Trim();
        var hex = e.Replace("-", "").Replace("{", "").Replace("}", "");
        if (hex.Length == 32 && hex.All(Uri.IsHexDigit))
        {
            return "keepassxc://by-uuid/" + hex.ToLowerInvariant();
        }

        if (e.Contains("://", StringComparison.Ordinal))
        {
            return e;
        }

        var target = e.Length > 0 ? e : host;
        return port is 22 or 0 ? $"ssh://{target}" : $"ssh://{target}:{port}";
    }

    /// <summary>
    /// The login for a connection. With a user name, the entry with that login; without one,
    /// the only match, whose login then becomes the user name.
    /// </summary>
    public static async Task<KeePassXcLogin> LookupAsync(
        CredentialVault vault, string entry, string host, int port, string username, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var (client, known) = await OpenAsync(vault, pairIfNeeded: true, token);
            using (client)
            {
                var query = QueryFor(entry, host, port);
                var logins = await client.GetLoginsAsync(query, known, token);

                if (logins.Count == 0)
                {
                    throw new KeePassXcException(query.StartsWith("keepassxc://", StringComparison.Ordinal)
                        ? $"KeePassXC has no entry {entry.Trim()} (or access was denied)."
                        : $"KeePassXC has no entry for {query}. Give the entry that URL, or put its UUID in the shortcut.");
                }

                if (!string.IsNullOrEmpty(username))
                {
                    var mine = logins.Where(l => l.Login.Equals(username, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (mine.Count == 1 || (mine.Count > 1 && query.StartsWith("keepassxc://", StringComparison.Ordinal)))
                    {
                        return mine[0];
                    }

                    if (mine.Count == 0 && logins.Count == 1 && string.IsNullOrEmpty(logins[0].Login))
                    {
                        return logins[0];
                    }

                    throw new KeePassXcException(mine.Count == 0
                        ? $"KeePassXC has {logins.Count} entr{(logins.Count == 1 ? "y" : "ies")} for {query}, none with user name {username}: {Names(logins)}."
                        : $"KeePassXC has {mine.Count} entries for {username} at {query}: {Names(mine)}. Put the right one's UUID in the shortcut.");
                }

                return logins.Count == 1
                    ? logins[0]
                    : throw new KeePassXcException(
                        $"KeePassXC has {logins.Count} entries for {query}: {Names(logins)}. Set a user name or the entry's UUID in the shortcut.");
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Names(IEnumerable<KeePassXcLogin> logins) =>
        string.Join(", ", logins.Select(l => string.IsNullOrEmpty(l.Login) ? l.Name : $"{l.Name} ({l.Login})"));
}
