using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Transfer;

namespace Quarterdeck.App.Views;

public partial class TransferWindow : Window
{
    private readonly FileTransferService _service;
    private string _path = ".";

    public TransferWindow(SessionDefinition definition, CredentialVault vault)
    {
        InitializeComponent();

        Title = $"File Transfer - {definition.Name}";
        _service = new FileTransferService(definition, vault);

        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _service.Dispose();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = $"Listing {_path}...";
            var entries = await _service.ListAsync(_path);
            Grid.ItemsSource = entries;
            PathBox.Text = _path;
            StatusText.Text = $"{entries.Count} items.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        _path = PathBox.Text;
        await RefreshAsync();
    }

    private async void OnUp(object sender, RoutedEventArgs e)
    {
        _path = _path is "." or "/" ? "/" : (Path.GetDirectoryName(_path.TrimEnd('/'))?.Replace('\\', '/') ?? "/");
        if (string.IsNullOrEmpty(_path))
        {
            _path = "/";
        }

        await RefreshAsync();
    }

    private async void OnRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is RemoteEntry { IsDirectory: true } entry)
        {
            _path = entry.FullPath;
            await RefreshAsync();
        }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not RemoteEntry entry || entry.IsDirectory)
        {
            StatusText.Text = "Select a file to download.";
            return;
        }

        var dialog = new SaveFileDialog { FileName = entry.Name };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunTransferAsync(
            () => UseScpBox.IsChecked == true
                ? _service.ScpDownloadAsync(entry.FullPath, dialog.FileName)
                : _service.DownloadAsync(entry.FullPath, dialog.FileName, ReportProgress),
            $"Downloaded {entry.Name}.");
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var remote = $"{_path.TrimEnd('/')}/{Path.GetFileName(dialog.FileName)}";

        await RunTransferAsync(
            () => UseScpBox.IsChecked == true
                ? _service.ScpUploadAsync(dialog.FileName, remote)
                : _service.UploadAsync(dialog.FileName, remote, ReportProgress),
            $"Uploaded to {remote}.");

        await RefreshAsync();
    }

    private async Task RunTransferAsync(Func<Task> transfer, string doneMessage)
    {
        try
        {
            Progress.Value = 0;
            await transfer();
            Progress.Value = 100;
            StatusText.Text = doneMessage;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>Progress arrives on a transfer thread; Progress&lt;T&gt; marshals it back to the UI.</summary>
    private IProgress<TransferProgress> ReportProgress =>
        new Progress<TransferProgress>(p =>
        {
            Progress.Value = p.Fraction * 100;
            StatusText.Text = $"{p.Transferred:N0} / {p.Total:N0} bytes";
        });
}
