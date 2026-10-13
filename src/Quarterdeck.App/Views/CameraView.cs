using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Quarterdeck.Core.Camera;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// An RTSP camera in a tab: LibVLC plays the stream; ONVIF finds the stream when no path is
/// given and drives pan, tilt, zoom and presets when the camera has them. The controls sit
/// beside the video, not over it: the video is a native window and would hide WPF drawn on top.
/// </summary>
internal sealed class CameraView : DockPanel
{
    private static LibVLC? _libVlc;

    private readonly SessionDefinition _definition;
    private readonly CredentialVault _vault;
    private readonly Action<SessionState, string?> _report;
    private readonly MediaPlayer _player;
    private readonly VideoSurface _video = new();
    private readonly StackPanel _ptz = new() { IsEnabled = false };
    private readonly ComboBox _presets = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _ptzNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)) };
    private readonly Button _mute = new() { Content = "Mute" };

    private OnvifClient? _onvif;
    private OnvifCamera? _camera;
    private Media? _media;
    private readonly TaskCompletionSource _surfaceReady = new();

    public CameraView(SessionDefinition definition, CredentialVault vault, Action<SessionState, string?> report)
    {
        _definition = definition;
        _vault = vault;
        _report = report;

        if (_libVlc is null)
        {
            LibVLCSharp.Shared.Core.Initialize();   // finds libvlc\win-x64 beside the exe
            _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-lua", "--no-stats");
        }

        _player = new MediaPlayer(_libVlc);
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() => _report(SessionState.Connected, $"Playing {Describe()}."));
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            _report(SessionState.Failed, $"The stream failed: {Describe()}. Check the address, path and login."));
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _report(SessionState.Disconnected, "The camera ended the stream."));

        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C));
        var side = BuildSidePanel();
        DockPanel.SetDock(side, Dock.Right);
        Children.Add(side);
        Children.Add(_video);

        // LibVLC draws into a native child window we own. Its handle exists once the view is
        // shown; the player gets it then, and only then may playback start. Without a handle
        // LibVLC opens a window of its own.
        _video.HandleReady += handle =>
        {
            _player.Hwnd = handle;
            _surfaceReady.TrySetResult();
        };
    }

    private string Describe() => $"rtsp://{_definition.Host}:{_definition.Port}{NormalisedPath()}";

    private string NormalisedPath()
    {
        var p = _definition.RtspPath.Trim();
        return p.Length == 0 ? "" : p.StartsWith('/') ? p : "/" + p;
    }

    private FrameworkElement BuildSidePanel()
    {
        var panel = new StackPanel { Width = 170, Margin = new Thickness(8) };

        Button Pad(string text, double pan, double tilt, double zoom)
        {
            var b = new Button { Content = text, Width = 44, Height = 34, Margin = new Thickness(2), FontSize = 15 };
            // Moves while held, stops on release: how PTZ joysticks behave.
            b.PreviewMouseLeftButtonDown += async (_, _) => await MoveAsync(pan, tilt, zoom);
            b.PreviewMouseLeftButtonUp += async (_, _) => await StopMoveAsync();
            b.MouseLeave += async (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    await StopMoveAsync();
                }
            };
            return b;
        }

        var grid = new UniformGridLike(3);
        grid.Add(new Border());
        grid.Add(Pad("▲", 0, 0.5, 0));
        grid.Add(new Border());
        grid.Add(Pad("◀", -0.5, 0, 0));
        var stop = new Button { Content = "■", Width = 44, Height = 34, Margin = new Thickness(2) };
        stop.Click += async (_, _) => await StopMoveAsync();
        grid.Add(stop);
        grid.Add(Pad("▶", 0.5, 0, 0));
        grid.Add(new Border());
        grid.Add(Pad("▼", 0, -0.5, 0));
        grid.Add(new Border());

        var zoom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        zoom.Children.Add(Pad("−", 0, 0, -0.5));
        zoom.Children.Add(new TextBlock { Text = "zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        zoom.Children.Add(Pad("+", 0, 0, 0.5));

        var go = new Button { Content = "Go to preset", Margin = new Thickness(0, 4, 0, 0) };
        go.Click += async (_, _) => await GotoPresetAsync();

        _ptz.Children.Add(new TextBlock { Text = "Pan / tilt / zoom", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _ptz.Children.Add(grid.Panel);
        _ptz.Children.Add(zoom);
        _ptz.Children.Add(_presets);
        _ptz.Children.Add(go);
        panel.Children.Add(_ptz);
        panel.Children.Add(_ptzNote);

        var snapshot = new Button { Content = "Snapshot", Margin = new Thickness(0, 14, 0, 0), ToolTip = "Save the current frame as a PNG in Pictures\\BaudHoundTerm" };
        snapshot.Click += (_, _) => Snapshot();
        panel.Children.Add(snapshot);

        _mute.Margin = new Thickness(0, 4, 0, 0);
        _mute.Click += (_, _) =>
        {
            _player.Mute = !_player.Mute;
            _mute.Content = _player.Mute ? "Unmute" : "Mute";
        };
        panel.Children.Add(_mute);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Starts (or restarts) the stream; finds it over ONVIF when no path is set.</summary>
    public async Task PlayAsync()
    {
        await _surfaceReady.Task;
        _report(SessionState.Connecting, $"Connecting to {Describe()}...");
        var password = _vault.Get(_definition.Id) ?? "";
        _onvif ??= new OnvifClient(_definition.Host, _definition.OnvifPort, _definition.Username, password);

        string url;
        if (NormalisedPath().Length == 0)
        {
            try
            {
                _camera ??= await _onvif.DiscoverAsync();
                url = await _onvif.GetStreamUriAsync(_camera);
            }
            catch (Exception ex)
            {
                _report(SessionState.Failed, $"No stream path set, and the camera could not be asked for one over ONVIF: {ex.Message}");
                return;
            }
        }
        else
        {
            url = Describe();
        }

        _media?.Dispose();
        _media = new Media(_libVlc!, new Uri(url));
        // Credentials as options, never in the URL, so they do not land in logs or the title.
        if (!string.IsNullOrEmpty(_definition.Username))
        {
            _media.AddOption($":rtsp-user={_definition.Username}");
            _media.AddOption($":rtsp-pwd={password}");
        }

        if (_definition.RtspTcp)
        {
            _media.AddOption(":rtsp-tcp");
        }

        _media.AddOption($":network-caching={(_definition.LowLatency ? 200 : 1000)}");
        _player.Play(_media);

        _ = Task.Run(SetUpPtzAsync);
    }

    private async Task SetUpPtzAsync()
    {
        try
        {
            _camera ??= await _onvif!.DiscoverAsync();
            if (!_camera.HasPtz)
            {
                await Dispatcher.InvokeAsync(() => _ptzNote.Text = "This camera reports no PTZ.");
                return;
            }

            var presets = await _onvif!.GetPresetsAsync(_camera);
            await Dispatcher.InvokeAsync(() =>
            {
                _ptz.IsEnabled = true;
                _presets.ItemsSource = presets;
                _presets.SelectedIndex = presets.Count > 0 ? 0 : -1;
                _ptzNote.Text = presets.Count > 0 ? $"{presets.Count} presets." : "No presets saved on the camera.";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => _ptzNote.Text = $"PTZ unavailable (ONVIF on port {_definition.OnvifPort}): {ex.Message}");
        }
    }

    private async Task MoveAsync(double pan, double tilt, double zoom)
    {
        if (_camera is null || _onvif is null)
        {
            return;
        }

        try
        {
            await _onvif.ContinuousMoveAsync(_camera, pan, tilt, zoom);
        }
        catch (Exception ex)
        {
            _ptzNote.Text = "Move failed: " + ex.Message;
        }
    }

    private async Task StopMoveAsync()
    {
        if (_camera is null || _onvif is null)
        {
            return;
        }

        try
        {
            await _onvif.StopAsync(_camera);
        }
        catch (Exception ex)
        {
            _ptzNote.Text = "Stop failed: " + ex.Message;
        }
    }

    private async Task GotoPresetAsync()
    {
        if (_camera is null || _onvif is null || _presets.SelectedItem is not OnvifPreset preset)
        {
            return;
        }

        try
        {
            await _onvif.GotoPresetAsync(_camera, preset.Token);
            _ptzNote.Text = $"Going to {preset}.";
        }
        catch (Exception ex)
        {
            _ptzNote.Text = "Preset failed: " + ex.Message;
        }
    }

    private void Snapshot()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BaudHoundTerm");
        Directory.CreateDirectory(folder);
        var safe = string.Concat(_definition.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        _report(_player.IsPlaying ? SessionState.Connected : SessionState.Disconnected,
            _player.TakeSnapshot(0, path, 0, 0) ? $"Snapshot saved: {path}" : "Snapshot failed: no picture yet.");
    }

    public Task StopAsync() => Task.Run(() =>
    {
        // Stop can block while the network side winds down; never on the UI thread.
        _player.Stop();
    });

    /// <summary>A tiny fixed grid of equal cells, filled left to right.</summary>
    private sealed class UniformGridLike(int columns)
    {
        public System.Windows.Controls.Primitives.UniformGrid Panel { get; } = new() { Columns = columns, HorizontalAlignment = HorizontalAlignment.Center };

        public void Add(UIElement e) => Panel.Children.Add(e);
    }

    /// <summary>
    /// A plain native child window for LibVLC to draw into, handed over before playback starts.
    /// (Embedding needs VLC's "drawable" plugin; without it VLC opens a window of its own.)
    /// </summary>
    private sealed class VideoSurface : System.Windows.Interop.HwndHost
    {
        private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, SsBlackRect = 0x4;

        public event Action<IntPtr>? HandleReady;

        protected override System.Runtime.InteropServices.HandleRef BuildWindowCore(System.Runtime.InteropServices.HandleRef parent)
        {
            var hwnd = CreateWindowEx(0, "static", "", WsChild | WsVisible | WsClipChildren | SsBlackRect,
                0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Dispatcher.BeginInvoke(() => HandleReady?.Invoke(hwnd));
            return new System.Runtime.InteropServices.HandleRef(this, hwnd);
        }

        protected override void DestroyWindowCore(System.Runtime.InteropServices.HandleRef hwnd) => DestroyWindow(hwnd.Handle);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
