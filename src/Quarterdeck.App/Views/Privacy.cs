using System.Text.RegularExpressions;
using Quarterdeck.Core.Model;

namespace Quarterdeck.App.Views;

/// <summary>
/// Hide addresses: for demos and screen recordings, every IP address, host name and user name the
/// app draws itself is shown as dots. Either for every session (the global switch) or for the ones
/// marked "always hide". Only what is drawn changes: connections, the filter and the agent still
/// use the real values, and what the far end prints (a shell prompt) is beyond reach.
/// </summary>
internal static partial class Privacy
{
    public const string Dots = "•••";

    /// <summary>The global switch (toolbar, Ctrl+Shift+H), remembered in ui.json.</summary>
    public static bool HideAll { get; set; }

    public static bool Hides(SessionDefinition? s) => HideAll || s is { HideAddress: true };

    // Dotted quads, and anything shaped like IPv6 (two or more colons, compressed forms included).
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}(?![\w:])")]
    private static partial Regex IpLiteral();

    // A clock time (10:42:15) has the IPv6 shape too: only "::", a hex letter, or four or more
    // colons make it an address.
    private static string DotIps(string text) => IpLiteral().Replace(text, m =>
        m.Value.Contains('.') || m.Value.Contains("::") || m.Value.Any(char.IsAsciiLetter) || m.Value.Count(c => c == ':') >= 4
            ? Dots
            : m.Value);

    /// <summary>
    /// Text that may carry the session's address or login: a target, a status line, a shell
    /// title. The session's host, gateway and user name go, and any IP address with them.
    /// </summary>
    public static string Mask(string text, SessionDefinition? s)
    {
        if (string.IsNullOrEmpty(text) || !Hides(s))
        {
            return text;
        }

        if (s is not null)
        {
            // The jump host is "user@host:port"; its parts can turn up in errors on their own.
            var jump = s.JumpHost.Trim();
            var colon = jump.LastIndexOf(':');
            if (colon > jump.LastIndexOf('@') && jump[(colon + 1)..].All(char.IsDigit))
            {
                jump = jump[..colon];   // the port is not secret, and "22" must not dot out every 22
            }

            text = ReplaceHost(text, s.Host);
            foreach (var value in new[] { s.RdpGateway, s.Username }.Concat(jump.Split('@')))
            {
                text = Replace(text, value);
            }
        }

        return DotIps(text);
    }

    // The machine in a shell title's "user@machine:" (bash's default title and prompt).
    [GeneratedRegex(@"(?<=[\w.-]@)[A-Za-z0-9][\w.-]*(?=:)")]
    private static partial Regex TitleMachine();

    /// <summary>
    /// The host as typed, and its other spellings: a shell title says "web01" for web01.corp.example,
    /// and "web01.corp.example" for a shortcut that says web01. So the first label goes, whole
    /// word, with any domain after it. For a host given as an IP address the machine's name is
    /// unknown, so the one place a title reliably shows it, "user@name:", goes instead.
    /// </summary>
    private static string ReplaceHost(string text, string host)
    {
        var h = host.Trim();
        if (System.Net.IPAddress.TryParse(h, out _))
        {
            return Replace(TitleMachine().Replace(text, Dots), h);
        }

        var label = h.Split('.')[0];
        if (label.Length < 2)
        {
            return Replace(text, h);
        }

        // The label with its domain first: replacing the bare host first would leave ".corp.example".
        text = Regex.Replace(text, $@"(?<![\w.-]){Regex.Escape(label)}(?:\.[A-Za-z0-9-]+)*(?![\w-])", Dots, RegexOptions.IgnoreCase);
        return Replace(text, h);
    }

    /// <summary>
    /// A shortcut's name, which is the user's own words: only an IP address in it, or the
    /// address and login that quick connect wrote into it (user@host, host:port), are hidden.
    /// A name that happens to be the machine's DNS name ("atlas") stays, or every row would read
    /// the same.
    /// </summary>
    public static string MaskName(string name, SessionDefinition? s)
    {
        if (string.IsNullOrEmpty(name) || !Hides(s))
        {
            return name;
        }

        if (s is not null)
        {
            name = Replace(name, s.Username);
            if (s.Host.Trim().Length > 0)
            {
                var host = Regex.Escape(s.Host.Trim());
                name = Regex.Replace(name, $@"(?<=@){host}\b|\b{host}(?=:\d)", Dots, RegexOptions.IgnoreCase);
            }
        }

        return DotIps(name);
    }

    // Whole words only, and nothing shorter than two characters, so a one-letter user name
    // does not dot out every "a" in a sentence.
    private static string Replace(string text, string value)
    {
        var v = value.Trim();
        return v.Length < 2
            ? text
            : Regex.Replace(text, $@"(?<![\w.-]){Regex.Escape(v)}(?![\w-])", Dots, RegexOptions.IgnoreCase);
    }
}
