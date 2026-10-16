using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Quarterdeck.App;

/// <summary>
/// Where the app meets Windows' own settings: a dark title bar to match the window, dialogs
/// without minimise and maximise boxes, the Accessibility "Text size" setting, and High
/// Contrast, which must win over the app's palette.
/// </summary>
internal static class SystemLook
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19;   // Windows 10 before 20H1
    private const int GwlStyle = -16;
    private const int WsMaximizeBox = 0x10000;
    private const int WsMinimizeBox = 0x20000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>The scale each window was last sized for, so a change applies the ratio once.</summary>
    private static readonly ConditionalWeakTable<Window, StrongBox<double>> Sized = new();

    /// <summary>Windows' text size, 1.0 to 2.25. Display scaling is separate and WPF already follows it.</summary>
    public static double TextScale { get; private set; } = ReadTextScale();

    public static void Install(Application app)
    {
        // High Contrast replaces the palette: drop the app's colour overrides and implicit
        // styles so system-themed controls take the user's colours. Keyed templates stay,
        // because windows refer to them by name.
        if (SystemParameters.HighContrast)
        {
            var keys = app.Resources.Keys.Cast<object>()
                .Where(k => k is Type || k.GetType().Name == "SystemResourceKey")
                .ToList();
            foreach (var key in keys)
            {
                app.Resources.Remove(key);
            }
        }

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    TextScale = ReadTextScale();
                    foreach (var window in app.Windows.OfType<Window>())
                    {
                        Scale(window);
                    }
                });
            }
        };
    }

    private static void Apply(Window window)
    {
        Scale(window);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!SystemParameters.HighContrast)
        {
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
            }
        }
        else
        {
            window.Background = SystemColors.WindowBrush;
            window.Foreground = SystemColors.WindowTextBrush;
        }

        // A dialog is a step in a task, not a place to work: no minimise or maximise.
        if (window.Owner is not null)
        {
            var style = GetWindowLong(hwnd, GwlStyle);
            SetWindowLong(hwnd, GwlStyle, style & ~WsMaximizeBox & ~WsMinimizeBox);
        }
    }

    /// <summary>
    /// The window's content grows with the text size. A layout transform rather than font
    /// sizes, because sizes are set in many places; the terminal keeps its own font setting.
    /// </summary>
    private static void Scale(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.LayoutTransform = TextScale > 1.0 ? new ScaleTransform(TextScale, TextScale) : Transform.Identity;
        }

        // The window grows with its content, or a fixed width (the BaudHound finder) clips it.
        // Dimensions the window sizes to its content already follow; the rest take the ratio,
        // kept inside the screen's work area.
        var box = Sized.GetValue(window, _ => new StrongBox<double>(1.0));
        var ratio = TextScale / box.Value;
        box.Value = TextScale;
        if (Math.Abs(ratio - 1.0) < 0.01 || window.WindowState != WindowState.Normal)
        {
            return;
        }

        var area = SystemParameters.WorkArea;

        // While the window sizes itself to its content it ignores a new Width or Height, so
        // switch that off for the change and back on after (the finder fits its height).
        var fit = window.SizeToContent;
        window.SizeToContent = SizeToContent.Manual;
        if (!fit.HasFlag(SizeToContent.Width) && !double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width * ratio, area.Width);
        }

        if (!fit.HasFlag(SizeToContent.Height) && !double.IsNaN(window.Height))
        {
            window.Height = Math.Min(window.Height * ratio, area.Height);
        }

        window.SizeToContent = fit;

        // Re-centre a dialog on its owner (it was centred at its old size), then keep it on screen.
        var width = double.IsNaN(window.Width) ? window.ActualWidth * ratio : window.Width;
        var height = double.IsNaN(window.Height) ? window.ActualHeight * ratio : window.Height;
        var left = window.Left;
        var top = window.Top;
        if (window.Owner is { } owner && window.WindowStartupLocation == WindowStartupLocation.CenterOwner)
        {
            left = owner.Left + (owner.ActualWidth - width) / 2;
            top = owner.Top + (owner.ActualHeight - height) / 2;
        }

        if (!double.IsNaN(left) && !double.IsNaN(top))
        {
            window.Left = Math.Max(area.Left, Math.Min(left, area.Right - width));
            window.Top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));
        }
    }

    private static double ReadTextScale()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent and >= 100 and <= 225 ? percent / 100.0 : 1.0;
        }
        catch (System.Security.SecurityException)
        {
            return 1.0;
        }
    }
}
