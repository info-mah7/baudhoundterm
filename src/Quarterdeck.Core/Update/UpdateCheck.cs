using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quarterdeck.Core.Update;

/// <summary>Result of Help > Check for updates.</summary>
/// <param name="Latest">Newest published release, as "0.8.0 build 68"; null when the check failed.</param>
/// <param name="Newer">True when Latest is newer than the running build.</param>
public sealed record UpdateResult(string? Latest, bool Newer, string Text);

/// <summary>
/// Manual update check: one GET to GitHub's release list, only when the user asks. Reads the
/// newest tag and compares it with the running build. Downloads and installs nothing, and never
/// opens a link from the response; the user fetches the build from <see cref="ReleasesPage"/>.
/// </summary>
public static partial class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/info-mah7/baudhoundterm/releases";

    // Every build so far is a prerelease, which releases/latest skips, so ask for the newest one.
    private const string ApiUrl = "https://api.github.com/repos/info-mah7/baudhoundterm/releases?per_page=1";

    [GeneratedRegex(@"^v(\d+)\.(\d+)\.(\d+)-dev\.(\d+)$")]
    private static partial Regex TagPattern();

    // Running build: "0.8.0-dev.20261020.68+<commit>". A plain build has no "-dev." part.
    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)-dev\.(?:\d+\.)?(\d+)(?:\+.*)?$")]
    private static partial Regex BuildPattern();

    /// <summary>"v0.8.0-dev.68" to (0, 8, 0, 68); null for anything else.</summary>
    public static (int, int, int, int)? ParseTag(string? tag) => Parse(TagPattern(), tag);

    /// <summary>The running build's InformationalVersion to (0, 8, 0, 68); null for a plain build.</summary>
    public static (int, int, int, int)? ParseBuild(string? informational) => Parse(BuildPattern(), informational);

    private static (int, int, int, int)? Parse(Regex pattern, string? s)
    {
        var m = pattern.Match(s ?? "");
        if (!m.Success)
        {
            return null;
        }

        int N(int i) => int.TryParse(m.Groups[i].Value, out var n) ? n : -1;
        return (N(1), N(2), N(3), N(4));
    }

    private static string Show((int, int, int, int) v) => $"{v.Item1}.{v.Item2}.{v.Item3} build {v.Item4}";

    public static async Task<UpdateResult> CheckAsync(string? runningInformational, CancellationToken token = default)
    {
        string? tag;
        try
        {
            // No cookies, no version or machine details sent: GitHub sees an address and this
            // User-Agent, which its API requires. Certificates are checked by Windows as usual.
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 1 << 20,
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.ParseAdd("BaudHoundTerm");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateResult(null, false, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            }

            var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonArray;
            tag = list?.FirstOrDefault()?["tag_name"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new UpdateResult(null, false, $"Could not reach GitHub: {ex.Message}");
        }

        if (ParseTag(tag) is not { } latest)
        {
            return new UpdateResult(null, false, "GitHub's answer had no release this version understands.");
        }

        if (ParseBuild(runningInformational) is not { } running)
        {
            return new UpdateResult(Show(latest), false, $"The newest release is {Show(latest)}. This copy is a local build, so it cannot be compared.");
        }

        return latest.CompareTo(running) > 0
            ? new UpdateResult(Show(latest), true, $"{Show(latest)} is available. You have {Show(running)}.")
            : new UpdateResult(Show(latest), false, $"You have the newest release, {Show(running)}.");
    }
}
