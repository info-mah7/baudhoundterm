using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Quarterdeck.App;

/// <summary>
/// Where the native libraries live. They are published into a "dll" folder beside the exe rather
/// than scattered around it, so an install is the program and three folders.
///
/// Nothing looks there by itself, and the two kinds of load need different help:
///
/// * .NET resolves a DllImport through the app folder and then the OS search. The hook below
///   catches what that misses and loads it from "dll" by full path. Adding the folder to the
///   process search path is not enough on its own: WPF's own imports came back "Dll was not
///   found" with the folder added, which is how this was found.
/// * The Windows loader resolves what one native library needs from another, and it does not
///   consult .NET at all. vcruntime140_cor3.dll, which the WPF libraries link against, is
///   therefore loaded here by name before anything can ask for it.
///
/// All of this runs before Main, so it is in place before the first WPF type touches a window.
/// </summary>
internal static class NativeLibraries
{
    // The application directory, the folders added with AddDllDirectory, and System32.
    private const uint SearchDefaultDirs = 0x00001000;   // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS

    private static string? _folder;

    [ModuleInitializer]
    internal static void Register()
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "dll");
            if (!Directory.Exists(folder))
            {
                return;   // a development build keeps them beside the exe; nothing to do
            }

            _folder = folder;

            if (SetDefaultDllDirectories(SearchDefaultDirs))
            {
                AddDllDirectory(folder);
            }

            // Loaded by name, so that every later load of a library that links against it finds
            // it already in the process rather than searching for it.
            foreach (var dependency in (string[])["vcruntime140_cor3.dll"])
            {
                var path = Path.Combine(folder, dependency);
                if (File.Exists(path))
                {
                    NativeLibrary.TryLoad(path, out _);
                }
            }

            AssemblyLoadContext.Default.ResolvingUnmanagedDll += Resolve;
        }
        catch (Exception)
        {
            // Nothing here is worth failing a start for: without it the loader falls back to its
            // usual search, and a library it cannot find reports itself where it is used.
        }
    }

    /// <summary>The last word on a native library .NET could not find: this folder, by full path.</summary>
    private static IntPtr Resolve(System.Reflection.Assembly assembly, string name)
    {
        if (_folder is null)
        {
            return IntPtr.Zero;
        }

        // Imports name libraries with and without the extension, and some with neither (the
        // runtime tries "sodium", "libsodium", "libsodium.dll" in turn).
        foreach (var candidate in (string[])[name, name + ".dll", "lib" + name, "lib" + name + ".dll"])
        {
            var path = Path.Combine(_folder, candidate);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
