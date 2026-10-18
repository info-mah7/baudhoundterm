namespace Quarterdeck.Core.Kvm;

/// <summary>
/// X11 keysyms (what the rest of the app speaks, because VNC does) turned into USB HID usage
/// codes with the modifiers they need, which is what a keyboard gadget sends.
///
/// The table is the US layout, as the daemon's own typing is: a target set to another layout
/// receives the key in that position, so some characters arrive wrong. There is no way to know
/// the target's layout from here, and guessing would be worse than saying so.
/// </summary>
public static class HidKeys
{
    /// <summary>Modifier bits of a boot-keyboard report.</summary>
    public const byte LeftCtrl = 0x01, LeftShift = 0x02, LeftAlt = 0x04, LeftGui = 0x08, RightAlt = 0x40;

    /// <summary>The usage and modifiers for one keysym, or null when this layout cannot type it.</summary>
    public static (byte Usage, byte Modifier)? Map(uint keysym)
    {
        if (Named.TryGetValue(keysym, out var named))
        {
            return named;
        }

        // Unicode keysyms (0x01000000 + code point) come back as their Latin-1 equivalent when
        // there is one; anything else has no key on a US keyboard.
        var c = keysym switch
        {
            < 0x100 => (char)keysym,
            >= 0x01000020 and <= 0x010000ff => (char)(keysym - 0x01000000),
            _ => '\0'
        };

        return c == '\0' ? null : Character(c);
    }

    /// <summary>The usage and modifiers that type one character on a US keyboard.</summary>
    public static (byte Usage, byte Modifier)? Character(char c) => c switch
    {
        >= 'a' and <= 'z' => ((byte)(0x04 + c - 'a'), (byte)0),
        >= 'A' and <= 'Z' => ((byte)(0x04 + c - 'A'), LeftShift),
        >= '1' and <= '9' => ((byte)(0x1E + c - '1'), (byte)0),
        '0' => (0x27, 0),
        ' ' => (0x2C, 0),
        '\n' or '\r' => (0x28, 0),
        '\t' => (0x2B, 0),
        '\b' => (0x2A, 0),
        '-' => (0x2D, 0),
        '_' => (0x2D, LeftShift),
        '=' => (0x2E, 0),
        '+' => (0x2E, LeftShift),
        '[' => (0x2F, 0),
        '{' => (0x2F, LeftShift),
        ']' => (0x30, 0),
        '}' => (0x30, LeftShift),
        '\\' => (0x31, 0),
        '|' => (0x31, LeftShift),
        ';' => (0x33, 0),
        ':' => (0x33, LeftShift),
        '\'' => (0x34, 0),
        '"' => (0x34, LeftShift),
        '`' => (0x35, 0),
        '~' => (0x35, LeftShift),
        ',' => (0x36, 0),
        '<' => (0x36, LeftShift),
        '.' => (0x37, 0),
        '>' => (0x37, LeftShift),
        '/' => (0x38, 0),
        '?' => (0x38, LeftShift),
        '!' => (0x1E, LeftShift),
        '@' => (0x1F, LeftShift),
        '#' => (0x20, LeftShift),
        '$' => (0x21, LeftShift),
        '%' => (0x22, LeftShift),
        '^' => (0x23, LeftShift),
        '&' => (0x24, LeftShift),
        '*' => (0x25, LeftShift),
        '(' => (0x26, LeftShift),
        ')' => (0x27, LeftShift),
        _ => null
    };

    /// <summary>Keys with a name rather than a character: the ones ScreenInput's combos use.</summary>
    private static readonly Dictionary<uint, (byte Usage, byte Modifier)> Named = new()
    {
        // Modifiers are keys in their own right: holding one is a report with the bit set and
        // no usage, so the usage is 0 and the caller ORs the modifier in.
        [0xffe1] = (0, LeftShift),    // shift
        [0xffe2] = (0, LeftShift),    // right shift, as left: the target cannot tell for typing
        [0xffe3] = (0, LeftCtrl),     // ctrl
        [0xffe4] = (0, LeftCtrl),
        [0xffe9] = (0, LeftAlt),      // alt
        [0xffea] = (0, RightAlt),     // right alt
        [0xfe03] = (0, RightAlt),     // altgr is right alt on the wire
        [0xffeb] = (0, LeftGui),      // win / super / meta / cmd
        [0xffec] = (0, LeftGui),
        [0xffe7] = (0, LeftGui),

        [0xff0d] = (0x28, 0),         // enter
        [0xff1b] = (0x29, 0),         // esc
        [0xff08] = (0x2A, 0),         // backspace
        [0xff09] = (0x2B, 0),         // tab
        [0xffff] = (0x4C, 0),         // delete
        [0xff63] = (0x49, 0),         // insert
        [0xff50] = (0x4A, 0),         // home
        [0xff57] = (0x4D, 0),         // end
        [0xff55] = (0x4B, 0),         // page up
        [0xff56] = (0x4E, 0),         // page down
        [0xff52] = (0x52, 0),         // up
        [0xff54] = (0x51, 0),         // down
        [0xff51] = (0x50, 0),         // left
        [0xff53] = (0x4F, 0),         // right
        [0xff67] = (0x65, 0),         // menu
        [0xff61] = (0x46, 0),         // print screen
        [0xffe5] = (0x39, 0),         // caps lock
        [0xff14] = (0x47, 0),         // scroll lock
        [0xff13] = (0x48, 0),         // pause
        [0xff7f] = (0x53, 0),         // num lock

        [0xffbe] = (0x3A, 0), [0xffbf] = (0x3B, 0), [0xffc0] = (0x3C, 0), [0xffc1] = (0x3D, 0),
        [0xffc2] = (0x3E, 0), [0xffc3] = (0x3F, 0), [0xffc4] = (0x40, 0), [0xffc5] = (0x41, 0),
        [0xffc6] = (0x42, 0), [0xffc7] = (0x43, 0), [0xffc8] = (0x44, 0), [0xffc9] = (0x45, 0)
    };
}
