namespace Quarterdeck.Core.Agent;

/// <summary>
/// A web page the agent can read and drive: a Web tab (WebView2) in the app, a headless browser
/// in tests. The primitives are a browser's own; reading the page and finding controls is done
/// by weboutline.js, which the agent puts into the page itself, so every target behaves alike.
/// </summary>
public interface IWebTarget : IAgentTarget
{
    /// <summary>The one site this tab is for, "https://192.0.2.99:443": goto stays inside it.</summary>
    string Origin { get; }

    /// <summary>Runs a script in the page; returns the value of its last expression as JSON.</summary>
    Task<string> EvalAsync(string script, CancellationToken token);

    /// <summary>A real left click at a point in the page's own pixels (CSS pixels of the view).</summary>
    Task ClickAsync(double x, double y, CancellationToken token);

    /// <summary>Text typed into whatever has focus, as if from the keyboard.</summary>
    Task InsertTextAsync(string text, CancellationToken token);

    /// <summary>One key: Enter, Tab, Escape, PageDown, PageUp, Home, End, ArrowDown, ArrowUp.</summary>
    Task PressAsync(string key, CancellationToken token);

    /// <summary>Opens an address on the site; returns the page's HTTP status, 0 when not known.</summary>
    Task<int> NavigateAsync(string url, CancellationToken token);

    /// <summary>The page as it looks, shrunk to fit maxWidth.</summary>
    Task<ScreenShot> CaptureAsync(int maxWidth, CancellationToken token);
}
