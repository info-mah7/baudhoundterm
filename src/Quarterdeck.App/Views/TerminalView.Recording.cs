using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// Recording this tab to a video file, by the user (tab menu) or the agent (its recording tool),
/// or by the shortcut itself when it is audited (build 75): then it starts with the session, cannot
/// be paused or stopped, and its files are hashed and copied away when it ends.
/// </summary>
public partial class TerminalView : IRecordable
{
    private TabRecorder? _recorder;
    private bool _audit;

    // Every session log this tab has written while audited, across reconnects.
    private readonly HashSet<string> _auditLogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a recording starts, pauses, resumes or stops, so the tab header can say so.</summary>
    public event EventHandler? RecordingChanged;

    /// <summary>Raised with a warning when an audit's files could not be hashed or copied.</summary>
    public event EventHandler<string>? AuditProblem;

    public bool IsRecording => _recorder is not null;
    public bool RecordingPaused => _recorder?.Paused == true;
    public TimeSpan RecordingElapsed => _recorder?.Elapsed ?? TimeSpan.Zero;

    /// <summary>An audit recording: no pause or stop, and Hide addresses stays as it is.</summary>
    public bool RecordingIsAudit => _recorder is not null && _audit;

    public static string RecordingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BaudHoundTerm", "recordings");

    /// <summary>
    /// What a start records. tab: the terminal and the agent pane beside it, when open; while another
    /// tab is in front, a card says so (see TabRecorder). window: null, meaning all of BaudHoundTerm
    /// (the window this tab is in, so a popped-out tab gives its own window).
    /// </summary>
    private FrameworkElement? RecordRegion(string scope) => scope == "window" ? null : RecordArea;

    /// <summary>The scope of the running recording, for the menu and the tab header.</summary>
    public string RecordingScope { get; private set; } = "";

    public Task<string> RecordingAsync(string action, string scope = "tab") =>
        Dispatcher.InvokeAsync(() => RecordingOnUiAsync(action, scope == "window" ? "window" : "tab")).Task.Unwrap();

    private const string AuditRefusal =
        "refused: this shortcut is audited, so its recording runs until the tab closes and cannot be paused or stopped";

    private async Task<string> RecordingOnUiAsync(string action, string scope)
    {
        switch (action)
        {
            case "start":
                return await StartRecordingAsync(scope, audit: false);

            case "pause":
                if (_recorder is null)
                {
                    return "not recording";
                }

                if (_audit)
                {
                    return AuditRefusal;
                }

                _recorder.Pause();
                RecordingChanged?.Invoke(this, EventArgs.Empty);
                return $"paused at {_recorder.Elapsed:mm\\:ss}";

            case "resume":
                if (_recorder is null)
                {
                    return "not recording";
                }

                _recorder.Resume();
                RecordingChanged?.Invoke(this, EventArgs.Empty);
                return $"recording again from {_recorder.Elapsed:mm\\:ss}";

            case "stop":
                if (_recorder is null)
                {
                    return "not recording";
                }

                return _audit ? AuditRefusal : await StopRecordingAsync();

            case "status":
                return _recorder is null
                    ? "not recording"
                    : $"{(_audit ? "audit recording" : _recorder.Paused ? "paused" : "recording")} {_recorder.Elapsed:mm\\:ss} to {_recorder.FilePath}";

            default:
                return $"error: unknown recording action '{action}'";
        }
    }

    private async Task<string> StartRecordingAsync(string scope, bool audit)
    {
        if (_recorder is not null)
        {
            return $"already recording to {_recorder.FilePath}";
        }

        // Tabs of one shortcut (its tmux sessions) can start in the same second: the session
        // name and, if still taken, a counter keep the files apart.
        var stem = (audit ? "audit-" : "") + Definition.Name + (string.IsNullOrWhiteSpace(TmuxSessionName) ? "" : "-" + TmuxSessionName);
        var safe = string.Concat(stem.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(RecordingsFolder, $"{safe}-{stamp}.mp4");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(RecordingsFolder, $"{safe}-{stamp}-{n}.mp4");
        }

        var window = Window.GetWindow(this);
        if (window is null)
        {
            return "error: the tab is not in a window";
        }

        var recorder = new TabRecorder(window, RecordRegion(scope), path, Definition.Name);
        try
        {
            await recorder.StartAsync();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // The full exception, stack included, names the step that failed; the dialog only has room for the gist.
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "recording-errors.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} scope={scope} {Environment.OSVersion}\n{ex}\n\n");
            }
            catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
            {
            }

