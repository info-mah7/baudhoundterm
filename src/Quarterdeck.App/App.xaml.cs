using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Quarterdeck.Core.Model;

namespace Quarterdeck.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled exception on the UI thread would otherwise kill the app with no trace,
        // taking every open session with it.
        DispatcherUnhandledException += OnUnhandled;

        SystemLook.Install(this);
    }

    /// <summary>Enables the text-box menu's items for the box it opened on.</summary>
    private void OnEditMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        var box = menu.PlacementTarget as TextBox;
        var password = menu.PlacementTarget as PasswordBox;
        var writable = box is { IsReadOnly: false } || password is not null;
        var selected = box is { SelectionLength: > 0 };
        var hasText = box is { Text.Length: > 0 } || password is { Password.Length: > 0 };

        bool canPaste;
        try
        {
            canPaste = Clipboard.ContainsText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            canPaste = true; // the clipboard is busy; let Paste try
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = (string)item.Tag switch
            {
                // A password box never gives its text back out.
                "cut" => writable && selected,
                "copy" => selected,
                "paste" => writable && canPaste,
                _ => hasText
            };
        }
    }

    private void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        var target = (item.Parent as ContextMenu)?.PlacementTarget;

        switch (target, (string)item.Tag)
        {
            case (TextBox box, "cut"): box.Cut(); break;
            case (TextBox box, "copy"): box.Copy(); break;
            case (TextBox box, "paste"): box.Paste(); break;
            case (TextBox box, "all"): box.SelectAll(); break;
            case (PasswordBox box, "paste"): box.Paste(); break;
            case (PasswordBox box, "all"): box.SelectAll(); break;
        }

        (target as UIElement)?.Focus();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = Path.Combine(AppPaths.DataDirectory, "crash.log");

        try
        {
            File.AppendAllText(path,
                $"--- {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} ---{Environment.NewLine}{e.Exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing useful left to do if even the crash log cannot be written.
        }

        MessageBox.Show(
            $"{e.Exception.Message}\n\nDetails were written to {path}.",
            "BaudHoundTerm", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }
}

/// <summary>
/// Whether this process is running elevated. It decides both what the Admin button has to do and
/// which WebView2 user data folder a terminal may use, so it lives in one place.
/// </summary>
internal static class Elevation
{
    public static bool IsCurrentProcessElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
