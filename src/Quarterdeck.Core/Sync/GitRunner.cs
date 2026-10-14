using System.Diagnostics;
using System.Text;

namespace Quarterdeck.Core.Sync;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The error text, or the output when git wrote its complaint there.</summary>
    public string Message => (Error.Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// Runs the git command-line program. Using the real git (not a library) means any remote works,
/// https or ssh, and sign-in is git's own business: Git Credential Manager opens a browser for
/// GitHub, and ssh remotes use the user's agent, KeePassXC's keys included.
/// </summary>
public sealed class GitRunner(string gitPath)
{
    public string GitPath { get; } = gitPath;

    /// <summary>The configured git, else one on PATH, else the usual Git for Windows places.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (dir.Length > 0 && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
        }.FirstOrDefault(File.Exists);
    }

    public Task<GitResult> RunAsync(string workDir, CancellationToken token, params string[] args) =>
        RunAsync(workDir, TimeSpan.FromMinutes(2), token, args);

    /// <summary>
    /// Runs one git command. Console prompts are off (nothing could answer them); a credential
    /// manager's own window still works. A command that overruns is killed with its children.
    /// </summary>
    public Task<GitResult> RunAsync(string workDir, TimeSpan timeout, CancellationToken token, params string[] args) =>
        RunAsync(workDir, timeout, null, token, args);

    public async Task<GitResult> RunAsync(string workDir, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment,
        CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo(GitPath)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";   // messages we may match on stay in English
        foreach (var (k, v) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[k] = v;
        }

        using var process = new Process { StartInfo = start };
        process.Start();

        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            if (token.IsCancellationRequested)
            {
                throw;
            }

            return new GitResult(-1, "", $"git {args.FirstOrDefault()} took longer than {timeout.TotalSeconds:0} s and was stopped.");
        }

        return new GitResult(process.ExitCode, await output, await error);
    }
}
