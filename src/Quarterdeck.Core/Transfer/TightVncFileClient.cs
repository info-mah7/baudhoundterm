using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Quarterdeck.Core.Transfer;

/// <summary>Browsing and moving files on some remote system: SFTP over SSH, TightVNC over VNC.</summary>
public interface IRemoteFiles : IDisposable
{
    /// <summary>Where browsing starts.</summary>
    string Home { get; }

    string Parent(string path);

    string Combine(string folder, string name);

    Task<List<RemoteEntry>> ListAsync(string path);

    Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null);

    Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null);
}

/// <summary>
/// TightVNC's file transfer (the 2.x "FTS" messages), over a second RFB connection beside the
/// screen view. Standard VNC has no file transfer; this works with TightVNC servers only, and
/// says so plainly for any other. Paths are "/" (the drive list) and "/C:/Users/...".
/// Message layouts were checked against TightVNC Server 2.x on Windows.
/// </summary>
public sealed class TightVncFileClient : IRemoteFiles
{
    private const uint CompressionSupportRequest = 0xFC000100, CompressionSupportReply = 0xFC000101;
    private const uint FileListRequest = 0xFC000102, FileListReply = 0xFC000103;
    private const uint UploadStartRequest = 0xFC000106, UploadStartReply = 0xFC000107;
    private const uint UploadDataRequest = 0xFC000108, UploadDataReply = 0xFC000109;
    private const uint UploadEndRequest = 0xFC00010A, UploadEndReply = 0xFC00010B;
    private const uint DownloadStartRequest = 0xFC00010C, DownloadStartReply = 0xFC00010D;
    private const uint DownloadDataRequest = 0xFC00010E, DownloadDataReply = 0xFC00010F, DownloadEndReply = 0xFC000110;
    private const uint MkdirRequest = 0xFC000111, MkdirReply = 0xFC000112;
    private const uint RemoveRequest = 0xFC000113, RemoveReply = 0xFC000114;
    private const uint LastRequestFailed = 0xFC000119;

    private const int Chunk = 64 * 1024;

    private readonly TcpClient _tcp = new();
    private NetworkStream _stream = null!;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TightVncFileClient()
    {
    }

    public string Home => "/";

    /// <summary>Connects, authenticates and checks the server offers file transfer.</summary>
    public static async Task<TightVncFileClient> ConnectAsync(string host, int port, string? password, CancellationToken token = default)
    {
        var client = new TightVncFileClient();
        try
        {
            await client._tcp.ConnectAsync(host, port, token);
            client._stream = client._tcp.GetStream();
            await client.HandshakeAsync(password, token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task HandshakeAsync(string? password, CancellationToken token)
    {
        var version = Encoding.ASCII.GetString(await ReadAsync(12, token));
        if (!version.StartsWith("RFB "))
        {
            throw new IOException("Not a VNC server.");
        }

        await WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), token);

        var count = (await ReadAsync(1, token))[0];
        if (count == 0)
        {
            var reason = await ReadStringAsync(token);
            throw new IOException($"The VNC server refused the connection: {reason}");
        }

        var types = await ReadAsync(count, token);
        if (!types.Contains((byte)16))
        {
            throw new NotSupportedException(
                "This VNC server does not offer TightVNC's extensions, so it has no file transfer this app can use. " +
                "TightVNC Server has it; UltraVNC, RealVNC and Windows' own servers do not speak it.");
        }

        await WriteAsync([16], token);

        // Tight security: tunnels (we take none), then authentication types.
        var tunnels = await ReadU32Async(token);
        await ReadAsync((int)tunnels * 16, token);
        if (tunnels > 0)
        {
            await WriteAsync(U32(0), token);
        }

        var authCount = await ReadU32Async(token);
        var auths = new List<uint>();
        for (var i = 0; i < authCount; i++)
        {
            var cap = await ReadAsync(16, token);
            auths.Add(BinaryPrimitives.ReadUInt32BigEndian(cap));
        }

        if (authCount > 0)
        {
            if (auths.Contains(1))
            {
                await WriteAsync(U32(1), token);   // no authentication
            }
            else if (auths.Contains(2))
            {
                await WriteAsync(U32(2), token);
                var challenge = await ReadAsync(16, token);
                await WriteAsync(VncAuthResponse(challenge, password ?? ""), token);
            }
            else
            {
                throw new NotSupportedException("The VNC server wants an authentication method this app does not support.");
            }
        }

        if (await ReadU32Async(token) != 0)
        {
            var reason = await ReadStringAsync(token);
            throw new UnauthorizedAccessException($"VNC authentication failed: {reason}. Check the password in the tab's Properties.");
        }

        await WriteAsync([1], token);   // shared: leave the screen view connected

        // ServerInit: size, pixel format, name; then the Tight capability lists.
        await ReadAsync(4 + 16, token);
        await ReadAsync((int)await ReadU32Async(token), token);
        var header = await ReadAsync(8, token);
        var serverMsgs = BinaryPrimitives.ReadUInt16BigEndian(header);
        var clientMsgs = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        var encodings = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));

