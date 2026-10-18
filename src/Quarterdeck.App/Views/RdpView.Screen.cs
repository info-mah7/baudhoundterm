using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Quarterdeck.Core.Agent;

namespace Quarterdeck.App.Views;

/// <summary>
/// The RDP tab as the agent's screen. The remote desktop is the control's input window
/// (IHWindowClass): it has the desktop's own size, which is not always what was asked for, and
/// the control centres it when it is smaller than the tab. Pictures are that part of the control
/// (PrintWindow, or the screen under it when that comes back blank); pointer and key events are
/// window messages to it, which it turns into RDP input as it does for the real mouse and
/// keyboard, but only while it has the focus. Keys arrive as X11 keysyms, as on VNC, and become
/// scan codes for the local keyboard layout, which RDP expects to match the remote one.
/// </summary>
internal sealed partial class RdpView
{
    private int _buttons;
    private readonly HashSet<ushort> _held = [];
    private readonly Dictionary<ushort, List<ushort>> _added = [];

    /// <summary>
    /// The input window, where the visible part of the remote desktop sits in the control (X, Y),
    /// its size, and where that part starts in the input window (Ix, Iy: not 0 when scrolled).
    /// </summary>
    private (IntPtr Input, int X, int Y, int W, int H, int Ix, int Iy) Desktop()
    {
        if (_ax is null || _lastState != 1)
        {
            throw new InvalidOperationException("no picture yet: the session is not connected");
        }

        var input = IntPtr.Zero;
        var name = new StringBuilder(64);
        EnumChildWindows(_ax.Handle, (child, _) =>
        {
            name.Clear();
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == "IHWindowClass")
            {
                input = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        if (input == IntPtr.Zero || !GetWindowRect(input, out var r) || !GetWindowRect(_ax.Handle, out var c))
        {
            throw new InvalidOperationException("the Remote Desktop control has no input window yet");
        }

        // A desktop larger than the tab is cut off at the tab's edge (the control scrolls it).
        var x = Math.Max(0, r.Left - c.Left);
        var y = Math.Max(0, r.Top - c.Top);
        var w = Math.Min(r.Right, c.Right) - Math.Max(r.Left, c.Left);
        var h = Math.Min(r.Bottom, c.Bottom) - Math.Max(r.Top, c.Top);
        return w > 0 && h > 0
            ? (input, x, y, w, h, Math.Max(0, c.Left - r.Left), Math.Max(0, c.Top - r.Top))
            : throw new InvalidOperationException("the remote desktop is not on screen");
    }

    public Task<ScreenShot> CaptureAsync(ScreenRegion? region, int maxWidth, ScreenMark? mark) => Dispatcher.InvokeAsync(() =>
    {
        using var fb = Grab();
        var r = region ?? new ScreenRegion(0, 0, 0, 0);
        var x = Math.Clamp(r.X, 0, fb.Width - 1);
        var y = Math.Clamp(r.Y, 0, fb.Height - 1);
        var w = Math.Max(1, Math.Min(fb.Width - x, r.Width > 0 ? r.Width : fb.Width));
        var h = Math.Max(1, Math.Min(fb.Height - y, r.Height > 0 ? r.Height : fb.Height));

        // As vnc.html: a region (zoom, click check) is enlarged to fit maxWidth, up to 3x; the
        // whole screen only ever shrinks.
        var fit = maxWidth > 0 ? Math.Min((double)maxWidth / w, (double)maxWidth / h) : 1;
        var scale = Math.Min(r.Width > 0 ? 3 : 1, fit);
        var ow = Math.Max(1, (int)Math.Round(w * scale));
        var oh = Math.Max(1, (int)Math.Round(h * scale));

        using var pic = new Bitmap(ow, oh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(pic))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(fb, new Rectangle(0, 0, ow, oh), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            if (mark is { } m)
            {
                DrawMark(g, (float)((m.X - x) * scale), (float)((m.Y - y) * scale), (float)Math.Max(6, m.Size * scale));
            }
        }

        using var jpeg = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        pic.Save(jpeg, codec, quality);
        return new ScreenShot(jpeg.ToArray(), ow, oh, fb.Width, fb.Height);
    }).Task;

    /// <summary>The crosshair vnc.html draws: four arms that stop short of the point.</summary>
    private static void DrawMark(Graphics g, float mx, float my, float r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var gap = Math.Max(8, r * 0.6f);
        var arm = gap + Math.Max(14, r);
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            foreach (var (colour, width) in new[] { (Color.White, 5f), (Color.Red, 3f) })
            {
                using var pen = new Pen(colour, width);
                g.DrawLine(pen, mx + dx * gap, my + dy * gap, mx + dx * arm, my + dy * arm);
            }
        }
    }

    public Task<byte[]> ThumbnailAsync() => Dispatcher.InvokeAsync(() =>
    {
        using var fb = Grab();
        using var small = new Bitmap(64, 36, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(fb, new Rectangle(0, 0, 64, 36));
        }

        var grey = new byte[64 * 36];
        for (var py = 0; py < 36; py++)
        {
            for (var px = 0; px < 64; px++)
            {
                var c = small.GetPixel(px, py);
                grey[py * 64 + px] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
            }
        }

        return grey;
    }).Task;

    /// <summary>
    /// The remote desktop as the control shows it. PrintWindow reads the window even when other
    /// windows cover it; when it comes back as one flat colour (some GPUs), the screen is read.
    /// </summary>
    private Bitmap Grab()
    {
        var (_, dx, dy, w, h, _, _) = Desktop();
        var hwnd = _ax!.Handle;
        using var all = new Bitmap(Math.Max(1, _ax.Width), Math.Max(1, _ax.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(all))
        {
            var dc = g.GetHdc();
            var ok = PrintWindow(hwnd, dc, PwRenderFullContent);
            g.ReleaseHdc(dc);
            if (!ok || Flat(all))
            {
                var at = _ax.PointToScreen(System.Drawing.Point.Empty);
                g.CopyFromScreen(at.X, at.Y, 0, 0, all.Size);
            }
        }

        return all.Clone(new Rectangle(dx, dy, Math.Min(w, all.Width - dx), Math.Min(h, all.Height - dy)), PixelFormat.Format24bppRgb);
    }

    private static bool Flat(Bitmap b)
    {
        var first = b.GetPixel(0, 0);
        for (var i = 1; i < 64; i++)
        {
            if (b.GetPixel(b.Width * i / 64, b.Height * (i * 7 % 64) / 64) != first)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The input window, given the focus first: it ignores input while it has none.</summary>
    private (IntPtr Input, int W, int H, int Ix, int Iy) Focused()
    {
        var (input, _, _, w, h, ix, iy) = Desktop();
        if (GetFocus() != input)
        {
            SetFocus(input);
        }

        return (input, w, h, ix, iy);
    }

    /// <summary>RFB-style pointer: position and button mask (1 left, 2 middle, 4 right, 8/16 wheel).</summary>
    public Task PointerAsync(int x, int y, int buttons) => Dispatcher.InvokeAsync(() =>
    {
        var (input, w, h, ix, iy) = Focused();
        x = Math.Clamp(x, 0, w - 1) + ix;
        y = Math.Clamp(y, 0, h - 1) + iy;
        var at = (IntPtr)((y << 16) | (x & 0xFFFF));

        PostMessage(input, WmMouseMove, (IntPtr)Held(_buttons), at);
        foreach (var (bit, down, up) in new[] { (1, WmLButtonDown, WmLButtonUp), (2, WmMButtonDown, WmMButtonUp), (4, WmRButtonDown, WmRButtonUp) })
        {
            if ((buttons & bit) != 0 && (_buttons & bit) == 0)
            {
                PostMessage(input, down, (IntPtr)Held(buttons & 7), at);
            }
            else if ((buttons & bit) == 0 && (_buttons & bit) != 0)
            {
                PostMessage(input, up, (IntPtr)Held(buttons & 7), at);
            }
        }

        // The wheel bits are a press and release in RFB; one notch per press.
        if ((buttons & 24) != 0 && (_buttons & 24) == 0)
        {
            var screen = new POINT { X = x, Y = y };
            ClientToScreen(input, ref screen);
            var delta = (buttons & 8) != 0 ? 120 : -120;
            PostMessage(input, WmMouseWheel, (IntPtr)((delta << 16) | Held(buttons & 7)), (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF)));
        }

        _buttons = buttons;
    }).Task;

    private static int Held(int mask) => ((mask & 1) != 0 ? 0x1 : 0) | ((mask & 4) != 0 ? 0x2 : 0) | ((mask & 2) != 0 ? 0x10 : 0);

    /// <summary>RFB-style key event: a keysym pressed or released.</summary>
    public Task KeyAsync(uint keysym, bool down) => Dispatcher.InvokeAsync(() =>
    {
        var (input, _, _, _, _) = Focused();
        if (Named(keysym) is { } key)
        {
            Post(input, key.Vk, key.Extended, down);
            return;
        }

        // A character: the key that types it on this keyboard layout, with the shift state it
        // needs. RFB servers add the shift themselves; RDP sends keys, so it is added here.
        var ch = keysym < 0x100 ? (char)keysym : keysym is >= 0x01000100 and <= 0x0100FFFF ? (char)(keysym - 0x01000000) : '\0';
        var scan = ch == '\0' ? (short)-1 : VkKeyScanW(ch);
        if (scan == -1)
        {
            throw new InvalidOperationException($"'{(ch == '\0' ? $"keysym {keysym:x}" : ch)}' is not on this PC's keyboard layout, so it cannot be typed over RDP");
        }

        var vk = (ushort)(scan & 0xFF);
        if (!down)
        {
            Post(input, vk, false, false);
            // Modifiers a character needed are let go with it, not held for the next one.
            if (_added.Remove(vk, out var added))
            {
                added.ForEach(m => Post(input, m, false, false));
            }

            return;
        }

        var shifts = (scan >> 8) & 0x7;
        var mods = new List<ushort>();
        if ((shifts & 1) != 0 && !_held.Contains(0x10)) mods.Add(0x10);
        if ((shifts & 2) != 0 && !_held.Contains(0x11)) mods.Add(0x11);
        if ((shifts & 4) != 0 && !_held.Contains(0x12)) mods.Add(0x12);
        mods.ForEach(m => Post(input, m, false, true));
        _added[vk] = mods;
        Post(input, vk, false, true);
    }).Task;

    private void Post(IntPtr input, ushort vk, bool extended, bool down)
    {
        if (down)
        {
            _held.Add(vk);
        }
        else
        {
            _held.Remove(vk);
        }

        var alt = _held.Contains(0x12) && vk != 0x12;
        var scan = MapVirtualKeyW(vk, 0);
        var lParam = 1u | (scan << 16) | (extended ? 1u << 24 : 0) | (alt ? 1u << 29 : 0) | (down ? 0 : 3u << 30);
        var msg = alt || vk == 0x12 ? (down ? WmSysKeyDown : WmSysKeyUp) : (down ? WmKeyDown : WmKeyUp);
        PostMessage(input, msg, (IntPtr)vk, (IntPtr)(int)lParam);
    }

    /// <summary>Virtual key for a named X11 keysym, and whether it is an extended key.</summary>
    private static (ushort Vk, bool Extended)? Named(uint keysym) => keysym switch
    {
        0xffe1 or 0xffe2 => (0x10, false),          // shift
        0xffe3 => (0x11, false),                    // ctrl
        0xffe4 => (0x11, true),                     // right ctrl
        0xffe9 => (0x12, false),                    // alt
        0xffea or 0xfe03 => (0x12, true),           // right alt, altgr
        0xffeb or 0xffe7 => (0x5B, true),           // win
        0xffec => (0x5C, true),
        0xff0d => (0x0D, false),
        0xff1b => (0x1B, false),
        0xff09 => (0x09, false),
        0xff08 => (0x08, false),
        0xffff => (0x2E, true),
        0xff63 => (0x2D, true),
        0xff50 => (0x24, true),
        0xff57 => (0x23, true),
        0xff55 => (0x21, true),
        0xff56 => (0x22, true),
        0xff51 => (0x25, true),
        0xff52 => (0x26, true),
        0xff53 => (0x27, true),
        0xff54 => (0x28, true),
        0xff67 => (0x5D, true),
        0xff61 => (0x2C, true),
        0xffe5 => (0x14, false),
        0x20 => (0x20, false),
        >= 0xffbe and <= 0xffc9 => ((ushort)(0x70 + keysym - 0xffbe), false),
        _ => null
    };

    private const uint PwRenderFullContent = 2;
    private const uint WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const uint WmMouseMove = 0x200, WmLButtonDown = 0x201, WmLButtonUp = 0x202, WmRButtonDown = 0x204, WmRButtonUp = 0x205;
    private const uint WmMButtonDown = 0x207, WmMButtonUp = 0x208, WmMouseWheel = 0x20A;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint type);
}
