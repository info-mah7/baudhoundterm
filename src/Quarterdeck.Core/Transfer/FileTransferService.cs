using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.Agent;
using Quarterdeck.Core.Security.KeePassXc;
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
    private Terminal.SshJump? _jump;

    public FileTransferService(SessionDefinition definition, CredentialVault vault)
    {
        _definition = definition;
        _vault = vault;
    }

    // A KeePassXC login fetched once serves both the SFTP and SCP connections of this service.
    private (string User, string Password)? _fetched;

    // One connect at a time: the window lists a folder on open while the user may already be
    // clicking Download, and two racing getters each disposed the other's half-built client
    // and raised a second host-key or KeePassXC prompt.
    private readonly object _connectGate = new();

    /// <summary>Runs on a pool thread (callers wrap it in Task.Run), so a KeePassXC wait blocks nothing visible.</summary>
    private ConnectionInfo BuildConnectionInfo()
    {
        var (host, port) = Endpoint();
        AuthenticationMethod method;
        var username = _definition.Username;

        if (_definition.AuthMethod == SshAuthMethod.PublicKey)
        {
            var passphrase = _vault.Get(_definition.Id, "passphrase");
            var key = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(_definition.KeyPath)
                : new PrivateKeyFile(_definition.KeyPath, passphrase);
            method = new PrivateKeyAuthenticationMethod(username, key);
        }
        else if (_definition.AuthMethod == SshAuthMethod.Agent)
        {
            method = new PrivateKeyAuthenticationMethod(username, AgentKeySource.FromAgents(_definition.AgentKeyFilter));
        }
        else
        {
            var password = ResolvePassword(ref username);

            if (_definition.AuthMethod == SshAuthMethod.KeyboardInteractive)
            {
                // Servers that only allow keyboard-interactive refuse plain password auth, which
                // is all transfers used to offer. Answer the password prompt; there is no one to
                // ask for anything else (an OTP), so those get an empty reply and fail cleanly.
                var interactive = new KeyboardInteractiveAuthenticationMethod(username);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                    {
                        // The same test as the terminal: "New password" or an OTP gets nothing.
                        prompt.Response = Terminal.SshTerminalSession.IsPasswordPrompt(prompt.Request) ? password : string.Empty;
                    }
                };

                return new ConnectionInfo(host, port, username,
                    interactive, new PasswordAuthenticationMethod(username, password));
            }

            method = new PasswordAuthenticationMethod(username, password);
        }

        return new ConnectionInfo(host, port, username, method);
    }

    /// <summary>Where to connect: the host, or the jump host's forward to it. One jump serves SFTP and SCP.</summary>
    private (string Host, int Port) Endpoint()
    {
        if (_definition.JumpHost.Trim().Length == 0)
        {
            return (_definition.Host, _definition.Port);
        }

        if (_jump is null || !_jump.IsConnected)
        {
            _jump?.Dispose();
            _jump = Terminal.SshJump.OpenAsync(_definition, _definition.Host, _definition.Port, _vault, null, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        return ("127.0.0.1", _jump.LocalPort);
    }

    /// <summary>The password from KeePassXC (fetched once) or the vault; fills in the user name from the entry when the shortcut has none.</summary>
    private string ResolvePassword(ref string username)
    {
        if (_definition.PasswordSource == PasswordSource.KeePassXc)
        {
            if (_fetched is null)
            {
                var login = KeePassXcPasswords.LookupAsync(_vault, _definition.KeePassEntry, _definition.Host,
                    _definition.Port, username).GetAwaiter().GetResult();
                _fetched = (string.IsNullOrEmpty(username) ? login.Login : username, login.Password);
            }

            username = _fetched.Value.User;
            return _fetched.Value.Password;
        }

        return _vault.Get(_definition.Id)
               ?? throw new InvalidOperationException(
                   $"No stored password for {_definition.Name}; transfers need one.");
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
            lock (_connectGate)
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
    }

    private ScpClient Scp
    {
        get
        {
            lock (_connectGate)
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
            // Into a .part beside the target: a transfer that fails must not leave an empty
            // file under the name of the one that was there.
            var temp = localPath + ".part";
            try
            {
                using (var local = File.Create(temp))
                {
                    Sftp.DownloadFile(remotePath, local,
                        downloaded => progress?.Report(
                            new TransferProgress(remotePath, (long)downloaded, size)));
                }

                File.Move(temp, localPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
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
        _jump?.Dispose();
    }
}
