using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.KeePassXc;
using System.IO;

namespace Quarterdeck.App.Views;

public partial class SessionEditorWindow : Window
{
    private readonly CredentialVault _vault;
    private readonly ObservableCollection<AutoLoginStep> _steps;
    private readonly ObservableCollection<HighlightRule> _highlights;
    private readonly ObservableCollection<CommandShortcut> _commands;

    public SessionEditorWindow(SessionDefinition definition, CredentialVault vault)
    {
        InitializeComponent();

        Definition = definition;
        _vault = vault;
        _steps = new ObservableCollection<AutoLoginStep>(definition.AutoLogin);
        _highlights = new ObservableCollection<HighlightRule>(definition.Highlights);
        _commands = new ObservableCollection<CommandShortcut>(definition.Commands);

        LoginGrid.ItemsSource = _steps;
        HighlightGrid.ItemsSource = _highlights;
        CommandGrid.ItemsSource = _commands;

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
        PasswordSourceBox.SelectedIndex = (int)d.PasswordSource;
        KeePassEntryBox.Text = d.KeePassEntry;
        AgentFilterBox.Text = d.AgentKeyFilter;
        UpdateAuthPanels();

        foreach (var port in System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p.Length))
        {
            SerialPortBox.Items.Add(port);
        }

        foreach (var rate in new[] { "9600", "14400", "19200", "28800", "38400", "57600",
                                     "74880", "115200", "230400", "460800", "921600" })
        {
            BaudBox.Items.Add(rate);
        }

        SerialPortBox.Text = d.SerialPort;
        BaudBox.Text = d.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBitsBox.Text = d.DataBits.ToString(CultureInfo.InvariantCulture);
        ParityBox.Text = d.Parity;
        StopBitsBox.Text = d.StopBits;
        ShellBox.Text = d.ShellCommand;
        TelnetHostBox.Text = d.Host;
        TelnetPortBox.Text = d.Port.ToString(CultureInfo.InvariantCulture);
        TelnetLineModeBox.IsChecked = d.LineMode;
        TelnetTlsBox.IsChecked = d.UseTls;
        VncHostBox.Text = d.Host;
        VncPortBox.Text = d.Port.ToString(CultureInfo.InvariantCulture);
        VncViewOnlyBox.IsChecked = d.ViewOnly;
        RtspHostBox.Text = d.Host;
        RtspPortBox.Text = (d.Protocol == SessionProtocol.Rtsp ? d.Port : 554).ToString(CultureInfo.InvariantCulture);
        RtspPathBox.Text = d.RtspPath;
        RtspUserBox.Text = d.Username;
        RtspTcpBox.IsChecked = d.RtspTcp;
        RtspLowLatencyBox.IsChecked = d.LowLatency;
        RtspOnvifPortBox.Text = d.OnvifPort.ToString(CultureInfo.InvariantCulture);

        ThemeBox.Text = d.Theme;
        FontBox.Text = d.FontFamily;
        FontSizeBox.Text = d.FontSize.ToString(CultureInfo.InvariantCulture);
        ScrollbackBox.Text = d.ScrollbackLines.ToString(CultureInfo.InvariantCulture);
        TabColorBox.Text = d.TabColor;

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
        TelnetPanel.Visibility = protocol == SessionProtocol.Telnet ? Visibility.Visible : Visibility.Collapsed;
        VncPanel.Visibility = protocol == SessionProtocol.Vnc ? Visibility.Visible : Visibility.Collapsed;
        RtspPanel.Visibility = protocol == SessionProtocol.Rtsp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAuthChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

