using System.IO;
using System.Windows;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Model;

namespace Quarterdeck.App.Views;

/// <summary>Recording this tab to a video file, by the user (tab menu) or the agent (its recording tool).</summary>
public partial class TerminalView : IRecordable
{
    private TabRecorder? _recorder;

    /// <summary>Raised when a recording starts, pauses, resumes or stops, so the tab header can say so.</summary>
    public event EventHandler? RecordingChanged;

    public bool IsRecording => _recorder is not null;
    public bool RecordingPaused => _recorder?.Paused == true;
    public TimeSpan RecordingElapsed => _recorder?.Elapsed ?? TimeSpan.Zero;

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

    private async Task<string> RecordingOnUiAsync(string action, string scope)
    {
        switch (action)
        {
            case "start":
                if (_recorder is not null)
                {
                    return $"already recording to {_recorder.FilePath}";
                }

                // Tabs of one shortcut (its tmux sessions) can start in the same second: the session
                // name and, if still taken, a counter keep the files apart.
                var stem = Definition.Name + (string.IsNullOrWhiteSpace(TmuxSessionName) ? "" : "-" + TmuxSessionName);
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
                RecordingScope = scope;
                RecordingChanged?.Invoke(this, EventArgs.Empty);
                return $"recording {(scope == "window" ? "the whole BaudHoundTerm window" : scope == "terminal" ? "this terminal" : "this tab")} to {path}";

            case "pause":
                if (_recorder is null)
                {
                    return "not recording";
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

                var done = _recorder;
                _recorder = null;
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
                return $"saved {done.FilePath}: {done.Elapsed:mm\\:ss}, {done.FramesWritten} frames, {bytes / 1024} KB";

            case "status":
                return _recorder is null
                    ? "not recording"
                    : $"{(_recorder.Paused ? "paused" : "recording")} {_recorder.Elapsed:mm\\:ss} to {_recorder.FilePath}";

            default:
                return $"error: unknown recording action '{action}'";
        }
    }

    /// <summary>
    /// A recording still running when the tab closes is finished, not thrown away. The window
    /// hears of it at once, so Hide addresses goes back off when it was on only for this.
    /// </summary>
    private void StopRecordingOnRelease()
    {
        if (_recorder is { } recorder)
        {
            _recorder = null;
            RecordingChanged?.Invoke(this, EventArgs.Empty);
            _ = recorder.StopAsync();
        }
    }

    /// <summary>Finishes a running recording and waits for the file, as before the app closes.</summary>
    public async Task FinishRecordingAsync()
    {
        if (IsRecording)
        {
            await RecordingAsync("stop");
        }
    }
}
