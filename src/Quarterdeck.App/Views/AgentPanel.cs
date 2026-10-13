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
    private readonly ITerminalTarget _target;
    private readonly Core.Model.SessionProtocol _protocol;
    private readonly CredentialVault _vault;
    private readonly AgentLog _log = new();

    private readonly ComboBox _endpoints = new() { Margin = new Thickness(0, 0, 4, 0) };
    private readonly WrapPanel _skills = new() { Margin = new Thickness(0, 4, 0, 4) };
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
        ToolTip = "What the agent should do in this session. Enter runs; Shift+Enter for a new line."
    };
    private readonly Button _run = new() { Content = "Run", MinWidth = 64 };
    private readonly Button _reset = new() { Content = "New", ToolTip = "Forget this conversation; the next goal starts fresh" };

    private AgentRunner? _runner;
    private string _runnerKey = "";
    private CancellationTokenSource? _cts;

    public event EventHandler? CloseRequested;

    public AgentPanel(ITerminalTarget target, Core.Model.SessionProtocol protocol, CredentialVault vault)
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
        setup.Children.Add(_skills);
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
        _goal.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await RunOrStopAsync();
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
        Append($"Attached to {_target.Describe}. It acts without asking; every step is logged.");
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

        var ticked = _skills.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Select(c => ((AgentSkill)c.Tag).Path).ToHashSet();
        var first = _skills.Children.Count == 0;

        _skills.Children.Clear();
        foreach (var skill in SkillLibrary.Load().Where(s => s.AppliesTo(_protocol)))
        {
            _skills.Children.Add(new CheckBox
            {
                Content = skill.Name,
                Tag = skill,
                ToolTip = string.IsNullOrEmpty(skill.Description) ? skill.Path : skill.Description,
                IsChecked = first || ticked.Contains(skill.Path),
                Margin = new Thickness(0, 0, 10, 2)
            });
        }

        if (config.Endpoints.Count == 0)
        {
            Append("No endpoint yet: press ⚙ and add one (base URL, model, key).");
        }
    }

    private async Task RunOrStopAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        var goal = _goal.Text.Trim();
        if (goal.Length == 0)
        {
            return;
        }

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

        var skills = _skills.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Select(c => (AgentSkill)c.Tag).ToList();

        // A different endpoint or skill set is a different agent: start its conversation fresh.
        var key = endpoint.Id + "|" + string.Join("|", skills.Select(s => s.Path));
        if (_runner is null || key != _runnerKey)
        {
            _runner = new AgentRunner(endpoint, _vault.Get(endpoint.Id, "apikey"), _target, skills, _log);
            _runner.Event += e => Dispatcher.BeginInvoke(() => Show(e));
            _runner.Approve = command => Dispatcher.InvokeAsync(() => MessageBox.Show(
                Window.GetWindow(this)!,
                $"The agent wants to run:\n\n{command}\n\nin {_target.Describe}.\n\nAllow it?",
                "BaudHoundTerm: agent approval", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes).Task;
            _runnerKey = key;
        }

        _goal.Clear();
        Append("");
        Append("▶ " + goal);
        _cts = new CancellationTokenSource();
        _run.Content = "Stop";
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
            _run.Content = "Run";
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

    /// <summary>Stop a running goal, e.g. when the tab is closed under it.</summary>
    public void Cancel() => _cts?.Cancel();

    public void FocusGoal() => _goal.Focus();
}
