using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Quarterdeck.App.Views;

/// <summary>
/// A tab moved into its own window, still connected: the view itself moves, not a copy, so the
/// session carries on. Closing the window puts the tab back in the main window rather than
/// ending the session; closing the tab there is what disconnects.
/// </summary>
internal sealed class PopOutWindow : Window
{
    private readonly FullScreenToggle _fullScreen;

    public TerminalView View { get; }

    /// <summary>Set by the main window when it takes the view back, so closing does not ask for it again.</summary>
    public bool Returned { get; set; }

    /// <summary>The user closed the window: the main window docks the view back into its tab.</summary>
    public event EventHandler? ReturnRequested;

    public PopOutWindow(TerminalView view, string title, Window main)
    {
        View = view;
        _fullScreen = new FullScreenToggle(this);
        Title = title + " - BaudHoundTerm";
        Icon = main.Icon;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1A));
        Width = Math.Max(640, main.ActualWidth * 0.75);
        Height = Math.Max(400, main.ActualHeight * 0.75);
        Left = main.Left + 40;
        Top = main.Top + 40;
        Content = view;

        Activated += (_, _) => View.FocusTerminal();
        PreviewKeyDown += OnKey;
        Closing += (_, _) =>
        {
            if (!Returned)
            {
                ReturnRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _fullScreen.Toggle();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            View.Find();
            e.Handled = true;
        }
    }
}

/// <summary>
/// Borderless and maximised over the taskbar, and back to exactly how the window was. Callers
/// hide their own chrome; this only handles the window frame.
/// </summary>
internal sealed class FullScreenToggle(Window window)
{
    private WindowStyle _style;
    private ResizeMode _resize;
    private WindowState _state;

    public bool IsOn { get; private set; }

    public void Toggle()
    {
        if (IsOn)
        {
            window.WindowStyle = _style;
            window.ResizeMode = _resize;
            window.WindowState = _state;
        }
        else
        {
            _style = window.WindowStyle;
            _resize = window.ResizeMode;
            _state = window.WindowState;
            // From Maximized, a borderless window keeps the taskbar's edge free: go through
            // Normal so the maximise afterwards covers the whole monitor.
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
        }

        IsOn = !IsOn;
    }
}
