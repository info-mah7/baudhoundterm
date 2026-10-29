using System.Drawing;
using System.Net.Http;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Kvm;

namespace Quarterdeck.App.Views;

/// <summary>
/// A KVM as the agent's screen: the same shape as a VNC or RDP tab, so everything built for
/// those (the click check, the risky-action rules, interrupts, skills) works here unchanged.
///
/// Eyes and hands come from two places on the same board. The picture is the vendor's MJPEG
/// stream, because agentkvmd only serves a screenshot where the firmware offers an
/// unauthenticated one and this firmware does not. The hands are agentkvmd, which is the point:
/// its reports go to a USB gadget the target cannot tell from a real keyboard and mouse.
/// </summary>
internal sealed class KvmScreen : IScreenTarget, IDisposable
{
    private readonly KvmClient _hands;
    private readonly KvmVendorClient? _eyes;
    private readonly CancellationTokenSource _life = new();
    private readonly object _gate = new();

    private byte[]? _latest;
    private (int W, int H) _size;
    private int _buttons;
    private byte _modifiers;
    private readonly List<byte> _down = [];

    public KvmScreen(string describe, string name, KvmClient hands, KvmVendorClient? eyes, Action<string>? note = null)
    {
        Describe = describe;
        SessionName = name;
        _hands = hands;
        _eyes = eyes;

        if (_eyes is not null)
        {
            _ = Task.Run(() => WatchAsync(note));
        }
    }

    public string Describe { get; }
    public string SessionName { get; }
    public bool IsConnected => _latest is not null;

    /// <summary>A KVM is never view only: it exists to type. The approval rules still apply.</summary>
    public bool ViewOnly => false;

    /// <summary>The most recent frame, for the tab to draw.</summary>
    public byte[]? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public event Action? FrameArrived;

