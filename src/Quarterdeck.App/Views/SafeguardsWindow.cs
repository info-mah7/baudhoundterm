using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Agent;

namespace Quarterdeck.App.Views;

/// <summary>
/// The agent's safeguards, for every endpoint: rules put ahead of its instructions, and the
/// patterns for text aimed at it in what it reads. Kept in agent.json.
/// </summary>
public sealed class SafeguardsWindow : Window
{
    private static readonly Brush Fore = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));

    private readonly AgentConfig _config;
    private readonly TextBox _rules = Box(170);
    private readonly TextBox _patterns = Box(130, wrap: false);
    private readonly TextBlock _note = new() { Foreground = Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public SafeguardsWindow(AgentConfig config)
    {
        _config = config;
        Title = "Agent safeguards";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Label("Rules for the agent. They come first in its instructions, on every endpoint, ahead of skills and the goal. Empty: none."));
        panel.Children.Add(_rules);
        panel.Children.Add(Label("Text aimed at the agent, one regex per line. When anything it reads matches, every later action of that " +
                                 "conversation waits for your yes, whatever the endpoint's approval setting. Empty: off."));
        panel.Children.Add(_patterns);
        panel.Children.Add(new TextBlock
        {
            Foreground = Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "Terminal output always reaches the agent fenced as data. Rules lower the odds that a model follows planted text, " +
                   "but no model obeys them every time: approvals and a login with only the rights the job needs are what limit the damage."
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Btn("Restore defaults", () =>
        {
            _rules.Text = AgentConfig.DefaultSafeguards;
            _patterns.Text = AgentConfig.DefaultInjectionPatterns;
            _note.Text = "Defaults filled in. Save to keep them.";
        }));
        buttons.Children.Add(Btn("Save", Save));
        buttons.Children.Add(Btn("Cancel", Close));
        panel.Children.Add(_note);
        panel.Children.Add(buttons);
        Content = panel;

        _rules.Text = config.Safeguards;
        _patterns.Text = config.InjectionPatterns;
    }

    private void Save()
    {
        var before = (_config.Safeguards, _config.InjectionPatterns);
        _config.Safeguards = _rules.Text.Trim();
        _config.InjectionPatterns = _patterns.Text.Trim();
        try
        {
            _config.Save();
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (_config.Safeguards, _config.InjectionPatterns) = before;
            _note.Text = "Not saved: " + ex.Message;
        }
    }

    private static TextBox Box(double height, bool wrap = true) => new()
    {
        AcceptsReturn = true, Height = height,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Fore, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4)
    };

    private static Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        b.Click += (_, _) => act();
        return b;
    }
}
