using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quarterdeck.Core.Security.KeePassXc;

/// <summary>What KeePassXC remembers us by: its database id and our identification key.</summary>
public sealed record KeePassXcAssociation(string Id, string IdKey);

public sealed record KeePassXcLogin(string Login, string Name, string Password, string Uuid);

public sealed class KeePassXcException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;

    public const int DatabaseNotOpened = 1;
    public const int CancelledOrDenied = 6;
    public const int AssociationFailed = 8;
    public const int NoLoginsFound = 15;
}

/// <summary>
/// KeePassXC's browser-integration protocol, spoken directly to its local socket (a named pipe
/// on Windows) the way keepassxc-proxy would. Each message is a flat JSON object; everything
/// but the key exchange is sealed with NaCl box under per-session keys. KeePassXC asks the
/// user to approve the pairing and, unless told to remember, each entry it hands out.
/// </summary>
public sealed class KeePassXcClient : IDisposable
{
    private const string ServerName = "org.keepassxc.KeePassXC.BrowserServer";

    private readonly Stream _stream;
    private readonly byte[] _publicKey;
    private readonly byte[] _secretKey;
    private readonly string _clientId;
    private byte[] _serverKey = Array.Empty<byte>();
    private readonly List<byte> _pending = new();

    private KeePassXcClient(Stream stream)
    {
        _stream = stream;
        (_publicKey, _secretKey) = NaClBox.KeyPair();
        _clientId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    }

    /// <summary>Whether KeePassXC is running with browser integration on.</summary>
    public static bool IsAvailable => FindEndpoint() is not null;

    public string Version { get; private set; } = "";

