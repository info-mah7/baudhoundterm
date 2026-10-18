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
        (_screen is null ? Tools : ScreenTools).Select(t => t?["function"]?["name"]?.ToString() ?? "");

    private JsonArray ToolsForRequest()
    {
        var tools = (_screen is null ? Tools : ScreenTools).DeepClone().AsArray();
        foreach (var h in Helpers)
        {
            tools.Add(h.ToolDefinition());
        }

        return tools;
    }

    private AgentHelper? HelperNamed(string name) => Helpers.FirstOrDefault(h => h.Name == name);

    private async Task<string> CallHelperAsync(AgentHelper helper, JsonObject args, CancellationToken token)
    {
        var shown = $"call helper {helper.Name} {Truncate(args.ToJsonString(), 200)}";
        if (_endpoint.Approval == AgentApproval.Always || helper.Ask)
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
            using var response = await Http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            return response.IsSuccessStatusCode
                ? Truncate(text, 6000)
                : $"error: helper {helper.Name} answered {(int)response.StatusCode}: {Truncate(text, 400)}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return $"error: helper {helper.Name} could not be reached ({ex.Message}). Tell the user it is not running.";
        }
    }
}
