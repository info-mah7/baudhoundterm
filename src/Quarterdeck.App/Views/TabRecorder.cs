using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Quarterdeck.App.Views;

/// <summary>
/// Records one tab's terminal to an MP4, for demos. Windows' own capture (the API under Game Bar
/// and the Snipping Tool) takes BaudHoundTerm's window, or the tab's popped-out window; every frame
/// is cropped to the terminal area, so the sidebar, other tabs and the desktop never reach the
/// file. Windows' own encoder writes H.264. Nothing is bundled and nothing leaves this PC.
/// Windows sends a frame only when the window changes; the encoder takes <see cref="Fps"/> frames a
/// second of recording time from the newest one, so a still screen is still in the video, and the
/// last change before a pause or stop always makes it in.
/// </summary>
internal sealed class TabRecorder
{
    public const int Fps = 15;

    // Null: the whole window (title bar included), to show BaudHoundTerm itself.
    private readonly FrameworkElement? _region;
    private readonly Window _window;
    private readonly string _path;
    private readonly Stopwatch _clock = new();
    private readonly ManualResetEventSlim _stopping = new();
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1.0 / Fps);

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IRandomAccessStream? _stream;
    private Task? _transcode;
    private int _width, _height;
    private Int32Rect _crop;
    private System.Windows.Threading.DispatcherTimer? _cropTimer;
    private TimeSpan _pausedTotal, _pausedAt;
    // The newest whole-window frame, top row first; replaced whole, never changed in place.
    private volatile Snapshot? _latest;
    private long _next;   // index of the next video frame, in Fps steps of recording time
    private Windows.Graphics.SizeInt32 _poolSize;

    public TabRecorder(Window window, FrameworkElement? region, string path, string hiddenLabel = "")
    {
        _window = window;
        _region = region;
        _path = path;
        _hiddenLabel = hiddenLabel;
    }

    // A recorded tab that is not in front is not drawn at all, and what sits in its place is another
    // session. While it is hidden the video shows this card instead, so no other session is ever
    // recorded and the timeline stays whole (an audit must not have gaps). Made on the UI thread.
    private readonly string _hiddenLabel;
    private volatile bool _hidden;
    private byte[]? _hiddenFrame;

    public string FilePath => _path;

    /// <summary>
    /// How this recording is encoded, in words, when it is not the first choice (full size,
    /// hardware encoder); empty when it is. For the status line.
    /// </summary>
    public string Fallback { get; private set; } = "";
    public bool Paused { get; private set; }
    public TimeSpan Elapsed => (Paused ? _pausedAt : _clock.Elapsed) - _pausedTotal;
    public int FramesWritten { get; private set; }

    /// <summary>
    /// Starts capturing. Called on the UI thread: the crop is read from the visual tree. A start
    /// that fails part way releases what it set up and leaves no empty file behind.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await StartCoreAsync();
        }
        catch
        {
            _cropTimer?.Stop();
            _session?.Dispose();
            _pool?.Dispose();
            var opened = _stream is not null;
            _stream?.Dispose();
            if (opened)
            {
                try
                {
                    File.Delete(_path);
                }
                catch (IOException)
                {
                }
            }

            throw;
        }
    }

    private async Task StartCoreAsync()
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("this version of Windows has no screen capture API");
        }

        _crop = CropRect();
        // The encoder wants even dimensions.
        _width = Math.Max(2, _crop.Width & ~1);
        _height = Math.Max(2, _crop.Height & ~1);

        var hwnd = new WindowInteropHelper(_window).Handle;
        _item = CaptureItemForWindow(hwnd);
        var device = CreateDevice();
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(_item);
        TryOff(() => _session.IsCursorCaptureEnabled = false);
        // Windows 11 draws a yellow border round the whole window being captured. A tab recording has
        // its own frame round the tab (TerminalView.RecordFrame), so the window border goes; a
        // whole-window recording keeps it. Needs Windows 11 (or Server 2022) and the user's consent,
        // which Windows grants a desktop app without asking.
        if (_region is not null
            && Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try
            {
                var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                if (access == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                {
                    _session.IsBorderRequired = false;
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
            {
                // The border stays; the recording is unaffected.
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // H.264 encoders refuse frames past about 4096 wide (a high-DPI or ultrawide window gets there),
        // and some refuse what others take. Try the full size capped to 4K with the hardware encoder,
        // then Windows' software encoder, then half size; the transcoder scales the frames to fit.
        // A refused attempt shuts its source and stream down, so each one gets fresh ones.
        var (outW, outH) = Fit(_width, _height, 3840, 2160);
        PrepareTranscodeResult? prepared = null;
        var refusals = new List<string>();
        Exception? first = null;
        var setups = new[] { (outW, outH, true), (outW, outH, false), (Even(outW / 2), Even(outH / 2), false) };
        // The setup that worked last time on this PC goes first, so a refused one is not retried every time.
        var order = Enumerable.Range(0, setups.Length).OrderBy(i => i == RememberedSetup ? -1 : i).ToList();
        foreach (var index in order)
        {
            var (w, h, hardware) = setups[index];
            _stream?.Dispose();
            _stream = await FileRandomAccessStream.OpenAsync(_path, FileAccessMode.ReadWrite, StorageOpenOptions.None,
                refusals.Count == 0 ? FileOpenDisposition.CreateNew : FileOpenDisposition.TruncateExisting);
            var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
            var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero, CanSeek = false };
            source.SampleRequested += OnSampleRequested;
            var what = $"{w}x{h} {(hardware ? "hardware" : "software")}";
            try
            {
                var attempt = await new MediaTranscoder { HardwareAccelerationEnabled = hardware }
                    .PrepareMediaStreamSourceTranscodeAsync(source, _stream, Profile(w, h));
                if (attempt.CanTranscode)
                {
                    prepared = attempt;
                    RememberedSetup = index;
                    var notes = new List<string>();
                    if (w != _width || h != _height)
                    {
                        notes.Add($"at {w}x{h} instead of {_width}x{_height}");
                    }

                    if (!hardware)
                    {
                        notes.Add("with Windows' software encoder");
                    }

                    if (notes.Count > 0)
                    {
                        Fallback = string.Join(" ", notes) + (refusals.Count > 0 ? ", because the encoder refused the first choice" : ", as on the last recording");
                    }

                    break;
                }

                refusals.Add($"{what}: {attempt.FailureReason}");
                first ??= new InvalidOperationException($"Windows could not set up the video encoder ({attempt.FailureReason})");
            }
            catch (COMException ex)
            {
                refusals.Add($"{what}: 0x{ex.HResult:X8}");
                first ??= ex;
            }
        }

        if (prepared is null)
        {
            // The first refusal is the real one; later ones can be knock-on effects. All are named.
            throw new COMException($"the video encoder refused every setup tried ({string.Join("; ", refusals)})", first!.HResult);
        }

        _hiddenFrame = _region is null ? null : HiddenCard(_width, _height, _hiddenLabel);
        _transcode = prepared.TranscodeAsync().AsTask();
        _clock.Start();
        _session.StartCapture();

        // The terminal can move inside the window (sidebar toggled, window resized): follow it.
        _cropTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cropTimer.Tick += (_, _) =>
        {
            // A tab popped out or brought back mid-recording leaves this window; keep the last crop.
            if (_region is null)
            {
                _crop = CropRect();
                return;
            }

            // Not in this window (popped out) or not shown (another tab in front): the card.
            _hidden = !_region.IsVisible || !_region.IsDescendantOf(_window);
            if (!_hidden)
            {
                _crop = CropRect();
            }
        };
        _cropTimer.Start();
    }

    public void Pause()
    {
        if (!Paused)
        {
            Paused = true;
            _pausedAt = _clock.Elapsed;
        }
    }

    public void Resume()
    {
        if (Paused)
        {
            _pausedTotal += _clock.Elapsed - _pausedAt;
            Paused = false;
        }
    }

    /// <summary>Stops capturing and finishes the file. Returns its size in bytes.</summary>
    public async Task<long> StopAsync()
    {
        _cropTimer?.Stop();
        _session?.Dispose();
        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrame;
            _pool.Dispose();
        }

        _stopping.Set();
        if (_transcode is not null)
        {
            await _transcode;
        }

        _stream?.Dispose();
        return new FileInfo(_path).Length;
    }

    // ---- capture side (a thread-pool thread) ----

    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null || Paused || _stopping.IsSet)
        {
            return;
        }

        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            // The window was resized: the pool must match it, or frames come back stretched.
            _poolSize = frame.ContentSize;
            pool.Recreate(CreateDevice(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            return;
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        var full = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(full.AsBuffer());
        _latest = new Snapshot(full, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private sealed record Snapshot(byte[] Bgra, int Width, int Height);

    /// <summary>
    /// The terminal's rectangle out of a whole-window frame, top row first. Media Foundation reads
    /// uncompressed RGB bottom row first, so rows are written in reverse; anything outside the
    /// frame stays black.
    /// </summary>
    private byte[] Crop(byte[] full, int fullWidth, int fullHeight, Int32Rect crop)
    {
        var outRow = _width * 4;
        var result = new byte[outRow * _height];
        for (var y = 0; y < _height; y++)
        {
            var sy = crop.Y + y;
            if (sy < 0 || sy >= fullHeight)
            {
                continue;
            }

            var sx = Math.Max(0, crop.X);
            var count = Math.Min(_width - (sx - crop.X), fullWidth - sx);
            if (count <= 0)
            {
                continue;
            }

            System.Buffer.BlockCopy(full, (sy * fullWidth + sx) * 4, result, (_height - 1 - y) * outRow + (sx - crop.X) * 4, count * 4);
        }

        return result;
    }

    // ---- encoder side (the transcoder's thread) ----

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        Task.Run(() =>
        {
            try
            {
                args.Request.Sample = NextSample();
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    /// <summary>
    /// Waits until the next frame is due in recording time (a pause stops that clock), then returns
    /// the newest capture with that timestamp. Null once stopped: the transcoder ends the file.
    /// </summary>
    private MediaStreamSample? NextSample()
    {
        while (true)
        {
            if (_stopping.IsSet)
            {
                return null;
            }

            var now = Elapsed;
            var due = _interval * _next;
            if (Paused || _latest is null || now < due)
            {
                var wait = Paused || _latest is null ? _interval : due - now;
                _stopping.Wait(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait);
                continue;
            }

            // An encoder that fell behind skips ahead rather than trailing real time.
            if (now - due > TimeSpan.FromSeconds(1))
            {
                _next = (long)(now / _interval);
                due = _interval * _next;
            }

            var snap = _latest;
            var pixels = _hidden && _hiddenFrame is { } card ? card : Crop(snap.Bgra, snap.Width, snap.Height, _crop);
            var sample = MediaStreamSample.CreateFromBuffer(pixels.AsBuffer(), due);
            sample.Duration = _interval;
            _next++;
            FramesWritten++;
            return sample;
        }
    }

    // ---- geometry ----

    /// <summary>The terminal's rectangle in the captured frame, in physical pixels.</summary>
    private Int32Rect CropRect()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>());
        if (_region is null)
        {
            return new Int32Rect(0, 0, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }

        var dpi = VisualTreeHelper.GetDpi(_window);
        var topLeft = _region.TransformToAncestor(_window).Transform(new Point(0, 0));

        // The capture covers the window's visible frame; the visual tree is measured from the
        // client area. The difference is the title bar and border.
        var client = new POINT();
        ClientToScreen(hwnd, ref client);

        return new Int32Rect(
            client.X - frame.Left + (int)Math.Round(topLeft.X * dpi.DpiScaleX),
            client.Y - frame.Top + (int)Math.Round(topLeft.Y * dpi.DpiScaleY),
            (int)Math.Round(_region.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(_region.ActualHeight * dpi.DpiScaleY));
    }

    // Which setup worked last on this PC (an index into the setups tried at start), kept in
    // recording-setup.txt in the app's data folder. Missing or unreadable: the usual order.
    private static readonly string SetupFile = Path.Combine(Quarterdeck.Core.Model.AppPaths.DataDirectory, "recording-setup.txt");

    private static int RememberedSetup
    {
        get
        {
            try
            {
                return int.TryParse(File.ReadAllText(SetupFile).Trim(), out var i) ? i : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
        set
        {
            try
            {
                File.WriteAllText(SetupFile, value.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The card shown while the recorded tab is not in front: BGRA at the frame size, rows ordered as Crop writes them.</summary>
    private static byte[] HiddenCard(int width, int height, string label)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)), null, new Rect(0, 0, width, height));
            var text = new FormattedText(
                (label.Length > 0 ? label : "This tab") + " is in the background.\nThe recording continues; the session is not on screen.",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                Math.Max(14, Math.Min(width, height) / 22.0), new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)), 1.0)
            { TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(10, width - 40) };
            dc.DrawText(text, new Point(20, (height - text.Height) / 2));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var rows = new byte[width * height * 4];
        bitmap.CopyPixels(rows, width * 4, 0);
        // Bottom row first, as Crop writes frames: Media Foundation reads uncompressed RGB that way.
        var pixels = new byte[rows.Length];
        for (var y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(rows, y * width * 4, pixels, (height - 1 - y) * width * 4, width * 4);
        }

        return pixels;
    }

    private static MediaEncodingProfile Profile(int width, int height)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = null;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = Fps;
        profile.Video.FrameRate.Denominator = 1;
        // Text needs sharp edges more than motion needs bits; about 0.15 bit per pixel per frame.
        profile.Video.Bitrate = (uint)Math.Clamp(width * height * Fps * 15L / 100, 1_000_000, 16_000_000);
        return profile;
    }

    /// <summary>The size scaled down, keeping its shape, to fit the box (turned for portrait).</summary>
    private static (int Width, int Height) Fit(int width, int height, int maxLong, int maxShort)
    {
        var (maxW, maxH) = width >= height ? (maxLong, maxShort) : (maxShort, maxLong);
        var scale = Math.Min(1.0, Math.Min((double)maxW / width, (double)maxH / height));
        return (Even((int)(width * scale)), Even((int)(height * scale)));
    }

    private static int Even(int n) => Math.Max(2, n & ~1);

    private static void TryOff(Action set)
    {
        try
        {
            set();
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or COMException or ArgumentException)
        {
            // Older Windows: the property is not there, and the default is acceptable.
        }
    }

    // ---- interop: a Direct3D device and a capture item for a window ----

    private static IDirect3DDevice CreateDevice()
    {
        // Hardware first; WARP (Windows' software renderer) where there is none to give, such as a
        // Remote Desktop session or a display driver without Direct3D 11.
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x20 /* BGRA */,
            IntPtr.Zero, 0, 7 /* SDK version */, out var d3d, IntPtr.Zero, IntPtr.Zero);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out d3d, IntPtr.Zero, IntPtr.Zero);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in dxgiGuid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(d3d);
        }
    }

    private static GraphicsCaptureItem CaptureItemForWindow(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");   // IGraphicsCaptureItem
        var pointer = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, IntPtr featureLevel, IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
