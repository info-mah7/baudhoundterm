using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Agent;
using Quarterdeck.Core.Security;

namespace Quarterdeck.App.Views;

/// <summary>
/// Agent endpoints (any OpenAI-compatible /v1) and a way into the skills folder. Keys go to the
/// DPAPI vault, never to agent.json.
/// </summary>
internal sealed class AgentSettingsWindow : Window
{
    private readonly CredentialVault _vault;
    private readonly AgentConfig _config = AgentConfig.Load();

    private readonly ListBox _list = new() { Width = 180, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _name = new();
    private readonly TextBox _url = new();
    private readonly ComboBox _model = new() { IsEditable = true };
    private readonly PasswordBox _key = new();
    private readonly TextBox _temperature = new();
    private readonly TextBox _steps = new();
    private readonly TextBox _extra = new() { AcceptsReturn = true, Height = 54, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _default = new() { Content = "Use by default for new agents" };
    private readonly ComboBox _approval = new();
    private readonly ComboBox _thinking = new();
    private readonly TextBox _context = new()
    {
        ToolTip = "The model's context window in tokens; the conversation is trimmed to fit. 0 uses a safe default (about 32k). Tune reads the real size from the server."
    };
    private readonly TextBox _risky = new()
    {
        AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11
    };
    private readonly TextBox _shotWidth = new() { ToolTip = "Screenshots of a remote desktop are scaled down to this width before they go to the model. Wider reads small text better and costs more time and context." };
    private readonly CheckBox _confirm = new()
    {
        Content = "Check each click on a remote desktop first",
        ToolTip = "Before a click, the model is shown a close-up with a crosshair on the point and asked what is there. If that is not what it meant to click, nothing is clicked."
    };
    private readonly CheckBox _noLocal = new()
    {
        Content = "Never on local shells",
        ToolTip = "Local shells run as you, and you can decrypt the credential vault: a model misled by remote output could read stored secrets."
    };
    private readonly TextBlock _note = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private readonly TextBlock _heading = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Button _saveButton;
    private readonly Button _deleteButton;
    private readonly Button _forgetKeyButton;

    // The endpoint in the form. A new one is a draft: not in the list or the config until saved.
    private AgentEndpoint _editing = new();
    private string _clean = "";

    public AgentSettingsWindow(CredentialVault vault)
    {
        _vault = vault;
        Title = "Agent endpoints";
        Width = 640;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var root = new DockPanel { Margin = new Thickness(12) };

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(Btn("Skills folder", () => AgentPanel.OpenFolder(AgentConfig.SkillsDirectory),
            "Each .md file is a skill; its header lists the protocols it applies to"));
        bottom.Children.Add(Btn("Restore built-in skills", () =>
        {
            var restored = SkillLibrary.RestoreBuiltIns();
            Note(restored.Count == 0
                ? "All built-in skills are already in the skills folder (edited ones are left as they are)."
                : "Put back: " + string.Join(", ", restored) + ". The agent pane lists them the next time you press Run.",
                Color.FromRgb(0x98, 0xC3, 0x79));
        }, "Put back any built-in skill (such as chess.com) that is missing from the skills folder"));
        // For every endpoint, so it is saved when ticked, not with the endpoint form.
        var keepLog = new CheckBox
        {
            Content = "Keep a log of agent conversations",
            IsChecked = _config.KeepLog,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE)),
            ToolTip = "Every goal, reply, command and result, one file per day in the logs folder. Off, nothing is written; " +
                      "files already there stay."
        };
        keepLog.Click += (_, _) =>
        {
            _config.KeepLog = keepLog.IsChecked == true;
            try
            {
                _config.Save();
                Note(_config.KeepLog ? "Agent conversations will be logged." : "Agent conversations will not be logged. Files already in the logs folder stay until you delete them.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Note("Not saved: " + ex.Message, Color.FromRgb(0xE0, 0x6C, 0x75));
            }
        };
        bottom.Children.Add(Btn("Logs folder", () => AgentPanel.OpenFolder(AgentConfig.LogDirectory)));
        bottom.Children.Add(Btn("Close", Close));
        Closing += (_, e) => e.Cancel = !ConfirmLeave();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        // Its own row above the buttons: the button row has no room left at this width.
        var logRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        keepLog.Margin = new Thickness(0);
        var safeguards = Btn("Safeguards...", () => new SafeguardsWindow(_config) { Owner = this }.ShowDialog(),
            "Rules for every agent, and the text that makes it ask before every action: against instructions planted in what it reads");
        DockPanel.SetDock(safeguards, Dock.Right);
        logRow.Children.Add(safeguards);
        logRow.Children.Add(keepLog);
        DockPanel.SetDock(logRow, Dock.Bottom);
        root.Children.Add(logRow);

        var left = new DockPanel();
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        leftButtons.Children.Add(Btn("New", New, "Start a new endpoint; it is added when you press Add endpoint"));
        _deleteButton = Btn("Delete", Delete);
        leftButtons.Children.Add(_deleteButton);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(_list);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);

        var form = new StackPanel();
        Field(form, "Name", _name);
        ((TextBlock)form.Children[0]).Margin = new Thickness(0, 0, 0, 2);
        Field(form, "Base URL (up to /v1)", _url);
        var modelRow = new DockPanel();
        var fetch = Btn("Fetch models", async () => await FetchModelsAsync(), "Ask the server which models it serves");
        DockPanel.SetDock(fetch, Dock.Right);
        modelRow.Children.Add(fetch);
        modelRow.Children.Add(_model);
        Field(form, "Model", modelRow);
        var keyRow = new DockPanel();
        _forgetKeyButton = Btn("Forget key", ForgetKey, "Remove the saved key, for a server that takes none");
        DockPanel.SetDock(_forgetKeyButton, Dock.Right);
        keyRow.Children.Add(_forgetKeyButton);
        keyRow.Children.Add(_key);
        Field(form, "API key (blank keeps the saved one)", keyRow);
        var nums = new UniformGrid2(_temperature, _steps);
        Field(form, "Temperature  /  Max steps per goal", nums);
        Field(form, "Extra request JSON (optional)", _extra);
        _thinking.Items.Add("Thinking: as the server has it");
        _thinking.Items.Add("Thinking: off (faster, one round trip per step)");
        _thinking.Items.Add("Thinking: on (slower, better on hard steps)");
        _thinking.ToolTip = "Thinking models plan before answering. That helps on hard steps and costs time on every step; " +
                            "on a remote desktop each click is checked against a close-up first, and that check waits too.";
        var thinkRow = new DockPanel();
        var tune = Btn("Tune for this model", async () => await TuneAsync(),
            "Measure this model on this server: its context window, whether thinking can be turned off, and what each costs");
        DockPanel.SetDock(tune, Dock.Right);
        thinkRow.Children.Add(tune);
        thinkRow.Children.Add(_thinking);
        Field(form, "Thinking", thinkRow);
        Field(form, "Context window in tokens (0 = default)", _context);
        _approval.Items.Add("Run freely: never ask");
        _approval.Items.Add("Ask before risky commands (patterns below)");
        _approval.Items.Add("Ask before every command");
        Field(form, "Approval", _approval);
        Field(form, "Risky commands: one regex per line, case-insensitive", _risky);
        form.Children.Add(_noLocal);
        Field(form, "Remote desktops (VNC): screenshot width in pixels. The model must accept images.", _shotWidth);
        form.Children.Add(_confirm);
        form.Children.Add(_default);

        // The heading and the action row stay put; only the fields scroll.
        var right = new DockPanel();
        DockPanel.SetDock(_heading, Dock.Top);
        right.Children.Add(_heading);
        var actions = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        _saveButton = Btn("Save changes", () => Save());
        _saveButton.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x5C, 0x8A));
        actionRow.Children.Add(_saveButton);
        actionRow.Children.Add(Btn("Revert", () => Edit(_editing), "Undo the edits since the last save"));
        actionRow.Children.Add(Btn("Test connection", async () => await TestAsync(),
            "Check the values as typed (saved or not): server reachable, key accepted, model answers, tool calling works"));
        actions.Children.Add(actionRow);
        actions.Children.Add(_note);
        DockPanel.SetDock(actions, Dock.Bottom);
        right.Children.Add(actions);
        right.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(right);

        Content = root;

        // The default ListBox is white with white-on-white selection on this theme.
        _list.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C));
        _list.Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
        _list.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x59));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x2F, 0x3B, 0x52));
        _list.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        _list.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x38));
        _list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));

        _list.SelectionChanged += (_, e) =>
        {
            if (_refreshing || _list.SelectedItem is not AgentEndpoint ep || ep == _editing)
            {
                return;
            }

            if (ConfirmLeave())
            {
                // A save inside ConfirmLeave re-selects the endpoint it saved; point the list
                // back at the one now in the form.
                Edit(ep);
                _refreshing = true;
                _list.SelectedItem = ep;
                _refreshing = false;
            }
            else
            {
                // Stay on the endpoint being edited.
                _refreshing = true;
                _list.SelectedItem = IsNew ? null : _editing;
                _refreshing = false;
            }
        };
        Refresh();
    }

    private bool IsNew => !_config.Endpoints.Contains(_editing);

    private void Hint() => Note(
        "Any OpenAI-compatible server: vLLM, llama.cpp, Ollama, OpenRouter, OpenAI. " +
        "For Qwen thinking models on vLLM, Extra JSON {\"chat_template_kwargs\":{\"enable_thinking\":false}} keeps replies fast.");

    private void Note(string text, Color? color = null)
    {
        _note.Text = text;
        _note.Foreground = new SolidColorBrush(color ?? Color.FromRgb(0x8B, 0x94, 0xA7));
    }

    /// <summary>Everything the form holds, to tell whether it has unsaved edits.</summary>
    private string Snapshot() => string.Join("\u0001", _name.Text, _url.Text, _model.Text, _key.Password,
        _temperature.Text, _steps.Text, _extra.Text, _default.IsChecked, _approval.SelectedIndex, _risky.Text, _noLocal.IsChecked, _shotWidth.Text, _confirm.IsChecked,
        _thinking.SelectedIndex, _context.Text);

    private bool Dirty => Snapshot() != _clean;

    /// <summary>
    /// Before the form is replaced or the window closes: offers to save unsaved edits.
    /// Returns false when the user cancels, or the save failed.
    /// </summary>
    private bool ConfirmLeave()
    {
        if (!Dirty)
        {
            return true;
        }

        var what = IsNew ? "the new endpoint" : $"the changes to {_editing.Name}";
        var answer = MessageBox.Show(this, $"Save {what}?", "Agent endpoints",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private bool _refreshing;

    /// <summary>
    /// Rebuilds the list and loads the chosen endpoint into the form, or a new draft when
    /// there are none. Selection changes made here are ignored: clearing the list used to
    /// fire one that blanked the form mid-save.
    /// </summary>
    private void Refresh(AgentEndpoint? select = null)
    {
        _refreshing = true;
        try
        {
            _list.Items.Clear();
            foreach (var ep in _config.Endpoints)
            {
                _list.Items.Add(ep);
            }

            _list.SelectedItem = select ?? _config.Endpoints.FirstOrDefault();
        }
        finally
        {
            _refreshing = false;
        }

        Edit(_list.SelectedItem as AgentEndpoint ?? new AgentEndpoint());
    }

    private void Edit(AgentEndpoint ep)
    {
        _editing = ep;
        var isNew = IsNew;
        _heading.Text = isNew ? "New endpoint (not saved yet)" : "Editing: " + ep.Name;
        _saveButton.Content = isNew ? "Add endpoint" : "Save changes";
        _deleteButton.Content = isNew ? "Discard" : "Delete";
        _deleteButton.IsEnabled = !isNew || _config.Endpoints.Count > 0;
        _name.Text = ep.Name;
        _url.Text = ep.BaseUrl;
        _model.Text = ep.Model;
        _key.Password = "";
        _temperature.Text = ep.Temperature.ToString(CultureInfo.InvariantCulture);
        _steps.Text = ep.MaxSteps.ToString(CultureInfo.InvariantCulture);
        _extra.Text = ep.ExtraJson;
        _default.IsChecked = _config.DefaultEndpoint == ep.Id || (isNew && _config.Endpoints.Count == 0);
        _approval.SelectedIndex = (int)ep.Approval;
        _thinking.SelectedIndex = (int)ep.Thinking;
        _context.Text = ep.ContextTokens.ToString(CultureInfo.InvariantCulture);
        _thinkingOff = ep.ThinkingOffJson;
        _thinkingOn = ep.ThinkingOnJson;
        _risky.Text = ep.RiskyPatterns;
        _noLocal.IsChecked = ep.NeverOnLocalShells;
        _shotWidth.Text = ep.ScreenshotWidth.ToString(CultureInfo.InvariantCulture);
        _confirm.IsChecked = ep.ConfirmClicks;
        ShowKeyState();
        _clean = Snapshot();
        Hint();
    }

    private void ShowKeyState()
    {
        var saved = !IsNew && _vault.Get(_editing.Id, "apikey") is not null;
        _key.ToolTip = saved ? "A key is saved" : "No key saved";
        _forgetKeyButton.IsEnabled = saved;
    }

    /// <summary>
    /// Removes the saved key. A blank field on save keeps the key, so without this the only
    /// way to clear one was to delete the endpoint.
    /// </summary>
    private void ForgetKey()
    {
        if (MessageBox.Show(this, $"Remove the saved API key of {_editing.Name}? Requests will be sent without one.",
                "Agent endpoints", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        RemoveSavedKey();
    }

    private void RemoveSavedKey()
    {
        try
        {
            _vault.Remove(_editing.Id, "apikey");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note("Key not removed: " + ex.Message, Color.FromRgb(0xE0, 0x6C, 0x75));
            return;
        }

        _key.Password = "";
        ShowKeyState();
        Note($"Removed the saved key of {_editing.Name}.", Color.FromRgb(0x98, 0xC3, 0x79));
    }

    private void New()
    {
        if (!ConfirmLeave())
        {
            return;
        }

        _refreshing = true;
        _list.SelectedItem = null;
        _refreshing = false;
        Edit(new AgentEndpoint());
        Note("Fill in the fields, then press Add endpoint. Test connection works before saving.");
        _name.Focus();
        _name.SelectAll();
    }

    private void Delete()
    {
        if (IsNew)
        {
            Refresh();
            return;
        }

        if (MessageBox.Show(this, $"Delete the endpoint {_editing.Name} and its saved key?", "Agent endpoints",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        _vault.Remove(_editing.Id);
        _config.Endpoints.Remove(_editing);
        if (_config.DefaultEndpoint == _editing.Id)
        {
            _config.DefaultEndpoint = _config.Endpoints.FirstOrDefault()?.Id;
        }

        var name = _editing.Name;
        _config.Save();
        Refresh();
        Note($"Deleted {name}.");
    }

    /// <summary>Writes the form to the endpoint, adding it first if it is a new one.</summary>
    private bool Save()
    {
        var ep = _editing;
        try
        {
            AgentConfig.ParseExtra(_extra.Text);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            Note("Not saved: Extra JSON is not valid: " + ex.Message, Color.FromRgb(0xE0, 0x6C, 0x75));
            return false;
        }

        var added = IsNew;
        if (added)
        {
            _config.Endpoints.Add(ep);
            _config.DefaultEndpoint ??= ep.Id;
        }

        ep.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Endpoint" : _name.Text.Trim();
        ep.BaseUrl = _url.Text.Trim();
        ep.Model = _model.Text.Trim();
        ep.Temperature = double.TryParse(_temperature.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0.2;
        ep.MaxSteps = int.TryParse(_steps.Text, out var s) && s > 0 ? s : new AgentEndpoint().MaxSteps;
        ep.ExtraJson = _extra.Text.Trim();
        // Nothing selected (an unknown value in agent.json) keeps the strictest mode, not Free.
        ep.Approval = _approval.SelectedIndex < 0 ? AgentApproval.Always : (AgentApproval)_approval.SelectedIndex;
        ep.Thinking = _thinking.SelectedIndex < 0 ? AgentThinking.Auto : (AgentThinking)_thinking.SelectedIndex;
        ep.ContextTokens = int.TryParse(_context.Text, out var ctx) && ctx > 0 ? Math.Clamp(ctx, 4_000, 2_000_000) : 0;
        ep.ThinkingOffJson = _thinkingOff;
        ep.ThinkingOnJson = _thinkingOn;
        ep.RiskyPatterns = _risky.Text.Trim().Length > 0 ? _risky.Text.Trim() : AgentEndpoint.DefaultRiskyPatterns;
        ep.NeverOnLocalShells = _noLocal.IsChecked == true;
        ep.ScreenshotWidth = int.TryParse(_shotWidth.Text, out var sw) ? Math.Clamp(sw, 320, 2560) : 1280;
        ep.ConfirmClicks = _confirm.IsChecked == true;

        try
        {
            if (_key.Password.Trim().Length > 0)
            {
                _vault.Set(ep.Id, _key.Password.Trim(), "apikey");
            }

            if (_default.IsChecked == true)
            {
                _config.DefaultEndpoint = ep.Id;
            }

            _config.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Not written: a new endpoint stays a draft, so leaving still offers to save it.
            if (added)
            {
                _config.Endpoints.Remove(ep);
                if (_config.DefaultEndpoint == ep.Id)
                {
                    _config.DefaultEndpoint = _config.Endpoints.FirstOrDefault()?.Id;
                }
            }

            Note("Not saved: " + ex.Message, Color.FromRgb(0xE0, 0x6C, 0x75));
            return false;
        }

        Refresh(ep);
        Note(added ? $"Added {ep.Name}." : $"Saved {ep.Name}.", Color.FromRgb(0x98, 0xC3, 0x79));
        return true;
    }

    /// <summary>Runs the connection test on the form's current values and shows each step.</summary>
    // Found by Tune and saved with the endpoint: the switch this server takes.
    private string _thinkingOff = "";
    private string _thinkingOn = "";

    private async Task TuneAsync()
    {
        var probe = new AgentEndpoint
        {
            BaseUrl = _url.Text.Trim(),
            Model = _model.Text.Trim(),
            ExtraJson = _extra.Text.Trim()
        };

        Note("Measuring this model: context window, and what thinking costs here...");
        EndpointTest.EndpointTuning tuning;
        try
        {
            tuning = await EndpointTest.TuneAsync(probe, FormKey());
        }
        catch (Exception ex)
        {
            Note("✖ Tuning failed: " + ex.Message, Color.FromRgb(0xE0, 0x6C, 0x75));
            return;
        }

        if (tuning.ContextTokens > 0)
        {
            _context.Text = tuning.ContextTokens.ToString(CultureInfo.InvariantCulture);
        }

        _thinkingOff = tuning.ThinkingOffJson;
        _thinkingOn = tuning.ThinkingOnJson;
        if (tuning.Recommended != AgentThinking.Auto || _thinking.SelectedIndex < 0)
        {
            _thinking.SelectedIndex = (int)tuning.Recommended;
        }

        Show(tuning.Notes);
    }

    private async Task TestAsync()
    {
        var probe = new AgentEndpoint
        {
            BaseUrl = _url.Text.Trim(),
            Model = _model.Text.Trim(),
            ExtraJson = _extra.Text.Trim()
        };
        var key = FormKey();

        Note("Testing...");
        List<EndpointCheck> lines;
        try
        {
            lines = await EndpointTest.RunAsync(probe, key);
        }
        catch (Exception ex)
        {
            // An async button handler that throws takes the app down with it.
            Note("✖ The test itself failed: " + ex.Message, Color.FromRgb(0xE0, 0x6C, 0x75));
            return;
        }

        // A switch that turns thinking off goes into the form, not the saved endpoint: Save keeps it.
        if (lines.LastOrDefault(l => l.SuggestedExtra is not null)?.SuggestedExtra is { } better)
        {
            _extra.Text = better;
        }

        Show(lines);
    }

    /// <summary>One line per check, coloured by the worst of them.</summary>
    private void Show(List<EndpointCheck> lines) =>
        Note(string.Join(Environment.NewLine, lines.Select(l => (l.Ok ? (l.Warning ? "! " : "✔ ") : "✖ ") + l.Text)),
            lines.Any(l => !l.Ok) ? Color.FromRgb(0xE0, 0x6C, 0x75)
            : lines.Any(l => l.Warning) ? Color.FromRgb(0xE5, 0xC0, 0x7B)
            : Color.FromRgb(0x98, 0xC3, 0x79));

    /// <summary>The key typed in the form, else the saved one. Pasted keys often carry a trailing space or newline.</summary>
    private string? FormKey()
    {
        var typed = _key.Password.Trim();
        return typed.Length > 0 ? typed : IsNew ? null : _vault.Get(_editing.Id, "apikey")?.Trim();
    }

    private async Task FetchModelsAsync()
    {
        var url = _url.Text.Trim().TrimEnd('/') + "/models";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var key = FormKey();
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await http.SendAsync(request);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var ids = (json?["data"] as JsonArray)?.Select(m => m?["id"]?.ToString()).OfType<string>().ToList() ?? new();

            var current = _model.Text;
            _model.Items.Clear();
            ids.ForEach(id => _model.Items.Add(id));
            _model.Text = current.Length > 0 ? current : ids.FirstOrDefault() ?? "";
            Note(ids.Count > 0 ? $"{ids.Count} model(s): {string.Join(", ", ids.Take(6))}" : $"{url} answered {(int)response.StatusCode} with no models.");
        }
        catch (Exception ex)
        {
            Note($"Could not reach {url}: {ex.Message}", Color.FromRgb(0xE0, 0x6C, 0x75));
        }
    }

    private static void Field(Panel form, string label, UIElement input)
    {
        form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        form.Children.Add(input);
    }

    private static Button Btn(string text, Action act, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = tip };
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>Two inputs side by side.</summary>
    private sealed class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2(UIElement a, UIElement b)
        {
            Columns = 2;
            ((FrameworkElement)a).Margin = new Thickness(0, 0, 6, 0);
            Children.Add(a);
            Children.Add(b);
        }
    }
}
