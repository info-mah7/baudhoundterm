using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// One terminal tab: an xterm.js surface in WebView2 wired to a <see cref="SessionController"/>.
/// Bytes never round-trip through the UI thread's message loop in bulk; only JSON envelopes do.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly CredentialVault _vault;
    private SessionController? _controller;
    private bool _webReady;
    private readonly Queue<string> _pending = new();

    public TerminalView(SessionDefinition definition, CredentialVault vault)
    {
        InitializeComponent();

        Definition = definition;
        _vault = vault;

        Loaded += OnLoaded;
    }

    public SessionDefinition Definition { get; }

    public SessionController? Controller => _controller;

    /// <summary>Raised when the far end changes the window title, so the tab header can follow.</summary>
    public event EventHandler<string>? TitleChanged;

    public event EventHandler<SessionStateEventArgs>? StateChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var userData = Path.Combine(AppPaths.DataDirectory, "webview");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userData);

        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");

        // Serve the terminal from a virtual host so xterm.js runs on a real origin and
        // navigator.clipboard is available. DenyCors keeps the page from reaching the network.
        core.SetVirtualHostNameToFolderMapping(
            "quarterdeck.assets", assets, CoreWebView2HostResourceAccessKind.DenyCors);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += OnNavigated;

        core.Navigate("https://quarterdeck.assets/terminal.html");
    }

    private void OnNavigated(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Post(new
        {
            t = "init",
            theme = Definition.Theme,
            fontFamily = Definition.FontFamily,
            fontSize = Definition.FontSize,
            scrollback = Definition.ScrollbackLines
        });

        Post(new { t = "highlights", rules = Definition.Highlights.Select(h => new
        {
            pattern = h.Pattern,
            isRegex = h.IsRegex,
            caseSensitive = h.CaseSensitive,
            color = h.Color,
            bold = h.Bold,
            enabled = h.Enabled
        }) });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
        var root = doc.RootElement;
        var kind = root.GetProperty("t").GetString();

        switch (kind)
        {
            case "ready":
                _webReady = true;
                while (_pending.Count > 0)
                {
                    Write(_pending.Dequeue());
                }

                _ = _controller?.ResizeAsync(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                break;

            case "in":
                _ = _controller?.WriteAsync(root.GetProperty("d").GetString() ?? "");
                break;

            case "size":
                _ = _controller?.ResizeAsync(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                break;

            case "title":
                TitleChanged?.Invoke(this, root.GetProperty("d").GetString() ?? Definition.Name);
                break;
        }
    }

    public async Task ConnectAsync()
    {
        _controller = new SessionController(Definition, _vault, PromptForSecret);
        _controller.DataReceived += (_, e) => Dispatcher.BeginInvoke(() => Write(e.Text));
        _controller.StateChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = e.Message ?? e.State.ToString();
            StateChanged?.Invoke(this, e);
        });

        await _controller.ConnectAsync();
    }

    private string? PromptForSecret(string prompt)
    {
        return Dispatcher.Invoke(() =>
        {
            var dialog = new SecretPromptWindow(Definition.Name, prompt)
            {
                Owner = Window.GetWindow(this)
            };

            return dialog.ShowDialog() == true ? dialog.Secret : null;
        });
    }

    private void Write(string text)
    {
        if (!_webReady)
        {
            _pending.Enqueue(text);
            return;
        }

        Post(new { t = "out", d = text });
    }

    private void Post(object message)
    {
        if (Web.CoreWebView2 is null)
        {
            return;
        }

        Web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }

    public void Find() => Post(new { t = "find" });

    public void ClearScreen() => Post(new { t = "clear" });

    public void ApplyHighlights() => OnNavigated(null, null!);

    public async Task DisconnectAsync()
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
            _controller = null;
        }
    }
}
