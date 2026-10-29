using System.Text;
using System.Text.RegularExpressions;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// Output the agent saw, kept word for word so a report can include it: a terminal command's
/// whole output ("o3") or a whole page ("p2"). The model refers to it by id and never retypes it.
/// </summary>
public sealed record AgentCapture(string Id, string Source, Func<string> Text);

/// <summary>
/// Reports the agent saves as Markdown: its own text (title, summary, findings) around blocks
/// BaudHoundTerm copies from the device. A model retyping a 900-line configuration drops and
/// invents lines; a copy cannot. Secrets are masked unless the user agreed to keep them.
/// </summary>
public static class AgentReport
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BaudHoundTerm", "reports");

    private static readonly Regex Placeholder = new(@"\{\{\s*(?<id>[op]\d+)\s*\}\}", RegexOptions.IgnoreCase);

    /// <summary>The ids a body refers to that are not among the captures.</summary>
    public static List<string> Unknown(string body, IReadOnlyDictionary<string, AgentCapture> captures) =>
        Placeholder.Matches(body).Select(m => m.Groups["id"].Value.ToLowerInvariant())
            .Where(id => !captures.ContainsKey(id)).Distinct().ToList();

    /// <summary>
    /// The finished file: a header written here (not by the model), then the body with each
    /// {{id}} replaced by that capture, fenced. Masked unless <paramref name="keepSecrets"/>.
    /// </summary>
    public static (string Markdown, int Masked) Build(string title, string body, IReadOnlyDictionary<string, AgentCapture> captures,
        string device, string model, string machine, bool keepSecrets, DateTime now)
    {
        var masked = 0;
        string Clean(string s, bool prose)
        {
            if (keepSecrets)
            {
                return s;
            }

            var result = SecretMask.Mask(s, out var n, prose);
            masked += n;
            return result;
        }

        // The model's own words are masked first (it may have quoted a value in passing); the
        // placeholders pass through untouched, and each copy is masked as it goes in.
        var used = new List<AgentCapture>();
        var prose = Placeholder.Replace(Clean(body, prose: true), m =>
        {
            var capture = captures[m.Groups["id"].Value.ToLowerInvariant()];
            used.Add(capture);
            var content = Clean(capture.Text(), prose: false).TrimEnd('\n');
            // A fence longer than any run of backticks inside, so the content cannot close it.
            var longest = Regex.Matches(content, "`+").Select(x => x.Length).DefaultIfEmpty(0).Max();
            var fence = new string('`', Math.Max(3, longest + 1));
            return $"*Exact output of `{capture.Source.Replace("`", "'")}` ({capture.Id}), copied by BaudHoundTerm:*\n\n{fence}\n{content}\n{fence}";
        });

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(OneLine(title)).AppendLine();
        sb.AppendLine($"- **Device:** {OneLine(device)}");
        sb.AppendLine($"- **Saved:** {now:yyyy-MM-dd HH:mm} on {OneLine(machine)}");
        sb.AppendLine($"- **Written by:** the BaudHoundTerm agent ({OneLine(model)}). " +
                      (used.Count > 0
                          ? "Blocks marked as exact output were copied from the device by BaudHoundTerm, not retyped by the model."
                          : "It holds no copied output: everything here is the model's own account."));
        if (used.Count > 0)
        {
            sb.AppendLine("- **Copied output:** " + string.Join(", ", used.DistinctBy(c => c.Id).Select(c => $"{c.Id} `{c.Source.Replace("`", "'")}`")));
        }

        sb.AppendLine(keepSecrets
            ? "- **Secrets: NOT masked.** This file holds passwords or keys as the device printed them. Keep it like a password."
            : $"- **Secrets:** masked ({masked} value{(masked == 1 ? "" : "s")}). Common forms only: check before sharing.");
        sb.AppendLine().AppendLine("---").AppendLine();
        sb.AppendLine(prose.Trim());
        return (sb.ToString(), masked);
    }

    /// <summary>Writes the report under folder\subject\, named by date and title; returns the path.</summary>
    public static string Save(string folder, string subject, string title, string markdown, DateTime now)
    {
        var dir = Path.Combine(folder, SafeFileName(subject, "agent"));
        Directory.CreateDirectory(dir);
        var stem = $"{now:yyyy-MM-dd HHmm} {SafeFileName(title, "report")}";
        var path = Path.Combine(dir, stem + ".md");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(dir, $"{stem} ({n}).md");
        }

        File.WriteAllText(path, markdown.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        return path;
    }

    private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string SafeFileName(string s, string fallback)
    {
        var name = Regex.Replace(OneLine(s), @"[\\/:*?""<>|\x00-\x1F]", "_").Trim(' ', '.');
        name = name.Length > 80 ? name[..80].TrimEnd(' ', '.') : name;
        return name.Length == 0 ? fallback : name;
    }
}

/// <summary>Masks the secrets a device configuration or a note commonly holds.</summary>
public static class SecretMask
{
    public const string Stars = "********";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private static readonly Regex PrivateKey = new(
        @"-----BEGIN (?<k>[A-Z ]*PRIVATE KEY)-----[\s\S]*?-----END \k<k>-----", RegexOptions.None, Limit);

    // A keyword, then (on the same line) an optional type (Cisco's 0/5/7/8/9, FortiOS's ENC) and
    // the value. Spaces and tabs only: "community" ending one line must not mask the next.
    private static readonly Regex Keyword = new(
        @"(?<pre>\b(?:password|passwd|pwd|passphrase|secret|psksecret|psk|pre-shared-key|key-string|auth-key|authentication-key|" +
        @"auth-pwd|priv-pwd|auth-password|priv-password|community|api[_-]?key|apikey|token|private-key|md5-key|ppk-secret)" +
        @"[""']?(?:[ \t]*[:=][ \t]*|[ \t]+)(?:(?:ENC|\d{1,2})[ \t]+)?)(?<val>""[^""\n]*""|'[^'\n]*'|[^\s""']+)",
        RegexOptions.IgnoreCase, Limit);

    // Cisco key chains: "key 7 0822455D0A16".
    private static readonly Regex CiscoKey = new(@"(?<pre>\bkey[ \t]+\d[ \t]+)(?<val>\S+)", RegexOptions.IgnoreCase, Limit);

    private static readonly Regex FortiEnc = new(@"\bENC[ \t]+(?!\*{8})[^\s""']+", RegexOptions.None, Limit);

    // In sentences ("the password is stored encrypted") a keyword is followed by ordinary words:
    // there only a value with a digit or symbol in it counts, and an ENC value must look like one.
    private static readonly Regex ProseValue = new(@"[\d!@#$%^&*+=~<>|]", RegexOptions.None, Limit);
    private static readonly Regex ProseKeyword = new(
        @"(?<pre>\b(?:password|passwd|pwd|passphrase|secret|psk|psksecret|pre-?shared[- ]key|api[ _-]?key|token|community|key-string)s?\b" +
        @"[""']?[ \t]*(?:(?:is|was|to|as|=|:|of)[ \t]*)?[""']?)(?<val>(?=[^\s""',;)]*[\d!@#$%^&*+=~<>|])[^\s""',;)]{6,})",
        RegexOptions.IgnoreCase, Limit);
    private static readonly Regex ProseEnc = new(@"\bENC[ \t]+(?!\*{8})[A-Za-z0-9+/=]{8,}", RegexOptions.None, Limit);
    private static readonly Regex CryptHash = new(@"\$\d[a-z]?\$[^\s""']{8,}", RegexOptions.None, Limit);

    /// <summary>
    /// Device output is masked strictly (a plain word after "community" is the community). With
    /// <paramref name="prose"/>, the model's own sentences: only values that look like secrets.
    /// </summary>
    public static string Mask(string text, out int count, bool prose = false)
    {
        var n = 0;
        text = PrivateKey.Replace(text, m => { n++; return $"-----BEGIN {m.Groups["k"].Value}----- {Stars} -----END {m.Groups["k"].Value}-----"; });

        string Value(Match m)
        {
            var val = m.Groups["val"].Value;
            if (val.Contains(Stars) || val.Length == 0 || val.StartsWith("{{", StringComparison.Ordinal)
                || (prose && (val.Length < 6 || !ProseValue.IsMatch(val.Trim('"', '\''))) && !m.Groups["pre"].Value.Contains("ENC")))
            {
                return m.Value;
            }

            n++;
            return m.Groups["pre"].Value + (val[0] is '"' or '\'' ? val[0] + Stars + val[0] : Stars);
        }

        text = (prose ? ProseKeyword : Keyword).Replace(text, Value);
        text = CiscoKey.Replace(text, Value);
        text = (prose ? ProseEnc : FortiEnc).Replace(text, _ => { n++; return "ENC " + Stars; });
        text = CryptHash.Replace(text, _ => { n++; return Stars; });
        count = n;
        return text;
    }
}