    /// <summary>Shows only what the chosen login needs: a key file, an agent key, or a password and where it comes from.</summary>
    private void UpdateAuthPanels()
    {
        if (SecretPanel is null || PasswordSourceBox is null)
        {
            return;
        }

        var auth = (SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex);
        var usesPassword = auth is SshAuthMethod.Password or SshAuthMethod.KeyboardInteractive;
        var fromKeePass = usesPassword && PasswordSourceBox.SelectedIndex == (int)PasswordSource.KeePassXc;

        KeyFilePanel.Visibility = auth == SshAuthMethod.PublicKey ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility = auth == SshAuthMethod.Agent ? Visibility.Visible : Visibility.Collapsed;
        PasswordSourcePanel.Visibility = usesPassword ? Visibility.Visible : Visibility.Collapsed;
        KeePassPanel.Visibility = fromKeePass ? Visibility.Visible : Visibility.Collapsed;
        SecretPanel.Visibility = auth == SshAuthMethod.PublicKey || (usesPassword && !fromKeePass) ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Text = auth == SshAuthMethod.PublicKey
            ? "Key passphrase (stored with DPAPI, blank keeps the existing one)"
            : "Password (stored with DPAPI, blank keeps the existing one; none saved means ask)";

        AuthNote.Text = auth switch
        {
            SshAuthMethod.Agent => "Keys stay in the agent; BaudHoundTerm only asks it to sign. KeePassXC adds its keys to Pageant or the " +
                                   "OpenSSH agent when the database unlocks (KeePassXC: Settings > SSH Agent).",
            _ when fromKeePass => "Nothing is saved here. The user name may be left blank: it then comes from the entry. " +
                                  "KeePassXC needs Browser Integration on; it asks once to allow BaudHoundTerm.",
            _ => ""
        };
    }

    private void OnListAgentKeys(object sender, RoutedEventArgs e)
    {
        var keys = Quarterdeck.Core.Security.Agent.SshAgentClient.ListIdentities(out var problems);
        var lines = keys.Select(k => $"{k.Comment}  ({k.KeyType}, {k.Fingerprint}, from {k.Source})").ToList();
        lines.AddRange(problems);
        AuthNote.Text = lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : "No agent is running. Start Pageant or the OpenSSH Authentication Agent service, then unlock KeePassXC.";
    }

