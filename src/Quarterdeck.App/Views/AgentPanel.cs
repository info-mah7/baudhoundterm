using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Security;

namespace Quarterdeck.App.Views;

/// <summary>
/// The agent docked beside one terminal: pick an endpoint and skills, give it a goal, watch it
/// work, stop it. It can only act on the tab it is docked in; every step goes to the log.
/// </summary>
internal sealed class AgentPanel : DockPanel
{
    private readonly IAgentTarget _target;
    private readonly Core.Model.SessionProtocol _protocol;
    private readonly CredentialVault _vault;
    private readonly AgentLog _log = new();

    private readonly ComboBox _endpoints = new() { Margin = new Thickness(0, 0, 4, 0) };
    // Skills in a collapsible tree, by group; the header lists the ticked ones.
    private static readonly Brush Light = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
    private readonly Expander _skillsBox = new() { Margin = new Thickness(0, 4, 0, 4), Foreground = Light };
    private readonly TreeView _skills = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        MaxHeight = 240,
        Foreground = Light
    };
    private readonly List<CheckBox> _skillChecks = new();
    private readonly TextBox _transcript = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 11.5,
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)),
        BorderThickness = new Thickness(0)
    };
    private readonly TextBox _goal = new()
    {
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = false,
        MinHeight = 44,
        MaxHeight = 120,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ToolTip = "What the agent should do in this session. Enter runs; Shift+Enter for a new line.\n" +
                  "While it runs, Enter sends what you type to the agent at once; Esc stops it."
    };
    private readonly Button _run = new() { Content = "Run", MinWidth = 64 };
    private readonly Button _reset = new() { Content = "New", ToolTip = "Forget this conversation; the next goal starts fresh" };

    private AgentRunner? _runner;
    private string _runnerKey = "";
    private CancellationTokenSource? _cts;

    public event EventHandler? CloseRequested;

    /// <summary>A local shell in an elevated BaudHoundTerm: it runs as administrator.</summary>
    private bool Elevated => _protocol == Core.Model.SessionProtocol.LocalShell && Elevation.IsCurrentProcessElevated;

    /// <summary>A remote desktop (VNC or RDP): the screen agent, not the terminal one.</summary>
    private bool Desktop => _protocol is Core.Model.SessionProtocol.Vnc or Core.Model.SessionProtocol.Rdp
                                      or Core.Model.SessionProtocol.Kvm;

    public AgentPanel(IAgentTarget target, Core.Model.SessionProtocol protocol, CredentialVault vault)
    {
        _target = target;
        _protocol = protocol;
        _vault = vault;

        Width = 360;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));
        LastChildFill = true;

        // Header: title, settings, logs, close.
        var header = new DockPanel { Margin = new Thickness(8, 6, 8, 4) };
        var close = new Button { Content = "✕", ToolTip = "Detach the agent from this tab", Padding = new Thickness(6, 0, 6, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var logs = new Button { Content = "Log", ToolTip = "Open the folder with every agent action (one file per day)", Margin = new Thickness(0, 0, 4, 0) };
        logs.Click += (_, _) => OpenFolder(AgentConfig.LogDirectory);
        DockPanel.SetDock(logs, Dock.Right);
        header.Children.Add(logs);

        var settings = new Button { Content = "⚙", ToolTip = "Endpoints and skills", Margin = new Thickness(0, 0, 4, 0) };
        settings.Click += (_, _) => OpenSettings();
        DockPanel.SetDock(settings, Dock.Right);
        header.Children.Add(settings);

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(SessionPresentation.BotIcon(16, new SolidColorBrush(Color.FromRgb(0x56, 0xB6, 0xC2))));
        title.Children.Add(new TextBlock
        {
            Text = "Agent",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        var setup = new StackPanel { Margin = new Thickness(8, 0, 8, 4) };
        setup.Children.Add(_endpoints);
        _skillsBox.Content = _skills;
        setup.Children.Add(_skillsBox);
        DockPanel.SetDock(setup, Dock.Top);
        Children.Add(setup);

        // Goal box and buttons at the bottom.
        var bottom = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(_reset);
        buttons.Children.Add(_run);
        _reset.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_goal);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);

        _transcript.Margin = new Thickness(8, 0, 8, 0);
        Children.Add(_transcript);

        _run.Click += async (_, _) => await RunOrStopAsync();
        _reset.Click += (_, _) =>
        {
            _runner = null;
            _transcript.Clear();
            Append("Conversation cleared.");
        };
        _goal.TextChanged += (_, _) => ShowRunButton();
        _goal.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (_cts is null)
                {
                    await RunOrStopAsync();
                }
                else
                {
                    // Enter no longer stops a run by accident: with words it interrupts, empty it does nothing.
                    SendInterruption();
                }
            }
            else if (e.Key == Key.Escape && _cts is not null)
            {
                e.Handled = true;
                _cts.Cancel();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                var at = _goal.CaretIndex;
                _goal.Text = _goal.Text.Insert(at, "\n");
                _goal.CaretIndex = at + 1;
            }
        };

        Reload();
        Append($"Attached to {_target.Describe}. Every step is logged; what needs your approval first depends on the endpoint's Approval setting (⚙).");
        if (Elevated)
        {
            Append("This shell runs as administrator: every command the agent wants to type asks you first, whatever the endpoint's Approval setting.");
        }
        if (Desktop)
        {
            Append($"This is a remote desktop: the agent sees screenshots and clicks and types over {_protocol switch { Core.Model.SessionProtocol.Rdp => "RDP", Core.Model.SessionProtocol.Kvm => "a KVM's USB keyboard and mouse", _ => "VNC" }}, so the endpoint's " +
                   "model must accept images (Test in Agent endpoints checks). Clicks are checked against a close-up first.");
        }
    }

    /// <summary>Endpoints and skills, re-read so edits in the settings window show up.</summary>
    private void Reload()
    {
        var config = AgentConfig.Load();
        var selected = (_endpoints.SelectedItem as AgentEndpoint)?.Id ?? config.DefaultEndpoint;

        _endpoints.Items.Clear();
        foreach (var ep in config.Endpoints)
        {
            _endpoints.Items.Add(ep);
        }

        _endpoints.SelectedItem = config.Endpoints.FirstOrDefault(e => e.Id == selected) ?? config.Endpoints.FirstOrDefault();

        // Groups keep whether they were open; skills are ticked as the user last left them,
        // or as their header says ("default: off" for a game or a whole suite of apps).
        var open = _skills.Items.OfType<TreeViewItem>().ToDictionary(i => (string)((CheckBox)i.Header).Content, i => i.IsExpanded);
        _skills.Items.Clear();
        _skillChecks.Clear();
        foreach (var group in SkillLibrary.Load().Where(s => s.AppliesTo(_protocol)).GroupBy(s => s.Group)
                     .OrderBy(g => g.Key == "General" ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupBox = new CheckBox { Content = group.Key, FontWeight = FontWeights.SemiBold, ToolTip = "Tick or untick every skill in " + group.Key };
            var item = new TreeViewItem
            {
                Header = groupBox,
                IsExpanded = open.GetValueOrDefault(group.Key, true),
                Focusable = false,
                Foreground = Light,
                Background = Brushes.Transparent
            };
            var children = new List<CheckBox>();
            foreach (var skill in group.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var file = System.IO.Path.GetFileName(skill.Path);
                var box = new CheckBox
                {
                    Content = skill.Name,
                    Tag = skill,
                    ToolTip = string.IsNullOrEmpty(skill.Description) ? skill.Path : skill.Description,
                    IsChecked = config.SkillChoices.TryGetValue(file, out var on) ? on : skill.DefaultOn,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                // The state, not Click: keyboard, screen readers and UI Automation tick boxes too.
                RoutedEventHandler changed = (_, _) =>
                {
                    if (_building)
                    {
                        return;
                    }

                    RememberSkills([box]);
                    ShowGroup(groupBox, children);
                };
                box.Checked += changed;
                box.Unchecked += changed;
                children.Add(box);
                _skillChecks.Add(box);
                item.Items.Add(new TreeViewItem { Header = box, Focusable = false, Foreground = Light, Background = Brushes.Transparent });
            }

            // Mixed (null) is only ever shown, never chosen: from it a click ticks the whole group.
            RoutedEventHandler all = (_, _) =>
            {
                if (_building || _showing)
                {
                    return;
                }

                var on = groupBox.IsChecked == true;
                children.ForEach(c => c.IsChecked = on);
            };
            groupBox.Checked += all;
            groupBox.Unchecked += all;
            _building = true;
            ShowGroup(groupBox, children);
            _building = false;
            _skills.Items.Add(item);
        }

        ShowSkills();

        if (config.Endpoints.Count == 0)
        {
            Append("No endpoint yet: press ⚙ and add one (base URL, model, key).");
        }
    }

    private async Task RunOrStopAsync()
    {
        if (_cts is not null)
        {
            if (_goal.Text.Trim().Length > 0)
            {
                SendInterruption();
            }
            else
            {
                _cts.Cancel();
            }

            return;
        }

        var goal = _goal.Text.Trim();
        if (goal.Length == 0)
        {
            return;
        }

        // The skills folder may have changed since the pane opened (a skill copied in, or one the
        // app added): a user once saw no chess.com skill until BaudHoundTerm was restarted.
        Reload();

        if (_endpoints.SelectedItem is not AgentEndpoint endpoint)
        {
            Append("Pick an endpoint first (⚙ to add one).");
            return;
        }

        if (endpoint.NeverOnLocalShells && _protocol == Core.Model.SessionProtocol.LocalShell)
        {
            Append($"\"{endpoint.Name}\" is set to never run on local shells (⚙ to change).");
            return;
        }

        var skills = _skillChecks.Where(c => c.IsChecked == true)
            .Select(c => (AgentSkill)c.Tag).ToList();

        // A different endpoint or skill set is a different agent: start its conversation fresh.
        var key = endpoint.Id + "|" + string.Join("|", skills.Select(s => s.Path));
        if (_runner is null || key != _runnerKey)
        {
            var apiKey = _vault.Get(endpoint.Id, "apikey")?.Trim();
            // A local shell in the elevated window runs as administrator: every command asks,
            // whatever the endpoint says (agent.json is writable by anything running as the user).
            if (Elevated)
            {
                endpoint = endpoint.Copy();
                endpoint.Approval = AgentApproval.Always;
            }

            _runner = Desktop && _target is IScreenTarget screen
                ? new AgentRunner(endpoint, apiKey, screen, skills, _log)
                : new AgentRunner(endpoint, apiKey, (ITerminalTarget)_target, skills, _log);
            // Helpers: only those enabled in agent.json and named by a ticked skill.
            _runner.Helpers = AgentHelper.ForRun(AgentConfig.Load().Helpers, skills, _runner.BuiltInToolNames,
                screen: Desktop);
            foreach (var h in AgentConfig.Load().Helpers.Where(h => skills.Any(s => s.Helpers.Contains(h.Name))))
            {
                if (h.Problem(_runner.BuiltInToolNames) is { } why)
                {
                    Append($"Helper {h.Name} is not offered: {why}.");
                }
            }

            _runner.Event += e => Dispatcher.BeginInvoke(() => Show(e));
            _runner.Approve = command => Dispatcher.InvokeAsync(() => MessageBox.Show(
                Window.GetWindow(this)!,
                $"The agent wants to {(Desktop ? "do this" : "run")}:\n\n{command}\n\nin {_target.Describe}" +
                (Elevated ? ", which runs as ADMINISTRATOR" : "") + ".\n\nAllow it?",
                "BaudHoundTerm: agent approval", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes).Task;
            _runnerKey = key;
        }

        _goal.Clear();
        Append("");
        Append("▶ " + goal);
        _cts = new CancellationTokenSource();
        ShowRunButton();
        _endpoints.IsEnabled = false;

        try
        {
            var runner = _runner;
            await Task.Run(() => runner.RunAsync(goal, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            Append("■ Stopped.");
        }
        catch (Exception ex)
        {
            Append("✖ " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ShowRunButton();
            _endpoints.IsEnabled = true;
            _goal.Focus();
        }
    }

    private void Show(AgentEvent e)
    {
        switch (e.Kind)
        {
            case "status":
                return;   // "Thinking (step n)" would bury the actions; the Stop button says it is busy
            case "action":
                Append("  " + e.Text);
                break;
            case "thought":
                Append("  · " + e.Text);
                break;
            case "final":
                Append("✔ " + e.Text);
                break;
            default:
                Append(e.Text);
                break;
        }
    }

    private void Append(string line)
    {
        _transcript.AppendText(line + Environment.NewLine);
        _transcript.ScrollToEnd();
    }

    private void OpenSettings()
    {
        new AgentSettingsWindow(_vault) { Owner = Window.GetWindow(this) }.ShowDialog();
        _runner = null;   // approval rules may have changed; the next goal starts with them
        Reload();
    }

    internal static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>A group's box: ticked when all its skills are, empty when none are, mixed otherwise.</summary>
    private void ShowGroup(CheckBox group, List<CheckBox> children)
    {
        var on = children.Count(c => c.IsChecked == true);
        _showing = true;
        group.IsChecked = on == children.Count ? true : on == 0 ? false : null;
        _showing = false;
        ShowSkills();
    }

    // Set while the tree is built or a group box only shows its skills' state, so neither
    // counts as the user ticking something.
    private bool _building;
    private bool _showing;

    /// <summary>The collapsed tree still says which skills are on.</summary>
    private void ShowSkills()
    {
        var on = _skillChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        _skillsBox.Header = new TextBlock
        {
            Text = _skillChecks.Count == 0 ? "Skills: none for this kind of tab"
                : "Skills: " + (on.Count == 0 ? "none ticked" : string.Join(", ", on)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Light
        };
        _skillsBox.ToolTip = "Open to choose which skills the agent gets. Your choices are kept.";
    }

    /// <summary>Ticks are kept in agent.json, so the next pane and the next start of the app have them.</summary>
    private static void RememberSkills(IEnumerable<CheckBox> boxes)
    {
        try
        {
            var config = AgentConfig.Load();
            foreach (var box in boxes)
            {
                config.SkillChoices[System.IO.Path.GetFileName(((AgentSkill)box.Tag).Path)] = box.IsChecked == true;
            }

            config.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this pane only.
        }
    }

    /// <summary>What the user types while the agent works goes to it now, not after the run.</summary>
    private void SendInterruption()
    {
        var text = _goal.Text.Trim();
        if (_runner is null || text.Length == 0)
        {
            return;
        }

        _goal.Clear();
        Append("✋ " + text);
        _runner.Interrupt(text);
    }

    /// <summary>Run when idle; while running, Send with words in the box, Stop without.</summary>
    private void ShowRunButton()
    {
        _run.Content = _cts is null ? "Run" : _goal.Text.Trim().Length > 0 ? "Send" : "Stop";
        _run.ToolTip = _cts is null ? null
            : _goal.Text.Trim().Length > 0 ? "Interrupt the agent with this message; it goes on from there"
            : "Stop the agent (Esc)";
    }

    /// <summary>Stop a running goal, e.g. when the tab is closed under it.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>The user typed into the terminal; the agent's view of the current line is stale.</summary>
    public void NoteUserInput() => _runner?.NoteOutsideInput();

    public void FocusGoal() => _goal.Focus();
}
