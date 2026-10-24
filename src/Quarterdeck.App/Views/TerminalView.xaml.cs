using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;
using Quarterdeck.Core.Transfer;

namespace Quarterdeck.App.Views;

/// <summary>
/// One terminal tab: an xterm.js surface in WebView2 wired to a <see cref="SessionController"/>.
/// Bytes never round-trip through the UI thread's message loop in bulk; only JSON envelopes do.
/// </summary>
public partial class TerminalView : UserControl, ITerminalTarget
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

        BuildLamps();
        _flush.Tick += (_, _) => FlushIncoming();
        Loaded += OnLoaded;
    }

    public SessionDefinition Definition { get; }

    /// <summary>Which of the shortcut's tmux sessions this tab runs in; blank for the main one.</summary>
    public string TmuxSessionName { get; init; } = "";

    public SessionController? Controller => _controller;

    /// <summary>Raised when the far end changes the window title, so the tab header can follow.</summary>
    public event EventHandler<string>? TitleChanged;

    public event EventHandler<SessionStateEventArgs>? StateChanged;

    private SessionState _state = SessionState.Connecting;

    /// <summary>Whether the tab holds a live (or coming-up) connection: what decides if the
    /// toolbar offers Disconnect or Reconnect, and whether Enter types or reconnects.</summary>
    public bool IsConnected => _state is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting;

    private void RaiseState(SessionState state, string? message)
    {
        _state = state;
        ShowLinkState();
        StateChanged?.Invoke(this, new SessionStateEventArgs(state, message));
    }

    private CameraView? _camera;
    private readonly TaskCompletionSource _cameraReady = new();
    private RdpView? _rdp;
    private readonly TaskCompletionSource _rdpReady = new();
    private KvmView? _kvm;
    private readonly TaskCompletionSource _kvmReady = new();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (Definition.Protocol == SessionProtocol.Rtsp)
        {
            // A camera is video, not a page: LibVLC draws it, and the web surface is never made.
            Web.Visibility = Visibility.Collapsed;
            _camera = new CameraView(Definition, _vault, (state, message) =>
            {
                ShowStatus(message ?? state.ToString());
                RaiseState(state, message);
            });
            ((Grid)Web.Parent).Children.Add(_camera);
            _cameraReady.TrySetResult();
            return;
        }

        if (Definition.Protocol == SessionProtocol.Rdp)
        {
            // Windows' Remote Desktop control draws the desktop; there is no web page either.
            Web.Visibility = Visibility.Collapsed;
            _rdp = new RdpView(Definition, _vault, (state, message) =>
            {
                ShowStatus(message ?? state.ToString());
                RaiseState(state, message);
            }, PromptForSecret);
            ((Grid)Web.Parent).Children.Add(_rdp);
            _rdpReady.TrySetResult();
            return;
        }

        if (Definition.Protocol == SessionProtocol.Kvm)
        {
            // A KVM draws a picture from the board's capture; no web page, no pty.
            Web.Visibility = Visibility.Collapsed;
            _kvm = new KvmView(Definition, _vault, (state, message) =>
            {
                ShowStatus(message ?? state.ToString());
                RaiseState(state, message);
            }, PromptForSecret);
            ((Grid)Web.Parent).Children.Add(_kvm);
            _kvmReady.TrySetResult();
            return;
        }

        // A WebView2 user data folder belongs to one browser process family. An elevated copy of
        // BaudHoundTerm cannot attach to the one a medium-integrity copy already started, and the
        // failure surfaces as ERROR_INVALID_STATE (0x8007139F), so the two get separate folders.
        string userData;
        try
        {
            userData = Elevation.IsCurrentProcessElevated
                ? Elevation.ElevatedWebViewFolder()
                : Path.Combine(AppPaths.DataDirectory, "webview");
            Directory.CreateDirectory(userData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"The terminal surface could not start, so the session was closed: no safe browser profile folder ({ex.Message}).");
            await DisconnectAsync();
            return;
        }

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

        // This runs from an async void handler, so anything thrown here reaches the dispatcher and
        // takes the whole window down with every open session in it. A terminal that cannot start
        // is one dead tab, not a dead app.
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData,
                options: options);

            await Web.EnsureCoreWebView2Async(env);

            // Serve the terminal from a virtual host so xterm.js runs on a real origin and
            // navigator.clipboard is available. DenyCors keeps the page from reaching the network.
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "baudhound.assets", ResolveAssetsDirectory(), CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch (Exception ex)
        {
            // The connection is already being made in parallel. Without a surface its output
            // goes nowhere and typing is lost, so end it rather than leave it live and invisible.
            SetStatus($"The terminal surface could not start, so the session was closed: {ex.Message} ({userData})");
            await DisconnectAsync();
            return;
        }

        var core = Web.CoreWebView2;

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
            if (args.PermissionKind is CoreWebView2PermissionKind.ClipboardRead
                && args.Uri.StartsWith(AssetOrigin, StringComparison.OrdinalIgnoreCase))
            {
                args.State = CoreWebView2PermissionState.Allow;
                args.Handled = true;
            }
        };

        // The page never leaves our own assets, and opens no windows of its own: a page from
        // anywhere else would get the bridge that types into the session (as administrator, in
        // an elevated window). Links go through OpenLink instead.
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith(AssetOrigin, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        core.FrameNavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith(AssetOrigin, StringComparison.OrdinalIgnoreCase) && args.Uri != "about:blank")
            {
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;

        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += OnNavigated;

        core.Navigate(Definition.Protocol == SessionProtocol.Vnc
            ? "https://baudhound.assets/vnc.html"
            : "https://baudhound.assets/terminal.html");
    }

    private LocalSocketBridge? _bridge;

    /// <summary>
    /// A link clicked in the terminal, in the default browser: http and https only, never a
    /// file, UNC path or other protocol. An OSC 8 link's text is chosen by the server and can
    /// differ from where it goes, so its address is shown first.
    /// </summary>
    /// <summary>
    /// A paste of more than one line, shown before it goes in: each line break runs a command,
    /// and the text may have been put on the clipboard by a web page or a VNC server.
    /// </summary>
    private void ConfirmPaste(string text, int lines)
    {
        var shown = string.Join("\n", text.Split('\n').Take(8).Select(l => l.TrimEnd('\r') is var t && t.Length > 100 ? t[..100] + "..." : t));
        if (lines > 8)
        {
            shown += $"\n... and {lines - 8} more";
        }

        if (MessageBox.Show(Window.GetWindow(this)!,
                $"Paste {lines} lines into {Definition.Name}? Each line break runs it as a command.\n\n{shown}",
                "BaudHoundTerm: paste", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            Post(new { t = "pasteok", d = text });
        }
    }

    private void OpenLink(string uri, bool osc8)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https") || link.IsUnc)
        {
            SetStatus("Not opened: only http and https links open from the terminal.");
            return;
        }

        if (osc8 && MessageBox.Show(Window.GetWindow(this)!, $"Open this address in your browser?\n\n{link.AbsoluteUri}",
                "BaudHoundTerm: open link", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus("The link could not be opened: " + ex.Message);
        }
    }

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

        PostSettings();
    }

    /// <summary>
    /// Sends the shortcut's command labels and highlight rules to the page. Called again after
    /// every edit of the shortcut: the page runs a command by its index, so labels that lag the
    /// definition would run a different command than the one clicked.
    /// </summary>
    private void PostSettings()
    {
        if (Definition.Protocol == SessionProtocol.Vnc)
        {
            return;
        }

        Post(new { t = "wheel", arrows = Definition.WheelSendsArrows });

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

    /// <summary>
    /// Puts the keyboard on the terminal. The page focuses xterm.js when it opens, but that only
    /// decides where keys go once the WebView itself has focus; without this the toolbar button
    /// that opened the tab keeps it, the first thing typed goes nowhere, and Space or Enter
    /// presses that button again. The page is then told to focus xterm (or the VNC canvas) itself,
    /// because its own term.focus() ran before the WebView had the keyboard. A tab that is not
    /// showing does not take focus: a slow session opening behind the active one must not steal it.
    /// </summary>
    public void FocusTerminal() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        // Before the WebView exists, Focus() only marks the control focused in WPF; the browser
        // window never gets the keyboard, and the real call on "ready" is then a no-op because
        // WPF thinks focus is already there. That is what lost the first keys after a
        // double-click in the session list.
        if (!IsVisible || Web.CoreWebView2 is null)
        {
            return;
        }

        Web.Focus();
        Post(new { t = "focus" });
    });

    // The page's grid size, kept so a connection made later (restored tab, Reconnect) starts at
    // the right size; the page only reports it on load and on resize.
    private int _cols;
    private int _rows;

    private void RememberSize(JsonElement root)
    {
        _cols = root.GetProperty("c").GetInt32();
        _rows = root.GetProperty("r").GetInt32();
    }

    private const string AssetOrigin = "https://baudhound.assets/";

    /// <summary>
    /// Messages from the page. Only our own page may send them: they type into the session and
    /// use the clipboard. A malformed one is ignored; it used to throw on the UI thread and end
    /// the whole app.
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(AssetOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            HandleWebMessage(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Not a message this page sends; drop it.
        }
    }

    private void HandleWebMessage(JsonElement root)
    {
        var kind = root.GetProperty("t").GetString();

        switch (kind)
        {
            case "agent":
                OnAgentReply(root);
                break;

            // VNC clipboard through the app, not navigator.clipboard: inside WebView2 the page's
            // own clipboard read failed quietly, so Ctrl+V sent no text and the far end pasted
            // whatever it held.
            case "clipget":
            {
                string text = "";
                try
                {
                    text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Another program holds the clipboard open: paste nothing rather than fail.
                }

                Post(new { t = "clip", text });
                break;
            }

            case "pasteask":
                ConfirmPaste(root.GetProperty("d").GetString() ?? "", root.GetProperty("n").GetInt32());
                break;

            case "slowrule":
                SetStatus($"Highlight \"{root.GetProperty("name").GetString()}\" took {root.GetProperty("ms").GetInt32()} ms and was switched off for this tab. " +
                          "Simplify the pattern in Properties > Highlight.");
                break;

            case "link":
                OpenLink(root.GetProperty("uri").GetString() ?? "",
                         root.TryGetProperty("osc8", out var osc8) && osc8.ValueKind == JsonValueKind.True);
                break;

            case "clipset":
                try
                {
                    var text = root.GetProperty("text").GetString() ?? "";
                    // What a VNC server copies lands here; a megabyte is far more than a real copy.
                    if (text.Length > 0 && text.Length <= 1_000_000)
                    {
                        Clipboard.SetText(text);
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }

                break;

            case "ready":
                _webReady = true;
                while (_pending.Count > 0)
                {
                    Write(_pending.Dequeue());
                }

                // The page exists now, so a focus request has something to land on.
                FocusTerminal();

                if (Definition.Protocol == SessionProtocol.Vnc)
                {
                    StartVnc();
                    break;
                }

                RememberSize(root);
                _ = _controller?.ResizeAsync(_cols, _rows);
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
                RememberSize(root);
                _ = _controller?.ResizeAsync(_cols, _rows);
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
                RaiseState(state switch
                {
                    "connected" => SessionState.Connected,
                    "disconnected" => SessionState.Disconnected,
                    _ => SessionState.Failed
                }, null);
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
            // Not the current directory: whatever folder the app was started from would have
            // supplied the page that types into the session.
            Path.GetDirectoryName(Environment.ProcessPath),
            AppContext.BaseDirectory
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
            "The Assets folder was not found next to BaudHoundTerm.exe. It must travel with the " +
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
            RaiseState(SessionState.Connecting, null);
        }
        catch (Exception ex)
        {
            SetStatus($"VNC bridge failed: {ex.Message}");
            RaiseState(SessionState.Failed, ex.Message);
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
        // Serial and telnet have no file channel: typing a Windows temp path into a router
        // console does nothing useful.
        if (Definition.Protocol is not (SessionProtocol.Ssh or SessionProtocol.LocalShell))
        {
            SetStatus("Pasting an image as a file works in SSH and local shell tabs.");
            return;
        }

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
            var localDir = Path.Combine(Path.GetTempPath(), "baudhoundterm");
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
    private void SetStatus(string text) => Dispatcher.BeginInvoke(() => ShowStatus(text));

    // The status line as reported, kept so Hide addresses can be switched on or off over it.
    private string _status = "Disconnected.";

    /// <summary>Every status line goes through here, so an address in it is masked when hidden.</summary>
    private void ShowStatus(string text)
    {
        _status = text;
        StatusText.Text = Privacy.Mask(text, Definition);
    }

    /// <summary>Hide addresses was switched, or this session's own flag changed: redraw the status line.</summary>
    public void RefreshPrivacy() => StatusText.Text = Privacy.Mask(_status, Definition);

    /// <summary>Report a bridge step to both the status bar and the page's own event log.</summary>
    private void Note(string text) => Dispatcher.BeginInvoke(() =>
    {
        ShowStatus(text);
        Post(new { t = "note", d = Privacy.Mask(text, Definition) });
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
                "BaudHoundTerm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

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
        if (!IsConnected)
        {
            // Only a real Enter reconnects. Pasted text containing line breaks used to start a
            // reconnect too, and the paste itself was lost.
            if (text == "\r")
            {
                await ReconnectAsync();
            }

            return;
        }

        _agent?.NoteUserInput();

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
        if (Definition.Protocol == SessionProtocol.Rtsp)
        {
            await _cameraReady.Task;
            await _camera!.PlayAsync();
            return;
        }

        if (Definition.Protocol == SessionProtocol.Rdp)
        {
            await _rdpReady.Task;
            await _rdp!.ConnectAsync();
            return;
        }

        if (Definition.Protocol == SessionProtocol.Kvm)
        {
            await _kvmReady.Task;
            await _kvm!.ConnectAsync();
            return;
        }

        // A VNC session is driven entirely by the page: the bridge starts once the document
        // is loaded, so there is no controller and no pty to build here.
        if (Definition.Protocol == SessionProtocol.Vnc)
        {
            return;
        }

        var controller = new SessionController(Definition, _vault, PromptForSecret) { TmuxSessionName = TmuxSessionName };
        _controller = controller;
        controller.DataReceived += (_, e) =>
        {
            Transcript.Append(e.Text);   // the agent's view of the screen, kept off the UI thread
            Buffer(e.Text);
        };
        controller.TmuxChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(controller, _controller))
            {
                TmuxChanged?.Invoke(this, EventArgs.Empty);
            }
        });
        controller.StateChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            // A disposed controller still reports its "Disconnected" a beat later; after a
            // Reconnect that would mark the new, live connection dead.
            if (!ReferenceEquals(controller, _controller))
            {
                return;
            }

            var wasLive = IsConnected;
            ShowStatus(e.Message ?? e.State.ToString());
            RaiseState(e.State, e.Message);

            // The far end ended it ("exit" in a shell): say how to get it back.
            if (wasLive && e.State == SessionState.Disconnected)
            {
                Write($"\r\n[baudhoundterm] {e.Message ?? "Disconnected."} Press Enter or Reconnect to start again.\r\n");
            }
        });

        if (_cols > 0 && _rows > 0)
        {
            await controller.ResizeAsync(_cols, _rows);
        }

        await controller.ConnectAsync();
    }

    /// <summary>
    /// Asks for a secret that is not saved. "Remember" stores it in the vault under the name it
    /// was asked for (password, passphrase, an auto-login secret), so the next connect needs no prompt.
    /// </summary>
    private string? PromptForSecret(string prompt, string key)
    {
        return Dispatcher.Invoke(() =>
        {
            var dialog = new SecretPromptWindow(Definition.Name, prompt)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            if (dialog.Remember && dialog.Secret.Length > 0)
            {
                _vault.Set(Definition.Id, dialog.Secret, key);
            }

            return dialog.Secret;
        });
    }

    /// <summary>VNC tabs: Ctrl+Alt+Del on the remote desktop (noVNC refuses it on a view-only session).</summary>
    public void SendCtrlAltDel() => Post(new { t = "cad" });

    // A server can send faster than the terminal can draw. Output used to go to the UI thread one
    // chunk at a time, and a flood grew that queue without limit until the window stopped
    // answering. It is gathered here instead and flushed a few times a second; past the cap the
    // oldest is dropped, which is what a terminal with a scrollback limit does anyway.
    private const int BufferCap = 4 * 1024 * 1024;
    private readonly System.Text.StringBuilder _incoming = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private long _dropped;

    private void Buffer(string text)
    {
        lock (_incoming)
        {
            _incoming.Append(text);
            if (_incoming.Length > BufferCap)
            {
                var cut = _incoming.Length - BufferCap;
                _incoming.Remove(0, cut);
                _dropped += cut;
            }
        }

        if (!_flush.IsEnabled)
        {
            Dispatcher.BeginInvoke(() => _flush.Start());
        }
    }

    private void FlushIncoming()
    {
        string text;
        long dropped;
        lock (_incoming)
        {
            if (_incoming.Length == 0)
            {
                _flush.Stop();
                return;
            }

            text = _incoming.ToString();
            _incoming.Clear();
            dropped = _dropped;
            _dropped = 0;
        }

        if (dropped > 0)
        {
            Write($"\r\n[baudhoundterm] {dropped:N0} characters dropped: the far end sent faster than this tab could show.\r\n");
        }

        Write(text);
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
        // Output or agent events can still arrive after the tab closed and the WebView went.
        if (_released || Web.CoreWebView2 is null)
        {
            return;
        }

        Web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }

    public void Find() => Post(new { t = "find" });

    public void ClearScreen() => Post(new { t = "clear" });

    /// <summary>Pushes edited commands, highlights, theme and font to an open tab without rebuilding the terminal.</summary>
    public void ApplyHighlights()
    {
        PostSettings();
        Post(new { t = "theme", d = Definition.Theme });
        Post(new { t = "font", size = Definition.FontSize });
    }

    public async Task DisconnectAsync()
    {
        var was = IsConnected;

        if (_rdp is not null)
        {
            await _rdp.DisconnectAsync();
            return;
        }

        if (_kvm is not null)
        {
            await _kvm.DisconnectAsync();
            return;
        }

        if (_camera is not null)
        {
            await _camera.StopAsync();
            RaiseState(SessionState.Disconnected, "Stopped.");
            ShowStatus("Stopped. Reconnect to play again.");
            return;
        }

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

        RaiseState(SessionState.Disconnected, "Disconnected.");

        if (was && Definition.Protocol != SessionProtocol.Vnc)
        {
            Write("\r\n[baudhoundterm] Disconnected. Press Enter or Reconnect to connect again.\r\n");
        }
    }

    // ---------- file sending ----------

    private CancellationTokenSource? _sendCts;

    public bool IsSending => _sendCts is not null;

    private void OnStatusCancel(object sender, RoutedEventArgs e) => _sendCts?.Cancel();

    /// <summary>
    /// Types a text file into the session line by line, paced for the far end: a serial
    /// console or a router's CLI drops characters when a whole file arrives at once. Works on
    /// every terminal protocol, which makes it the one file transfer that needs nothing on the
    /// other side (pair it with "cat > file" or a config mode).
    /// </summary>
    public async Task SendTextFileAsync(string path)
    {
        if (!IsConnected || _controller is null)
        {
            SetStatus("Connect the session before sending a file.");
            return;
        }

        if (_sendCts is not null)
        {
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not read {path}: {ex.Message}");
            return;
        }

        if (text.Contains('\0'))
        {
            SetStatus($"{Path.GetFileName(path)} looks binary; Send text file is for text. Use a binary transfer instead.");
            return;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];   // a trailing newline is not an extra empty line
        }

        // Per line, and per 64-character chunk within a line: generous for serial, where a
        // device's input buffer is often 64 bytes or less; nearly nothing on a network shell.
        var (lineDelay, chunkDelay) = Definition.Protocol switch
        {
            SessionProtocol.Serial => (40, 10),
            SessionProtocol.Telnet => (15, 2),
            _ => (3, 0)
        };

        _sendCts = new CancellationTokenSource();
        StatusCancel.Visibility = Visibility.Visible;
        var name = Path.GetFileName(path);

        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                _sendCts.Token.ThrowIfCancellationRequested();
                var line = lines[i] + "\r";

                for (var at = 0; at < line.Length; at += 64)
                {
                    await _controller.WriteAsync(line.Substring(at, Math.Min(64, line.Length - at)));
                    if (chunkDelay > 0 && at + 64 < line.Length)
                    {
                        await Task.Delay(chunkDelay, _sendCts.Token);
                    }
                }

                if (i % 10 == 0 || i == lines.Length - 1)
                {
                    ShowStatus($"Sending {name}: line {i + 1} of {lines.Length}");
                }

                await Task.Delay(lineDelay, _sendCts.Token);
            }

            ShowStatus($"Sent {name}: {lines.Length} lines.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus($"Stopped sending {name}.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Sending {name} failed: {ex.Message}");
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
            StatusCancel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- XMODEM / YMODEM ----------

    public bool CanModem => Definition.Protocol == SessionProtocol.Serial && IsConnected && _sendCts is null;

    public Task ModemSendAsync(ModemProtocol protocol, IReadOnlyList<string> files) =>
        RunModemAsync(protocol, "Sending", (m, token) => m.SendAsync(protocol, files, token));

    public Task ModemReceiveAsync(ModemProtocol protocol, string folder) =>
        RunModemAsync(protocol, "Receiving", async (m, token) =>
        {
            var name = $"xmodem-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
            var got = await m.ReceiveAsync(protocol, folder, name, token);
            if (got.Count > 0)
            {
                Dispatcher.Invoke(() => Write($"[baudhoundterm] saved in {folder}\r\n"));
            }
        });

    /// <summary>
    /// Borrows the serial port in raw mode for one transfer. The screen shows nothing of the
    /// binary traffic; progress is in the status bar and a summary lands in the terminal.
    /// </summary>
    private async Task RunModemAsync(ModemProtocol protocol, string verb, Func<ModemTransfer, CancellationToken, Task> body)
    {
        if (_controller?.Serial is not { } serial || _sendCts is not null)
        {
            return;
        }

        _sendCts = new CancellationTokenSource();
        StatusCancel.Visibility = Visibility.Visible;
        var label = protocol switch
        {
            ModemProtocol.Ymodem => "YMODEM",
            ModemProtocol.Xmodem1K => "XMODEM-1K",
            _ => "XMODEM"
        };

        var link = serial.BeginRaw();
        var modem = new ModemTransfer(link);
        modem.Log += line => Dispatcher.BeginInvoke(() => Write($"\r\n[baudhoundterm] {label}: {line}\r\n"));
        modem.Progress += p => Dispatcher.BeginInvoke(() => ShowStatus(p.Total > 0
            ? $"{verb} {p.File} ({label}): {p.Done:N0} of {p.Total:N0} bytes, {100 * p.Done / p.Total}%"
            : $"{verb} {p.File} ({label}): {p.Done:N0} bytes"));
        ShowStatus(verb == "Sending"
            ? $"{label}: waiting for the device to start receiving (loady / loadx / rb / rx)..."
            : $"{label}: waiting for the device to start sending...");

        try
        {
            var token = _sendCts.Token;
            await Task.Run(() => body(modem, token), token);
            ShowStatus($"{label} transfer finished.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus($"{label} transfer cancelled.");
        }
        catch (Exception ex)
        {
            ShowStatus($"{label} transfer failed: {ex.Message}");
            Write($"\r\n[baudhoundterm] {label}: {ex.Message}\r\n");
        }
        finally
        {
            serial.EndRaw();
            _sendCts.Dispose();
            _sendCts = null;
            StatusCancel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- agent ----------

    /// <summary>Plain text of everything this tab has printed, for an attached agent.</summary>
    public TerminalTranscript Transcript { get; } = new();

    public string SessionName => Definition.Name;

    public string Describe =>
        $"{Definition.Name} ({SessionPresentation.Badge(Definition).Text} {SessionPresentation.Target(Definition)})";

    /// <summary>Typed input from the agent, delivered exactly as keystrokes from the page are.</summary>
    public Task SendAsync(string text) =>
        Dispatcher.InvokeAsync(() => _controller?.WriteAsync(text) ?? Task.CompletedTask).Task.Unwrap();

    private AgentPanel? _agent;

    public bool IsAgentAttached => _agent is not null;

    public event EventHandler? AgentAttachedChanged;

    /// <summary>The tmux marker changed: the shell entered tmux, or the session went away.</summary>
    public event EventHandler? TmuxChanged;

    /// <summary>Whether this tab's shell is kept in tmux on the host.</summary>
    public TmuxState Tmux => _controller?.Tmux ?? TmuxState.Off;

    /// <summary>
    /// Ends the tmux session on the host, with whatever is running in it. The connection ends
    /// with it, because tmux was the shell.
    /// </summary>
    public Task<string?> EndTmuxAsync() => _controller?.EndTmuxAsync() ?? Task.FromResult<string?>("This tab has no session.");

    /// <summary>
    /// Terminals get the text agent, VNC and RDP the screen agent (TerminalView.Screen.cs). A
    /// camera has nothing to act on.
    /// </summary>
    public bool CanAttachAgent => Definition.Protocol is not SessionProtocol.Rtsp;

    public void AttachAgent()
    {
        if (_agent is not null || !CanAttachAgent)
        {
            _agent?.FocusGoal();
            return;
        }

        _agent = new AgentPanel(this, Definition.Protocol, _vault);
        _agent.CloseRequested += (_, _) => DetachAgent();
        AgentHost.Child = _agent;
        AgentHost.Visibility = Visibility.Visible;
        AgentAttachedChanged?.Invoke(this, EventArgs.Empty);
        _agent.FocusGoal();
    }

    public void DetachAgent()
    {
        if (_agent is null)
        {
            return;
        }

        _agent.Cancel();
        AgentHost.Child = null;
        AgentHost.Visibility = Visibility.Collapsed;
        _agent = null;
        AgentAttachedChanged?.Invoke(this, EventArgs.Empty);
        FocusTerminal();
    }

    /// <summary>
    /// Frees the tab's browser process and camera player once the tab is gone. Without it every
    /// closed tab left its msedgewebview2 processes (and the user-data folder lock) behind.
    /// </summary>
    private bool _released;

    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _flush.Stop();
        _lampTimer?.Stop();
        if (Web.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessage;
            core.NavigationCompleted -= OnNavigated;
        }

        Web.Dispose();
        ((object?)_camera as IDisposable)?.Dispose();
        _rdp?.Dispose();
        _kvm?.Dispose();
    }

    /// <summary>The same tab, connected again: scrollback stays, the session starts over.</summary>
    private bool _reconnecting;

    public async Task ReconnectAsync()
    {
        // Enter pressed twice while the old controller was still being disposed started two
        // connections, one of them orphaned.
        if (IsConnected || _reconnecting)
        {
            return;
        }

        _reconnecting = true;
        try
        {
            await ReconnectCoreAsync();
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private async Task ReconnectCoreAsync()
    {

        // A session that failed on its own still holds a controller (or bridge); clear it first.
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
            _controller = null;
        }

        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
            _bridge = null;
        }

        RaiseState(SessionState.Connecting, null);

        if (Definition.Protocol is SessionProtocol.Rtsp or SessionProtocol.Rdp or SessionProtocol.Kvm)
        {
            await ConnectAsync();
            return;
        }

        if (Definition.Protocol == SessionProtocol.Vnc)
        {
            StartVnc();
            return;
        }

        await ConnectAsync();
    }
}
