using System.Text;

namespace Quarterdeck.Core.Terminal;

/// <summary>
/// Local line editing for consoles that take whole lines and do not echo, such as BaudHound's
/// telnet port: keystrokes are echoed and edited here, and only a finished line goes out, ended
/// with CR LF so both CR- and LF-terminated readers act on it.
/// </summary>
public sealed class LineEditor
{
    private readonly StringBuilder _line = new();
    private bool _afterCr;
    private Escape _escape;

    private enum Escape { None, Esc, Sequence }

    /// <summary>
    /// Takes what the terminal produced and returns what to send to the far end and what to
    /// show locally. Escape sequences (arrow keys, function keys) are dropped: a line console
    /// has nothing to do with them.
    /// </summary>
    public (string Send, string Echo) Feed(string input)
    {
        var send = new StringBuilder();
        var echo = new StringBuilder();

        foreach (var c in input)
        {
            switch (_escape)
            {
                case Escape.Esc:
                    // ESC [ and ESC O start a sequence; any other byte ends a two-byte escape.
                    _escape = c is '[' or 'O' ? Escape.Sequence : Escape.None;
                    continue;
                case Escape.Sequence:
                    if (c is >= '@' and <= '~')
                    {
                        _escape = Escape.None;   // the final byte
                    }

                    continue;
            }

            // A pasted CR LF is one line break, not a line and then an empty one.
            if (c == '\n' && _afterCr)
            {
                _afterCr = false;
                continue;
            }

            _afterCr = c == '\r';

            switch (c)
            {
                case '\u001b':
                    _escape = Escape.Esc;
                    break;
                case '\r' or '\n':
                    send.Append(_line).Append("\r\n");
                    echo.Append("\r\n");
                    _line.Clear();
                    break;
                case '\b' or '\u007f':
                    if (_line.Length > 0)
                    {
                        var drop = _line.Length >= 2 && char.IsLowSurrogate(_line[^1]) ? 2 : 1;
                        _line.Length -= drop;
                        echo.Append("\b \b");
                    }

                    break;
                case '\u0003':   // Ctrl+C: abandon the line
                    _line.Clear();
                    echo.Append("^C\r\n");
                    break;
                case '\u0015':   // Ctrl+U: erase the line
                    echo.Insert(echo.Length, "\b \b", _line.Length);
                    _line.Clear();
                    break;
                case '\t':
                    _line.Append(c);
                    echo.Append(c);
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        _line.Append(c);
                        echo.Append(c);
                    }

                    break;
            }
        }

        return (send.ToString(), echo.ToString());
    }

    /// <summary>A fresh connection starts with an empty line.</summary>
    public void Reset()
    {
        _line.Clear();
        _afterCr = false;
        _escape = Escape.None;
    }
}
