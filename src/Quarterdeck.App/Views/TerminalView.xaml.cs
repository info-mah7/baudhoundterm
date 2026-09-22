using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;
using Quarterdeck.Core.Transfer;

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

        // WebView2 is Chromium and will talk to Microsoft on its own account unless told not
        // to: component updates, field-trial "variations", domain reliability, SmartScreen
        // lookups. None of that belongs in a terminal, so it is switched off at the source
        // rather than blocked at the firewall.
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ',
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-domain-reliability",
                "--disable-sync",
                "--disable-breakpad",
                "--no-pings",
                "--no-first-run",
                // Chromium resolves through its own DNS-over-HTTPS by default, which on a
                // stock machine means Cloudflare. A net-log capture showed exactly that and
                // nothing else. The only name this page ever resolves is a local virtual
                // host, so secure DNS buys nothing and costs an outbound connection that
                // bypasses the network's own resolver.
                "--disable-features=OptimizationHints,Translate,msSmartScreenProtection," +
                "EdgeCollections,MsEdgeShoppingAssistant,CalculateNativeWinOcclusion," +
                "DnsOverHttps,DnsOverHttpsUpgrade",
                "--disable-async-dns"),
            EnableTrackingPrevention = true
        };

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userData,
            options: options);

        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        var assets = ResolveAssetsDirectory();

        // Serve the terminal from a virtual host so xterm.js runs on a real origin and
        // navigator.clipboard is available. DenyCors keeps the page from reaching the network.
        core.SetVirtualHostNameToFolderMapping(
            "baudhound.assets", assets, CoreWebView2HostResourceAccessKind.DenyCors);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        // Our own page asking to read our own clipboard is not a decision worth interrupting
        // anyone for, and the prompt appears in the middle of a paste.
        core.PermissionRequested += (_, args) =>
        {
            if (args.PermissionKind is CoreWebView2PermissionKind.ClipboardRead)
            {
                args.State = CoreWebView2PermissionState.Allow;
                args.Handled = true;
            }
        };

        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += OnNavigated;

        core.Navigate(Definition.Protocol == SessionProtocol.Vnc
            ? "https://baudhound.assets/vnc.html"
            : "https://baudhound.assets/terminal.html");
    }

    private LocalSocketBridge? _bridge;

    private void OnNavigated(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (Definition.Protocol == SessionProtocol.Vnc)
        {
            // The bridge starts on the page's "ready", not here: this fires whether or not the
            // scripts ran, and a vncinit posted at a page with no listener yet is simply lost.
            return;
        }

        Post(new
        {
            t = "init",
            theme = Definition.Theme,
            fontFamily = Definition.FontFamily,
            fontSize = Definition.FontSize,
            scrollback = Definition.ScrollbackLines
        });

        Post(new { t = "commands", list = Definition.Commands.Select(c => new
        {
            label = string.IsNullOrWhiteSpace(c.Label) ? c.Text : c.Label
        }) });

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

                if (Definition.Protocol == SessionProtocol.Vnc)
                {
                    StartVnc();
                    break;
                }

                _ = _controller?.ResizeAsync(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                break;

            case "err":
                SetStatus(root.GetProperty("d").GetString() ?? "Script error.");
                break;

            case "probe":
                SetStatus("Bridge probe: " + (root.GetProperty("d").GetString() ?? "?"));
                break;

            case "in":
                SendInput(root.GetProperty("d").GetString() ?? "");
                break;

            case "size":
                _ = _controller?.ResizeAsync(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                break;

            case "title":
                TitleChanged?.Invoke(this, root.GetProperty("d").GetString() ?? Definition.Name);
                break;

            case "cmd":
                var index = root.GetProperty("i").GetInt32();
                if (index >= 0 && index < Definition.Commands.Count)
                {
                    RunShortcut(Definition.Commands[index]);
                }

                break;

            case "editcmd":
                EditCommandsRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "pasteimg":
                _ = PasteImageAsync();
                break;

            case "vnc":
                var state = root.GetProperty("s").GetString();
                SetStatus(state switch
                {
                    "connected" => $"Connected to {Definition.Host}:{Definition.Port}.",
                    "credentials" => "That server wants a password. Set one in Properties.",
                    "failed" => $"Rejected: {(root.TryGetProperty("reason", out var r) ? r.GetString() : "")}",
                    _ => "Disconnected."
                });
                break;
        }
    }

    private static readonly bool DebugInput =
        Environment.GetCommandLineArgs().Contains("--debug-input", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where terminal.html and xterm.js live. For a single-file app AppContext.BaseDirectory can
    /// resolve to the bundle's extraction directory under %TEMP%, which holds no Assets folder and
    /// fails the host mapping with 0x80070003. Environment.ProcessPath is the executable itself.
    /// </summary>
    private static string ResolveAssetsDirectory()
    {
        var candidates = new[]
        {
            Path.GetDirectoryName(Environment.ProcessPath),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var path = Path.Combine(root, "Assets");
            if (File.Exists(Path.Combine(path, "terminal.html")))
            {
                return path;
            }
        }

        throw new DirectoryNotFoundException(
            "The Assets folder was not found next to Quarterdeck.exe. It must travel with the " +
            "executable: extract the whole archive, not just the .exe.");
    }

    /// <summary>
    /// Bring up the loopback bridge and hand noVNC its URL. The password goes to the page
    /// because RFB authentication happens client-side; it never leaves this machine, and the
    /// bridge it talks to only listens on 127.0.0.1.
    /// </summary>
    private void StartVnc()
    {
        try
        {
            _bridge = new LocalSocketBridge(Definition.Host, Definition.Port);
            // Both halves of the handshake report into the page's log as well as the status
            // bar: the status bar shows only the last line, and the order of these against the
            // page's own events is the whole diagnosis when a session stalls.
            // Both halves of the handshake report into the page's log as well as the status bar:
            // the status bar shows only the last line, and the order of these against the page's
            // own events is the whole diagnosis when a session stalls. These fire on the thread
            // pool, and WebView2 may only be touched from the UI thread, hence the dispatch.
            _bridge.Closed += (_, reason) => Note("closed: " + reason);
            _bridge.Note += (_, note) => Note(note);

            Post(new
            {
                t = "vncinit",
                url = _bridge.Url,
                password = _vault.Get(Definition.Id) ?? "",
                viewOnly = Definition.ViewOnly
            });

            SetStatus($"Connecting to {Definition.Host}:{Definition.Port}...");
        }
        catch (Exception ex)
        {
            SetStatus($"VNC bridge failed: {ex.Message}");
        }
    }

    /// <summary>Raised when the user picks "Edit commands..." so the shell can open properties.</summary>
    public event EventHandler? EditCommandsRequested;

    /// <summary>
    /// Puts the clipboard image where the far end can read it and types its path.
    ///
    /// A program running over SSH reads the clipboard of the machine it runs on, not this one,
    /// so an image copied in Windows is invisible to it. Writing the image to a file on the far
    /// end and typing the path is what makes "paste a screenshot into Claude Code" work at all
    /// on a remote host. The path goes in without a newline so it can be typed around.
    /// </summary>
    private async Task PasteImageAsync()
    {
        if (!Clipboard.ContainsImage())
        {
            SetStatus("No image on the clipboard.");
            return;
        }

        try
        {
            var source = Clipboard.GetImage();
            if (source is null)
            {
                SetStatus("The clipboard image could not be read.");
                return;
            }

            var name = $"paste-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var localDir = Path.Combine(Path.GetTempPath(), "quarterdeck");
            Directory.CreateDirectory(localDir);
            var localPath = Path.Combine(localDir, name);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            await using (var file = File.Create(localPath))
            {
                encoder.Save(file);
            }

            var size = new FileInfo(localPath).Length;

            if (Definition.Protocol != SessionProtocol.Ssh)
            {
                // Local shell: the far end is this machine, so the file is already in reach.
                SendInput(Quote(localPath));
                SetStatus($"Image pasted: {localPath} ({size:N0} bytes)");
                return;
            }

            SetStatus($"Uploading image, {size:N0} bytes...");

            var remotePath = $"/tmp/{name}";
            using (var transfer = new FileTransferService(Definition, _vault))
            {
                await transfer.UploadAsync(localPath, remotePath);
            }

            SendInput(Quote(remotePath));
            SetStatus($"Image uploaded to {remotePath} ({size:N0} bytes)");
        }
        catch (Exception ex)
        {
            SetStatus($"Image paste failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Status line under the terminal. Notices go here rather than into the terminal itself,
    /// which belongs to the far end: writing into it lands in the middle of whatever is being
    /// typed.
    /// </summary>
    private void SetStatus(string text) => Dispatcher.BeginInvoke(() => StatusText.Text = text);

    /// <summary>Report a bridge step to both the status bar and the page's own event log.</summary>
    private void Note(string text) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = text;
        Post(new { t = "note", d = text });
    });

    /// <summary>Quote a path only when it needs it, so the common case stays clean.</summary>
    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private void RunShortcut(CommandShortcut shortcut)
    {
        if (shortcut.Confirm)
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                $"Send this to {Definition.Name}?\n\n{shortcut.Text}",
                "Quarterdeck", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SendInput(shortcut.AppendNewline ? shortcut.Text + "\r" : shortcut.Text);
    }

    /// <summary>
    /// Keystrokes on their way to the far end. Fire-and-forget would drop any exception on
    /// the floor and leave a terminal that silently ignores typing, which is exactly the bug
    /// this replaced: report the failure on the screen the user is looking at.
    /// </summary>
    private async void SendInput(string text)
    {
        if (_controller is null)
        {
            return;
        }

        // --debug-input echoes every keystroke the host actually received, locally. If the
        // marker appears, the browser-to-host path is fine and the transport is at fault; if it
        // does not, the keystroke never left the page.
        if (DebugInput)
        {
            Write($"\u001b[35m<{text.Replace("\r", "CR").Replace("\n", "LF")}>\u001b[0m");
        }

        try
        {
            var write = _controller.WriteAsync(text);

            // A write that never completes is the worst failure mode here: the terminal looks
            // alive and silently eats typing. Say so instead of hanging.
            if (await Task.WhenAny(write, Task.Delay(3000)) != write)
            {
                Write("\r\n[baudhoundterm] the transport did not accept input within 3s.\r\n");
                return;
            }

            await write;
        }
        catch (Exception ex)
        {
            Write($"\r\n[baudhoundterm] input not delivered: {ex.GetType().Name}: {ex.Message}\r\n");
        }
    }

    public async Task ConnectAsync()
    {
        // A VNC session is driven entirely by the page: the bridge starts once the document
        // is loaded, so there is no controller and no pty to build here.
        if (Definition.Protocol == SessionProtocol.Vnc)
        {
            return;
        }

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
        if (_bridge is not null)
        {
            Post(new { t = "disconnect" });
            await _bridge.DisposeAsync();
            _bridge = null;
        }

        if (_controller is not null)
        {
            await _controller.DisposeAsync();
            _controller = null;
        }
    }
}
