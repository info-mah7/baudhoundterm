using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Kvm;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.KeePassXc;
using System.IO;
using System.Net.Http;
using System.Windows.Media;

namespace Quarterdeck.App.Views;

public partial class SessionEditorWindow : Window
{
    private readonly CredentialVault _vault;
    private readonly ObservableCollection<AutoLoginStep> _steps;
    private readonly ObservableCollection<HighlightRule> _highlights;
    private readonly ObservableCollection<CommandShortcut> _commands;
    private readonly ObservableCollection<TmuxSessionEntry> _tmuxSessions;

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
        // Copies: Cancel must leave the shortcut's own list as it was.
        // Entries from the Claude-tick test builds show as the command they stood for.
        _tmuxSessions = new ObservableCollection<TmuxSessionEntry>(definition.TmuxSessionList().Select(t =>
            new TmuxSessionEntry { Name = t.Name, OpenOnConnect = t.OpenOnConnect, Folder = t.Folder, Command = t.StartCommand }));
        TmuxGrid.ItemsSource = _tmuxSessions;

        Load();
    }

    public SessionDefinition Definition { get; }

    private void Load()
    {
        var d = Definition;

        NameBox.Text = d.Name;
        FolderBox.Text = d.Folder;
        TagsBox.Text = string.Join(", ", d.Tags);
        HideAddressBox.IsChecked = d.HideAddress;
        ProtocolBox.SelectedIndex = (int)d.Protocol;

        HostBox.Text = d.Host;
        // Host and port are shared fields; a protocol's box shows its own default until it is the one in use.
        PortBox.Text = (d.Protocol == SessionProtocol.Ssh ? d.Port : 22).ToString(CultureInfo.InvariantCulture);
        UserBox.Text = d.Username;
        AuthBox.SelectedIndex = (int)d.AuthMethod;
        KeyBox.Text = d.KeyPath;
        PasswordSourceBox.SelectedIndex = (int)d.PasswordSource;
        KeePassEntryBox.Text = d.KeePassEntry;
        AgentFilterBox.Text = d.AgentKeyFilter;
        TmuxBox.IsChecked = d.Tmux;
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
        TelnetPortBox.Text = (d.Protocol == SessionProtocol.Telnet ? d.Port : 23).ToString(CultureInfo.InvariantCulture);
        TelnetLineModeBox.IsChecked = d.LineMode;
        TelnetTlsBox.IsChecked = d.UseTls;
        VncHostBox.Text = d.Host;
        VncPortBox.Text = (d.Protocol == SessionProtocol.Vnc ? d.Port : 5900).ToString(CultureInfo.InvariantCulture);
        VncViewOnlyBox.IsChecked = d.ViewOnly;
        RtspHostBox.Text = d.Host;
        RtspPortBox.Text = (d.Protocol == SessionProtocol.Rtsp ? d.Port : 554).ToString(CultureInfo.InvariantCulture);
        RdpHostBox.Text = d.Host;
        RdpPortBox.Text = (d.Protocol == SessionProtocol.Rdp ? d.Port : 3389).ToString(CultureInfo.InvariantCulture);
        RdpUserBox.Text = d.Username;
        RdpDomainBox.Text = d.RdpDomain;
        RdpPasswordSourceBox.SelectedIndex = d.PasswordSource == PasswordSource.KeePassXc ? 1 : 0;
        RdpKeePassEntryBox.Text = d.KeePassEntry;
        RdpGatewayBox.Text = d.RdpGateway;
        RdpClipboardBox.IsChecked = d.RdpClipboard;
        RdpDrivesBox.IsChecked = d.RdpDrives;
        RdpAdminBox.IsChecked = d.RdpAdmin;
        KvmHostBox.Text = d.Host;
        WebUrlBox.Text = d.WebUrl;
        WebAllowChangesBox.IsChecked = d.WebAllowChanges;
        ShowWebCertificate();
        KvmPortBox.Text = (d.KvmPort > 0 ? d.KvmPort : 9200).ToString(CultureInfo.InvariantCulture);
        KvmTlsBox.IsChecked = d.KvmTls;
        KvmVideoUrlBox.Text = d.KvmVideoUrl;
        KvmVideoUserBox.Text = d.KvmVideoUser;
        RtspPathBox.Text = d.RtspPath;
        RtspUserBox.Text = d.Username;
        RtspTcpBox.IsChecked = d.RtspTcp;
        RtspLowLatencyBox.IsChecked = d.LowLatency;
        RtspOnvifPortBox.Text = d.OnvifPort.ToString(CultureInfo.InvariantCulture);

        ThemeBox.Text = d.Theme;
        FontBox.Text = d.FontFamily;
        FontSizeBox.Text = d.FontSize.ToString(CultureInfo.InvariantCulture);
        ScrollbackBox.Text = d.ScrollbackLines.ToString(CultureInfo.InvariantCulture);
        WheelArrowsBox.IsChecked = d.WheelSendsArrows;
        RemoteClipboardBox.IsChecked = d.RemoteClipboardWrite;
        TabColorBox.Text = d.TabColor;

        AuditBox.IsChecked = d.Audit.Enabled;
        AuditFolderBox.Text = d.Audit.CopyFolder;
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
        RdpPanel.Visibility = protocol == SessionProtocol.Rdp ? Visibility.Visible : Visibility.Collapsed;
        KvmPanel.Visibility = protocol == SessionProtocol.Kvm ? Visibility.Visible : Visibility.Collapsed;
        WebPanel.Visibility = protocol == SessionProtocol.Web ? Visibility.Visible : Visibility.Collapsed;

        // Only the tabs that mean something for this protocol. Commands, Logging, Auto-login,
        // Highlight and Reconnect are all about a terminal's text stream, which only ssh, serial,
        // telnet and local shells have. A desktop or camera keeps just Connection and
        // the tab colour; its terminal appearance (font, theme, scrollback) is hidden too.
        var terminal = protocol is SessionProtocol.Ssh or SessionProtocol.Serial
                               or SessionProtocol.Telnet or SessionProtocol.LocalShell;

        TerminalAppearancePanel.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tab in new[] { CommandsTab, LoggingTab, AutoLoginTab, HighlightTab, ReconnectTab })
        {
            tab.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        }

        // If the tab now hidden was the one on screen, fall back to Connection so the dialog does
        // not show a blank body.
        if (EditorTabs.SelectedItem is TabItem { Visibility: Visibility.Collapsed })
        {
            EditorTabs.SelectedIndex = 0;
        }
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
            ? "Key passphrase (encrypted on this PC; blank keeps the existing one)"
            : "Password (encrypted on this PC; blank keeps the existing one, none saved means ask)";

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

    private void OnAddTmuxSession(object sender, RoutedEventArgs e)
    {
        var n = 2;
        while (_tmuxSessions.Any(t => t.Name == $"bht-{n}"))
        {
            n++;
        }

        var entry = new TmuxSessionEntry { Name = $"bht-{n}", OpenOnConnect = false };
        _tmuxSessions.Add(entry);
        TmuxGrid.SelectedItem = entry;
        TmuxGrid.CurrentCell = new DataGridCellInfo(entry, TmuxGrid.Columns[0]);
        TmuxGrid.BeginEdit();
    }

    private void ShowWebCertificate()
    {
        var home = BrowserView.Home(WebUrlBox.Text);
        var known = home is null ? null : Quarterdeck.Core.Security.WebCertificates.Fingerprint($"{home.Host}:{home.Port}");
        WebCertText.Text = known is null ? "No certificate remembered for this address." : "Trusted certificate, SHA-256 " + known;
    }

    private void OnWebUrlChanged(object sender, RoutedEventArgs e) => ShowWebCertificate();

    private void OnWebForgetCert(object sender, RoutedEventArgs e)
    {
        if (BrowserView.Home(WebUrlBox.Text) is { } home)
        {
            Quarterdeck.Core.Security.WebCertificates.Forget($"{home.Host}:{home.Port}");
        }

        ShowWebCertificate();
    }

    /// <summary>The presets as a menu under the button; a pick fills the selected session's start command.</summary>
    private void OnTmuxPresets(object sender, RoutedEventArgs e)
    {
        TmuxGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var target = TmuxGrid.SelectedItem as TmuxSessionEntry ?? _tmuxSessions.FirstOrDefault();
        if (target is null || sender is not FrameworkElement anchor)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, make) in Quarterdeck.Core.Terminal.TmuxStart.Presets)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                var index = _tmuxSessions.IndexOf(target);
                if (index >= 0)
                {
                    // Replaced, not changed in place: the grid has no change notice to redraw a cell from.
                    var c = target.Copy();
                    c.Command = make();
                    _tmuxSessions[index] = c;
                    TmuxGrid.SelectedItem = c;
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OnRemoveTmuxSession(object sender, RoutedEventArgs e)
    {
        if (TmuxGrid.SelectedItem is TmuxSessionEntry entry && _tmuxSessions.Count > 1)
        {
            _tmuxSessions.Remove(entry);
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

    // The order here is the order on the terminal's right-click menu.
    private void OnMoveCommandUp(object sender, RoutedEventArgs e) => MoveCommand(-1);

    private void OnMoveCommandDown(object sender, RoutedEventArgs e) => MoveCommand(1);

    private void MoveCommand(int by)
    {
        if (CommandGrid.SelectedItem is not CommandShortcut command)
        {
            return;
        }

        CommandGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var from = _commands.IndexOf(command);
        var to = from + by;
        if (from < 0 || to < 0 || to >= _commands.Count)
        {
            return;
        }

        _commands.Move(from, to);
        CommandGrid.SelectedItem = command;
        CommandGrid.ScrollIntoView(command);
    }

    private void OnCommandGridKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control
            || e.Key is not (System.Windows.Input.Key.Up or System.Windows.Input.Key.Down))
        {
            return;
        }

        e.Handled = true;
        MoveCommand(e.Key == System.Windows.Input.Key.Up ? -1 : 1);
    }

    // Drag a row by its header (the bar at its left edge), so a press in a cell still edits it.
    private CommandShortcut? _dragCommand;
    private Point _dragFrom;

    private void OnCommandDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragCommand = Ancestor<System.Windows.Controls.Primitives.DataGridRowHeader>(e.OriginalSource as DependencyObject) is not null
            ? Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as CommandShortcut
            : null;
        _dragFrom = e.GetPosition(CommandGrid);
    }

    private void OnCommandDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragCommand is not { } command || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(CommandGrid) - _dragFrom;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCommand = null;
        CommandGrid.CommitEdit(DataGridEditingUnit.Row, true);
        DragDrop.DoDragDrop(CommandGrid, new DataObject(typeof(CommandShortcut), command), DragDropEffects.Move);
    }

    private void OnCommandDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CommandShortcut)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCommandDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CommandShortcut)) is not CommandShortcut command)
        {
            return;
        }

        var from = _commands.IndexOf(command);
        // Dropped on a row: take its place. Below the last row: go to the end.
        var target = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as CommandShortcut;
        var to = target is null ? _commands.Count - 1 : _commands.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to)
        {
            _commands.Move(from, to);
        }

        CommandGrid.SelectedItem = command;
        e.Handled = true;
    }

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
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

    /// <summary>
    /// Pairs with the board: signs in to its web interface with the login in the panel, trades
    /// that session for the daemon key, and saves it. One login instead of copying the key by
    /// hand; the picture's password is saved at the same time, since it is the same login.
    /// </summary>
    private async void OnKvmPair(object sender, RoutedEventArgs e)
    {
        var host = KvmHostBox.Text.Trim();
        if (host.Length == 0)
        {
            PairStatus("Enter the KVM board address first.", false);
            return;
        }

        var scheme = KvmTlsBox.IsChecked == true ? "https" : "http";
        var daemon = $"{scheme}://{host}:{ParseInt(KvmPortBox.Text, 9200)}";
        if (KvmClient.Refuse(daemon) is { } why)
        {
            PairStatus(why, false);
            return;
        }

        var video = KvmVideoUrlBox.Text.Trim();
        video = video.Length > 0 && video != "-" ? video : $"{scheme}://{host}";
        var user = KvmVideoUserBox.Text.Trim().Length > 0 ? KvmVideoUserBox.Text.Trim() : "admin";

        // The web interface password: the box, or the saved one, or asked. Whatever is used for
        // pairing is also what the picture needs, so it is saved too.
        var password = KvmVideoSecretBox.Password;
        var fromBox = password.Length > 0;
        if (password.Length == 0)
        {
            password = _vault.Get(Definition.Id) ?? "";
        }

        if (password.Length == 0)
        {
            var dialog = new SecretPromptWindow($"{user} on {host}", "web interface password") { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Secret.Length == 0)
            {
                PairStatus("Pairing needs the web interface password.", false);
                return;
            }

            password = dialog.Secret;
        }

        KvmPairButton.IsEnabled = false;
        PairStatus("Signing in to the board and fetching the key...", true);
        try
        {
            using var vendor = new KvmVendorClient(video, user, password);
            await vendor.SignInAsync();
            var key = await KvmClient.PairAsync(daemon, vendor.Token);

            _vault.Set(Definition.Id, key, "kvmkey");
            // Save the web password used, so the picture works later without asking again.
            if (fromBox || _vault.Get(Definition.Id) is null or "")
            {
                _vault.Set(Definition.Id, password);
            }

            KvmKeyBox.Clear();   // the key is in the vault now; nothing to type
            PairStatus("Paired. The daemon key is set and saved on this PC.", true);
        }
        catch (Exception ex) when (ex is KvmException or HttpRequestException or IOException)
        {
            PairStatus(ex.Message, false);
        }
        finally
        {
            KvmPairButton.IsEnabled = true;
        }
    }

    private void PairStatus(string text, bool ok)
    {
        KvmPairStatus.Text = text;
        KvmPairStatus.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x98, 0xC3, 0x79) : Color.FromRgb(0xE0, 0x6C, 0x75));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if ((SessionProtocol)Math.Max(0, ProtocolBox.SelectedIndex) == SessionProtocol.Web && BrowserView.Home(WebUrlBox.Text) is null)
        {
            MessageBox.Show(this, "The address must be an http or https address, or a host name (https is then used).",
                "Shortcut properties", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TmuxGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_tmuxSessions.FirstOrDefault(t => !Quarterdeck.Core.Terminal.SessionController.TmuxFolderOk(t.Folder.Trim())) is { } badFolder)
        {
            MessageBox.Show(this, $"The folder of tmux session \"{badFolder.Name}\" has characters a shell would act on. " +
                                  "Use letters, digits, spaces and / . _ - ~ + @ , only.", "Shortcut properties",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_tmuxSessions.FirstOrDefault(t => !Quarterdeck.Core.Terminal.TmuxStart.Ok(t.Command)) is { } badCommand)
        {
            MessageBox.Show(this, $"The start command of tmux session \"{badCommand.Name}\" has a single quote, a backslash or a " +
                                  "line break, which cannot be passed to the host safely. Put the command in a script and start that.",
                "Shortcut properties", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
            SessionProtocol.Rdp => RdpHostBox.Text,
            SessionProtocol.Kvm => KvmHostBox.Text,
            SessionProtocol.Web => BrowserView.Home(WebUrlBox.Text)?.Host ?? WebUrlBox.Text,
            SessionProtocol.Serial => SerialPortBox.Text,
            _ => "Local shell"
        }).Trim();
        d.Name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? (fallback.Length > 0 ? fallback : "Untitled")
            : NameBox.Text.Trim();
        d.Folder = FolderBox.Text.Trim().Trim('/');
        d.Tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        d.HideAddress = HideAddressBox.IsChecked == true;
        d.Protocol = (SessionProtocol)Math.Max(0, ProtocolBox.SelectedIndex);

        d.Host = HostBox.Text.Trim();
        d.Port = ParseInt(PortBox.Text, 22);
        d.Username = UserBox.Text.Trim();
        d.AuthMethod = (SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex);
        d.KeyPath = KeyBox.Text.Trim();
        d.PasswordSource = (PasswordSource)Math.Max(0, PasswordSourceBox.SelectedIndex);
        d.KeePassEntry = KeePassEntryBox.Text.Trim();
        d.AgentKeyFilter = AgentFilterBox.Text.Trim();
        d.Tmux = TmuxBox.IsChecked == true;
        // Names as tmux will take them, blanks and repeats dropped, at least one. The first one is
        // also written to TmuxSession, which builds before 72 read.
        TmuxGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var sessions = _tmuxSessions
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => new TmuxSessionEntry
            {
                Name = Quarterdeck.Core.Terminal.SessionController.TmuxName(t.Name.Trim()), OpenOnConnect = t.OpenOnConnect,
                Folder = t.Folder.Trim(), Command = t.Command.Trim()
            })
            .GroupBy(t => t.Name).Select(g => g.First()).ToList();
        if (sessions.Count == 0)
        {
            sessions.Add(new TmuxSessionEntry { Name = "bht", OpenOnConnect = true });
        }

        d.TmuxSessions = sessions;
        d.TmuxSession = sessions[0].Name;

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

        if (d.Protocol == SessionProtocol.Rdp)
        {
            d.Host = RdpHostBox.Text.Trim();
            d.Port = ParseInt(RdpPortBox.Text, 3389);
            d.Username = RdpUserBox.Text.Trim();
            d.RdpDomain = RdpDomainBox.Text.Trim();
            d.PasswordSource = RdpPasswordSourceBox.SelectedIndex == 1 ? PasswordSource.KeePassXc : PasswordSource.Saved;
            d.KeePassEntry = RdpKeePassEntryBox.Text.Trim();
            d.RdpGateway = RdpGatewayBox.Text.Trim();
            d.RdpClipboard = RdpClipboardBox.IsChecked == true;
            d.RdpDrives = RdpDrivesBox.IsChecked == true;
            d.RdpAdmin = RdpAdminBox.IsChecked == true;

            if (!string.IsNullOrEmpty(RdpSecretBox.Password))
            {
                _vault.Set(d.Id, RdpSecretBox.Password);
            }
        }

        if (d.Protocol == SessionProtocol.Kvm)
        {
            d.Host = KvmHostBox.Text.Trim();
            d.KvmPort = ParseInt(KvmPortBox.Text, 9200);
            d.KvmTls = KvmTlsBox.IsChecked == true;
            d.KvmVideoUrl = KvmVideoUrlBox.Text.Trim();
            d.KvmVideoUser = KvmVideoUserBox.Text.Trim();

            // Two secrets on one board: the daemon's bearer key, and the web interface's own
            // password for the picture. The second is the session's "password", as elsewhere.
            if (!string.IsNullOrEmpty(KvmKeyBox.Password))
            {
                _vault.Set(d.Id, KvmKeyBox.Password, "kvmkey");
            }

            if (!string.IsNullOrEmpty(KvmVideoSecretBox.Password))
            {
                _vault.Set(d.Id, KvmVideoSecretBox.Password);
            }
        }

        if (d.Protocol == SessionProtocol.Web)
        {
            d.WebUrl = WebUrlBox.Text.Trim();
            d.WebAllowChanges = WebAllowChangesBox.IsChecked == true;
            // Host and port as well, so the page groups with the device's SSH and other shortcuts.
            if (BrowserView.Home(d.WebUrl) is { } home)
            {
                d.Host = home.Host;
                d.Port = home.Port;
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
        d.WheelSendsArrows = WheelArrowsBox.IsChecked == true;
        d.RemoteClipboardWrite = RemoteClipboardBox.IsChecked == true;
        d.TabColor = TabColorBox.Text.Trim();

        d.Audit.Enabled = AuditBox.IsChecked == true;
        d.Audit.CopyFolder = AuditFolderBox.Text.Trim();
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
