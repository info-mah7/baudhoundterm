using System.Windows;

namespace Quarterdeck.App.Views;

public partial class SecretPromptWindow : Window
{
    public SecretPromptWindow(string sessionName, string prompt)
    {
        InitializeComponent();
        PromptText.Text = $"{sessionName}: {prompt}";
        Loaded += (_, _) => SecretBox.Focus();
    }

    public string Secret { get; private set; } = "";

    public bool Remember => RememberBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Secret = SecretBox.Password;
        DialogResult = true;
    }
}
