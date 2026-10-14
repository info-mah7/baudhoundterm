using System.Text;
using System.Text.RegularExpressions;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Logging;

/// <summary>
/// Writes a session transcript to disk. Optionally strips ANSI control sequences so the
/// log is readable text rather than a replay of the screen, and rolls at a size limit.
/// </summary>
public sealed partial class SessionLogger : IDisposable
{
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[@-Z\\-_]")]
    private static partial Regex AnsiPattern();

    private readonly LoggingSettings _settings;
    private readonly SessionDefinition _definition;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private long _written;
    private bool _atLineStart = true;

    // Output arrives in arbitrary chunks: an escape sequence or a CR LF pair can straddle two.
    // The unfinished tail waits here for the next chunk instead of leaking into the log.
    private string _pendingEscape = "";
    private bool _pendingCr;

    public string? CurrentPath { get; private set; }

    public SessionLogger(SessionDefinition definition)
    {
        _definition = definition;
        _settings = definition.Logging;

        if (_settings.Enabled)
        {
            Open();
        }
    }

    private void Open()
    {
        var dir = string.IsNullOrWhiteSpace(_settings.Directory)
            ? AppPaths.DefaultLogDirectory
            : _settings.Directory;
        Directory.CreateDirectory(dir);

        CurrentPath = NotFull(Path.Combine(dir, ExpandPattern(_settings.FileNamePattern)));
        _writer = new StreamWriter(
            new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        _written = new FileInfo(CurrentPath).Length;
        _writer.Write($"--- session opened {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} ---{Environment.NewLine}");
    }

    /// <summary>
    /// The path itself, or "name.1.log", "name.2.log"... when it is already at the size limit.
    /// A roll used to reopen the same full file whenever the pattern had no seconds in it, and
    /// then rolled again on every write.
    /// </summary>
    private string NotFull(string path)
    {
        if (_settings.MaxBytes <= 0)
        {
            return path;
        }

        var stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
        var ext = Path.GetExtension(path);
        var candidate = path;
        for (var n = 1; File.Exists(candidate) && new FileInfo(candidate).Length >= _settings.MaxBytes; n++)
        {
            candidate = $"{stem}.{n}{ext}";
        }

        return candidate;
    }

    private static string Safe(string text) =>
        string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private string ExpandPattern(string pattern)
    {
        var now = DateTimeOffset.Now;

        // Both are user text: a host can be an IPv6 literal (fe80::1), which is no file name.
        return pattern
            .Replace("%NAME%", Safe(_definition.Name))
            .Replace("%HOST%", string.IsNullOrEmpty(_definition.Host) ? "local" : Safe(_definition.Host))
            .Replace("%Y%", now.ToString("yyyy"))
            .Replace("%M%", now.ToString("MM"))
            .Replace("%D%", now.ToString("dd"))
            .Replace("%H%", now.ToString("HH"))
            .Replace("%m%", now.ToString("mm"))
            .Replace("%S%", now.ToString("ss"));
    }

    /// <summary>Feed everything the far end sent. Input echoes back through the same stream.</summary>
    public void Append(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            // Checked under the lock: Dispose (or a failed roll) can clear it at any moment.
            if (_writer is null)
            {
                return;
            }

            var payload = _settings.StripAnsi ? StripAnsi(text) : text;

            if (_pendingCr)
            {
                payload = "\r" + payload;
            }

            _pendingCr = payload.EndsWith('\r');
            if (_pendingCr)
            {
                payload = payload[..^1];
            }

            payload = payload.Replace("\r\n", "\n").Replace('\r', '\n');

            if (_settings.PrefixTimestamps)
            {
                payload = StampLines(payload);
            }

            _writer.Write(payload);
            _written += Encoding.UTF8.GetByteCount(payload);

            if (_settings.MaxBytes > 0 && _written >= _settings.MaxBytes)
            {
                Roll();
            }
        }
    }

    /// <summary>Strips complete sequences; an unfinished one at the end is held for the next chunk.</summary>
    private string StripAnsi(string text)
    {
        text = _pendingEscape + text;
        _pendingEscape = "";

        var esc = text.LastIndexOf('\x1B');
        if (esc >= 0 && text.Length - esc < 256)
        {
            var match = AnsiPattern().Match(text, esc);
            var complete = match.Success && match.Index == esc
                           // "ESC ]" alone matches the short form, but an OSC runs to BEL or ST.
                           && !(match.Length == 2 && text[esc + 1] == ']');
            if (!complete)
            {
                _pendingEscape = text[esc..];
                text = text[..esc];
            }
        }

        return AnsiPattern().Replace(text, "");
    }

    private string StampLines(string payload)
    {
        var stamp = $"[{DateTimeOffset.Now:HH:mm:ss}] ";
        var sb = new StringBuilder(payload.Length + 16);

        foreach (var ch in payload)
        {
            if (_atLineStart && ch != '\n')
            {
                sb.Append(stamp);
                _atLineStart = false;
            }

            sb.Append(ch);

            if (ch == '\n')
            {
                _atLineStart = true;
            }
        }

        return sb.ToString();
    }

    private void Roll()
    {
        _writer?.Write($"--- rolled {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} ---{Environment.NewLine}");
        _writer?.Dispose();
        _writer = null;
        Open();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_writer is not null)
            {
                if (_pendingCr)
                {
                    _writer.Write("\n");
                }

                _writer.Write(
                    $"--- session closed {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} ---{Environment.NewLine}");
                _writer.Dispose();
                _writer = null;
            }
        }
    }
}
