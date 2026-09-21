using System.Windows;

namespace Quarterdeck.App.Views;

/// <summary>A one-line text prompt, for folder names and paths.</summary>
public partial class InputPromptWindow : Window
{
    public InputPromptWindow(string prompt, string initial = "", string hint = "")
    {
        InitializeComponent();

        PromptText.Text = prompt;
        ValueBox.Text = initial;
        HintText.Text = hint;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value { get; private set; } = "";

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text.Trim();
        DialogResult = true;
    }
}
