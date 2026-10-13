using System.Text;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// What a terminal has printed, as plain text an agent can read: escape sequences removed,
/// carriage-return overwrites and backspaces applied. Positions are absolute (they keep counting
/// after old text is trimmed), so "everything since I pressed Enter" stays meaningful.
/// Thread-safe: the UI thread appends, the agent reads from the thread pool.
/// </summary>
public sealed class TerminalTranscript
{
    private const int Cap = 400_000;

    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private long _dropped;
    private State _state = State.Normal;

    private enum State { Normal, Esc, Csi, Osc, OscEsc, Charset }

    public DateTime LastChangeUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Absolute position of the end of the text.</summary>
    public long Mark
    {
        get { lock (_gate) { return _dropped + _text.Length; } }
    }

    public void Append(string raw)
    {
        lock (_gate)
        {
            foreach (var c in raw)
            {
                Feed(c);
            }

            if (_text.Length > Cap)
            {
                var cut = _text.Length - Cap / 2;
                _text.Remove(0, cut);
                _dropped += cut;
            }

            LastChangeUtc = DateTime.UtcNow;
        }
    }

    // A small state machine rather than a regex: output arrives in chunks, and an escape
    // sequence split across two of them must not leak half of itself into the text.
    private void Feed(char c)
    {
        switch (_state)
        {
            case State.Esc:
                _state = c switch
                {
                    '[' => State.Csi,
                    ']' => State.Osc,
                    '(' or ')' or '*' or '+' or '#' => State.Charset,
                    _ => State.Normal
                };
                return;

            case State.Csi:
                if (c >= '@' && c <= '~')
                {
                    _state = State.Normal;
                }

                return;

            case State.Osc:
                if (c == '\a')
                {
                    _state = State.Normal;
                }
                else if (c == '\u001b')
                {
                    _state = State.OscEsc;
                }

                return;

            case State.OscEsc:
                _state = c == '\\' ? State.Normal : State.Osc;
                return;

            case State.Charset:
                _state = State.Normal;
                return;
        }

        switch (c)
        {
            case '\u001b':
                _state = State.Esc;
                break;

            case '\n':
                _text.Append('\n');
                break;

            case '\r':
                // Deferred: "\r\n" is a newline, a lone "\r" rewinds the line. Mark it and let
                // the next character decide.
                _text.Append('\r');
                break;

            case '\b':
                if (_text.Length > 0 && _text[^1] != '\n')
                {
                    _text.Length--;
                }

                break;

            case '\t':
                _text.Append('\t');
                break;

            default:
                if (c < ' ' || c == '\u007f')
                {
                    break;   // other control characters carry nothing readable
                }

                if (_text.Length > 0 && _text[^1] == '\r')
                {
                    // A lone carriage return followed by text: the line is being redrawn.
                    _text.Length--;
                    var lineStart = LastLineStart();
                    _text.Length = lineStart;
                }

                _text.Append(c);
                break;
        }

        if (c == '\n' && _text.Length >= 2 && _text[^2] == '\r')
        {
            _text.Remove(_text.Length - 2, 1);
        }
    }

    private int LastLineStart()
    {
        for (var i = _text.Length - 1; i >= 0; i--)
        {
            if (_text[i] == '\n')
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Text after an absolute position (what arrived since then).</summary>
    public string Since(long mark, int maxChars)
    {
        lock (_gate)
        {
            var from = (int)Math.Max(0, mark - _dropped);
            if (from >= _text.Length)
            {
                return "";
            }

            var s = _text.ToString(from, _text.Length - from).Replace("\r", "");
            return s.Length > maxChars ? "[... earlier output cut ...]\n" + s[^maxChars..] : s;
        }
    }

    /// <summary>The last <paramref name="lines"/> lines.</summary>
    public string Tail(int lines, int maxChars)
    {
        lock (_gate)
        {
            var s = _text.ToString().Replace("\r", "");
            var parts = s.Split('\n');
            var tail = string.Join('\n', parts.Skip(Math.Max(0, parts.Length - lines)));
            return tail.Length > maxChars ? tail[^maxChars..] : tail;
        }
    }
}
