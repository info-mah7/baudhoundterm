using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quarterdeck.Core.Kvm;

/// <summary>A KVM's answer that a request could not be carried out.</summary>
public sealed class KvmException(string message) : Exception(message);

/// <summary>
/// One agentkvm daemon: the hands (USB HID reports to the gadget the target sees as a keyboard
/// and mouse) and, where the board offers one, the eyes (a JPEG of the HDMI input).
///
/// Everything needs the bearer key the board generated; there is no exemption, by design. The
/// daemon speaks plain HTTP, so anything that is not on this machine's own network belongs in a
/// tunnel: the key is the whole of the access control, and whoever has it can type on the target.
/// </summary>
public sealed class KvmClient : IDisposable
{
    /// <summary>The daemon's pointer space: absolute, origin top left, whatever the screen is.</summary>
    public const int AbsMax = 32767;

    private readonly HttpClient _http;
    private readonly string _key;

    public KvmClient(string baseUrl, string key, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _key = key;

        // One connection, kept alive: the board's README notes it drops SYNs now and then, so a
        // fresh connection per request occasionally waits a second where a kept one answers in
        // milliseconds.
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 2,
            AllowAutoRedirect = false
        })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
    }

    public string BaseUrl { get; }

    /// <summary>Whether the daemon is up. Unauthenticated, so it also works before a key is set.</summary>
    public async Task<bool> HealthyAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await _http.GetAsync(BaseUrl + "/healthz", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonNode.Parse(body)?["ok"]?.GetValue<bool>() == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>A JPEG of what the target is showing, or null where this board offers no capture.</summary>
    public async Task<byte[]?> ScreenshotAsync(CancellationToken token = default)
    {
        using var request = Authorised(HttpMethod.Get, "/v1/screenshot");
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);

        // Firmware without an unauthenticated snapshot answers 404 (and a web page, not an image),
        // which is a board without eyes rather than a failure to report.
        if (response.StatusCode is HttpStatusCode.NotFound
            || response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await ThrowIfRefusedAsync(response, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    /// <summary>Where the pointer is and which buttons are down, in the daemon's absolute space.</summary>
    public Task MouseAsync(int buttons, int x, int y, CancellationToken token = default) =>
        PostAsync("/v1/mouse", new JsonObject
        {
            ["buttons"] = buttons,
            ["x"] = Math.Clamp(x, 0, AbsMax),
            ["y"] = Math.Clamp(y, 0, AbsMax)
        }, token);

    /// <summary>One wheel report; negative scrolls the way a wheel pushed away from you does.</summary>
    public Task WheelAsync(int dy, CancellationToken token = default) =>
        PostAsync("/v1/wheel", new JsonObject { ["dy"] = Math.Clamp(dy, -127, 127) }, token);

    /// <summary>
    /// A raw boot-keyboard report: the modifiers held and up to six usages down. It REPLACES what
    /// is held, so an empty call releases everything.
    /// </summary>
    public Task KeysAsync(byte modifier, IReadOnlyList<byte> usages, CancellationToken token = default)
    {
        var keys = new JsonArray();
        foreach (var usage in usages.Where(u => u != 0).Take(6))
        {
            keys.Add(usage);
        }

        return PostAsync("/v1/keys", new JsonObject { ["modifier"] = modifier, ["keys"] = keys }, token);
    }

    /// <summary>
    /// Press and release on the board, which is the point: held past the target's auto-repeat
    /// delay a key duplicates ("notepad" arrives as "noteeeepad"), and two round trips from a
    /// busy host drift far enough apart to do exactly that.
    /// </summary>
    public Task TapAsync(byte modifier, IReadOnlyList<byte> usages, int holdMs = 40, CancellationToken token = default)
    {
        var keys = new JsonArray();
        foreach (var usage in usages.Where(u => u != 0).Take(6))
        {
            keys.Add(usage);
        }

        return PostAsync("/v1/tap", new JsonObject
        {
            ["modifier"] = modifier,
            ["keys"] = keys,
            ["hold_ms"] = Math.Clamp(holdMs, 1, 200)
        }, token);
    }

    /// <summary>Everything up. The pointer stays where it is.</summary>
    public Task ReleaseAsync(CancellationToken token = default) => PostAsync("/v1/release", new JsonObject(), token);

    private HttpRequestMessage Authorised(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return request;
    }

    private async Task PostAsync(string path, JsonObject body, CancellationToken token)
    {
        using var request = Authorised(HttpMethod.Post, path);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        await ThrowIfRefusedAsync(response, token).ConfigureAwait(false);
    }

    private static async Task ThrowIfRefusedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
        if (body.Length > 300)
        {
            body = body[..300] + "...";
        }

        throw new KvmException((int)response.StatusCode switch
        {
            401 or 403 => "The KVM refused the key.",
            // The daemon's own answer when the target is not reading that HID interface: the
            // machine is off, asleep, or has not enumerated the gadget yet.
            503 => $"The KVM could not deliver the report: {body}",
            _ => $"The KVM answered {(int)response.StatusCode}: {body}"
        });
    }

    public void Dispose() => _http.Dispose();
}
