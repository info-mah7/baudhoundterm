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

    public TabRecorder(Window window, FrameworkElement? region, string path)
    {
        _window = window;
        _region = region;
        _path = path;
    }

    public string FilePath => _path;
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
        // Windows 11 draws a yellow outline round a window being captured. It is on screen only, never
        // in the file; turning it off (IsBorderRequired) needs a newer SDK than this build targets.
        TryOff(() => _session.IsCursorCaptureEnabled = false);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _stream = await FileRandomAccessStream.OpenAsync(_path, FileAccessMode.ReadWrite, StorageOpenOptions.None,
                                                         FileOpenDisposition.CreateNew);

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = null;
        profile.Video.Width = (uint)_width;
        profile.Video.Height = (uint)_height;
        profile.Video.FrameRate.Numerator = Fps;
        profile.Video.FrameRate.Denominator = 1;
        // Text needs sharp edges more than motion needs bits; about 0.15 bit per pixel per frame.
        profile.Video.Bitrate = (uint)Math.Clamp(_width * _height * Fps * 15L / 100, 1_000_000, 16_000_000);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, _stream, profile);
        if (!prepared.CanTranscode)
        {
            throw new InvalidOperationException($"Windows could not set up the video encoder ({prepared.FailureReason})");
        }

        _transcode = prepared.TranscodeAsync().AsTask();
        _clock.Start();
        _session.StartCapture();

        // The terminal can move inside the window (sidebar toggled, window resized): follow it.
        _cropTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cropTimer.Tick += (_, _) =>
        {
            // A tab popped out or brought back mid-recording leaves this window; keep the last crop.
            if (_region is null || _region.IsDescendantOf(_window))
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
            var sample = MediaStreamSample.CreateFromBuffer(Crop(snap.Bgra, snap.Width, snap.Height, _crop).AsBuffer(), due);
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
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x20 /* BGRA */,
            IntPtr.Zero, 0, 7 /* SDK version */, out var d3d, IntPtr.Zero, IntPtr.Zero));
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
