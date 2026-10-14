using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.Agent;
using Quarterdeck.Core.Security.KeePassXc;

namespace Quarterdeck.App.Views;

/// <summary>
/// The KeePassXC pairing (for passwords) and the SSH agent's keys (for key logins), with the
/// setup steps for each. Pairing also happens on its own at the first lookup; this is where
/// to check it or undo it.
/// </summary>
internal sealed class KeePassXcWindow : Window
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));

    private readonly CredentialVault _vault;
    private readonly TextBlock _kpStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBlock _agentStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11 };
    private readonly StackPanel _kpButtons = new() { Orientation = Orientation.Horizontal };

    public KeePassXcWindow(CredentialVault vault)
    {
        _vault = vault;
        Title = "Passwords and keys";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(Heading("KeePassXC passwords"));
        root.Children.Add(Text("Shortcuts set to take their password from KeePassXC look it up at each connect and never store it. " +
                               "In KeePassXC: Settings > Browser Integration > Enable. Give each entry the URL ssh://host " +
                               "(ssh://host:port for other ports), or put the entry's UUID in the shortcut."));
        root.Children.Add(_kpStatus);
        _kpButtons.Children.Add(Btn("Check", async () => await CheckAsync(pair: false), "Is KeePassXC running, and does the open database know BaudHoundTerm?"));
        _kpButtons.Children.Add(Btn("Pair with open database", async () => await CheckAsync(pair: true),
            "KeePassXC asks you to name and allow this connection"));
        _kpButtons.Children.Add(Btn("Forget pairings", Forget, "BaudHoundTerm forgets its pairings; remove them in KeePassXC too " +
                                                               "(Database > Database Settings > Browser Integration)"));
        root.Children.Add(_kpButtons);

        root.Children.Add(Heading("SSH agent keys", top: 18));
        root.Children.Add(Text("Shortcuts set to \"SSH agent\" sign in with these keys; the keys never leave the agent. " +
                               "In KeePassXC: Settings > SSH Agent > Enable, choose Pageant or OpenSSH, and on each key entry " +
                               "tick \"Add key to agent when database is opened\". The OpenSSH agent is the Windows service " +
                               "\"OpenSSH Authentication Agent\", which needs starting once."));
        root.Children.Add(_agentStatus);
        var agentButtons = new StackPanel { Orientation = Orientation.Horizontal };
        agentButtons.Children.Add(Btn("Refresh", ListKeys));
        root.Children.Add(agentButtons);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        bottom.Children.Add(Btn("Close", Close));
        root.Children.Add(bottom);

        Content = root;
        Loaded += async (_, _) =>
        {
            ListKeys();
            await CheckAsync(pair: false);
        };
    }

    private async Task CheckAsync(bool pair)
    {
        _kpButtons.IsEnabled = false;
        Status(_kpStatus, pair ? "Waiting for you to allow the pairing in KeePassXC..." : "Checking...", Muted);

        try
        {
            if (!KeePassXcClient.IsAvailable)
            {
                Status(_kpStatus, "KeePassXC is not running, or its browser integration is off.", Bad);
                return;
            }

            var (client, known) = await Task.Run(() => KeePassXcPasswords.OpenAsync(_vault, pair, CancellationToken.None));
            using (client)
            {
                Status(_kpStatus, $"KeePassXC {client.Version} is running and the open database knows BaudHoundTerm " +
                                  $"(pairing {string.Join(", ", known.Select(k => k.Id))}).", Good);
            }
        }
        catch (KeePassXcException ex)
        {
            var saved = KeePassXcPasswords.LoadPairings(_vault).Count;
            Status(_kpStatus, ex.Message + (saved == 0 && !pair ? " Press \"Pair with open database\"." : ""), Bad);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Status(_kpStatus, "Could not talk to KeePassXC: " + ex.Message, Bad);
        }
        finally
        {
            _kpButtons.IsEnabled = true;
        }
    }

    private void Forget()
    {
        KeePassXcPasswords.Forget(_vault);
        Status(_kpStatus, "Pairings forgotten. KeePassXC still lists the old one until you remove it there.", Muted);
    }

    private void ListKeys()
    {
        var keys = SshAgentClient.ListIdentities(out var problems);
        var lines = keys.Select(k => $"{k.Comment}\n    {k.KeyType}  {k.Fingerprint}  ({k.Source})").ToList();
        lines.AddRange(problems);

        if (keys.Count == 0 && problems.Count == 0)
        {
            Status(_agentStatus, SshAgentClient.FindAgents().Count == 0
                ? "No agent is running: neither Pageant nor the OpenSSH agent."
                : "The agent is running but holds no keys. Unlock the KeePassXC database to load them.", Bad);
            return;
        }

        Status(_agentStatus, string.Join("\n", lines), keys.Count > 0 ? Brushes.White : Bad);
    }

    private static void Status(TextBlock block, string text, Brush brush)
    {
        block.Text = text;
        block.Foreground = brush;
    }

    private static TextBlock Heading(string text, double top = 0) =>
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Muted };

    private static Button Btn(string text, Action act, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = tip };
        b.Click += (_, _) => act();
        return b;
    }
}
