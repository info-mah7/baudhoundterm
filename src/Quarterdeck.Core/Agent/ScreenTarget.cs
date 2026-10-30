namespace Quarterdeck.Core.Agent;

/// <summary>What every agent target has, whether it shows text or pictures.</summary>
public interface IAgentTarget
{
    /// <summary>"web01 (SSH admin@192.168.0.20:22)", for the prompt and the log.</summary>
    string Describe { get; }

    string SessionName { get; }
    bool IsConnected { get; }
}

/// <summary>
/// A tab BaudHoundTerm can record to a video file (build 72), for demos. The agent may start,
/// pause, resume and stop it like the user can.
/// </summary>
public interface IRecordable
{
    /// <summary>
    /// "start", "pause", "resume", "stop" or "status"; returns what happened, in words. A start
    /// records <paramref name="scope"/>: "terminal", "tab" (the terminal and the agent pane) or
    /// "window" (all of BaudHoundTerm, for showing the app itself).
    /// </summary>
    Task<string> RecordingAsync(string action, string scope = "tab");
}

/// <summary>A JPEG of the remote screen or part of it.</summary>
/// <param name="Jpeg">The picture.</param>
/// <param name="Width">Its width in pixels, after scaling.</param>
/// <param name="Height">Its height in pixels, after scaling.</param>
/// <param name="ScreenWidth">The remote framebuffer's width.</param>
/// <param name="ScreenHeight">The remote framebuffer's height.</param>
public sealed record ScreenShot(byte[] Jpeg, int Width, int Height, int ScreenWidth, int ScreenHeight);

/// <summary>A rectangle in framebuffer pixels.</summary>
public readonly record struct ScreenRegion(int X, int Y, int Width, int Height);

/// <summary>
/// A crosshair drawn on a picture at the point a click is about to hit. Its four arms stop
/// short of the point so the label under it stays readable: a ring through "Export" made it
/// "Expo.t", and the model could no longer say what was there.
/// </summary>
public readonly record struct ScreenMark(int X, int Y, int Size);

/// <summary>
/// A remote desktop the agent can see and drive, through the connection the tab already has
/// (VNC). Coordinates are framebuffer pixels. The primitives are RFB's own: pointer state and
/// keysyms; clicks, typing and key combinations are built from them in <see cref="ScreenInput"/>.
/// </summary>
public interface IScreenTarget : IAgentTarget
{
    /// <summary>The session is set to view only: the agent may look but not act.</summary>
    bool ViewOnly { get; }

    /// <summary>
    /// Whole screen when region is null, shrunk to fit maxWidth. A region is scaled to fit
    /// maxWidth, enlarged up to 3x, so a close-up is a close-up.
    /// </summary>
    Task<ScreenShot> CaptureAsync(ScreenRegion? region, int maxWidth, ScreenMark? mark, CancellationToken token);

    /// <summary>A 64x36 grey thumbnail (0-255 per pixel), for telling when the screen changed.</summary>
    Task<byte[]> ThumbnailAsync(CancellationToken token);

    /// <summary>RFB PointerEvent: position and button mask (1 left, 2 middle, 4 right, 8/16 wheel).</summary>
    Task PointerAsync(int x, int y, int buttons, CancellationToken token);

    /// <summary>RFB KeyEvent.</summary>
    Task KeyAsync(uint keysym, bool down, CancellationToken token);
}

/// <summary>Clicks, typing and key combinations as RFB pointer and key events.</summary>
public static class ScreenInput
{
    private const int Gap = 15;   // ms between events: some servers drop a press and release in the same frame

    // Stopped part way, a press is still let go (with CancellationToken.None): otherwise a
    // mouse button or Ctrl stayed held down on the remote desktop after the user pressed Stop.

    public static async Task ClickAsync(IScreenTarget t, int x, int y, string button, int count, CancellationToken token)
    {
        var mask = button switch { "right" => 4, "middle" => 2, _ => 1 };
        await t.PointerAsync(x, y, 0, token);
        await Task.Delay(Gap * 3, token);
        try
        {
            for (var i = 0; i < Math.Clamp(count, 1, 3); i++)
            {
                await t.PointerAsync(x, y, mask, token);
                await Task.Delay(Gap * 3, token);
                await t.PointerAsync(x, y, 0, token);
                await Task.Delay(Gap * 4, token);
            }
        }
        catch (OperationCanceledException)
        {
            await t.PointerAsync(x, y, 0, CancellationToken.None);
            throw;
        }
    }

