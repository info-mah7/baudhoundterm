using System.Collections.Concurrent;
using System.Text.Json;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Model;

namespace Quarterdeck.App.Views;

/// <summary>
/// The VNC tab as the agent's screen: requests go to vnc.html with an id, which answers with a
/// picture from the framebuffer or after sending an RFB key or pointer event. Nothing here
/// parses RFB; the page's noVNC connection is the only one there is. An RDP tab hands the same
/// calls to its Remote Desktop control (RdpView.Screen.cs).
/// </summary>
public partial class TerminalView : IScreenTarget
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _agentCalls = new();
    private int _agentCallId;

    public bool ViewOnly => Definition.Protocol == SessionProtocol.Vnc && Definition.ViewOnly;

    /// <summary>The KVM tab's screen, once it has connected; null before that and for other protocols.</summary>
    private IScreenTarget? Kvm => _kvm?.Screen;

    public async Task<ScreenShot> CaptureAsync(ScreenRegion? region, int maxWidth, ScreenMark? mark, CancellationToken token)
    {
        if (_rdp is not null)
        {
            return await _rdp.CaptureAsync(region, maxWidth, mark);
        }

        if (Kvm is { } kvm)
        {
            return await kvm.CaptureAsync(region, maxWidth, mark, token);
        }

        var r = region ?? new ScreenRegion(0, 0, 0, 0);
        var reply = await AskPageAsync(new Dictionary<string, object?>
        {
            ["t"] = "shot", ["x"] = r.X, ["y"] = r.Y, ["w"] = r.Width, ["h"] = r.Height, ["maxw"] = maxWidth,
            ["mark"] = mark is { } g ? new { x = g.X, y = g.Y, r = g.Size } : null
        }, token);

        return new ScreenShot(
            Convert.FromBase64String(reply.GetProperty("data").GetString() ?? ""),
            reply.GetProperty("w").GetInt32(), reply.GetProperty("h").GetInt32(),
            reply.GetProperty("fw").GetInt32(), reply.GetProperty("fh").GetInt32());
    }

    public async Task<byte[]> ThumbnailAsync(CancellationToken token)
    {
        if (_rdp is not null)
        {
            return await _rdp.ThumbnailAsync();
        }

        if (Kvm is { } kvm)
        {
            return await kvm.ThumbnailAsync(token);
        }

        var reply = await AskPageAsync(new Dictionary<string, object?> { ["t"] = "thumb" }, token);
        return reply.GetProperty("grey").EnumerateArray().Select(v => (byte)v.GetInt32()).ToArray();
    }

    public Task PointerAsync(int x, int y, int buttons, CancellationToken token) =>
        _rdp is not null ? _rdp.PointerAsync(x, y, buttons)
        : Kvm is { } kvm ? kvm.PointerAsync(x, y, buttons, token)
        : AskPageAsync(new Dictionary<string, object?> { ["t"] = "ptr", ["x"] = x, ["y"] = y, ["mask"] = buttons }, token);

    public Task KeyAsync(uint keysym, bool down, CancellationToken token) =>
        _rdp is not null ? _rdp.KeyAsync(keysym, down)
        : Kvm is { } kvm ? kvm.KeyAsync(keysym, down, token)
        : AskPageAsync(new Dictionary<string, object?> { ["t"] = "key", ["keysym"] = keysym, ["down"] = down }, token);

    /// <summary>One request to the page, answered by a {t:'agent', id} message or failed after 10 s.</summary>
    private async Task<JsonElement> AskPageAsync(Dictionary<string, object?> message, CancellationToken token)
    {
        if (Definition.Protocol != SessionProtocol.Vnc)
        {
            throw new InvalidOperationException("only a VNC tab has a screen");
        }

        var id = Interlocked.Increment(ref _agentCallId);
        var call = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _agentCalls[id] = call;
        message["id"] = id;

        try
        {
            await Dispatcher.InvokeAsync(() => Post(message));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using (timeout.Token.Register(() => call.TrySetCanceled()))
            {
                try
                {
                    var reply = await call.Task;
                    if (!reply.GetProperty("ok").GetBoolean())
                    {
                        throw new InvalidOperationException(reply.TryGetProperty("err", out var err) ? err.GetString() : "the page refused");
                    }

                    return reply;
                }
                catch (TaskCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("the VNC page did not answer within 10 s");
                }
            }
        }
        finally
        {
            _agentCalls.TryRemove(id, out _);
        }
    }

    /// <summary>Called from OnWebMessage for {t:'agent'} replies.</summary>
    private void OnAgentReply(JsonElement root)
    {
        if (root.TryGetProperty("id", out var id) && _agentCalls.TryRemove(id.GetInt32(), out var call))
        {
            call.TrySetResult(root.Clone());
        }
    }
}
