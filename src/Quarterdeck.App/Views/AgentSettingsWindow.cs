using System.Globalization;
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
    private readonly TextBlock _note = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private AgentEndpoint? _editing;

    public AgentSettingsWindow(CredentialVault vault)
    {
        _vault = vault;
        Title = "Agent endpoints";
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        var root = new DockPanel { Margin = new Thickness(12) };

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(Btn("Skills folder", () => AgentPanel.OpenFolder(AgentConfig.SkillsDirectory),
            "Each .md file is a skill; its header lists the protocols it applies to"));
        bottom.Children.Add(Btn("Logs folder", () => AgentPanel.OpenFolder(AgentConfig.LogDirectory)));
        bottom.Children.Add(Btn("Close", Close));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var left = new DockPanel();
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        leftButtons.Children.Add(Btn("Add", Add));
        leftButtons.Children.Add(Btn("Delete", Delete));
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(_list);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);

        var form = new StackPanel();
        Field(form, "Name", _name);
        Field(form, "Base URL (up to /v1)", _url);
        var modelRow = new DockPanel();
        var fetch = Btn("Fetch models", async () => await FetchModelsAsync(), "Ask the server which models it serves");
        DockPanel.SetDock(fetch, Dock.Right);
        modelRow.Children.Add(fetch);
        modelRow.Children.Add(_model);
        Field(form, "Model", modelRow);
        Field(form, "API key (blank keeps the saved one)", _key);
        var nums = new UniformGrid2(_temperature, _steps);
        Field(form, "Temperature  /  Max steps per goal", nums);
        Field(form, "Extra request JSON (optional)", _extra);
        form.Children.Add(_default);
        form.Children.Add(Btn("Save", Save));
        form.Children.Add(_note);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

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

        _list.SelectionChanged += (_, _) => Edit(_list.SelectedItem as AgentEndpoint);
        _note.Text = "Any OpenAI-compatible server: vLLM, llama.cpp, Ollama, OpenRouter, OpenAI. " +
                     "For Qwen thinking models on vLLM, Extra JSON {\"chat_template_kwargs\":{\"enable_thinking\":false}} keeps replies fast.";
        Refresh();
    }

    private void Refresh(AgentEndpoint? select = null)
    {
        _list.Items.Clear();
        foreach (var ep in _config.Endpoints)
        {
            _list.Items.Add(ep);
        }

        _list.SelectedItem = select ?? _config.Endpoints.FirstOrDefault();
        if (_list.SelectedItem is null)
        {
            Edit(null);
        }
    }

    private void Edit(AgentEndpoint? ep)
    {
        _editing = ep;
        _name.Text = ep?.Name ?? "";
        _url.Text = ep?.BaseUrl ?? "";
        _model.Text = ep?.Model ?? "";
        _key.Password = "";
        _temperature.Text = (ep?.Temperature ?? 0.2).ToString(CultureInfo.InvariantCulture);
        _steps.Text = (ep?.MaxSteps ?? 30).ToString(CultureInfo.InvariantCulture);
        _extra.Text = ep?.ExtraJson ?? "";
        _default.IsChecked = ep is not null && _config.DefaultEndpoint == ep.Id;
        _key.ToolTip = ep is not null && _vault.Get(ep.Id, "apikey") is not null ? "A key is saved" : "No key saved";
    }

    private void Add()
    {
        var ep = new AgentEndpoint();
        _config.Endpoints.Add(ep);
        _config.DefaultEndpoint ??= ep.Id;
        Refresh(ep);
    }

    private void Delete()
    {
        if (_editing is null)
        {
            return;
        }

        _vault.Remove(_editing.Id);
        _config.Endpoints.Remove(_editing);
        if (_config.DefaultEndpoint == _editing.Id)
        {
            _config.DefaultEndpoint = _config.Endpoints.FirstOrDefault()?.Id;
        }

        _config.Save();
        Refresh();
    }

    private void Save()
    {
        if (_editing is null)
        {
            Add();
        }

        var ep = _editing!;
        try
        {
            AgentConfig.ParseExtra(_extra.Text);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            _note.Text = "Extra JSON is not valid: " + ex.Message;
            return;
        }

        ep.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Endpoint" : _name.Text.Trim();
        ep.BaseUrl = _url.Text.Trim();
        ep.Model = _model.Text.Trim();
        ep.Temperature = double.TryParse(_temperature.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0.2;
        ep.MaxSteps = int.TryParse(_steps.Text, out var s) && s > 0 ? s : 30;
        ep.ExtraJson = _extra.Text.Trim();

        if (_key.Password.Length > 0)
        {
            _vault.Set(ep.Id, _key.Password, "apikey");
        }

        if (_default.IsChecked == true)
        {
            _config.DefaultEndpoint = ep.Id;
        }

        _config.Save();
        _note.Text = $"Saved {ep.Name}.";
        Refresh(ep);
    }

    private async Task FetchModelsAsync()
    {
        var url = _url.Text.Trim().TrimEnd('/') + "/models";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var key = _key.Password.Length > 0 ? _key.Password : _editing is null ? null : _vault.Get(_editing.Id, "apikey");
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
            _note.Text = ids.Count > 0 ? $"{ids.Count} model(s): {string.Join(", ", ids.Take(6))}" : $"{url} answered {(int)response.StatusCode} with no models.";
        }
        catch (Exception ex)
        {
            _note.Text = $"Could not reach {url}: {ex.Message}";
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
