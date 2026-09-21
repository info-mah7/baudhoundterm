using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;

namespace Quarterdeck.App.Views;

public partial class SessionEditorWindow : Window
{
    private readonly CredentialVault _vault;
    private readonly ObservableCollection<AutoLoginStep> _steps;
    private readonly ObservableCollection<HighlightRule> _highlights;

    public SessionEditorWindow(SessionDefinition definition, CredentialVault vault)
    {
        InitializeComponent();

        Definition = definition;
        _vault = vault;
        _steps = new ObservableCollection<AutoLoginStep>(definition.AutoLogin);
        _highlights = new ObservableCollection<HighlightRule>(definition.Highlights);

        LoginGrid.ItemsSource = _steps;
        HighlightGrid.ItemsSource = _highlights;

        Load();
    }

    public SessionDefinition Definition { get; }

    private void Load()
    {
        var d = Definition;

        NameBox.Text = d.Name;
        FolderBox.Text = d.Folder;
        ProtocolBox.SelectedIndex = (int)d.Protocol;

        HostBox.Text = d.Host;
        PortBox.Text = d.Port.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = d.Username;
        AuthBox.SelectedIndex = (int)d.AuthMethod;
        KeyBox.Text = d.KeyPath;

        SerialPortBox.Text = d.SerialPort;
        BaudBox.Text = d.BaudRate.ToString(CultureInfo.InvariantCulture);
        ShellBox.Text = d.ShellCommand;

        ThemeBox.Text = d.Theme;
        FontBox.Text = d.FontFamily;
        FontSizeBox.Text = d.FontSize.ToString(CultureInfo.InvariantCulture);
        ScrollbackBox.Text = d.ScrollbackLines.ToString(CultureInfo.InvariantCulture);

        LogEnabledBox.IsChecked = d.Logging.Enabled;
        LogDirBox.Text = d.Logging.Directory;
        LogPatternBox.Text = d.Logging.FileNamePattern;
        LogStampBox.IsChecked = d.Logging.PrefixTimestamps;
        LogStripBox.IsChecked = d.Logging.StripAnsi;
        LogRollBox.Text = (d.Logging.MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);

        ReconnectBox.IsChecked = d.Reconnect.Enabled;
        MaxAttemptsBox.Text = d.Reconnect.MaxAttempts.ToString(CultureInfo.InvariantCulture);
        InitialDelayBox.Text = d.Reconnect.InitialDelayMs.ToString(CultureInfo.InvariantCulture);
        MaxDelayBox.Text = d.Reconnect.MaxDelayMs.ToString(CultureInfo.InvariantCulture);
        BackoffBox.Text = d.Reconnect.BackoffFactor.ToString(CultureInfo.InvariantCulture);

        UpdateProtocolPanels();
    }

    private void OnProtocolChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateProtocolPanels();

    private void UpdateProtocolPanels()
    {
        if (SshPanel is null)
        {
            return;
        }

        var protocol = (SessionProtocol)Math.Max(0, ProtocolBox.SelectedIndex);
        SshPanel.Visibility = protocol == SessionProtocol.Ssh ? Visibility.Visible : Visibility.Collapsed;
        SerialPanel.Visibility = protocol == SessionProtocol.Serial ? Visibility.Visible : Visibility.Collapsed;
        ShellPanel.Visibility = protocol == SessionProtocol.LocalShell ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select private key", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            KeyBox.Text = dialog.FileName;
        }
    }

    private void OnAddStep(object sender, RoutedEventArgs e) =>
        _steps.Add(new AutoLoginStep { Expect = "assword:", IsSecret = true });

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if (LoginGrid.SelectedItem is AutoLoginStep step)
        {
            _steps.Remove(step);
        }
    }

    private void OnAddHighlight(object sender, RoutedEventArgs e) =>
        _highlights.Add(new HighlightRule { Pattern = "ERROR|FAIL", Color = "#ff6b6b" });

    private void OnRemoveHighlight(object sender, RoutedEventArgs e)
    {
        if (HighlightGrid.SelectedItem is HighlightRule rule)
        {
            _highlights.Remove(rule);
        }
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var d = Definition;

        d.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Untitled" : NameBox.Text.Trim();
        d.Folder = FolderBox.Text.Trim().Trim('/');
        d.Protocol = (SessionProtocol)Math.Max(0, ProtocolBox.SelectedIndex);

        d.Host = HostBox.Text.Trim();
        d.Port = ParseInt(PortBox.Text, 22);
        d.Username = UserBox.Text.Trim();
        d.AuthMethod = (SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex);
        d.KeyPath = KeyBox.Text.Trim();

        d.SerialPort = SerialPortBox.Text.Trim();
        d.BaudRate = ParseInt(BaudBox.Text, 115200);
        d.ShellCommand = ShellBox.Text.Trim();

        d.Theme = ThemeBox.Text;
        d.FontFamily = FontBox.Text.Trim();
        d.FontSize = ParseInt(FontSizeBox.Text, 13);
        d.ScrollbackLines = ParseInt(ScrollbackBox.Text, 10000);

        d.Logging.Enabled = LogEnabledBox.IsChecked == true;
        d.Logging.Directory = LogDirBox.Text.Trim();
        d.Logging.FileNamePattern = LogPatternBox.Text.Trim();
        d.Logging.PrefixTimestamps = LogStampBox.IsChecked == true;
        d.Logging.StripAnsi = LogStripBox.IsChecked == true;
        d.Logging.MaxBytes = (long)ParseInt(LogRollBox.Text, 64) * 1024 * 1024;

        d.Reconnect.Enabled = ReconnectBox.IsChecked == true;
        d.Reconnect.MaxAttempts = ParseInt(MaxAttemptsBox.Text, 10);
        d.Reconnect.InitialDelayMs = ParseInt(InitialDelayBox.Text, 1000);
        d.Reconnect.MaxDelayMs = ParseInt(MaxDelayBox.Text, 30000);
        d.Reconnect.BackoffFactor = ParseDouble(BackoffBox.Text, 1.8);

        d.AutoLogin = _steps.ToList();
        d.Highlights = _highlights.ToList();

        // A blank secret box means "leave the stored secret alone", so an edit never wipes it.
        if (!string.IsNullOrEmpty(SecretBox.Password))
        {
            var key = d.AuthMethod == SshAuthMethod.PublicKey ? "passphrase" : "password";
            _vault.Set(d.Id, SecretBox.Password, key);
        }

        DialogResult = true;
    }
}
