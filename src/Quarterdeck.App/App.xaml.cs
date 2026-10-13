using System.IO;
using System.Windows;
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
