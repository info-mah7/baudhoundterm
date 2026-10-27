using Quarterdeck.Core.Agent;

namespace Quarterdeck.App.Views;

/// <summary>A Web tab as the agent's target: everything goes to its <see cref="BrowserView"/>.</summary>
public partial class TerminalView : IWebTarget
{
    private BrowserView WebPage => _browser ?? throw new InvalidOperationException("this is not a Web tab");

    public string Origin => _browser?.Origin ?? "";

    public Task<string> EvalAsync(string script, CancellationToken token) => WebPage.EvalAsync(script, token);

    public Task ClickAsync(double x, double y, CancellationToken token) => WebPage.ClickAsync(x, y, token);

    public Task InsertTextAsync(string text, CancellationToken token) => WebPage.InsertTextAsync(text, token);

    public Task PressAsync(string key, CancellationToken token) => WebPage.PressAsync(key, token);

    public Task<int> NavigateAsync(string url, CancellationToken token) => WebPage.NavigateAsync(url, token);

    public Task<ScreenShot> CaptureAsync(int maxWidth, CancellationToken token) => WebPage.CaptureAsync(maxWidth, token);
}
