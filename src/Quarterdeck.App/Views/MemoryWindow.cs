using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quarterdeck.Core.Agent;

namespace Quarterdeck.App.Views;

/// <summary>
/// The agent's memory for one host (or shortcut), as the plain text it is: read it, correct a
/// note, delete one, or clear the lot. A conversation already open keeps what it started with.
/// </summary>
internal sealed class MemoryWindow : Window
{
    private readonly AgentMemory _memory;
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12
    };

    public MemoryWindow(AgentMemory memory)
    {
        _memory = memory;
        Title = $"Agent memory: {memory.Subject}";
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(12) };
        var note = new TextBlock
        {
            Text = (memory.PerHost
                       ? $"Shared by every shortcut to {memory.Subject}. "
                       : "This shortcut's own notes. ") +
                   "The agent adds a note when it learns something worth keeping, and a line after each run. " +
                   "It reads them as its own past notes, never as instructions. A conversation already open keeps the notes it " +
                   "started with; New in the agent pane starts one with these.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var clear = new Button { Content = "Clear...", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Remove every note and run line" };
        var save = new Button { Content = "Save", Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
        clear.Click += (_, _) => Clear();
        save.Click += (_, _) => Save();
        buttons.Children.Add(clear);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(_text);
        Content = root;

        try
        {
            var text = memory.Read();
            _text.Text = text.Length > 0 ? text.Replace("\n", Environment.NewLine) : "(Nothing yet: the agent has not kept anything about this host.)";
            _text.IsReadOnly = text.Length == 0;
        }
        catch (IOException ex)
        {
            _text.Text = "The memory file could not be read: " + ex.Message;
            _text.IsReadOnly = true;
            save.IsEnabled = false;
        }
    }

    private void Save()
    {
        if (_text.IsReadOnly)
        {
            Close();
            return;
        }

        // Saved as typed, but a secret typed in by hand is refused like one the agent tried to keep.
        if (AgentMemory.FindSecret(_text.Text) is { } secret)
        {
            MessageBox.Show(this, $"This looks like it holds a password or key (\"{secret}\"). Memory never keeps secrets, and Sync " +
                                  "would refuse to send it. Remove the value and save again.", "Agent memory",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _memory.Write(_text.Text);
        DialogResult = true;
    }

    private void Clear()
    {
        if (MessageBox.Show(this, $"Remove everything the agent keeps about {_memory.Subject}? With Sync, the other PCs' copy is emptied too.",
                "Agent memory", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _memory.Write("");
        DialogResult = true;
    }
}