            return $"error: recording could not start: {StartFailure(ex)}. Details: recording-errors.log in the BaudHoundTerm data folder.";
        }

        _recorder = recorder;
        _audit = audit;
        // The tab's own indicator; Windows' yellow capture border is switched off for tab recordings.
        RecordFrame.BorderBrush = scope == "window" ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));
        RecordingScope = scope;
        RecordingChanged?.Invoke(this, EventArgs.Empty);
        // One line, so the audit notice is never replaced by the encoder note.
        var note = recorder.Fallback.Length > 0 ? $"Recording {recorder.Fallback}." : "";
        if (audit)
        {
            SetStatus("This session is recorded for audit." + (note.Length > 0 ? " " + note : ""));
        }
        else if (note.Length > 0)
        {
            SetStatus(note);
        }

        return $"recording {(scope == "window" ? "the whole BaudHoundTerm window" : "this tab")} to {path}"
               + (recorder.Fallback.Length > 0 ? $" ({recorder.Fallback})" : "");
    }

    /// <summary>
    /// What went wrong, in words: Windows' capture and Direct3D errors often carry no message at
    /// all, which left the dialog reading "could not start:" and nothing else.
    /// </summary>
    private static string StartFailure(Exception ex)
    {
        var meaning = (uint)ex.HResult switch
        {
            0x887A0004 => "this PC has no Direct3D 11 device to capture with (DXGI_ERROR_UNSUPPORTED)",
            0x887A0005 => "the graphics device was removed or reset (DXGI_ERROR_DEVICE_REMOVED)",
            0x80070005 => "access denied; another program may be blocking screen capture",
            0x80070057 => "Windows rejected the window for capture (E_INVALIDARG); is it minimized or on a disconnected display?",
            0x80004002 => "screen capture is not available in this session (E_NOINTERFACE)",
            0x80070050 => "a recording file with that name already exists",
            0xC00D6D60 => "Windows has no H.264 video encoder that accepts this size (MF_E_TRANSCODE_NO_MATCHING_ENCODER); on an N edition of Windows, install the Media Feature Pack",
            _ => "",
        };
        var text = string.IsNullOrWhiteSpace(ex.Message) || ex.Message.StartsWith("Exception from HRESULT", StringComparison.Ordinal)
            ? "" : ex.Message.Trim();
        var parts = new[] { meaning, text }.Where(p => p.Length > 0).Distinct().ToList();
        return $"{(parts.Count > 0 ? string.Join("; ", parts) : ex.GetType().Name)} (0x{ex.HResult:X8})";
    }

    /// <param name="sessionOver">
    /// False when only the window changes mid-session: that part's list holds just its video,
    /// since the log is still being written; the part that ends with the session carries it.
    /// </param>
    private async Task<string> StopRecordingAsync(bool sessionOver = true)
    {
        var done = _recorder!;
        var audit = _audit;
        _recorder = null;
        RecordFrame.BorderBrush = Brushes.Transparent;
        _audit = false;
        long bytes;
        try
        {
            bytes = await done.StopAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            RecordingChanged?.Invoke(this, EventArgs.Empty);
            if (audit)
            {
                AuditProblem?.Invoke(this, $"The audit recording of {Definition.Name} did not finish cleanly ({ex.Message}). {done.FilePath} may be incomplete and was not copied.");
            }

            return $"error: the recording did not finish cleanly ({ex.Message}); {done.FilePath} may be incomplete";
        }

        RecordingChanged?.Invoke(this, EventArgs.Empty);
        SetStatus($"Recording saved: {done.FilePath}");
        if (audit)
        {
            await PackageAuditAsync(done.FilePath, sessionOver);
        }

        return $"saved {done.FilePath}: {done.Elapsed:mm\\:ss}, {done.FramesWritten} frames, {bytes / 1024} KB";
    }

    /// <summary>An audited shortcut's tab connected: its recording starts now, once per tab.</summary>
    private void StartAuditIfDue(SessionState state)
    {
        if (state == SessionState.Connected && Definition.Audit.Enabled && _recorder is null && !_released)
        {
            _ = StartAuditAsync();
        }
    }

    /// <summary>
    /// A local shell can connect before its tab is even on screen: the audit then waits for the
    /// tab to be laid out in its window. A start that fails is reported, never left silent.
    /// </summary>
    private async Task StartAuditAsync()
    {
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            RoutedEventHandler once = null!;
            once = (_, _) => { Loaded -= once; loaded.TrySetResult(); };
            Loaded += once;
            await loaded.Task;
        }

        // After layout, so the area to record has its size.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        if (_recorder is not null || _released)
        {
            return;
        }

        var result = await StartRecordingAsync("tab", audit: true);
        if (result.StartsWith("error", StringComparison.Ordinal))
        {
            AuditProblem?.Invoke(this, $"The audit recording of {Definition.Name} did not start ({result}). The session is not being recorded.");
        }
    }

    /// <summary>Remembers the session's log files before its connection is let go.</summary>
    private void NoteAuditLogs()
    {
        if (Definition.Audit.Enabled && _controller is { } controller)
        {
            _auditLogs.UnionWith(controller.LogFiles);
        }
    }

    /// <summary>
    /// The tab is moving to another window. A recording follows the window it started in, so it is
    /// finished here; an audit carries on in a new file in the new window.
    /// </summary>
    public async Task<string> RecordingMovedAsync()
    {
        if (_recorder is null)
        {
            return "not recording";
        }

        var audit = _audit;
        var result = await Dispatcher.InvokeAsync(() => StopRecordingAsync(sessionOver: false)).Task.Unwrap();
        if (audit)
        {
            await Dispatcher.InvokeAsync(StartAuditAsync).Task.Unwrap();
        }

        return result;
    }

    /// <summary>
    /// Writes the SHA-256 list of the recording and the session's logs next to the recording
    /// (sha256sum -c reads it), then copies all of them to the shortcut's audit folder and checks
    /// the copies against the list. Anything that fails is reported, never passed over.
    /// </summary>
    private async Task PackageAuditAsync(string video, bool withLogs)
    {
        NoteAuditLogs();
        var files = new List<string> { video };
        if (withLogs)
        {
            files.AddRange(_auditLogs.Where(File.Exists));
        }

        var folder = Definition.Audit.CopyFolder.Trim();
        var name = Definition.Name;
        string? problem;
        try
        {
            problem = await Task.Run(() => Package(video, files, folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Argument and NotSupported: a copy folder that is not a path at all.
            problem = ex.Message;
        }

        if (problem is null)
        {
            SetStatus(folder.Length == 0
                ? $"Audit saved with its SHA-256 list: {video}"
                : $"Audit copied to {folder} and checked.");
        }
        else
        {
            AuditProblem?.Invoke(this, $"The audit files of {name} were not all copied: {problem}. They are still in {RecordingsFolder}.");
        }
    }

    private static string? Package(string video, List<string> files, string folder)
    {
        var hashes = files.ToDictionary(f => f, Sha256);
        var list = Path.ChangeExtension(video, ".sha256");

        // Here the logs stay in their own folder, so this list points at them from the video's;
        // in the copy everything sits side by side and its list names bare files.
        var here = Path.GetDirectoryName(video)!;
        File.WriteAllLines(list, hashes.Select(h => $"{h.Value}  {Path.GetRelativePath(here, h.Key).Replace('\\', '/')}"));
        if (folder.Length == 0)
        {
            return null;
        }

        var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(video));
        Directory.CreateDirectory(target);
        foreach (var (file, hash) in hashes)
        {
            var copy = Path.Combine(target, Path.GetFileName(file));
            CopyShared(file, copy);
            if (Sha256(copy) != hash)
            {
                return $"{Path.GetFileName(file)} differs after copying";
            }
        }

        using (var copied = new StreamWriter(new FileStream(Path.Combine(target, Path.GetFileName(list)), FileMode.CreateNew, FileAccess.Write)))
        {
            foreach (var (file, hash) in hashes)
            {
                copied.WriteLine($"{hash}  {Path.GetFileName(file)}");
            }
        }

        return null;
    }

    // A log can still be open for writing (the app closing); read it the way the logger shares it.
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string Sha256(string path)
    {
        using var stream = OpenShared(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void CopyShared(string from, string to)
    {
        using var source = OpenShared(from);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
        source.CopyTo(target);
    }

    /// <summary>
    /// A recording still running when the tab closes is finished, not thrown away. The window
    /// hears of it at once, so Hide addresses goes back off when it was on only for this.
    /// </summary>
    private void StopRecordingOnRelease()
    {
        if (_recorder is not null)
        {
            _ = StopRecordingAsync();
        }
    }

    /// <summary>Finishes a running recording, an audit's included, and waits for its files.</summary>
    public async Task FinishRecordingAsync()
    {
        if (_recorder is not null)
        {
            await Dispatcher.InvokeAsync(() => StopRecordingAsync()).Task.Unwrap();
        }
    }
}
