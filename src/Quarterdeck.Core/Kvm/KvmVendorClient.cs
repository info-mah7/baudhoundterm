using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quarterdeck.Core.Kvm;

/// <summary>
/// The KVM board's own web interface, used for one thing: the picture.
///
/// agentkvmd serves a screenshot only where the firmware offers an unauthenticated one, and the
/// NanoKVM 2.x firmware does not: it answers "404 page not found". The vendor's own MJPEG stream
/// does work, with the token its login hands out, so the eyes come from there while the hands
/// stay on the daemon.
/// </summary>
public sealed class KvmVendorClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _password;
    private string _token = "";

    public KvmVendorClient(string baseUrl, string user, string password)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _user = user;
        _password = password;
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan   // the stream never ends; each read has its own deadline
        };
    }

    public string BaseUrl { get; }

    /// <summary>
    /// The password and the session token go to this address, which a session can set to
    /// anything (a reverse proxy, another port): the same rule as the daemon's, plain http only
    /// to this machine or a private network. Checked before each request rather than in the
    /// constructor, so the caller's sign-in error handling reports it.
    /// </summary>
    private void Refuse()
    {
        if (KvmClient.Refuse(BaseUrl) is { } why)
        {
            throw new KvmException("The KVM's web interface: " + why);
        }
    }

    /// <summary>The session token from the last sign-in, empty before one. Used to pair the daemon.</summary>
    public string Token => _token;

    /// <summary>
    /// Signs in and keeps the token. The page encrypts the password with a passphrase built into
    /// its own JavaScript before posting it, so a plain one is refused however correct it is.
    /// </summary>
    public async Task SignInAsync(CancellationToken token = default)
    {
        Refuse();
        var body = new JsonObject
        {
            ["username"] = _user,
            ["password"] = Uri.EscapeDataString(Scramble(_password))
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/auth/login")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KvmException($"The KVM's web interface answered {(int)response.StatusCode} to the sign-in.");
        }

        var answer = JsonNode.Parse(text);
        if (answer?["code"]?.GetValue<int>() != 0)
        {
            throw new KvmException($"The KVM's web interface refused the sign-in: {answer?["msg"]?.ToString() ?? text}");
        }

        _token = answer["data"]?["token"]?.ToString()
                 ?? throw new KvmException("The KVM's web interface returned no token.");
    }

    /// <summary>
    /// Reads the MJPEG stream and hands each whole frame to <paramref name="frame"/> until the
    /// token is cancelled. One connection carries every frame; a dropped one is the caller's to retry.
    /// </summary>
    public async Task ReadFramesAsync(Action<byte[]> frame, CancellationToken token)
    {
        Refuse();
        if (_token.Length == 0)
        {
            await SignInAsync(token).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/stream/mjpeg?v={Environment.TickCount}");
        request.Headers.Add("Cookie", $"nano-kvm-token={_token}");
        request.Headers.Add("Authorization", _token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KvmException($"The KVM's video stream answered {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        var held = new List<byte>(512 * 1024);

        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                return;   // the board closed the stream
            }

            held.AddRange(buffer.AsSpan(0, read).ToArray());

            // Frames are JPEG, so they are found by their own markers rather than by parsing the
            // multipart boundary, which some firmware writes inconsistently.
            while (true)
            {
                var start = Find(held, 0xFF, 0xD8, 0);
                if (start < 0)
                {
                    if (held.Count > 4 * 1024 * 1024)
                    {
                        held.Clear();   // nothing that looks like a frame: do not grow for ever
                    }

                    break;
                }

                var end = Find(held, 0xFF, 0xD9, start + 2);
                if (end < 0)
                {
                    if (start > 0)
                    {
                        held.RemoveRange(0, start);   // drop the headers before the frame
                    }

                    break;
                }

                frame(held.GetRange(start, end - start + 2).ToArray());
                held.RemoveRange(0, end + 2);
            }
        }
    }

    private static int Find(List<byte> data, byte first, byte second, int from)
    {
        for (var i = Math.Max(0, from); i < data.Count - 1; i++)
        {
            if (data[i] == first && data[i + 1] == second)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The password as the board's own login page sends it: AES with a passphrase compiled into
    /// that page, in OpenSSL's "Salted__" form (an MD5 key derivation), base64. Not a secret and
    /// not protection: the board simply refuses anything else.
    /// </summary>
    public static string Scramble(string password, string passphrase = "nanokvm-sipeed-2024")
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        var secret = Encoding.UTF8.GetBytes(passphrase);

        // EVP_BytesToKey: MD5 over passphrase and salt, chained until 32 key + 16 IV bytes.
        var material = new List<byte>(48);
        var previous = Array.Empty<byte>();
        while (material.Count < 48)
        {
            previous = MD5.HashData([.. previous, .. secret, .. salt]);
            material.AddRange(previous);
        }

        using var aes = Aes.Create();
        aes.Key = material.Take(32).ToArray();
        aes.IV = material.Skip(32).Take(16).ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(password), aes.IV);
        return Convert.ToBase64String([.. "Salted__"u8.ToArray(), .. salt, .. cipher]);
    }

    public void Dispose() => _http.Dispose();
}
