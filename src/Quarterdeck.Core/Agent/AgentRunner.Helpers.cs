using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Quarterdeck.Core.Agent;

public sealed partial class AgentRunner
{
    /// <summary>Helpers for this run (see <see cref="AgentHelper.ForRun"/>); set before the first goal.</summary>
    public IReadOnlyList<AgentHelper> Helpers { get; set; } = Array.Empty<AgentHelper>();

    /// <summary>The names of the tools the agent always has, so a helper cannot take one over.</summary>
    public IEnumerable<string> BuiltInToolNames =>
        ModeTools.Select(t => t?["function"]?["name"]?.ToString() ?? "");

    private JsonArray ModeTools => _web is not null ? WebTools : _screen is not null ? ScreenTools : Tools;

    private JsonArray ToolsForRequest()
    {
        var tools = ModeTools.DeepClone().AsArray();
        if (_target is IRecordable)
        {
            tools.Add(RecordingTool.DeepClone());
        }

        foreach (var h in Helpers)
        {
            tools.Add(h.ToolDefinition());
        }

        return tools;
    }

    private AgentHelper? HelperNamed(string name) => Helpers.FirstOrDefault(h => h.Name == name);

    /// <summary>Offered only on a tab that can be recorded (<see cref="IRecordable"/>).</summary>
    private static readonly JsonNode RecordingTool = JsonNode.Parse("""
        {"type":"function","function":{"name":"recording",
          "description":"Record a video for a demo. start begins a new file; pause and resume skip a stretch without a gap in the video; stop finishes the file and returns its path; status says what is happening. Addresses are hidden while it runs.",
          "parameters":{"type":"object","required":["action"],"properties":{
            "action":{"type":"string","enum":["start","pause","resume","stop","status"]},
            "scope":{"type":"string","enum":["tab","window"],"description":"For start: tab (the terminal and this agent pane, the default) or window (all of BaudHoundTerm)."}}}}}
        """)!;

    private async Task<string> CallHelperAsync(AgentHelper helper, JsonObject args, CancellationToken token)
    {
        var shown = $"call helper {helper.Name} {Truncate(args.ToJsonString(), 200)}";
        if (_endpoint.Approval == AgentApproval.Always || helper.Ask || Tripped)
        {
            var ok = Approve is not null && await Approve(shown);
            Log(ok ? "approved" : "denied", tool: helper.Name, text: shown);
            if (!ok)
            {
                return "denied: the user did not allow this helper call.";
            }
        }

        if (helper.Run is { } run)
        {
            Emit("action", $"helper {helper.Name}");
            var screen = helper.Screenshot && _screen is not null ? await _screen.CaptureAsync(null, 4096, null, token) : null;
            try
            {
                return Truncate(await run((JsonObject)args.DeepClone(), screen, token), 6000);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"error: helper {helper.Name} failed: {ex.Message}";
            }
        }

        var body = (JsonObject)args.DeepClone();
        if (helper.Screenshot && _screen is not null)
        {
            var shot = await _screen.CaptureAsync(null, 4096, null, token);
            body["screenshot"] = Convert.ToBase64String(shot.Jpeg);
            body["screen_width"] = shot.ScreenWidth;
            body["screen_height"] = shot.ScreenHeight;
        }

        Emit("action", $"helper {helper.Name}");
        using var request = new HttpRequestMessage(HttpMethod.Post, helper.Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await HelperHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return $"error: helper {helper.Name} answered with a redirect, which is not followed. Tell the user.";
            }

            var text = await ReadCappedAsync(response, cts.Token);
            return response.IsSuccessStatusCode
                ? Truncate(text, 6000)
                : $"error: helper {helper.Name} answered {(int)response.StatusCode}: {Truncate(text, 400)}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return $"error: helper {helper.Name} could not be reached ({ex.Message}). Tell the user it is not running.";
        }
    }

    // Helpers get their own client that never follows a redirect: a 307 from a loopback helper
    // would re-send the request, screenshot included, to wherever it pointed, past allowRemote.
    private static readonly HttpClient HelperHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    /// <summary>At most 64 KB of a helper's answer: only the first 6000 characters are used anyway.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[64 * 1024];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), token);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