    public static async Task DragAsync(IScreenTarget t, int x1, int y1, int x2, int y2, CancellationToken token)
    {
        await t.PointerAsync(x1, y1, 0, token);
        await Task.Delay(Gap * 3, token);
        try
        {
            await t.PointerAsync(x1, y1, 1, token);
            const int steps = 12;
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(Gap * 2, token);
                await t.PointerAsync(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps, 1, token);
            }

            await Task.Delay(Gap * 3, token);
            await t.PointerAsync(x2, y2, 0, token);
        }
        catch (OperationCanceledException)
        {
            await t.PointerAsync(x2, y2, 0, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Wheel notches at a point; negative scrolls up.</summary>
    public static async Task ScrollAsync(IScreenTarget t, int x, int y, int notches, CancellationToken token)
    {
        var mask = notches < 0 ? 8 : 16;
        await t.PointerAsync(x, y, 0, token);
        for (var i = 0; i < Math.Min(Math.Abs(notches), 30); i++)
        {
            await t.PointerAsync(x, y, mask, token);
            await t.PointerAsync(x, y, 0, token);
            await Task.Delay(Gap * 2, token);
        }
    }

    public static async Task TypeAsync(IScreenTarget t, string text, CancellationToken token)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var keysym = CharKeysym(rune.Value);
            if (keysym == 0)
            {
                continue;
            }

            // Press, hold briefly, release, and leave room before the next key. Sent back to back,
            // Windows over TightVNC lost the key after each space ("agent test" arrived as "gent est").
            await t.KeyAsync(keysym, true, token);
            try
            {
                await Task.Delay(Gap, token);
            }
            finally
            {
                await t.KeyAsync(keysym, false, CancellationToken.None);
            }

            await Task.Delay(rune.Value == ' ' ? Gap * 5 : Gap * 2, token);
        }
    }

    /// <summary>"ctrl+alt+del", "win+r", "enter", "shift+tab": modifiers pressed in order, released in reverse.</summary>
    public static async Task ComboAsync(IScreenTarget t, string combo, CancellationToken token)
    {
        var keys = ParseCombo(combo);
        var down = 0;
        try
        {
            foreach (var k in keys)
            {
                await t.KeyAsync(k, true, token);
                down++;
                await Task.Delay(Gap, token);
            }
        }
        finally
        {
            // Released in reverse whether or not the run was stopped mid-way.
            for (var i = down - 1; i >= 0; i--)
            {
                await t.KeyAsync(keys[i], false, CancellationToken.None);
                await Task.Delay(Gap, CancellationToken.None);
            }
        }
    }

    public static List<uint> ParseCombo(string combo)
    {
        var parts = combo.Trim().ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 && combo.Contains('+'))
        {
            parts = ["+"];
        }

        var keys = new List<uint>();
        foreach (var p in parts)
        {
            keys.Add(NamedKeysym(p) ?? throw new ArgumentException($"unknown key '{p}' in '{combo}'"));
        }

        return keys;
    }

    private static uint? NamedKeysym(string name) => name switch
    {
        "ctrl" or "control" => 0xffe3,
        "shift" => 0xffe1,
        "alt" => 0xffe9,
        "altgr" => 0xfe03,
        "win" or "super" or "meta" or "cmd" => 0xffeb,
        "enter" or "return" => 0xff0d,
        "esc" or "escape" => 0xff1b,
        "tab" => 0xff09,
        "backspace" => 0xff08,
        "del" or "delete" => 0xffff,
        "ins" or "insert" => 0xff63,
        "home" => 0xff50,
        "end" => 0xff57,
        "pgup" or "pageup" => 0xff55,
        "pgdn" or "pagedown" => 0xff56,
        "up" => 0xff52,
        "down" => 0xff54,
        "left" => 0xff51,
        "right" => 0xff53,
        "space" => 0x20,
        "menu" => 0xff67,
        "printscreen" or "prtsc" => 0xff61,
        "capslock" => 0xffe5,
        _ when name.Length > 1 && name[0] == 'f' && int.TryParse(name[1..], out var n) && n is >= 1 and <= 12 => (uint)(0xffbe + n - 1),
        _ when name.Length == 1 => CharKeysym(name[0]),
        _ => null
    };

    /// <summary>X11 keysym for a character: Latin-1 maps to itself, other Unicode to 0x01000000 + code point.</summary>
    public static uint CharKeysym(int c) => c switch
    {
        '\n' or '\r' => 0xff0d,
        '\t' => 0xff09,
        '\b' => 0xff08,
        < 0x20 or 0x7f => 0,
        < 0x100 => (uint)c,
        _ => 0x01000000u + (uint)c
    };
}
