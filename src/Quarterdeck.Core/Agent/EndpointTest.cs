using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Quarterdeck.Core.Agent;

/// <summary>One line of a connection test: what was checked, and whether it passed.</summary>
/// <param name="SuggestedExtra">Extra request JSON the test found works better; the form offers it.</param>
public sealed record EndpointCheck(bool Ok, string Text, bool Warning = false, string? SuggestedExtra = null);

/// <summary>
/// "Test connection" for an agent endpoint: can we reach it, does it accept the key, does the
/// model answer, and can it call a tool (the agent cannot work without tool calls). Uses the
/// values as typed, saved or not.
/// </summary>
public static class EndpointTest
{
    public static async Task<List<EndpointCheck>> RunAsync(AgentEndpoint endpoint, string? apiKey, CancellationToken token = default)
    {
        var results = new List<EndpointCheck>();
        var baseUrl = endpoint.BaseUrl.Trim().TrimEnd('/');

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            results.Add(new EndpointCheck(false, "Base URL is not a valid http(s) address."));
            return results;
        }

        if (string.IsNullOrWhiteSpace(endpoint.Model))
        {
            results.Add(new EndpointCheck(false, "No model set. Fill in Model (or use Fetch models)."));
            return results;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        HttpRequestMessage Request(HttpMethod method, string path, JsonObject? body = null)
        {
            var r = new HttpRequestMessage(method, baseUrl + path);
            if (!string.IsNullOrEmpty(apiKey))
            {
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            if (body is not null)
            {
                r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            return r;
        }

        // 1. Reachability, key, and whether the model is offered. Some gateways do not list
        //    models (405), so a failure here is a warning unless it is about the key.
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "/models"), token);
            var text = await response.Content.ReadAsStringAsync(token);

            if ((int)response.StatusCode is 401 or 403)
            {
                results.Add(new EndpointCheck(false, string.IsNullOrEmpty(apiKey)
                    ? $"Reached the server, but it requires an API key and none was sent ({(int)response.StatusCode})."
                    : $"Reached the server, but it refused the API key ({(int)response.StatusCode})."));
                return results;
            }

            if (response.IsSuccessStatusCode)
            {
                List<string> ids;
                try
                {
                    ids = (JsonNode.Parse(text)?["data"] as JsonArray)?
                        .Select(m => m?["id"]?.ToString()).OfType<string>().ToList() ?? new();
                }
                catch (System.Text.Json.JsonException)
                {
                    // A proxy or a wrong URL answering with a web page.
                    results.Add(new EndpointCheck(false, $"{uri.Host} answered /models with something that is not JSON: {Short(text)}"));
                    return results;
                }

                results.Add(new EndpointCheck(true, $"Reached {uri.Host}: {ids.Count} models listed."));

                if (ids.Count > 0 && !ids.Contains(endpoint.Model))
                {
                    results.Add(new EndpointCheck(true,
                        $"\"{endpoint.Model}\" is not in the server's list; check the spelling (Fetch models).", Warning: true));
                }
            }
            else
            {
                results.Add(new EndpointCheck(true, $"Reached {uri.Host}, which does not list models ({(int)response.StatusCode}); testing a reply anyway.", Warning: true));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            results.Add(new EndpointCheck(false, $"Could not reach {uri.Host}: {Plain(ex)}"));
            return results;
        }

        // 2 and 3. A real completion with one tool on offer, which the model is asked to call.
        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Connection test. Call the ping tool with text \"ok\"." }
            },
            ["tools"] = new JsonArray
            {
                JsonNode.Parse("""
                    {"type":"function","function":{"name":"ping","description":"Reply to a connection test.",
                     "parameters":{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}}}
                    """)
            },
            ["tool_choice"] = "auto",
            ["max_tokens"] = 256,
            ["temperature"] = 0
        };

        try
        {
            if (AgentConfig.ParseExtra(endpoint.ExtraJson) is { } extra)
            {
                foreach (var (k, v) in extra)
                {
                    body[k] = v?.DeepClone();
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            results.Add(new EndpointCheck(false, "Extra request JSON is not valid: " + ex.Message));
            return results;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Post, "/chat/completions", body), token);
            var text = await response.Content.ReadAsStringAsync(token);
            var seconds = clock.Elapsed.TotalSeconds;

            if ((int)response.StatusCode is 401 or 403 && string.IsNullOrEmpty(apiKey))
            {
                // Some servers (NVIDIA NIM) list models without a key, so step 1 passes and
                // only the completion shows the key is missing.
                results.Add(new EndpointCheck(false,
                    $"No API key was sent, and {uri.Host} requires one ({(int)response.StatusCode}). Paste the key into API key, then Save."));
                return results;
            }

            if (!response.IsSuccessStatusCode)
            {
                results.Add(new EndpointCheck(false, $"The model did not answer ({(int)response.StatusCode}): {Short(ErrorText(text))}"));
                return results;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(text)?["choices"] is JsonArray { Count: > 0 } choices ? choices[0]?["message"] : null;
            }
            catch (System.Text.Json.JsonException)
            {
                results.Add(new EndpointCheck(false, $"The reply was not JSON: {Short(text)}"));
                return results;
            }

            if (message is null)
            {
                results.Add(new EndpointCheck(false, $"The reply had no choices[0].message: {Short(text)}"));
                return results;
            }

            var calls = message?["tool_calls"] as JsonArray;
            var content = message?["content"]?.ToString() ?? "";

            if (calls is { Count: > 0 })
            {
                var name = calls[0]?["function"]?["name"]?.ToString();
                results.Add(new EndpointCheck(true, $"{endpoint.Model} answered in {seconds:0.0} s."));
                if (AgentRunner.Thinks(message))
                {
                    // Short questions finish thinking in time, so the test above passes; the click
                    // check on a busy screenshot did not, and came back empty every time.
                    results.Add(await ThinkingSwitchAsync(http, Request, body, endpoint, token));
                }
                results.Add(new EndpointCheck(name == "ping", name == "ping"
                    ? "Tool calling works: the agent can drive terminals with this model."
                    : $"It called an unexpected tool \"{name}\"."));
            }
            else if (AgentRunner.OnlyThought(message))
            {
                results.Add(new EndpointCheck(false, AgentRunner.ThinkingHint));
            }
            else
            {
                results.Add(new EndpointCheck(true, $"{endpoint.Model} answered in {seconds:0.0} s" +
                    (content.Length > 0 ? $": \"{Short(content)}\"" : " (empty reply)") + "."));
                results.Add(new EndpointCheck(false,
                    "But it did not call the tool: this model (or this server's settings) may not support tool calling, " +
                    "which the agent needs. On vLLM that means --enable-auto-tool-choice and a tool parser."));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            results.Add(new EndpointCheck(false, $"The completion request failed: {Plain(ex)}"));
            return results;
        }

        // 4. Pictures, which only the remote-desktop (VNC) agent needs: a generated image, left
        //    half red and right half blue, and a question about it. A miss is a warning.
        var picture = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "What colour is the left half of this picture, and the right half? Reply exactly like: left=red right=green" },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(TestPicture()) } }
                    }
                }
            },
            ["max_tokens"] = 256,
            ["temperature"] = 0
        };

        foreach (var (k, v) in AgentConfig.ParseExtra(endpoint.ExtraJson) ?? new JsonObject())
        {
            picture[k] = v?.DeepClone();
        }

        const string noPictures = "so this model cannot drive remote desktops (VNC); use an endpoint whose model takes images for those. Terminals are not affected.";
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Post, "/chat/completions", picture), token);
            var text = await response.Content.ReadAsStringAsync(token);
            var reply = response.IsSuccessStatusCode
                ? (JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.ToString() ?? "").ToLowerInvariant()
                : "";

            results.Add(!response.IsSuccessStatusCode
                ? new EndpointCheck(true, $"Pictures refused ({(int)response.StatusCode}: {Short(ErrorText(text))}), " + noPictures, Warning: true)
                : reply.Contains("red") && reply.Contains("blue") && reply.IndexOf("red", StringComparison.Ordinal) < reply.IndexOf("blue", StringComparison.Ordinal)
                    ? new EndpointCheck(true, "Pictures work: the agent can also drive remote desktops (VNC) with this model.")
                    : new EndpointCheck(true, $"It did not read a test picture correctly (\"{Short(reply)}\"), " + noPictures, Warning: true));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            results.Add(new EndpointCheck(true, $"The picture test failed ({Plain(ex)}), " + noPictures, Warning: true));
        }

        return results;
    }

    /// <summary>
    /// Ways servers take to turn thinking off or down, most common first. There is no standard:
    /// a wrong one is ignored, or rejected with a 400, so each is tried on its own.
    /// </summary>
    internal static readonly string[] ThinkingSwitches =
    [
        """{"chat_template_kwargs":{"enable_thinking":false}}""",   // Qwen 3, GLM on vLLM / SGLang / llama.cpp
        """{"chat_template_kwargs":{"thinking":false}}""",          // DeepSeek V3.1+ on vLLM
        """{"reasoning":{"enabled":false}}""",                       // OpenRouter
        """{"think":false}""",                                       // Ollama
        """{"reasoning_effort":"none"}""",                           // Gemini (OpenAI-compatible)
        """{"reasoning_effort":"minimal"}""",                        // OpenAI GPT-5
        """{"reasoning_effort":"low"}"""                             // o-series, gpt-oss: less, not none
    ];

    /// <summary>
    /// The model thinks: try each switch on the same request and offer the first one that gets an
    /// answer with no reasoning. None works on models that always think (DeepSeek R1, gpt-oss).
    /// </summary>
    private static async Task<EndpointCheck> ThinkingSwitchAsync(HttpClient http, Func<HttpMethod, string, JsonObject?, HttpRequestMessage> request,
        JsonObject body, AgentEndpoint endpoint, CancellationToken token)
    {
        foreach (var candidate in ThinkingSwitches)
        {
            var extra = AgentConfig.ParseExtra(endpoint.ExtraJson) ?? new JsonObject();
            Merge(extra, JsonNode.Parse(candidate)!.AsObject());
            var tryBody = (JsonObject)body.DeepClone();
            foreach (var (k, v) in extra)
            {
                tryBody[k] = v?.DeepClone();
            }

            try
            {
                using var response = await http.SendAsync(request(HttpMethod.Post, "/chat/completions", tryBody), token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?["choices"]?[0]?["message"];
                var answered = reply?["tool_calls"] is JsonArray { Count: > 0 } || !string.IsNullOrWhiteSpace(reply?["content"]?.ToString());
                if (answered && !AgentRunner.Thinks(reply))
                {
                    var json = extra.ToJsonString();
                    return new EndpointCheck(true,
                        $"This model thinks before answering, which can leave remote-desktop clicks with no answer. {candidate} turns it " +
                        "off on this server: it is now in Extra request JSON. Save to keep it.",
                        Warning: true, SuggestedExtra: json);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // This switch is not it; try the next.
            }
        }

        return new EndpointCheck(true,
            "This model thinks before answering, and none of the known switches turned it off on this server. Terminals " +
            "are fine. On remote desktops each click check waits for the thinking (about 20 s on a 27B), and on a confusing " +
            "picture it may never finish, which stops the run: use a model that can answer without thinking for those.",
            Warning: true);
    }

    /// <summary>Copies b into a, merging nested objects so chat_template_kwargs keeps keys already set.</summary>
    internal static void Merge(JsonObject a, JsonObject b)
    {
        foreach (var (k, v) in b)
        {
            if (v is JsonObject inner && a[k] is JsonObject existing)
            {
                Merge(existing, inner);
            }
            else
            {
                a[k] = v?.DeepClone();
            }
        }
    }

    /// <summary>A 64x32 PNG: left half red, right half blue. Built by hand to keep Core free of imaging code.</summary>
    public static byte[] TestPicture()
    {
        const int w = 64, h = 32;
        var raw = new byte[h * (1 + w * 3)];
        for (var y = 0; y < h; y++)
        {
            var row = y * (1 + w * 3);
            for (var x = 0; x < w; x++)
            {
                var i = row + 1 + x * 3;
                (raw[i], raw[i + 1], raw[i + 2]) = x < w / 2 ? ((byte)220, (byte)30, (byte)30) : ((byte)30, (byte)60, (byte)220);
            }
        }

        using var idat = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [0, 0, 0, w, 0, 0, 0, h, 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(BigEndian((uint)data.Length));
        s.Write(typeBytes);
        s.Write(data);
        s.Write(BigEndian(Crc32([.. typeBytes, .. data])));
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static string ErrorText(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString() ?? node?["message"]?.ToString() ?? body;
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }

    private static string Plain(Exception ex) => ex is TaskCanceledException ? "it timed out." : ex.Message;

    private static string Short(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] + "..." : s;
    }
}
