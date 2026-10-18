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
        // As administrator, only from a folder that only administrators can change: otherwise
        // any program running as the user could put its own pages or DLLs into this process.
        if (Elevation.IsCurrentProcessElevated && Elevation.UnsafeInstall is { } where)
        {
            MessageBox.Show(
                "BaudHoundTerm will not run as administrator from this folder, because programs without administrator " +
                $"rights can change it:\n\n{where}\n\n" +
                "Move BaudHoundTerm into a folder only administrators can change, such as C:\\Program Files\\BaudHoundTerm, " +
                "to use it as administrator. Started normally, it runs from anywhere.",
                "BaudHoundTerm", MessageBoxButton.OK, MessageBoxImage.Warning);
            StartupUri = null;   // or WPF opens the main window anyway
            Shutdown();
            return;
        }

        if (Elevation.IsCurrentProcessElevated)
        {
            // These point WebView2 at another browser or profile, and anything running as the
            // user can set them; an administrator process takes none of them.
            foreach (var name in new[] { "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER",
                                         "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",
                                         "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER" })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        base.OnStartup(e);

        // An unhandled exception on the UI thread would otherwise kill the app with no trace,
        // taking every open session with it.
        DispatcherUnhandledException += OnUnhandled;

        SystemLook.Install(this);

        // Built-in agent helpers: offered on every remote-desktop run, nothing to set up.
        Core.Agent.AgentHelper.BuiltIn.Add(OcrHelper.Definition);
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

    private static readonly Lazy<string?> Unsafe = new(FindUnsafeInstall);

    /// <summary>
    /// Null when BaudHoundTerm may run as administrator; otherwise the file or folder that
    /// programs without administrator rights could change. An elevated copy runs its own exe and
    /// loads its pages (Assets) and libvlc from beside it, so a copy unzipped into Downloads let
    /// anything running as the user put its own code into an administrator process.
    /// </summary>
    public static string? UnsafeInstall => Unsafe.Value;

    private static string? FindUnsafeInstall()
    {
        try
        {
            var exe = Environment.ProcessPath;
            var dir = Path.GetDirectoryName(exe);
            if (exe is null || dir is null)
            {
                return "(unknown location)";
            }

            if (Writable(new FileInfo(exe).GetAccessControl(), contents: true))
            {
                return exe;
            }

            // The folder and every folder under it: the DLLs beside the exe, Assets, libvlc.
            var folders = new List<string> { dir };
            folders.AddRange(Directory.GetDirectories(dir, "*", SearchOption.AllDirectories));

            foreach (var folder in folders)
            {
                if (Writable(new DirectoryInfo(folder).GetAccessControl(), contents: true))
                {
                    return folder;
                }
            }

            // Above it, only the right to delete or rename it matters: whoever can do that can
            // put a folder of their own in its place.
            for (var parent = Directory.GetParent(dir); parent is not null; parent = parent.Parent)
            {
                if (Writable(parent.GetAccessControl(), contents: false))
                {
                    return parent.FullName;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or System.Security.Principal.IdentityNotMappedException)
        {
            return "(the folder's permissions could not be read)";
        }
    }

    /// <summary>
    /// The WebView2 profile of an elevated window. It used to live in the user's own data folder,
    /// where any program running as the user could edit the profile an administrator browser
    /// process then loaded.
    /// It now sits inside an administrators-only folder in %ProgramData%. The profile itself
    /// still allows the user, because WebView2's sandboxed child processes open it with a
    /// restricted token and cannot start without that; but nothing running as the user can reach
    /// it, since opening a path means traversing the folder above it, and that one refuses them.
    /// </summary>
    public static string ElevatedWebViewFolder()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BaudHoundTerm");
        var info = new DirectoryInfo(baseDir);
        if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || Writable(info.GetAccessControl(), contents: true)))
        {
            // Normal users may create folders in %ProgramData%: one made by someone else, or a
            // link, is moved aside as it is (never followed), with whatever is in it.
            Directory.Move(baseDir, $"{baseDir}.untrusted-{DateTime.Now:yyyyMMddHHmmss}");
            info = new DirectoryInfo(baseDir);
        }

        if (!info.Exists)
        {
            info.Create();
            Protect(baseDir, "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)");
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User!.Value;
        var folder = Path.Combine(baseDir, "webview-" + user);
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            Protect(folder, $"O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;FA;;;{user})");
        }

        return folder;
    }

    private static void Protect(string path, string sddl)
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl,
            System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    // Administrators, SYSTEM and TrustedInstaller. Anyone else who can change the files counts
    // as "programs without administrator rights", the current user included.
    private static readonly HashSet<string> Trusted =
    [
        "S-1-5-32-544",
        "S-1-5-18",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    ];

    private static bool Writable(System.Security.AccessControl.FileSystemSecurity acl, bool contents)
    {
        var owner = acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Value;
        if (owner is null || !Trusted.Contains(owner))
        {
            return true;   // the owner can always rewrite the permissions
        }

        const System.Security.AccessControl.FileSystemRights change =
            System.Security.AccessControl.FileSystemRights.ChangePermissions |
            System.Security.AccessControl.FileSystemRights.TakeOwnership;
        const System.Security.AccessControl.FileSystemRights write =
            System.Security.AccessControl.FileSystemRights.WriteData |
            System.Security.AccessControl.FileSystemRights.AppendData |
            System.Security.AccessControl.FileSystemRights.WriteAttributes |
            System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes |
            System.Security.AccessControl.FileSystemRights.Delete |
            System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles;
        const System.Security.AccessControl.FileSystemRights deleteChild =
            System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles;

        foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                 acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow
                || rule.PropagationFlags.HasFlag(System.Security.AccessControl.PropagationFlags.InheritOnly)
                || Trusted.Contains(rule.IdentityReference.Value))
            {
                continue;
            }

            if ((rule.FileSystemRights & (change | (contents ? write : deleteChild))) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
