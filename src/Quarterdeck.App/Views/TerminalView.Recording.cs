using System.IO;
using System.Security.Cryptography;
using System.Windows;
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
    /// What a start records. terminal: the terminal surface (also VNC's), no status line. tab: the
    /// terminal and the agent pane beside it, when open. window: null, meaning all of BaudHoundTerm
    /// (the window this tab is in, so a popped-out tab gives its own window).
    /// </summary>
    private FrameworkElement? RecordRegion(string scope) => scope switch
    {
        "window" => null,
        "terminal" when Definition.Protocol is SessionProtocol.Ssh or SessionProtocol.Telnet or SessionProtocol.Serial
                                            or SessionProtocol.LocalShell or SessionProtocol.Vnc => Web,
        _ => RecordArea,
    };

    /// <summary>The scope of the running recording, for the menu and the tab header.</summary>
    public string RecordingScope { get; private set; } = "";

    public Task<string> RecordingAsync(string action, string scope = "tab") =>
        Dispatcher.InvokeAsync(() => RecordingOnUiAsync(action, scope is "terminal" or "tab" or "window" ? scope : "tab")).Task.Unwrap();

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

        var recorder = new TabRecorder(window, RecordRegion(scope), path);
        try
        {
            await recorder.StartAsync();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return $"error: recording could not start: {ex.Message}";
        }

        _recorder = recorder;
        _audit = audit;
        RecordingScope = scope;
        RecordingChanged?.Invoke(this, EventArgs.Empty);
        if (audit)
        {
            SetStatus("This session is recorded for audit.");
        }

        return $"recording {(scope == "window" ? "the whole BaudHoundTerm window" : scope == "terminal" ? "this terminal" : "this tab")} to {path}";
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
        _audit = false;
        long bytes;
        try
        {
            bytes = await done.StopAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            RecordingChanged?.Invoke(this, EventArgs.Empty);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
        File.WriteAllLines(list, hashes.Select(h => $"{h.Value}  {Path.GetFileName(h.Key)}"));
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

        File.Copy(list, Path.Combine(target, Path.GetFileName(list)), overwrite: false);
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
