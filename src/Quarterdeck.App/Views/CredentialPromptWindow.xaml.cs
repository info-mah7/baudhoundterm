using System.Windows;

namespace Quarterdeck.App.Views;

/// <summary>
/// Asks who is connecting. Quick connect takes only an address, so the username lives here
/// rather than in the address box.
/// </summary>
public partial class CredentialPromptWindow : Window
{
    public CredentialPromptWindow(string target, string suggestedUser)
    {
        InitializeComponent();

        TargetText.Text = $"Connecting to {target}";
        UserBox.Text = suggestedUser;

        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(UserBox.Text))
            {
                UserBox.Focus();
            }
            else
            {
                SecretBox.Focus();
            }
        };
    }

    public string Username { get; private set; } = "";

    public string Password { get; private set; } = "";

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            UserBox.Focus();
            return;
        }

        Username = UserBox.Text.Trim();
        Password = SecretBox.Password;
        DialogResult = true;
    }
}
