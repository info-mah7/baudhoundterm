using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Quarterdeck.Core.Kvm;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// A KVM tab: the picture comes from the board's HDMI capture, the keyboard and mouse from
/// agentkvmd, which hands them to a USB gadget the target cannot tell from real hardware. Nothing
/// is installed on the target and it need not be reachable on the network at all; the board is.
///
/// Two addresses on the same board, because they are two services: port 9200 for the hands (a
/// bearer key, no session) and the vendor's own web interface for the eyes (a login, a token).
/// </summary>
internal sealed class KvmView : Grid, IDisposable
{
    private readonly SessionDefinition _definition;
    private readonly CredentialVault _vault;
    private readonly Action<SessionState, string?> _report;
    private readonly Func<string, string, string?> _promptForSecret;

    private readonly Image _picture = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed
    };

    private readonly TextBlock _notice = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE)),
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 560
    };

    private KvmScreen? _screen;
    private int _buttons;
    private bool _rendering;
    private (int W, int H) _frame;

    public KvmView(SessionDefinition definition, CredentialVault vault,
                   Action<SessionState, string?> report, Func<string, string, string?> promptForSecret)
    {
        _definition = definition;
        _vault = vault;
        _promptForSecret = promptForSecret;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C));
        Children.Add(_picture);
        Children.Add(_notice);
        Focusable = true;

        _report = (state, message) =>
        {
            var live = state == SessionState.Connected;
            _picture.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            _notice.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            if (!live)
            {
                _notice.Text = (message ?? state.ToString()) +
                               (message?.Contains("Reconnect", StringComparison.Ordinal) == true ? "" : "\n\nPress Reconnect to try again.");
            }

            report(state, message);
        };

        _notice.Text = "Not connected.";

        // The tab takes the keyboard when clicked, like the other desktop tabs; a key that would
        // otherwise move the focus (Tab, the arrows) goes to the target instead.
        MouseDown += (_, _) => Keyboard.Focus(this);
        _picture.MouseMove += OnMouse;
        _picture.MouseDown += OnMouse;
        _picture.MouseUp += OnMouse;
        _picture.MouseWheel += OnWheel;
        PreviewKeyDown += (s, e) => OnKey(e, true);
        PreviewKeyUp += (s, e) => OnKey(e, false);
        PreviewTextInput += OnText;

        // A right-click is sent to the target (OnMouse carries the right button), so BaudHoundTerm's
        // own tab menu must not open over it. Unlike the VNC and RDP tabs, whose hosted controls
        // swallow the click, this tab is plain WPF, so the tab's context menu would otherwise show.
        // The tab menu is still reachable by right-clicking the tab header.
        ContextMenuOpening += (_, e) => e.Handled = true;
    }

    /// <summary>The agent's screen, once connected.</summary>
    public KvmScreen? Screen => _screen;

    public async Task ConnectAsync()
    {
        var host = _definition.Host.Trim();
        if (host.Length == 0)
        {
            _report(SessionState.Failed, "No KVM address set. Add one in Properties.");
            return;
        }

        _report(SessionState.Connecting, $"Connecting to the KVM at {host}...");

        var scheme = _definition.KvmTls ? "https" : "http";
        var port = _definition.KvmPort > 0 ? _definition.KvmPort : 9200;
        var hands = $"{scheme}://{host}:{port}";
        if (KvmClient.Refuse(hands) is { } why)
        {
            _report(SessionState.Failed, why);
            return;
        }

        var key = _vault.Get(_definition.Id, "kvmkey")?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            key = _promptForSecret($"Daemon key for the KVM at {host}", "kvm key")?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                _report(SessionState.Failed, "No daemon key, so nothing can be typed on the target.");
                return;
            }
        }

        KvmClient client;
        try
        {
            client = new KvmClient(hands, key);
        }
        catch (KvmException ex)
        {
            _report(SessionState.Failed, ex.Message);
            return;
        }

        if (!await client.HealthyAsync())
        {
            client.Dispose();
            _report(SessionState.Failed,
                    $"No answer from agentkvmd at {hands}. Check the board is on and the daemon running (its /healthz needs no key).");
            return;
        }

        // The eyes: the board's own web interface, which needs a login of its own. Its address is
        // the same board unless the session says otherwise (a reverse proxy, a different port).
        KvmVendorClient? eyes = null;
        var video = _definition.KvmVideoUrl.Trim().Length > 0 ? _definition.KvmVideoUrl.Trim() : $"{scheme}://{host}";
        if (video != "-")
        {
            var user = _definition.KvmVideoUser.Trim().Length > 0 ? _definition.KvmVideoUser.Trim() : "admin";
            var password = _vault.Get(_definition.Id);
            if (string.IsNullOrEmpty(password))
            {
                password = _promptForSecret($"Web interface password for {user} on the KVM at {host}", "password");
                if (string.IsNullOrEmpty(password))
                {
                    client.Dispose();
                    _report(SessionState.Failed, "No web interface password, so there is no picture.");
                    return;
                }
            }

            eyes = new KvmVendorClient(video, user, password);
            try
            {
                await eyes.SignInAsync();
            }
            catch (Exception ex) when (ex is KvmException or HttpRequestException or IOException)
            {
                eyes.Dispose();
                client.Dispose();
                _report(SessionState.Failed, $"The KVM's web interface would not sign in, so there is no picture: {ex.Message}");
                return;
            }
        }

        _screen = new KvmScreen(
            $"{_definition.Name} (KVM {host}: USB keyboard and mouse to the target, picture from its HDMI output)",
            _definition.Name, client, eyes,
            note => Dispatcher.BeginInvoke(() => _notice.Text = note));
        _screen.FrameArrived += OnFrame;

        _report(SessionState.Connected,
                eyes is null
                    ? $"Typing to {host}; this board has no picture."
                    : $"Connected to the KVM at {host}.");
        if (eyes is null)
        {
            _notice.Visibility = Visibility.Visible;
            _notice.Text = "This KVM has no capture, so there is nothing to show. Keyboard and mouse still work, " +
                           "including for the agent, but neither of you can see the result.";
        }

        Keyboard.Focus(this);
    }

    /// <summary>A new frame: decoded and shown, dropping any that arrive while one is being drawn.</summary>
    private void OnFrame()
    {
        if (_rendering)
        {
            return;
        }

        _rendering = true;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_screen?.Latest is not { } jpeg)
                {
                    return;
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(jpeg);
                image.EndInit();
                image.Freeze();

                _frame = (image.PixelWidth, image.PixelHeight);
                _screen.SetFrameSize(image.PixelWidth, image.PixelHeight);
                _picture.Source = image;
                if (_picture.Visibility != Visibility.Visible)
                {
                    _picture.Visibility = Visibility.Visible;
                    _notice.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
                // A truncated or odd frame: the next one is along in a fortieth of a second.
            }
            finally
            {
                _rendering = false;
            }
        });
    }

    /// <summary>Where a point in the control is on the target's screen, in frame pixels.</summary>
    private (int X, int Y)? Point(MouseEventArgs e)
    {
        if (_frame.W == 0 || _picture.ActualWidth <= 0)
        {
            return null;
        }

        var p = e.GetPosition(_picture);
        var x = (int)Math.Round(p.X / _picture.ActualWidth * _frame.W);
        var y = (int)Math.Round(p.Y / _picture.ActualHeight * _frame.H);
        return (Math.Clamp(x, 0, _frame.W - 1), Math.Clamp(y, 0, _frame.H - 1));
    }

    private void OnMouse(object sender, MouseEventArgs e)
    {
        if (_screen is null || Point(e) is not { } at)
        {
            return;
        }

        var buttons = (e.LeftButton == MouseButtonState.Pressed ? 1 : 0)
                      | (e.MiddleButton == MouseButtonState.Pressed ? 2 : 0)
                      | (e.RightButton == MouseButtonState.Pressed ? 4 : 0);

        // A move with nothing pressed is still worth sending (hovers, menus), but not every pixel:
        // the board takes one report per request, so a dragged mouse would queue hundreds.
        _buttons = buttons;
        Send(_screen!.PointerAsync(at.X, at.Y, buttons, CancellationToken.None));
        if (e is MouseButtonEventArgs)
        {
            Keyboard.Focus(this);
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_screen is not { } screen || Point(e) is not { } at)
        {
            return;
        }

        var notch = e.Delta > 0 ? 8 : 16;
        var held = _buttons;
        Send(Task.Run(async () =>
        {
            await screen.PointerAsync(at.X, at.Y, held | notch, CancellationToken.None);
            await screen.PointerAsync(at.X, at.Y, held, CancellationToken.None);
        }));
        e.Handled = true;
    }

    /// <summary>
    /// Printable characters come from the text input event, so the key the user's own layout
    /// produces is the character sent, whatever its position. Control combinations cannot: they
    /// produce no text, and arrive through OnKey.
    /// </summary>
    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (_screen is not { } screen || e.Text.Length == 0)
        {
            return;
        }

        var text = e.Text;
        e.Handled = true;
        Send(Task.Run(async () =>
        {
            foreach (var c in text)
            {
                var keysym = Core.Agent.ScreenInput.CharKeysym(c);
                if (keysym == 0)
                {
                    continue;
                }

                await screen.KeyAsync(keysym, true, CancellationToken.None);
                await screen.KeyAsync(keysym, false, CancellationToken.None);
            }
        }));
    }

    private void OnKey(KeyEventArgs e, bool down)
    {
        if (_screen is not { } screen)
        {
            return;
        }

        // Alt makes WPF report the key as Key.System, with the real one in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl+V types the clipboard: a KVM has no shared clipboard, so the only way to get text
        // from this PC onto the target is to type it through the keyboard. (Ctrl+Shift+V still
        // passes through as a key combination, for apps that use it.) Both the press and release
        // are swallowed so a bare "v" does not also reach the target.
        if (key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (down)
            {
                PasteToTarget(screen);
            }

            return;
        }

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                      || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
                      || Keyboard.Modifiers.HasFlag(ModifierKeys.Windows);

        if (Keysym(key, control) is not { } keysym)
        {
            return;
        }

        e.Handled = true;
        Send(screen.KeyAsync(keysym, down, CancellationToken.None));
    }

    /// <summary>
    /// Pastes by typing: reads this PC's clipboard and sends it through the KVM keyboard, since
    /// the board has no clipboard channel. A multi-line or long paste is confirmed first, because
    /// on a desktop a line break presses Enter and can submit a form or run a command.
    /// </summary>
    private void PasteToTarget(KvmScreen screen)
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (Exception)
        {
            _report(SessionState.Connected, "The clipboard could not be read.");
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        var lines = text.Split('\n').Length;
        if ((lines > 1 || text.Length > 200) && MessageBox.Show(Window.GetWindow(this)!,
                $"Type {text.Length} characters ({lines} line{(lines == 1 ? "" : "s")}) into {_definition.Name} over the KVM?\n\n" +
                "A line break presses Enter on the target.",
                "BaudHoundTerm: paste", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        Send(Task.Run(async () =>
        {
            await screen.ClearHeldAsync(CancellationToken.None);
            await Core.Agent.ScreenInput.TypeAsync(screen, text, CancellationToken.None);
        }));
    }

    /// <summary>
    /// The keysym for a key press that text input will not deliver: modifiers, the named keys, and
    /// the letters and digits while Ctrl, Alt or Windows is held. A plain letter returns null, so
    /// the text event sends it instead and the user's layout is respected.
    /// </summary>
    private static uint? Keysym(Key key, bool control) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => 0xffe3,
        Key.LeftShift or Key.RightShift => 0xffe1,
        Key.LeftAlt => 0xffe9,
        Key.RightAlt => 0xfe03,
        Key.LWin or Key.RWin => 0xffeb,
        Key.Enter => 0xff0d,
        Key.Escape => 0xff1b,
        Key.Tab => 0xff09,
        Key.Back => 0xff08,
        Key.Delete => 0xffff,
        Key.Insert => 0xff63,
        Key.Home => 0xff50,
        Key.End => 0xff57,
        Key.PageUp => 0xff55,
        Key.PageDown => 0xff56,
        Key.Up => 0xff52,
        Key.Down => 0xff54,
        Key.Left => 0xff51,
        Key.Right => 0xff53,
        Key.Apps => 0xff67,
        Key.CapsLock => 0xffe5,
        Key.PrintScreen or Key.Snapshot => 0xff61,
        >= Key.F1 and <= Key.F12 => (uint)(0xffbe + (key - Key.F1)),
        >= Key.A and <= Key.Z when control => (uint)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 when control => (uint)('0' + (key - Key.D0)),
        _ => null
    };

    /// <summary>
    /// Input is fire and forget: a lost report is not worth a dialog, but a board that has stopped
    /// answering is, so the first failure says so in the notice and the picture keeps running.
    /// </summary>
    private void Send(Task work) => _ = work.ContinueWith(
        t =>
        {
            var ex = t.Exception?.GetBaseException();
            if (ex is KvmException or HttpRequestException or IOException)
            {
                Dispatcher.BeginInvoke(() => _report(SessionState.Connected, $"The KVM refused an input report: {ex.Message}"));
            }
        },
        TaskContinuationOptions.OnlyOnFaulted);

    public Task DisconnectAsync()
    {
        _screen?.Dispose();
        _screen = null;
        _picture.Source = null;
        _report(SessionState.Disconnected, "Disconnected.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _screen?.Dispose();
        _screen = null;
    }
}
