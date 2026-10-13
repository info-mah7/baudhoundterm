using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Quarterdeck.Core.Agent;

/// <summary>One line of a connection test: what was checked, and whether it passed.</summary>
public sealed record EndpointCheck(bool Ok, string Text, bool Warning = false);

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
                results.Add(new EndpointCheck(false, $"Reached the server, but it refused the API key ({(int)response.StatusCode})."));
                return results;
            }

            if (response.IsSuccessStatusCode)
            {
                var ids = (JsonNode.Parse(text)?["data"] as JsonArray)?
                    .Select(m => m?["id"]?.ToString()).OfType<string>().ToList() ?? new();
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

            if (!response.IsSuccessStatusCode)
            {
                results.Add(new EndpointCheck(false, $"The model did not answer ({(int)response.StatusCode}): {Short(ErrorText(text))}"));
                return results;
            }

            var message = JsonNode.Parse(text)?["choices"]?[0]?["message"];
            var calls = message?["tool_calls"] as JsonArray;
            var content = message?["content"]?.ToString() ?? "";

            if (calls is { Count: > 0 })
            {
                var name = calls[0]?["function"]?["name"]?.ToString();
                results.Add(new EndpointCheck(true, $"{endpoint.Model} answered in {seconds:0.0} s."));
                results.Add(new EndpointCheck(name == "ping", name == "ping"
                    ? "Tool calling works: the agent can drive terminals with this model."
                    : $"It called an unexpected tool \"{name}\"."));
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
        }

        return results;
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
