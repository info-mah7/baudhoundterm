using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Quarterdeck.App.Views;

namespace Quarterdeck.App;

/// <summary>
/// Recording as the tab shows it: nothing until a recording runs (a start is deliberate: the Record
/// menu, the tab's right-click menu or Ctrl+Shift+R; a dot on hover that started one on a single
/// click used to catch stray clicks), then a pulsing dot, the time, pause and stop. An audit shows
/// the dot and the time only. Ctrl+Shift+R stops, Ctrl+Shift+P pauses or resumes.
/// </summary>
public partial class MainWindow
{
    private static readonly Color RecRed = Color.FromRgb(0xE0, 0x6C, 0x75);
    private static readonly Color RecGrey = Color.FromRgb(0x8B, 0x94, 0xA7);

    // The time shown on each recording tab, moved on once a second.
    private readonly Dictionary<TerminalView, TextBlock> _recClocks = new();
    private DispatcherTimer? _recTick;

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    /// <summary>The recording part of a tab header: nothing while idle, the running controls while it records.</summary>
    private FrameworkElement RecordControls(Panel header, TerminalView view)
    {
        _recClocks.Remove(view);
        if (!view.IsRecording)
        {
            return new FrameworkElement { Visibility = Visibility.Collapsed };
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        var dot = new TextBlock { Text = view.RecordingPaused ? "❚❚" : "●", Foreground = new SolidColorBrush(RecRed), VerticalAlignment = VerticalAlignment.Center };
        if (!view.RecordingPaused)
        {
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        panel.Children.Add(dot);
        var clock = new TextBlock
        {
            Text = (view.RecordingIsAudit ? "AUDIT " : "") + Clock(view.RecordingElapsed),
            Foreground = new SolidColorBrush(RecRed),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(clock);
        _recClocks[view] = clock;
        EnsureRecTick();

        if (view.RecordingScope == "window")
        {
            panel.Children.Add(new TextBlock { Text = " (window)", Foreground = new SolidColorBrush(RecRed), VerticalAlignment = VerticalAlignment.Center });
        }

        if (view.RecordingIsAudit)
        {
            panel.ToolTip = "This session is recorded for audit until the tab closes";
            return panel;
        }

        panel.ToolTip = view.RecordingPaused ? "Recording paused" : "Recording";
        var pause = RecButton(view.RecordingPaused ? "▶" : "❚❚", RecGrey,
            view.RecordingPaused ? "Resume recording (Ctrl+Shift+P)" : "Pause recording (Ctrl+Shift+P)");
        pause.Click += async (_, e) =>
        {
            e.Handled = true;
            await view.RecordingAsync(view.RecordingPaused ? "resume" : "pause");
        };
        panel.Children.Add(pause);

        var stop = RecButton("■", RecGrey, "Stop recording and save (Ctrl+Shift+R)");
        stop.Click += async (_, e) =>
        {
            e.Handled = true;
            ShowRecordingResult(await view.RecordingAsync("stop"));
        };
        panel.Children.Add(stop);
        return panel;
    }

    private static Button RecButton(string glyph, Color colour, string tip)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 11, Foreground = new SolidColorBrush(colour) },
            Padding = new Thickness(3, 0, 3, 0),
            Margin = new Thickness(4, 0, 0, 0),
            MinWidth = 0,
            MinHeight = 0,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Focusable = false,   // a click must not pull the keyboard off the terminal
            ToolTip = tip
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private void EnsureRecTick()
    {
        if (_recTick is not null)
        {
            return;
        }

        _recTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recTick.Tick += (_, _) =>
        {
            foreach (var (view, clock) in _recClocks.ToList())
            {
                if (!view.IsRecording)
                {
                    _recClocks.Remove(view);
                    continue;
                }

                clock.Text = (view.RecordingIsAudit ? "AUDIT " : "") + Clock(view.RecordingElapsed);
            }

            if (_recClocks.Count == 0)
            {
                _recTick.Stop();
                _recTick = null;
            }
        };
        _recTick.Start();
    }

    /// <summary>A recording tab's mark on its row in the list, so it shows while the tab is hidden.</summary>
    private static TextBlock? LiveRowRecMark(TerminalView view) =>
        !view.IsRecording ? null : new TextBlock
        {
            Text = view.RecordingIsAudit ? "● AUDIT" : view.RecordingPaused ? "❚❚ REC" : "● REC",
            Foreground = new SolidColorBrush(RecRed),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = view.RecordingIsAudit ? "Recorded for audit until the tab closes"
                : view.RecordingPaused ? "Recording paused" : "Recording"
        };

    /// <summary>Ctrl+Shift+R starts or stops recording the tab in focus; Ctrl+Shift+P pauses or resumes.</summary>
    private async Task<bool> RecordKeyAsync(Key key)
    {
        if (FocusedLive?.View is not { } view)
        {
            return false;
        }

        if (view.RecordingIsAudit)
        {
            view.SetStatus("This session is recorded for audit until the tab closes; it cannot be paused or stopped.");
            return true;
        }

        if (key == Key.R)
        {
            ShowRecordingResult(await view.RecordingAsync(view.IsRecording ? "stop" : "start", "tab"));
        }
        else if (view.IsRecording)
        {
            await view.RecordingAsync(view.RecordingPaused ? "resume" : "pause");
        }

        return true;
    }
}
