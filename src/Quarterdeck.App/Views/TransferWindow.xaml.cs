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
    private readonly IRemoteFiles _files;

    // SCP exists only beside SFTP; for any other kind of remote it is hidden.
    private readonly FileTransferService? _sftp;
    private string _path;

    /// <summary>SFTP (and SCP) on an SSH session's server.</summary>
    public TransferWindow(SessionDefinition definition, CredentialVault vault)
        : this(new FileTransferService(definition, vault), $"Files - {definition.Name} (SFTP)")
    {
    }

    /// <summary>Any remote file system, e.g. TightVNC's file transfer beside a VNC tab.</summary>
    public TransferWindow(IRemoteFiles files, string title)
    {
        InitializeComponent();

        Title = title;
        _files = files;
        _sftp = files as FileTransferService;
        _path = files.Home;
        UseScpBox.Visibility = _sftp is null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _files.Dispose();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = $"Listing {_path}...";
            var entries = await _files.ListAsync(_path);
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
        _path = _files.Parent(_path);

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
            () => UseScpBox.IsChecked == true && _sftp is not null
                ? _sftp.ScpDownloadAsync(entry.FullPath, dialog.FileName)
                : _files.DownloadAsync(entry.FullPath, dialog.FileName, ReportProgress),
            $"Downloaded {entry.Name}.");
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var remote = _files.Combine(_path, Path.GetFileName(dialog.FileName));

        await RunTransferAsync(
            () => UseScpBox.IsChecked == true && _sftp is not null
                ? _sftp.ScpUploadAsync(dialog.FileName, remote)
                : _files.UploadAsync(dialog.FileName, remote, ReportProgress),
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
