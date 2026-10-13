using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Quarterdeck.Core.Transfer;

public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Length, DateTime Modified)
{
    /// <summary>Blank for folders: a "0" size reads as an empty file.</summary>
    public string SizeText => IsDirectory ? "" : Length.ToString("N0");

    /// <summary>Blank when the server gives none (TightVNC sends no time for folders).</summary>
    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    public string DisplayName => IsDirectory ? Name + "/" : Name;
}

public sealed class TransferProgress(string file, long transferred, long total)
{
    public string File { get; } = file;
    public long Transferred { get; } = transferred;
    public long Total { get; } = total;
    public double Fraction => Total > 0 ? (double)Transferred / Total : 0;
}

/// <summary>
/// SFTP browsing plus SFTP and SCP transfers. Opens its own authenticated connection
/// rather than borrowing the shell's, so a busy transfer cannot stall typing.
/// </summary>
public sealed class FileTransferService : IRemoteFiles
{
    private readonly SessionDefinition _definition;
    private readonly CredentialVault _vault;
    private SftpClient? _sftp;
    private ScpClient? _scp;

    public FileTransferService(SessionDefinition definition, CredentialVault vault)
    {
        _definition = definition;
        _vault = vault;
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        AuthenticationMethod method;

        if (_definition.AuthMethod == SshAuthMethod.PublicKey)
        {
            var passphrase = _vault.Get(_definition.Id, "passphrase");
            var key = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(_definition.KeyPath)
                : new PrivateKeyFile(_definition.KeyPath, passphrase);
            method = new PrivateKeyAuthenticationMethod(_definition.Username, key);
        }
        else
        {
            var password = _vault.Get(_definition.Id)
                           ?? throw new InvalidOperationException(
                               $"No stored password for {_definition.Name}; transfers need one.");
            method = new PasswordAuthenticationMethod(_definition.Username, password);
        }

        return new ConnectionInfo(_definition.Host, _definition.Port, _definition.Username, method);
    }

    public string Home => ".";

    public string Parent(string path)
    {
        if (path is "." or "/")
        {
            return "/";
        }

        var p = path.TrimEnd('/');
        var cut = p.LastIndexOf('/');
        return cut <= 0 ? "/" : p[..cut];
    }

    public string Combine(string folder, string name) => $"{folder.TrimEnd('/')}/{name}";

    public SftpClient Sftp
    {
        get
        {
            if (_sftp is null || !_sftp.IsConnected)
            {
                _sftp?.Dispose();
                _sftp = new SftpClient(BuildConnectionInfo());
                KnownHosts.Connect(_sftp, _definition.Host, _definition.Port);
            }

            return _sftp;
        }
    }

    private ScpClient Scp
    {
        get
        {
            if (_scp is null || !_scp.IsConnected)
            {
                _scp?.Dispose();
                // Shell-quote remote paths: the plain constructor passes them to a remote
                // shell unescaped, which turns a filename with a backtick into command execution.
                _scp = new ScpClient(BuildConnectionInfo(), RemotePathTransformation.ShellQuote);
                KnownHosts.Connect(_scp, _definition.Host, _definition.Port);
            }

            return _scp;
        }
    }

    public Task<List<RemoteEntry>> ListAsync(string path = ".") => Task.Run(() =>
    {
        var entries = new List<RemoteEntry>();

        foreach (ISftpFile file in Sftp.ListDirectory(path))
        {
            if (file.Name is ".")
            {
                continue;
            }

            entries.Add(new RemoteEntry(
                file.Name, file.FullName, file.IsDirectory, file.Length, file.LastWriteTime));
        }

        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    public Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null) =>
        Task.Run(() =>
        {
            var size = Sftp.GetAttributes(remotePath).Size;
            using var local = File.Create(localPath);
            Sftp.DownloadFile(remotePath, local,
                downloaded => progress?.Report(
                    new TransferProgress(remotePath, (long)downloaded, size)));
        });

    public Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null) =>
        Task.Run(() =>
        {
            var size = new FileInfo(localPath).Length;
            using var local = File.OpenRead(localPath);
            Sftp.UploadFile(local, remotePath, true,
                uploaded => progress?.Report(
                    new TransferProgress(localPath, (long)uploaded, size)));
        });

    /// <summary>SCP path, for hosts whose sshd has no SFTP subsystem (network gear, mostly).</summary>
    public Task ScpUploadAsync(string localPath, string remotePath) =>
        Task.Run(() => Scp.Upload(new FileInfo(localPath), remotePath));

    public Task ScpDownloadAsync(string remotePath, string localPath) =>
        Task.Run(() => Scp.Download(remotePath, new FileInfo(localPath)));

    public void Dispose()
    {
        _sftp?.Dispose();
        _scp?.Dispose();
    }
}