    /// <summary>
    /// The size of the frame as the tab decoded it. The agent's own captures set this themselves;
    /// a click from the user arrives in those pixels before any capture has happened.
    /// </summary>
    public void SetFrameSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _size = (width, height);
        }
    }

    /// <summary>Keeps the newest frame, reconnecting when the board drops the stream.</summary>
    private async Task WatchAsync(Action<string>? note)
    {
        var failures = 0;
        while (!_life.IsCancellationRequested)
        {
            try
            {
                await _eyes!.ReadFramesAsync(frame =>
                {
                    lock (_gate)
                    {
                        _latest = frame;
                    }

                    failures = 0;
                    FrameArrived?.Invoke();
                }, _life.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is KvmException or HttpRequestException or IOException)
            {
                note?.Invoke($"The KVM's video stopped ({ex.Message}); reconnecting.");
            }

            // The board drops a stream now and then; wait a little longer each time rather than
            // hammering it, and sign in again in case the token was what expired.
            var wait = Math.Min(10, ++failures) * 500;
            try
            {
                await Task.Delay(wait, _life.Token);
                await _eyes!.SignInAsync(_life.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is KvmException or HttpRequestException)
            {
                // Reported on the next failed read.
            }
        }
    }

    public Task<ScreenShot> CaptureAsync(ScreenRegion? region, int maxWidth, ScreenMark? mark, CancellationToken token)
    {
        var jpeg = Latest ?? throw new KvmException("No picture from the KVM yet.");
        using var full = new Bitmap(new MemoryStream(jpeg));
        _size = (full.Width, full.Height);

        var r = region ?? new ScreenRegion(0, 0, 0, 0);
        var x = Math.Clamp(r.X, 0, full.Width - 1);
        var y = Math.Clamp(r.Y, 0, full.Height - 1);
        var w = Math.Max(1, Math.Min(full.Width - x, r.Width > 0 ? r.Width : full.Width));
        var h = Math.Max(1, Math.Min(full.Height - y, r.Height > 0 ? r.Height : full.Height));

        // A region (a zoom, or the close-up a click is checked against) is enlarged to fit,
        // up to 3x; the whole screen only ever shrinks. The same rule as VNC and RDP.
        var fit = maxWidth > 0 ? Math.Min((double)maxWidth / w, (double)maxWidth / h) : 1;
        var scale = Math.Min(r.Width > 0 ? 3 : 1, fit);
        var ow = Math.Max(1, (int)Math.Round(w * scale));
        var oh = Math.Max(1, (int)Math.Round(h * scale));

        using var pic = new Bitmap(ow, oh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(pic))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle(0, 0, ow, oh), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            if (mark is { } m)
            {
                RdpView.DrawCrosshair(g, (float)((m.X - x) * scale), (float)((m.Y - y) * scale), (float)Math.Max(6, m.Size * scale));
            }
        }

        using var buffer = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        pic.Save(buffer, codec, quality);
        return Task.FromResult(new ScreenShot(buffer.ToArray(), ow, oh, full.Width, full.Height));
    }

    public Task<byte[]> ThumbnailAsync(CancellationToken token)
    {
        var jpeg = Latest ?? throw new KvmException("No picture from the KVM yet.");
        using var full = new Bitmap(new MemoryStream(jpeg));
        using var small = new Bitmap(64, 36, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, new Rectangle(0, 0, 64, 36));
        }

        var grey = new byte[64 * 36];
        for (var y = 0; y < 36; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var c = small.GetPixel(x, y);
                grey[y * 64 + x] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
            }
        }

        return Task.FromResult(grey);
    }

    /// <summary>
    /// RFB's pointer, in the frame's own pixels, turned into the daemon's absolute space
    /// (0..32767 per axis, whatever the screen's resolution).
    /// </summary>
    public async Task PointerAsync(int x, int y, int buttons, CancellationToken token)
    {
        var (w, h) = _size.W > 0 ? _size : (1920, 1080);
        var ax = (int)Math.Round((double)Math.Clamp(x, 0, w - 1) / Math.Max(1, w - 1) * KvmClient.AbsMax);
        var ay = (int)Math.Round((double)Math.Clamp(y, 0, h - 1) / Math.Max(1, h - 1) * KvmClient.AbsMax);

        // The wheel is its own report here, not a button, so scroll bits become notches.
        if ((buttons & 8) != 0 && (_buttons & 8) == 0)
        {
            await _hands.WheelAsync(-3, token);
        }
        else if ((buttons & 16) != 0 && (_buttons & 16) == 0)
        {
            await _hands.WheelAsync(3, token);
        }

        _buttons = buttons;
        await _hands.MouseAsync(buttons & 7, ax, ay, token);
    }

    /// <summary>
    /// Releases every held key and modifier. A paste types the clipboard as keystrokes, and a
    /// Ctrl still held from the Ctrl+V that triggered it would turn every character into a Ctrl
    /// combo, so the held set is cleared first.
    /// </summary>
    public async Task ClearHeldAsync(CancellationToken token)
    {
        _modifiers = 0;
        _down.Clear();
        await _hands.KeysAsync(0, _down, token);
    }

    /// <summary>
    /// A keysym pressed or released. The daemon's keyboard report replaces what is held, so the
    /// held set is kept here and sent whole each time.
    /// </summary>
    public async Task KeyAsync(uint keysym, bool down, CancellationToken token)
    {
        if (HidKeys.Map(keysym) is not { } key)
        {
            throw new KvmException($"This keyboard layout has no key for keysym 0x{keysym:x}, so it cannot be typed through the KVM.");
        }

        if (key.Usage == 0)
        {
            _modifiers = down ? (byte)(_modifiers | key.Modifier) : (byte)(_modifiers & ~key.Modifier);
        }
        else if (down)
        {
            if (!_down.Contains(key.Usage))
            {
                _down.Add(key.Usage);
            }

            // A character that needs a modifier (a capital, a symbol) carries it in the report.
            await _hands.KeysAsync((byte)(_modifiers | key.Modifier), _down, token);
            return;
        }
        else if (!_down.Remove(key.Usage))
        {
            return;   // a release for a key not held (typed through the text path): nothing to report
        }

        await _hands.KeysAsync(_modifiers, _down, token);
    }

    public void Dispose()
    {
        _life.Cancel();
        // Releasing held keys off the UI thread: a board that has gone away used to hold the
        // whole window for 2 s per tab.
        var hands = _hands;
        _ = Task.Run(async () =>
        {
            try
            {
                await hands.ReleaseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // The board may already be gone; nothing useful to do with it here.
            }
            finally
            {
                hands.Dispose();
            }
        });

        _eyes?.Dispose();
        _life.Dispose();
    }
}
