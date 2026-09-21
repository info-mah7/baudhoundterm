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
        var assets = ResolveAssetsDirectory();

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
                SendInput(root.GetProperty("d").GetString() ?? "");
                break;

            case "size":
                _ = _controller?.ResizeAsync(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                break;

            case "title":
                TitleChanged?.Invoke(this, root.GetProperty("d").GetString() ?? Definition.Name);
                break;

            case "menu":
                ShowTerminalMenu(root.TryGetProperty("sel", out var sel) ? sel.GetString() ?? "" : "");
                break;
        }
    }

    /// <summary>
    /// Where terminal.html and xterm.js live. For a single-file app AppContext.BaseDirectory can
    /// resolve to the bundle's extraction directory under %TEMP%, which holds no Assets folder and
    /// fails the host mapping with 0x80070003. Environment.ProcessPath is the executable itself.
    /// </summary>
    private static readonly bool DebugInput =
        Environment.GetCommandLineArgs().Contains("--debug-input", StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Raised when the user picks "Edit commands..." so the shell can open properties.</summary>
    public event EventHandler? EditCommandsRequested;

    /// <summary>
    /// The terminal's right-click menu: clipboard, then this session's command shortcuts.
    /// Built fresh each time so edits in properties show up without reconnecting.
    /// </summary>
    private void ShowTerminalMenu(string selection)
    {
        var menu = new ContextMenu
        {
            Background = System.Windows.Media.Brushes.Transparent,
            PlacementTarget = Web,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        var copy = new MenuItem { Header = "Copy", IsEnabled = selection.Length > 0 };
        copy.Click += (_, _) => Clipboard.SetText(selection);
        menu.Items.Add(copy);

        var paste = new MenuItem { Header = "Paste" };
        paste.Click += (_, _) =>
        {
            if (Clipboard.ContainsText())
            {
                SendInput(Clipboard.GetText());
            }
        };
        menu.Items.Add(paste);

        var clear = new MenuItem { Header = "Clear screen" };
        clear.Click += (_, _) => ClearScreen();
        menu.Items.Add(clear);

        if (Definition.Commands.Count > 0)
        {
            menu.Items.Add(new Separator());

            foreach (var command in Definition.Commands)
            {
                var shortcut = command;
                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(shortcut.Label) ? shortcut.Text : shortcut.Label,
                    ToolTip = shortcut.Text
                };

                item.Click += (_, _) => RunShortcut(shortcut);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Edit commands..." };
        edit.Click += (_, _) => EditCommandsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(edit);

        menu.IsOpen = true;
    }

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
                Write("\r\n[quarterdeck] the transport did not accept input within 3s.\r\n");
                return;
            }

            await write;
        }
        catch (Exception ex)
        {
            Write($"\r\n[quarterdeck] input not delivered: {ex.GetType().Name}: {ex.Message}\r\n");
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
