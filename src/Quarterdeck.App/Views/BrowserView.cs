using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// A Web tab: a device's admin page in a browser of its own. It has its own profile (cookies and
/// logins stay with this shortcut), no link to the app (no host objects, no messages), and it stays
/// on one site: other sites, new windows and downloads are refused. A self-signed certificate is
/// shown once and remembered, like an SSH host key; a different one later is refused.
/// </summary>
internal sealed class BrowserView : Grid, IDisposable
{
    private readonly SessionDefinition _definition;
    private readonly Action<SessionState, string?> _state;
    private readonly WebView2 _web = new();
    private readonly TextBlock _address = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Uri? _home;
    private TaskCompletionSource<int>? _navigated;

    // The Home tab: BaudHoundTerm's own pages from this folder, and links to other sites opened in
    // the user's browser instead of refused (they are links this app wrote, not a device's).
    private readonly string? _localFolder;
    private readonly Action<string>? _external;

    /// <summary>"https://192.0.2.99:443": the one site this tab may show.</summary>
    public string Origin { get; }

    public BrowserView(SessionDefinition definition, Action<SessionState, string?> state,
                       string? localFolder = null, Action<string>? external = null)
    {
        _definition = definition;
        _state = state;
        _localFolder = localFolder;
        _external = external;
        _home = Home(definition.WebUrl);
        Origin = _home is null ? "" : $"{_home.Scheme}://{_home.Host}:{_home.Port}";

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22)), LastChildFill = true };
        bar.Children.Add(BarButton("←", "Back", () => { if (_web.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack(); }));
        bar.Children.Add(BarButton("⟳", "Reload", () => _web.CoreWebView2?.Reload()));
        var start = localFolder is null ? _home?.AbsoluteUri : HomePage.Url("home.html");
        bar.Children.Add(BarButton("⌂", "Home: " + (start ?? definition.WebUrl), () => { if (start is not null) _web.CoreWebView2?.Navigate(start); }));
        bar.Children.Add(_address);
        SetRow(bar, 0);
        Children.Add(bar);
        SetRow(_web, 1);
        Children.Add(_web);
    }

    /// <summary>The address as typed, made absolute: a bare host or IP means https.</summary>
    public static Uri? Home(string url)
    {
        var u = url.Trim();
        if (u.Length == 0)
        {
            return null;
        }

        if (!u.Contains("://", StringComparison.Ordinal))
        {
            u = "https://" + u;
        }

        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    private static Button BarButton(string glyph, string tip, Action act)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Focusable = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE))
        };
        b.Click += (_, _) => act();
        DockPanel.SetDock(b, Dock.Left);
        return b;
    }

    private bool SameSite(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var u) && _home is not null
        && u.Scheme == _home.Scheme && string.Equals(u.Host, _home.Host, StringComparison.OrdinalIgnoreCase) && u.Port == _home.Port;

    // A refused link is a note, not a connection: a tab whose page never loaded stays Failed.
    private bool _loaded;

    private void Note(string text) => _state(_loaded ? SessionState.Connected : SessionState.Failed, text);

    public async Task StartAsync()
    {
        if (_home is null)
        {
            _state(SessionState.Failed, $"No page to open: \"{_definition.WebUrl}\" is not an http or https address.");
            return;
        }

        _state(SessionState.Connecting, $"Opening {Privacy.Mask(_home.AbsoluteUri, _definition)}...");
        if (_web.CoreWebView2 is { } started)
        {
            // Reconnect: the browser is up and its handlers are on; a second environment would be
            // refused ("already initialized") and the handlers would fire twice. Open the home page again.
            started.Navigate(_home.AbsoluteUri);
            return;
        }

        try
        {
            // A profile of its own per shortcut, apart from the terminal surface's: cookies and logins
            // stay with this device, and a page here never shares a browser process with the bridge pages.
            var root = Elevation.IsCurrentProcessElevated ? Elevation.ElevatedWebViewFolder() : AppPaths.DataDirectory;
            var folder = Path.Combine(root, "web", _definition.Id.ToString("N"));
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = TerminalView.PrivateBrowserArguments,
                EnableTrackingPrevention = true
            });
            await _web.EnsureCoreWebView2Async(env);
            if (_localFolder is not null)
            {
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(HomePage.Host, _localFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            }
        }
        catch (Exception ex)
        {
            _state(SessionState.Failed, "The browser could not start: " + ex.Message);
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.ServerCertificateErrorDetected += OnCertificate;
        core.NavigationStarting += (_, e) =>
        {
            if (!SameSite(e.Uri) && !e.Uri.StartsWith("about:", StringComparison.Ordinal))
            {
                e.Cancel = true;
                if (_external is not null)
                {
                    _external(e.Uri);
                    return;
                }

                Note($"Not opened: {Privacy.Mask(e.Uri, _definition)} is another site. This tab stays on {Privacy.Mask(Origin, _definition)}.");
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (SameSite(e.Uri))
            {
                core.Navigate(e.Uri);
            }
            else if (_external is not null)
            {
                _external(e.Uri);
            }
            else
            {
                Note($"Not opened: {Privacy.Mask(e.Uri, _definition)} is another site.");
            }
        };
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            Note("Downloads are off in a Web tab.");
        };
        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
        core.SourceChanged += (_, _) => _address.Text = Privacy.Mask(core.Source, _definition);
        core.NavigationCompleted += (_, e) =>
        {
            _address.Text = Privacy.Mask(core.Source, _definition);
            _navigated?.TrySetResult(e.HttpStatusCode);
            if (e.IsSuccess)
            {
                _loaded = true;
                _state(SessionState.Connected, null);
            }
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                _state(SessionState.Failed, $"The page did not load ({e.WebErrorStatus}).");
            }
        };

        core.Navigate(_home.AbsoluteUri);
    }

    /// <summary>
    /// A certificate the browser does not trust (self-signed, the usual on a device): shown once with
    /// its fingerprint, kept on a yes; the same one later passes, a different one is refused.
    /// </summary>
    private async void OnCertificate(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.RequestUri);
            var hostPort = $"{uri.Host}:{uri.Port}";
            var pem = e.ServerCertificate.ToPemEncoding();
            var body = string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim()));
            var fingerprint = string.Join(':', SHA256.HashData(Convert.FromBase64String(body)).Select(b => b.ToString("X2")));

            switch (WebCertificates.Check(hostPort, fingerprint))
            {
                case WebCertificates.Verdict.Known:
                    e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                    return;

                case WebCertificates.Verdict.Changed:
                    e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                    _state(SessionState.Failed, "Refused: the certificate changed since you accepted it.");
                    MessageBox.Show(TerminalView.Owner(this),
                        $"{Privacy.Mask(hostPort, _definition)} now shows a different certificate from the one you accepted.\n\n" +
                        $"New: {fingerprint}\n\nThat is what an intercepted connection looks like. If the device's certificate was " +
                        "replaced on purpose, forget the old one in the shortcut's properties and open the tab again.",
                        "BaudHoundTerm: certificate changed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
            }

            var cert = e.ServerCertificate;
            await Task.Yield();
            var yes = MessageBox.Show(TerminalView.Owner(this),
                $"{Privacy.Mask(hostPort, _definition)} has a certificate this PC does not trust ({e.ErrorStatus}). Devices often use " +
                "one of their own. Trust it for this address?\n\n" +
                $"Subject: {cert.Subject}\nIssuer: {cert.Issuer}\nValid: {cert.ValidFrom:yyyy-MM-dd} to {cert.ValidTo:yyyy-MM-dd}\n" +
                $"SHA-256: {fingerprint}\n\nCompare the fingerprint with the device's own (on a FortiGate: System > Certificates). " +
                "It is remembered; a different one later is refused.",
                "BaudHoundTerm: certificate", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (yes)
            {
                WebCertificates.Remember(hostPort, fingerprint);
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            }
            else
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                _state(SessionState.Failed, "Not opened: the certificate was not trusted.");
            }
        }
        catch (Exception ex) when (ex is FormatException or UriFormatException or IOException or UnauthorizedAccessException)
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            _state(SessionState.Failed, "Not opened: the certificate could not be checked (" + ex.Message + ").");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------- what the agent drives (see IWebTarget) ----------

    private Task<T> OnUi<T>(Func<Task<T>> work) => Dispatcher.InvokeAsync(work).Task.Unwrap();

    private CoreWebView2 Core => _web.CoreWebView2 ?? throw new InvalidOperationException("the page is not open yet");

    private Task<string> Cdp(string method, object parameters) =>
        OnUi(() => Core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters)));

    public Task<string> EvalAsync(string script, CancellationToken token) => OnUi(() => Core.ExecuteScriptAsync(script));

    /// <summary>A real click through the browser's input (DevTools protocol), as a mouse would make it.</summary>
    public async Task ClickAsync(double x, double y, CancellationToken token)
    {
        await Cdp("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
        await Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    }

    public Task InsertTextAsync(string text, CancellationToken token) => Cdp("Input.insertText", new { text });

    public async Task PressAsync(string key, CancellationToken token)
    {
        var (code, vk, text) = key switch
        {
            "Enter" => ("Enter", 13, "\r"),
            "Escape" => ("Escape", 27, ""),
            "Tab" => ("Tab", 9, ""),
            "PageDown" => ("PageDown", 34, ""),
            "PageUp" => ("PageUp", 33, ""),
            "Home" => ("Home", 36, ""),
            "End" => ("End", 35, ""),
            "ArrowDown" => ("ArrowDown", 40, ""),
            "ArrowUp" => ("ArrowUp", 38, ""),
            _ => throw new ArgumentException("unknown key " + key)
        };
        await Cdp("Input.dispatchKeyEvent", new { type = text.Length > 0 ? "keyDown" : "rawKeyDown", key, code, windowsVirtualKeyCode = vk, text });
        await Cdp("Input.dispatchKeyEvent", new { type = "keyUp", key, code, windowsVirtualKeyCode = vk });
    }

    public async Task<int> NavigateAsync(string url, CancellationToken token)
    {
        if (!SameSite(url))
        {
            throw new InvalidOperationException("only addresses on " + Origin + " open in this tab");
        }

        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigated = done;
        await Dispatcher.InvokeAsync(() => Core.Navigate(url));
        return await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30), token)) == done.Task ? done.Task.Result : 0;
    }

    public Task<ScreenShot> CaptureAsync(int maxWidth, CancellationToken token) => OnUi(async () =>
    {
        using var raw = new MemoryStream();
        await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, raw);
        raw.Position = 0;
        var frame = BitmapFrame.Create(raw, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource image = frame;
        if (frame.PixelWidth > maxWidth)
        {
            var scale = (double)maxWidth / frame.PixelWidth;
            image = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new ScreenShot(output.ToArray(), image.PixelWidth, image.PixelHeight, frame.PixelWidth, frame.PixelHeight);
    });

    public void Dispose() => _web.Dispose();
}
