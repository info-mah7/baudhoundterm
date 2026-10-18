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

    /// <summary>What Tune found: the settings to apply, and a line per check for the user.</summary>
    public sealed record EndpointTuning(int ContextTokens, string ThinkingOffJson, string ThinkingOnJson,
                                        AgentThinking Recommended, List<EndpointCheck> Notes);

    /// <summary>
    /// Measures this model on this server rather than assuming: the real context window, which
    /// switch (if any) turns thinking off and on, and how long a step takes each way. Nothing is
    /// saved here; the form shows the result and the user applies it.
    /// </summary>
    public static async Task<EndpointTuning> TuneAsync(AgentEndpoint endpoint, string? apiKey, CancellationToken token = default)
    {
        var notes = new List<EndpointCheck>();
        var baseUrl = endpoint.BaseUrl.Trim().TrimEnd('/');
        var context = 0;
        var off = "";
        var on = "";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(endpoint.Model))
        {
            notes.Add(new EndpointCheck(false, "Fill in the base URL and the model first."));
            return new EndpointTuning(0, "", "", AgentThinking.Auto, notes);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

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

        // 1. The context window, as the server reports it for this model.
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "/models"), token);
            if (response.IsSuccessStatusCode)
            {
                var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?["data"] as JsonArray;
                var mine = models?.FirstOrDefault(m => m?["id"]?.ToString() == endpoint.Model) ?? models?.FirstOrDefault();
                foreach (var field in new[] { "max_model_len", "context_length", "max_context_length", "context_window" })
                {
                    if (mine?[field]?.GetValue<object>() is { } raw && int.TryParse(raw.ToString(), out var size) && size > 0)
                    {
                        context = size;
                        notes.Add(new EndpointCheck(true, $"Context window: {size:N0} tokens, as the server reports it."));
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Fall through to the note below.
        }

        if (context == 0)
        {
            notes.Add(new EndpointCheck(true,
                "The server does not say how big this model's context window is; leave Context at 0 for the safe default, " +
                "or type the number from the model's own documentation.", Warning: true));
        }

        // 2. Does it think at all, and what turns that off and on? Timed, so the cost is visible.
        var probe = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "In one short sentence: what is a terminal emulator?" } },
            ["temperature"] = 0,
            ["max_tokens"] = 1024
        };

        var (plainOk, plainThinks, plainMs, plainWhy) = await TryAsync(http, Request, probe, endpoint.ExtraJson, null, token);
        if (!plainOk)
        {
            notes.Add(new EndpointCheck(false, $"The model did not answer a plain question: {plainWhy}"));
            return new EndpointTuning(context, "", "", AgentThinking.Auto, notes);
        }

        notes.Add(new EndpointCheck(true, $"As it stands: {(plainThinks ? "thinks" : "answers straight away")}, {plainMs:N0} ms for a one-line answer."));

        foreach (var candidate in ThinkingSwitches)
        {
            var (ok, thinks, ms, _) = await TryAsync(http, Request, probe, endpoint.ExtraJson, candidate, token);
            if (ok && !thinks)
            {
                off = candidate;
                notes.Add(new EndpointCheck(true, $"Thinking off: {candidate} works here ({ms:N0} ms)."));
                break;
            }
        }

        foreach (var candidate in ThinkingOnSwitches)
        {
            var (ok, thinks, ms, _) = await TryAsync(http, Request, probe, endpoint.ExtraJson, candidate, token);
            if (ok && thinks)
            {
                on = candidate;
                notes.Add(new EndpointCheck(true, $"Thinking on: {candidate} works here ({ms:N0} ms)."));
                break;
            }
        }

        // 3. What a long conversation costs. The model re-reads the whole history on every step,
        //    so the window the server allows is not the window worth using: on a 27B at 0.6 ms a
        //    token, a 60k-token history makes one step take half a minute.
        var suggested = context;
        var (fillOk, _, fillMs, _) = await TryAsync(http, Request, Filler(endpoint.Model, 6000), endpoint.ExtraJson,
            off.Length > 0 ? off : null, token);
        if (fillOk && fillMs > plainMs)
        {
            var perToken = (fillMs - plainMs) / 6000.0;          // ms per token of history
            var affordable = perToken > 0.01 ? (int)(StepBudgetMs / perToken) : context;
            suggested = context > 0 ? Math.Min(context, affordable) : affordable;
            suggested = Math.Max(8_000, suggested / 1000 * 1000);
            notes.Add(new EndpointCheck(true,
                $"Reading history costs about {perToken * 1000:N0} ms per 1,000 tokens here, so a full {(context > 0 ? context : affordable):N0}-token " +
                $"conversation would add about {perToken * (context > 0 ? context : affordable) / 1000:N0} s to every step."));
            if (context > 0 && suggested < context)
            {
                notes.Add(new EndpointCheck(true,
                    $"Suggested context: {suggested:N0} tokens, which keeps a step's share near {StepBudgetMs / 1000:N0} s. " +
                    "Raise it for long tasks that need the history, and expect slower steps.", Warning: true));
            }
        }

        AgentThinking recommended;
        if (off.Length > 0 || (!plainThinks && on.Length > 0))
        {
            recommended = AgentThinking.Off;
            notes.Add(new EndpointCheck(true,
                "Suggested: thinking Off. Every step is one round trip, which matters most on remote desktops, where each " +
                "click is checked against a close-up first. Turn it On for a model you only use on hard, slow tasks."));
        }
        else if (plainThinks)
        {
            recommended = AgentThinking.Auto;
            notes.Add(new EndpointCheck(true,
                "This model always thinks and no switch here turns it off. Expect slow steps, and empty answers on the " +
                "click check; a model that can answer straight away suits remote desktops better.", Warning: true));
        }
        else
        {
            recommended = AgentThinking.Auto;
            notes.Add(new EndpointCheck(true, "This model does not think, so the setting makes no difference here."));
        }

        return new EndpointTuning(suggested, off, on, recommended, notes);
    }

    /// <summary>What one step may spend re-reading the conversation before Tune suggests a smaller window.</summary>
    private const double StepBudgetMs = 8_000;

    /// <summary>A request with about this many tokens of filler, to time how history is read.</summary>
    private static JsonObject Filler(string model, int tokens) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = string.Concat(Enumerable.Repeat("word ", tokens)) + "\nIn one short sentence: what is a terminal emulator?" }
        },
        ["temperature"] = 0,
        ["max_tokens"] = 64
    };

    /// <summary>Switches that turn thinking on, in the same order as the ones that turn it off.</summary>
    internal static readonly string[] ThinkingOnSwitches =
    [
        @"{""chat_template_kwargs"":{""enable_thinking"":true}}",
        @"{""chat_template_kwargs"":{""thinking"":true}}",
        @"{""reasoning"":{""enabled"":true}}",
        @"{""think"":true}",
        @"{""reasoning_effort"":""medium""}"
    ];

    /// <summary>One timed request with an extra switch merged in: did it answer, and did it think?</summary>
    private static async Task<(bool Ok, bool Thinks, double Ms, string Why)> TryAsync(
        HttpClient http, Func<HttpMethod, string, JsonObject?, HttpRequestMessage> request, JsonObject probe,
        string extraJson, string? switchJson, CancellationToken token)
    {
        var body = (JsonObject)probe.DeepClone();
        try
        {
            if (AgentConfig.ParseExtra(extraJson) is { } extra)
            {
                foreach (var (k, v) in extra)
                {
                    body[k] = v?.DeepClone();
                }
            }

            if (switchJson is not null && JsonNode.Parse(switchJson) is JsonObject sw)
            {
                foreach (var (k, v) in sw)
                {
                    body[k] = v?.DeepClone();
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return (false, false, 0, "the Extra request JSON is not valid JSON");
        }

        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request(HttpMethod.Post, "/chat/completions", body), token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                return (false, false, clock.Elapsed.TotalMilliseconds, $"{(int)response.StatusCode}: {Short(text)}");
            }

            var message = JsonNode.Parse(text)?["choices"]?[0]?["message"];
            var answered = message?["tool_calls"] is JsonArray { Count: > 0 } || !string.IsNullOrWhiteSpace(message?["content"]?.ToString());
            return (answered, AgentRunner.Thinks(message), clock.Elapsed.TotalMilliseconds, answered ? "" : "it answered with nothing");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (false, false, clock.Elapsed.TotalMilliseconds, Plain(ex));
        }
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
