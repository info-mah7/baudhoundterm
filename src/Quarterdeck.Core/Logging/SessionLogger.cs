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

        CurrentPath = Path.Combine(dir, ExpandPattern(_settings.FileNamePattern));
        _writer = new StreamWriter(
            new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        _written = new FileInfo(CurrentPath).Length;
        _writer.Write($"--- session opened {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} ---{Environment.NewLine}");
    }

    private string ExpandPattern(string pattern)
    {
        var now = DateTimeOffset.Now;
        var safeName = string.Concat(_definition.Name.Select(
            c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        return pattern
            .Replace("%NAME%", safeName)
            .Replace("%HOST%", string.IsNullOrEmpty(_definition.Host) ? "local" : _definition.Host)
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
        if (_writer is null || text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var payload = _settings.StripAnsi ? AnsiPattern().Replace(text, "") : text;
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
                _writer.Write(
                    $"--- session closed {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} ---{Environment.NewLine}");
                _writer.Dispose();
                _writer = null;
            }
        }
    }
}