    private async void OnTestKeePass(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        AuthNote.Text = "Asking KeePassXC (it may want you to unlock it or allow access)...";
        try
        {
            var port = int.TryParse(PortBox.Text, out var p) ? p : 22;
            var login = await Task.Run(() => KeePassXcPasswords.LookupAsync(
                _vault, KeePassEntryBox.Text, HostBox.Text.Trim(), port, UserBox.Text.Trim()));
            AuthNote.Text = $"Found \"{login.Name}\", user {(login.Login.Length > 0 ? login.Login : "(none)")}, " +
                            $"password of {login.Password.Length} characters. UUID {login.Uuid}.";
        }
        catch (Exception ex) when (ex is KeePassXcException or IOException or TimeoutException or InvalidOperationException)
        {
            AuthNote.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>Rebuild the port list on open: USB adapters appear and vanish.</summary>
    private void OnSerialPortsDropDown(object sender, EventArgs e)
    {
        if (sender is not ComboBox box)
        {
            return;
        }

        var typed = box.Text;
        box.Items.Clear();

        foreach (var port in System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p.Length))
        {
            box.Items.Add(port);
        }

        box.Text = typed;
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

    private void OnAddCommand(object sender, RoutedEventArgs e) =>
        _commands.Add(new CommandShortcut { Label = "New command", Text = "" });

    private void OnRemoveCommand(object sender, RoutedEventArgs e)
    {
        if (CommandGrid.SelectedItem is CommandShortcut command)
        {
            _commands.Remove(command);
        }
    }

    private void OnDefaultCommands(object sender, RoutedEventArgs e)
    {
        foreach (var command in CommandShortcut.Defaults())
        {
            // Keep whatever the user already has; only fill in the missing defaults.
            if (!_commands.Any(c => string.Equals(c.Text, command.Text, StringComparison.OrdinalIgnoreCase)))
            {
                _commands.Add(command);
            }
        }
    }

    private void OnTabColorChanged(object sender, TextChangedEventArgs e)
    {
        if (ColourSwatch is null)
        {
            return;
        }

        try
        {
            ColourSwatch.Fill = string.IsNullOrWhiteSpace(TabColorBox.Text)
                ? System.Windows.Media.Brushes.Transparent
                : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                    .ConvertFromString(TabColorBox.Text.Trim())!;
        }
        catch (FormatException)
        {
            // Half-typed colours are normal; show nothing rather than complain on each keystroke.
            ColourSwatch.Fill = System.Windows.Media.Brushes.Transparent;
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

        // A blank name takes the host (or port): in a host group that reads as just the badge
        // and target, which is all a second protocol on the same machine needs.
        var protocol = (SessionProtocol)Math.Max(0, ProtocolBox.SelectedIndex);
        var fallback = (protocol switch
        {
            SessionProtocol.Ssh => HostBox.Text,
            SessionProtocol.Telnet => TelnetHostBox.Text,
            SessionProtocol.Vnc => VncHostBox.Text,
            SessionProtocol.Rtsp => RtspHostBox.Text,
            SessionProtocol.Serial => SerialPortBox.Text,
            _ => "Local shell"
        }).Trim();
        d.Name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? (fallback.Length > 0 ? fallback : "Untitled")
            : NameBox.Text.Trim();
        d.Folder = FolderBox.Text.Trim().Trim('/');
        d.Protocol = (SessionProtocol)Math.Max(0, ProtocolBox.SelectedIndex);

        d.Host = HostBox.Text.Trim();
        d.Port = ParseInt(PortBox.Text, 22);
        d.Username = UserBox.Text.Trim();
        d.AuthMethod = (SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex);
        d.KeyPath = KeyBox.Text.Trim();
        d.PasswordSource = (PasswordSource)Math.Max(0, PasswordSourceBox.SelectedIndex);
        d.KeePassEntry = KeePassEntryBox.Text.Trim();
        d.AgentKeyFilter = AgentFilterBox.Text.Trim();

        d.SerialPort = SerialPortBox.Text.Trim();
        d.BaudRate = ParseInt(BaudBox.Text, 115200);
        d.DataBits = ParseInt(DataBitsBox.Text, 8);
        d.Parity = string.IsNullOrWhiteSpace(ParityBox.Text) ? "None" : ParityBox.Text;
        d.StopBits = string.IsNullOrWhiteSpace(StopBitsBox.Text) ? "One" : StopBitsBox.Text;
        d.ShellCommand = ShellBox.Text.Trim();

        // Telnet keeps its host and port in the same fields as SSH, so only copy them
        // across when telnet is the protocol actually selected.
        if (d.Protocol == SessionProtocol.Telnet)
        {
            d.Host = TelnetHostBox.Text.Trim();
            d.Port = ParseInt(TelnetPortBox.Text, 23);
            d.LineMode = TelnetLineModeBox.IsChecked == true;
            d.UseTls = TelnetTlsBox.IsChecked == true;
        }

        if (d.Protocol == SessionProtocol.Vnc)
        {
            d.Host = VncHostBox.Text.Trim();
            d.Port = ParseInt(VncPortBox.Text, 5900);
            d.ViewOnly = VncViewOnlyBox.IsChecked == true;

            if (!string.IsNullOrEmpty(VncSecretBox.Password))
            {
                _vault.Set(d.Id, VncSecretBox.Password);
            }
        }

        if (d.Protocol == SessionProtocol.Rtsp)
        {
            d.Host = RtspHostBox.Text.Trim();
            d.Port = ParseInt(RtspPortBox.Text, 554);
            d.RtspPath = RtspPathBox.Text.Trim();
            d.Username = RtspUserBox.Text.Trim();
            d.RtspTcp = RtspTcpBox.IsChecked == true;
            d.LowLatency = RtspLowLatencyBox.IsChecked == true;
            d.OnvifPort = ParseInt(RtspOnvifPortBox.Text, 80);

            if (!string.IsNullOrEmpty(RtspSecretBox.Password))
            {
                _vault.Set(d.Id, RtspSecretBox.Password);
            }
        }

        d.Theme = ThemeBox.Text;
        d.FontFamily = FontBox.Text.Trim();
        d.FontSize = ParseInt(FontSizeBox.Text, 13);
        d.ScrollbackLines = ParseInt(ScrollbackBox.Text, 10000);
        d.TabColor = TabColorBox.Text.Trim();

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
        d.Commands = _commands.ToList();

        // A blank secret box means "leave the stored secret alone", so an edit never wipes it.
        // SSH only: the box keeps its text when the protocol is switched, and VNC and RTSP keep
        // their password in the same vault slot, which the stale SSH text used to overwrite.
        if (d.Protocol == SessionProtocol.Ssh
            && !string.IsNullOrEmpty(SecretBox.Password) && SecretPanel.Visibility == Visibility.Visible)
        {
            var key = d.AuthMethod == SshAuthMethod.PublicKey ? "passphrase" : "password";
            _vault.Set(d.Id, SecretBox.Password, key);
        }

        DialogResult = true;
    }
}