    /// <summary>Connects and swaps session keys. Fails if KeePassXC is closed or its browser integration is off.</summary>
    public static async Task<KeePassXcClient> ConnectAsync(CancellationToken token = default)
    {
        var endpoint = FindEndpoint() ?? throw new KeePassXcException(
            "KeePassXC is not running, or browser integration is off (KeePassXC: Settings > Browser Integration > Enable).");

        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, token);
            stream = pipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), token);
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        var client = new KeePassXcClient(stream);
        try
        {
            await client.ChangePublicKeysAsync(token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string? FindEndpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            // Qt names the pipe "/org.keepassxc...BrowserServer_<user>"; match on the stable part.
            return Directory.GetFiles(@"\\.\pipe\")
                .Select(p => p[@"\\.\pipe\".Length..])
                .FirstOrDefault(p => p.Contains(ServerName, StringComparison.Ordinal)
                                     && p.EndsWith("_" + Environment.UserName, StringComparison.OrdinalIgnoreCase));
        }

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
        return new[]
        {
            Path.Combine(runtime, "app", "org.keepassxc.KeePassXC", ServerName),
            Path.Combine(runtime, ServerName)
        }.FirstOrDefault(File.Exists);
    }

    private async Task ChangePublicKeysAsync(CancellationToken token)
    {
        var nonce = RandomNumberGenerator.GetBytes(NaClBox.NonceBytes);
        await SendAsync(new JsonObject
        {
            ["action"] = "change-public-keys",
            ["publicKey"] = Convert.ToBase64String(_publicKey),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["clientID"] = _clientId
        }, token);

        var reply = await ReceiveAsync("change-public-keys", TimeSpan.FromSeconds(10), token);
        ThrowIfError(reply);

        if (reply["success"]?.ToString() != "true" || reply["publicKey"]?.ToString() is not { Length: > 0 } key)
        {
            throw new KeePassXcException("KeePassXC did not accept the key exchange.");
        }

        _serverKey = Convert.FromBase64String(key);
        if (_serverKey.Length != 32)
        {
            // libsodium reads exactly 32 bytes from it.
            throw new InvalidDataException("KeePassXC sent a public key of the wrong length.");
        }
        Version = reply["version"]?.ToString() ?? "";
    }

    /// <summary>
    /// Pairs with the open database. KeePassXC shows a dialog asking the user to name and allow
    /// this connection, so this waits up to two minutes.
    /// </summary>
    public async Task<KeePassXcAssociation> AssociateAsync(CancellationToken token = default)
    {
        var (idKey, _) = NaClBox.KeyPair();
        var reply = await RequestAsync("associate", new JsonObject
        {
            ["key"] = Convert.ToBase64String(_publicKey),
            ["idKey"] = Convert.ToBase64String(idKey)
        }, TimeSpan.FromMinutes(2), token);

        var id = reply["id"]?.ToString();
        return string.IsNullOrEmpty(id)
            ? throw new KeePassXcException("KeePassXC did not return a pairing id.")
            : new KeePassXcAssociation(id, Convert.ToBase64String(idKey));
    }

    /// <summary>True when the open database still knows this pairing. Unlocks the database first if it is locked.</summary>
    public async Task<bool> TestAssociateAsync(KeePassXcAssociation association, CancellationToken token = default)
    {
        try
        {
            await RequestAsync("test-associate", new JsonObject
            {
                ["id"] = association.Id,
                ["key"] = association.IdKey
            }, TimeSpan.FromMinutes(2), token);
            return true;
        }
        catch (KeePassXcException ex) when (ex.Code == KeePassXcException.AssociationFailed)
        {
            return false;
        }
    }

    /// <summary>
    /// Entries matching <paramref name="url"/>: "ssh://host[:port]" matches entries whose URL
    /// names that host, and "keepassxc://by-uuid/&lt;hex&gt;" picks one entry. Needs a successful
    /// <see cref="TestAssociateAsync"/> on this connection first; KeePassXC may ask the user
    /// to allow access.
    /// </summary>
    public async Task<List<KeePassXcLogin>> GetLoginsAsync(string url, IEnumerable<KeePassXcAssociation> associations, CancellationToken token = default)
    {
        var keys = new JsonArray();
        foreach (var a in associations)
        {
            keys.Add(new JsonObject { ["id"] = a.Id, ["key"] = a.IdKey });
        }

        try
        {
            var reply = await RequestAsync("get-logins", new JsonObject { ["url"] = url, ["keys"] = keys }, TimeSpan.FromMinutes(2), token);
            return (reply["entries"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(e => new KeePassXcLogin(
                    e["login"]?.ToString() ?? "",
                    e["name"]?.ToString() ?? "",
                    e["password"]?.ToString() ?? "",
                    e["uuid"]?.ToString() ?? ""))
                .ToList();
        }
        catch (KeePassXcException ex) when (ex.Code == KeePassXcException.NoLoginsFound)
        {
            return new List<KeePassXcLogin>();
        }
    }

    private async Task<JsonObject> RequestAsync(string action, JsonObject inner, TimeSpan timeout, CancellationToken token)
    {
        inner["action"] = action;
        var nonce = RandomNumberGenerator.GetBytes(NaClBox.NonceBytes);
        var sealedMessage = NaClBox.Seal(Encoding.UTF8.GetBytes(inner.ToJsonString()), nonce, _serverKey, _secretKey);

        await SendAsync(new JsonObject
        {
            ["action"] = action,
            ["message"] = Convert.ToBase64String(sealedMessage),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["clientID"] = _clientId,
            ["triggerUnlock"] = "true"
        }, token);

        var reply = await ReceiveAsync(action, timeout, token);
        ThrowIfError(reply);

        // The answer must come back under our nonce plus one: anything else is a replay or a mix-up.
        var replyNonce = Base64(reply["nonce"]);
        if (!replyNonce.AsSpan().SequenceEqual(NaClBox.Increment(nonce)))
        {
            throw new KeePassXcException("KeePassXC's reply did not match the request.");
        }

        var plain = NaClBox.Open(Base64(reply["message"]), replyNonce, _serverKey, _secretKey)
                    ?? throw new KeePassXcException("KeePassXC's reply could not be decrypted.");

        var message = JsonNode.Parse(plain) as JsonObject ?? throw new KeePassXcException("KeePassXC sent an empty reply.");
        ThrowIfError(message);
        return message;
    }

    private static void ThrowIfError(JsonObject reply)
    {
        if (reply["errorCode"] is { } code)
        {
            _ = int.TryParse(code.ToString(), out var n);
            throw new KeePassXcException("KeePassXC: " + (reply["error"]?.ToString() ?? $"error {n}") + ".", n);
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken token)
    {
        await _stream.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), token);
        await _stream.FlushAsync(token);
    }

    /// <summary>
    /// Reads until a reply to <paramref name="action"/> arrives. Replies are bare JSON objects
    /// with no framing, possibly split across reads or run together; KeePassXC also broadcasts
    /// unrequested notices (database-locked and the like), which are skipped.
    /// </summary>
    private async Task<JsonObject> ReceiveAsync(string action, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                while (TakeObject() is { } message)
                {
                    if (message["action"]?.ToString() == action)
                    {
                        return message;
                    }
                }

                var read = await _stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    throw new KeePassXcException("KeePassXC closed the connection.");
                }

                _pending.AddRange(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new KeePassXcException(action is "associate" or "get-logins" or "test-associate"
                ? "KeePassXC did not answer in time (is it waiting for you to unlock or allow access?)."
                : "KeePassXC did not answer in time.");
        }
    }

    private static byte[] Base64(JsonNode? node)
    {
        try
        {
            return Convert.FromBase64String(node?.ToString() ?? "");
        }
        catch (FormatException)
        {
            throw new KeePassXcException("KeePassXC's reply was not readable.");
        }
    }

    private JsonObject? TakeObject()
    {
        // A loop, not recursion: a buffer of garbage braces must not grow the stack per brace.
        while (true)
        {
            var data = _pending.ToArray();
            var start = Array.IndexOf(data, (byte)'{');
            if (start < 0)
            {
                _pending.Clear();
                return null;
            }

            var reader = new Utf8JsonReader(data.AsSpan(start), isFinalBlock: false, state: default);
            try
            {
                if (!JsonDocument.TryParseValue(ref reader, out var document))
                {
                    return null;
                }

                using (document)
                {
                    _pending.RemoveRange(0, start + (int)reader.BytesConsumed);
                    return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
                }
            }
            catch (JsonException)
            {
                // Garbage before a message: drop through the brace and look again.
                _pending.RemoveRange(0, start + 1);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