        var serverCodes = new HashSet<uint>();
        for (var i = 0; i < serverMsgs; i++)
        {
            serverCodes.Add(BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(16, token)));
        }

        await ReadAsync((clientMsgs + encodings) * 16, token);

        if (!serverCodes.Contains(FileListReply))
        {
            throw new NotSupportedException("This TightVNC server has file transfer turned off.");
        }

        await RequestAsync(U32(CompressionSupportRequest), CompressionSupportReply, async t => await ReadAsync(1, t), token);
    }

    // ---------- operations ----------

    public string Parent(string path)
    {
        var p = path.TrimEnd('/');
        var cut = p.LastIndexOf('/');
        return cut <= 0 ? "/" : p[..cut];
    }

    public string Combine(string folder, string name) => folder == "/" ? "/" + name : folder.TrimEnd('/') + "/" + name;

    public async Task<List<RemoteEntry>> ListAsync(string path)
    {
        var request = Concat(U32(FileListRequest), [0], Utf8(path));
        return await RequestAsync(request, FileListReply, async t =>
        {
            var data = await ReadCompressedAsync(t);
            var entries = new List<RemoteEntry>();
            var count = BinaryPrimitives.ReadUInt32BigEndian(data);
            var at = 4;

            for (var i = 0; i < count; i++)
            {
                var size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at));
                var modified = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 16));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 18));
                // Names arrive with their C terminator counted in the length.
                var name = Encoding.UTF8.GetString(data, at + 22, length).TrimEnd('\0');
                at += 22 + length;

                var isDir = (flags & 1) != 0;
                entries.Add(new RemoteEntry(name, Combine(path, name), isDir, isDir ? 0 : size,
                    modified > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(modified).LocalDateTime : DateTime.MinValue));
            }

            return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }, CancellationToken.None);
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null)
    {
        await _gate.WaitAsync();
        try
        {
            // Size first: a listing must not be interleaved with a download in progress.
            var total = await SizeOfAsync(remotePath);
            await WriteAsync(Concat(U32(DownloadStartRequest), Utf8(remotePath), U64(0)), default);
            await ExpectAsync(DownloadStartReply, default);
            var temp = localPath + ".part";
            long done = 0;

            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                while (true)
                {
                    await WriteAsync(Concat(U32(DownloadDataRequest), [0], U32(Chunk)), default);
                    var type = await ReadReplyTypeAsync(default);

                    if (type == DownloadDataReply)
                    {
                        var data = await ReadCompressedAsync(default);
                        await file.WriteAsync(data);
                        done += data.Length;
                        progress?.Report(new TransferProgress(Path.GetFileName(remotePath), done, total));
                    }
                    else if (type == DownloadEndReply)
                    {
                        await ReadAsync(1 + 8, default);   // file flags, modification time
                        break;
                    }
                    else if (type == LastRequestFailed)
                    {
                        throw new IOException(await ReadStringAsync(default));
                    }
                    else
                    {
                        throw new IOException($"Unexpected reply 0x{type:X8} during a download.");
                    }
                }
            }

            File.Move(temp, localPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The listing already knows sizes; one lookup gives the download a total for its progress bar.</summary>
    private async Task<long> SizeOfAsync(string remotePath)
    {
        // The gate is held by the caller, so this reads directly instead of through RequestAsync.
        var name = remotePath[(remotePath.LastIndexOf('/') + 1)..];
        await WriteAsync(Concat(U32(FileListRequest), [0], Utf8(Parent(remotePath))), default);
        if (await ReadReplyTypeAsync(default) != FileListReply)
        {
            await ReadStringAsync(default);
            return 0;
        }

        var data = await ReadCompressedAsync(default);
        var count = BinaryPrimitives.ReadUInt32BigEndian(data);
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            var size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 18));
            var entry = Encoding.UTF8.GetString(data, at + 22, length).TrimEnd('\0');
            at += 22 + length;
            if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
            {
                return size;
            }
        }

        return 0;
    }

    public async Task UploadAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null)
    {
        await _gate.WaitAsync();
        try
        {
            // Flag 1: replace a file of the same name, as SFTP upload does.
            await WriteAsync(Concat(U32(UploadStartRequest), Utf8(remotePath), [1], U64(0)), default);
            await ExpectAsync(UploadStartReply, default);

            var info = new FileInfo(localPath);
            var buffer = new byte[Chunk];
            long done = 0;
            await using (var file = File.OpenRead(localPath))
            {
                int read;
                while ((read = await file.ReadAsync(buffer)) > 0)
                {
                    await WriteAsync(Concat(U32(UploadDataRequest), [0], U32((uint)read), U32((uint)read), buffer[..read]), default);
                    await ExpectAsync(UploadDataReply, default);
                    done += read;
                    progress?.Report(new TransferProgress(info.Name, done, info.Length));
                }
            }

            var mtime = (ulong)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            await WriteAsync(Concat(U32(UploadEndRequest), [0, 0], U64(mtime)), default);
            await ExpectAsync(UploadEndReply, default);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CreateFolderAsync(string path) =>
        RequestAsync(Concat(U32(MkdirRequest), Utf8(path)), MkdirReply, _ => Task.FromResult(true), CancellationToken.None);

    public Task DeleteAsync(string path) =>
        RequestAsync(Concat(U32(RemoveRequest), Utf8(path)), RemoveReply, _ => Task.FromResult(true), CancellationToken.None);

    // ---------- wire ----------

    private async Task<T> RequestAsync<T>(byte[] request, uint expect, Func<CancellationToken, Task<T>> readBody, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await WriteAsync(request, token);
            await ExpectAsync(expect, token);
            return await readBody(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExpectAsync(uint expect, CancellationToken token)
    {
        var type = await ReadReplyTypeAsync(token);
        if (type == LastRequestFailed)
        {
            throw new IOException((await ReadStringAsync(token)).Trim());
        }

        if (type != expect)
        {
            throw new IOException($"Unexpected reply 0x{type:X8} (expected 0x{expect:X8}).");
        }
    }

    /// <summary>
    /// The next file-transfer reply, skipping what a VNC server may send at any moment: a bell,
    /// a clipboard update (classic or UTF-8). No screen updates arrive, since none are requested.
    /// </summary>
    private async Task<uint> ReadReplyTypeAsync(CancellationToken token)
    {
        while (true)
        {
            var first = (await ReadAsync(1, token))[0];
            switch (first)
            {
                case 0xFC:
                    var rest = await ReadAsync(3, token);
                    var type = (uint)(0xFC << 24 | rest[0] << 16 | rest[1] << 8 | rest[2]);
                    if (type == 0xFC000200)   // UTF-8 clipboard
                    {
                        await ReadAsync((int)await ReadU32Async(token), token);
                        continue;
                    }

                    return type;

                case 2:   // Bell
                    continue;

                case 3:   // ServerCutText: 3 padding bytes, length, text
                    await ReadAsync(3, token);
                    await ReadAsync((int)await ReadU32Async(token), token);
                    continue;

                default:
                    throw new IOException($"Unexpected VNC message type {first}.");
            }
        }
    }

    /// <summary>A compression byte, sizes, and the data (zlib when the byte is nonzero).</summary>
    private async Task<byte[]> ReadCompressedAsync(CancellationToken token)
    {
        var level = (await ReadAsync(1, token))[0];
        var compressed = (int)await ReadU32Async(token);
        var uncompressed = (int)await ReadU32Async(token);
        var data = await ReadAsync(compressed, token);
        if (level == 0)
        {
            return data;
        }

        var output = new byte[uncompressed];
        await using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        await z.ReadExactlyAsync(output, token);
        return output;
    }

    private async Task<byte[]> ReadAsync(int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await _stream.ReadExactlyAsync(buffer, token);
        return buffer;
    }

    private async Task<uint> ReadU32Async(CancellationToken token) =>
        BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, token));

    private async Task<string> ReadStringAsync(CancellationToken token) =>
        Encoding.UTF8.GetString(await ReadAsync((int)await ReadU32Async(token), token)).TrimEnd('\0').Trim();

    private Task WriteAsync(byte[] data, CancellationToken token) => _stream.WriteAsync(data, token).AsTask();

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    private static byte[] Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return Concat(U32((uint)bytes.Length), bytes);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// Classic VNC authentication: DES-encrypt the challenge with the password as the key, each
    /// key byte bit-reversed (a quirk of the original implementation that every server expects).
    /// </summary>
    public static byte[] VncAuthResponse(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (var i = 0; i < 8 && i < pw.Length; i++)
        {
            var b = pw[i];
            byte r = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                r |= (byte)(((b >> bit) & 1) << (7 - bit));
            }

            key[i] = r;
        }

        try
        {
            using var des = DES.Create();
            des.Key = key;
            return des.EncryptEcb(challenge, PaddingMode.None);
        }
        catch (CryptographicException)
        {
            // .NET refuses DES "weak" keys; an empty password is one of them.
            throw new UnauthorizedAccessException("The VNC server wants a password, and none (or an unusable one) is saved for this tab.");
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp.Dispose();
        _gate.Dispose();
    }
}
