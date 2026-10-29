using System.IO;
using System.Net;
using Quarterdeck.Core.Model;

namespace Quarterdeck.App.Views;

/// <summary>
/// BaudHoundTerm's own pages in a Web tab (Help > Home): a start page with the build and links into
/// the guides, the guides themselves, and the credits and licences, all from the files this build
/// ships. Served from a local folder through the browser's virtual host, so nothing is fetched.
/// </summary>
internal static class HomePage
{
    public const string Host = "bht.home";

    /// <summary>One Home tab, one browser profile: a fixed id instead of a new one each time.</summary>
    public static readonly Guid Id = new("6a1d2c3e-0b7f-4e59-9f0d-b47e5a1c2d90");

    public static string Url(string page) => $"https://{Host}/{page}";

    /// <summary>Writes the start page and copies the guides beside it; returns the folder to serve.</summary>
    public static string Prepare(string version)
    {
        var root = Elevation.IsCurrentProcessElevated ? Elevation.ElevatedWebViewFolder() : AppPaths.DataDirectory;
        var folder = Path.Combine(root, "home");
        Directory.CreateDirectory(folder);

        var missing = new List<string>();
        foreach (var (from, name) in new[]
        {
            (Path.Combine(AppContext.BaseDirectory, "docs", "HELP.html"), "HELP.html"),
            (Path.Combine(AppContext.BaseDirectory, "docs", "MANUAL.html"), "MANUAL.html"),
            (Path.Combine(AppContext.BaseDirectory, "NOTICE.txt"), "NOTICE.txt"),
            (Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"), "THIRD-PARTY-NOTICES.txt"),
            (Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"), "LICENSE.txt")
        })
        {
            if (File.Exists(from))
            {
                File.Copy(from, Path.Combine(folder, name), overwrite: true);
            }
            else
            {
                missing.Add(name);
            }
        }

        File.WriteAllText(Path.Combine(folder, "home.html"), Page(version, missing));
        return folder;
    }

    private static string Page(string version, List<string> missing)
    {
        string Link(string href, string text) => $"<li><a href=\"{href}\">{WebUtility.HtmlEncode(text)}</a></li>";
        var note = missing.Count == 0 ? "" :
            $"<p class=\"warn\">Not found beside BaudHoundTerm.exe: {WebUtility.HtmlEncode(string.Join(", ", missing))}. " +
            "Extract the whole zip, not just the executable.</p>";

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Home - BaudHoundTerm</title>
            <style>
            body{background:#10141C;color:#DFE4EE;font:15px/1.5 "Segoe UI",system-ui,sans-serif;margin:0;padding:32px 40px;max-width:900px}
            h1{font-weight:600;margin:0 0 4px}h2{font-size:15px;color:#8B94A7;text-transform:uppercase;letter-spacing:.06em;margin:28px 0 8px}
            .build{color:#8B94A7;margin:0 0 8px}a{color:#61AFEF;text-decoration:none}a:hover{text-decoration:underline}
            ul{list-style:none;padding:0;margin:0;display:grid;grid-template-columns:1fr 1fr;gap:6px 24px}.warn{color:#E5C07B}
            .tip{border-left:3px solid #56B6C2;padding:6px 12px;color:#B8C0CE;margin-top:28px}
            </style></head>
            <body>
            <h1>BaudHoundTerm</h1>
            <p class="build">{{WebUtility.HtmlEncode(version)}}</p>
            {{note}}
            <h2>Guides</h2>
            <ul>
            {{Link("HELP.html", "Getting started")}}
            {{Link("MANUAL.html", "User manual")}}
            {{Link("MANUAL.html#11-keyboard-shortcuts", "Keyboard shortcuts")}}
            {{Link("MANUAL.html#8-the-ai-agent", "The AI agent")}}
            {{Link("MANUAL.html#safeguards-against-planted-instructions", "Agent safeguards")}}
            {{Link("MANUAL.html#memory", "Agent memory")}}
            {{Link("MANUAL.html#reports", "Agent reports and exports")}}
            {{Link("MANUAL.html#recording-a-tab", "Recording a tab")}}
            {{Link("MANUAL.html#audit-recording-supervised-access", "Audit recording")}}
            {{Link("MANUAL.html#web-tabs", "Web tabs")}}
            </ul>
            <h2>Help</h2>
            <ul>
            {{Link("https://mah7.com/discord", "MAH7 Discord: problems and questions")}}
            {{Link("https://github.com/mahseven/baudhoundterm/releases", "Releases")}}
            </ul>
            <h2>Credits and licences</h2>
            <ul>
            {{Link("NOTICE.txt", "Credits")}}
            {{Link("THIRD-PARTY-NOTICES.txt", "Third-party licences")}}
            {{Link("LICENSE.txt", "Licence (Apache-2.0)")}}
            </ul>
            <p class="tip">Attach the agent to this tab (the bot icon) and ask it how to do something in BaudHoundTerm:
            it reads these pages.</p>
            </body></html>
            """;
    }
}
