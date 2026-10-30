using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quarterdeck.App.Views;

/// <summary>
/// A password or key for the program in an SSH tab (a coding agent): typed, or read from a file. It goes
/// to a file on the host that only the login can read; the tab gets a line naming the file, never the value.
/// </summary>
public sealed class SendSecretWindow : Window
{
    private static readonly Brush Fore = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));

    private static readonly (string Label, TimeSpan? After)[] Removals =
    [
        ("15 minutes", TimeSpan.FromMinutes(15)),
        ("1 hour", TimeSpan.FromHours(1)),
        ("8 hours", TimeSpan.FromHours(8)),
        ("Keep it (delete it yourself)", null)
    ];

    private readonly TextBox _name = new() { Text = "secret", Margin = new Thickness(0, 0, 0, 4) };
    private readonly PasswordBox _value = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _file = new() { Foreground = Grey, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _removal = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly CheckBox _note = new()
    {
        Foreground = Fore, IsChecked = true, Margin = new Thickness(0, 8, 0, 0),
        Content = "Type a line at the prompt saying where it is (you press Enter)"
    };
    private readonly TextBlock _problem = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private byte[]? _fromFile;

    public SendSecretWindow(string tabName)
    {
        Title = $"Send a secret to the program in {tabName}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22));

        foreach (var (label, _) in Removals)
        {
            _removal.Items.Add(label);
        }

        _removal.SelectedIndex = 1;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Foreground = Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "The value goes to a file on the host that only this login can read, over a channel of its own: " +
                   "it never passes through the terminal, its log or the program's conversation. The program is told the " +
                   "file's path, not the value. If it reads the file into its conversation, the value goes to its model provider."
        });
        panel.Children.Add(Label("Name (the file is named after it)"));
        panel.Children.Add(_name);
        panel.Children.Add(Label("Password or token"));
        panel.Children.Add(_value);
        var browse = Btn("Use a file instead (a key)...", Browse);
        browse.HorizontalAlignment = HorizontalAlignment.Left;
        browse.Margin = new Thickness(0, 2, 0, 2);
        panel.Children.Add(browse);
        panel.Children.Add(_file);
        panel.Children.Add(Label("Delete it from the host after"));
        panel.Children.Add(_removal);
        panel.Children.Add(_note);
        panel.Children.Add(_problem);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var send = Btn("Send", Send);
        send.IsDefault = true;
        buttons.Children.Add(send);
        var cancel = Btn("Cancel", Close);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _value.Focus();
        Closed += (_, _) => _value.Clear();
    }

    public string SecretName => _name.Text.Trim();

    /// <summary>The bytes to send, valid after Send. The caller clears them when done.</summary>
    public byte[] Value { get; private set; } = [];

    public TimeSpan? RemoveAfter => Removals[Math.Max(0, _removal.SelectedIndex)].After;

    public bool TypeNote => _note.IsChecked == true;

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "A key or other file to send" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 1_000_000)
            {
                _problem.Text = "That file is over 1 MB; a key or token is a few KB at most.";
                return;
            }

            _fromFile = File.ReadAllBytes(dialog.FileName);
            _value.Clear();
            _value.IsEnabled = false;
            _file.Text = $"Sending {info.Name} ({_fromFile.Length:N0} bytes), not a typed value.";
            if (_name.Text.Trim() is "" or "secret")
            {
                _name.Text = info.Name;
            }

            _problem.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _problem.Text = "Could not read it: " + ex.Message;
        }
    }

    private void Send()
    {
        if (SecretName.Length == 0)
        {
            _problem.Text = "Give it a name.";
            return;
        }

        Value = _fromFile ?? System.Text.Encoding.UTF8.GetBytes(_value.Password);
        if (Value.Length == 0)
        {
            _problem.Text = "Type the value, or choose a file.";
            return;
        }

        DialogResult = true;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Fore, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2)
    };

    private static Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        b.Click += (_, _) => act();
        return b;
    }
}
