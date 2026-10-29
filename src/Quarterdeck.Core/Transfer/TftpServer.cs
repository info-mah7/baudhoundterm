using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Quarterdeck.Core.Transfer;

/// <summary>
/// A TFTP server for firmware work: routers, switches, IP phones and boot loaders pull images
/// with TFTP. Serves one folder (RFC 1350), with the blksize, timeout and tsize options
/// (RFC 2347-2349) so large images go quickly. Uploads are off unless asked for, and even then
/// never overwrite. Each transfer gets its own socket, as the protocol requires.
/// </summary>
public sealed class TftpServer : IAsyncDisposable
{
    private const ushort OpRrq = 1, OpWrq = 2, OpData = 3, OpAck = 4, OpError = 5, OpOack = 6;

    private readonly string _root;
    private readonly IPEndPoint _bind;
    private readonly bool _allowUploads;
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;

    // TFTP has no authentication, so a request can come from anyone who can reach the port, with
    // a forged sender address. Without limits a few requests tied up all the memory, and a large
    // blksize made the server a packet amplifier aimed at whoever the sender claimed to be.
    private const int MaxBlockSize = 1468;       // one Ethernet frame, unfragmented
    private const int MaxTransfers = 16;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxUploadBytes = 512L * 1024 * 1024;
    private int _transfers;

    public TftpServer(string root, IPEndPoint bind, bool allowUploads)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _bind = bind;
        _allowUploads = allowUploads;
    }

    /// <summary>One line per event: requests, completions, refusals.</summary>
    public event Action<string>? Log;

    public bool IsRunning => _listener is not null;

    /// <summary>Retries per block before a transfer is abandoned.</summary>
    public int Retries { get; set; } = 5;

    public void Start()
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {_root}");
        }

        _listener = new UdpClient(_bind);
        IgnoreConnectionResets(_listener);

        // Bound to every interface, each request must be answered from the address it reached:
        // a device checks the reply's source, and on a PC with several addresses the routing
        // table may pick another. Packet information says which address that was.
        if (_bind.Address.Equals(IPAddress.Any) || _bind.Address.Equals(IPAddress.IPv6Any))
        {
            _listener.Client.SetSocketOption(
                _bind.AddressFamily == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
                SocketOptionName.PacketInformation, true);
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
        Write($"Serving {_root} on {_bind}{(_allowUploads ? " (uploads allowed)" : "")}");
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" for an earlier datagram as an error on the
    /// next receive. On UDP that is noise; switched off, it can no longer cut a wait short.
    /// </summary>
    private static void IgnoreConnectionResets(UdpClient client)
    {
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            client.Client.IOControl(SioUdpConnReset, new byte[4], null);
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var buffer = new byte[65536];
        var any = new IPEndPoint(_bind.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var wildcard = _bind.Address.Equals(IPAddress.Any) || _bind.Address.Equals(IPAddress.IPv6Any);

        while (!token.IsCancellationRequested && _listener is not null)
        {
            byte[] request;
            IPEndPoint peer;
            var local = _bind.Address;
            try
            {
                var got = await _listener.Client.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, token);
                request = buffer.AsSpan(0, got.ReceivedBytes).ToArray();
                peer = (IPEndPoint)got.RemoteEndPoint;
                if (wildcard && got.PacketInformation.Address is { } to)
                {
                    local = to;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A lasting fault (the network went away) must not spin a core.
                await Task.Delay(250, CancellationToken.None);
                continue;
            }

            if (Interlocked.Increment(ref _transfers) > MaxTransfers)
            {
                Interlocked.Decrement(ref _transfers);
                Write($"{peer.Address}  refused: {MaxTransfers} transfers already running");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleRequestAsync(request, peer, local, token);
                }
                finally
                {
                    Interlocked.Decrement(ref _transfers);
                }
            }, token);
        }
    }

    private async Task HandleRequestAsync(byte[] request, IPEndPoint peer, IPAddress local, CancellationToken token)
    {
        // Every transfer answers from a fresh port (its TID), on the address the request reached.
        using var socket = new UdpClient(new IPEndPoint(local, 0));
        IgnoreConnectionResets(socket);

        try
        {
            if (request.Length < 4)
            {
                return;
            }

            var op = BinaryPrimitives.ReadUInt16BigEndian(request);
            if (op is not (OpRrq or OpWrq))
            {
                await SendErrorAsync(socket, peer, 4, "Expected a read or write request");
                return;
            }

            var fields = ParseStrings(request.AsSpan(2));
            if (fields.Count < 2)
            {
                await SendErrorAsync(socket, peer, 4, "Malformed request");
                return;
            }

            var name = fields[0];
            var mode = fields[1].ToLowerInvariant();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 2; i + 1 < fields.Count; i += 2)
            {
                options[fields[i]] = fields[i + 1];
            }

            if (mode is not ("octet" or "netascii"))
            {
                await SendErrorAsync(socket, peer, 4, $"Unsupported mode {mode}");
                return;
            }

            var path = Resolve(name);
            if (path is null)
            {
                Write($"{peer.Address}  refused \"{name}\": outside the served folder");
                await SendErrorAsync(socket, peer, 2, "Access violation");
                return;
            }

            if (op == OpRrq)
            {
                await SendFileAsync(socket, peer, name, path, mode == "netascii", options, token);
            }
            else
            {
                await ReceiveFileAsync(socket, peer, name, path, options, mode == "netascii", token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Write($"{peer.Address}  error: {ex.Message}");
        }
    }

    /// <summary>
    /// The served path for a requested name, or null if it would leave the folder. Devices often
    /// ask for "/tftpboot/x" or "\\x", so leading separators are dropped; "..", drive letters
    /// and alternate data streams are refused.
    /// </summary>
    public string? Resolve(string requested)
    {
        var name = requested.Replace('\\', '/').TrimStart('/');
        if (name.Length == 0 || name.Contains(':') || name.Split('/').Any(s => s == ".."))
        {
            return null;
        }

        // Device names are not files: a request for CON or COM1 (with or without an extension,
        // and whatever trailing dots or spaces) must not open a device.
        foreach (var part in name.Split('/'))
        {
            var stem = part.Split('.')[0].Trim().TrimEnd('.');
            if (stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
                || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(?i:COM|LPT)[0-9]$"))
            {
                return null;
            }
        }

        var full = Path.GetFullPath(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A junction or symbolic link inside the folder can point anywhere, and the check above
        // compares the path as written, not where it leads.
        for (var walk = full; walk.Length > _root.Length; walk = Path.GetDirectoryName(walk) ?? "")
        {
            var info = new FileInfo(walk);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            var dir = new DirectoryInfo(walk);
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }
        }

        return full;
    }

    private async Task SendFileAsync(UdpClient socket, IPEndPoint peer, string name, string path, bool netascii,
                                     Dictionary<string, string> options, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            Write($"{peer.Address}  GET {name}: not found");
            await SendErrorAsync(socket, peer, 1, "File not found");
            return;
        }

        var onDisk = new FileInfo(path).Length;
        if (onDisk >= MaxFileBytes)   // ReadAllBytes takes less than 2 GiB; netascii grows it further
        {
            Write($"{peer.Address}  GET {name}: {Size(onDisk)} is larger than this server sends");
            await SendErrorAsync(socket, peer, 3, "File too large");
            return;
        }

        byte[] data = await File.ReadAllBytesAsync(path, token);
        if (netascii)
        {
            data = ToNetascii(data);
        }

        var (blockSize, timeout, accepted) = Negotiate(options, data.Length);
        var clock = Stopwatch.StartNew();
        Write($"{peer.Address}  GET {name}  {Size(data.Length)}" + (accepted.Count > 0 ? $"  ({string.Join(", ", accepted.Select(kv => $"{kv.Key}={kv.Value}"))})" : ""));

        if (accepted.Count > 0)
        {
            // An OACK is acknowledged with block 0 before data flows.
            if (!await ExchangeAsync(socket, peer, Oack(accepted), 0, timeout, token))
            {
                Write($"{peer.Address}  GET {name}: no answer to the option acknowledgement");
                return;
            }
        }

        var blocks = data.Length / blockSize + 1;   // a final short (possibly empty) block ends it
        for (var i = 0; i < blocks; i++)
        {
            var offset = i * blockSize;
            var length = Math.Min(blockSize, data.Length - offset);
            var blockNumber = (ushort)((i + 1) & 0xFFFF);   // wraps after 65535 for very large images

            var packet = new byte[4 + length];
            BinaryPrimitives.WriteUInt16BigEndian(packet, OpData);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), blockNumber);
            Buffer.BlockCopy(data, offset, packet, 4, length);

            if (!await ExchangeAsync(socket, peer, packet, blockNumber, timeout, token))
            {
                Write($"{peer.Address}  GET {name}: stopped at block {i + 1} of {blocks} (no acknowledgement)");
                return;
            }
        }

        var seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
        Write($"{peer.Address}  GET {name}: done, {Size(data.Length)} in {seconds:0.0} s ({Size((long)(data.Length / seconds))}/s)");
    }

    /// <summary>Sends a packet and waits for the matching ACK, resending on timeout.</summary>
    private async Task<bool> ExchangeAsync(UdpClient socket, IPEndPoint peer, byte[] packet, ushort expectAck,
                                           TimeSpan timeout, CancellationToken token)
    {
        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            await socket.SendAsync(packet, peer, token);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var reply = await ReceiveAsync(socket, deadline - DateTime.UtcNow, token);
                if (reply is null)
                {
                    break;
                }

                var (buffer, from) = reply.Value;
                if (!from.Equals(peer))
                {
                    await SendErrorAsync(socket, from, 5, "Unknown transfer ID");
                    continue;
                }

                if (buffer.Length < 4)
                {
                    continue;
                }

                var op = BinaryPrimitives.ReadUInt16BigEndian(buffer);
                if (op == OpError)
                {
                    return false;
                }

                if (op == OpAck && BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)) == expectAck)
                {
                    return true;
                }

                // An older, duplicate ACK: ignore it rather than resend (avoids the
                // "sorcerer's apprentice" doubling of RFC 1123 4.2.3.1).
            }
        }

        return false;
    }

    private async Task ReceiveFileAsync(UdpClient socket, IPEndPoint peer, string name, string path,
                                        Dictionary<string, string> options, bool netascii, CancellationToken token)
    {
        var pendingCr = false;
        if (!_allowUploads)
        {
            Write($"{peer.Address}  PUT {name}: refused (uploads are off)");
            await SendErrorAsync(socket, peer, 2, "Uploads are not allowed on this server");
            return;
        }

        if (File.Exists(path))
        {
            Write($"{peer.Address}  PUT {name}: refused, a file with that name exists");
            await SendErrorAsync(socket, peer, 6, "File already exists");
            return;
        }

        var (blockSize, timeout, accepted) = Negotiate(options, -1);
        var clock = Stopwatch.StartNew();
        Write($"{peer.Address}  PUT {name}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A name of its own: an old .part, or a second upload of the same name, is never touched.
        var temp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.part";
        var done = false;
        long written = 0;
        byte[] answer = accepted.Count > 0 ? Oack(accepted) : Ack(0);

        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
        {
            ushort expect = 1;

            while (!done)
            {
                (byte[] Buffer, IPEndPoint From)? got = null;
                for (var attempt = 0; attempt <= Retries && got is null; attempt++)
                {
                    await socket.SendAsync(answer, peer, token);
                    got = await ReceiveDataAsync(socket, peer, expect, timeout, token);
                }

                if (got is null)
                {
                    break;
                }

                var payload = got.Value.Buffer.AsMemory(4);
                if (payload.Length > blockSize)
                {
                    // More than the agreed block size: not a transfer this server accepts.
                    Write($"{peer.Address}  PUT {name}: a block of {payload.Length} bytes is larger than the agreed {blockSize}");
                    await SendErrorAsync(socket, peer, 4, "Illegal TFTP operation");
                    break;
                }

                if (written + payload.Length > MaxUploadBytes)
                {
                    Write($"{peer.Address}  PUT {name}: larger than the {Size(MaxUploadBytes)} upload limit");
                    await SendErrorAsync(socket, peer, 3, "Disk full or allocation exceeded");
                    break;
                }

                await file.WriteAsync(netascii ? FromNetascii(payload.ToArray(), ref pendingCr) : payload, token);
                written += payload.Length;
                answer = Ack(expect);
                done = payload.Length < blockSize;   // a short block is the last one
                expect = (ushort)((expect + 1) & 0xFFFF);
            }

        }

        if (done)
        {
            // Only a complete upload takes the real name; a broken one stays visible as .part.
            // The rename comes before the last ACK, so a device that checks straight after its
            // upload finishes always finds the file.
            try
            {
                File.Move(temp, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Usually another upload of the same name finished first; otherwise a disk or
                // permission problem, which gets its own message.
                var exists = File.Exists(path);
                Write($"{peer.Address}  PUT {name}: " + (exists ? "a file with that name appeared meanwhile" : "could not rename: " + ex.Message) +
                      $"; kept as {Path.GetFileName(temp)}");
                await SendErrorAsync(socket, peer, exists ? (ushort)6 : (ushort)2, exists ? "File already exists" : "Access violation");
                return;
            }

            await socket.SendAsync(answer, peer, token);
            Write($"{peer.Address}  PUT {name}: done, {Size(written)} in {clock.Elapsed.TotalSeconds:0.0} s");
        }
        else
        {
            Write($"{peer.Address}  PUT {name}: incomplete after {Size(written)}; kept as {Path.GetFileName(temp)}");
        }
    }

    private async Task<(byte[] Buffer, IPEndPoint From)?> ReceiveDataAsync(UdpClient socket, IPEndPoint peer,
                                                                          ushort expect, TimeSpan timeout,
                                                                          CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var reply = await ReceiveAsync(socket, deadline - DateTime.UtcNow, token);
            if (reply is null)
            {
                return null;
            }

            var (buffer, from) = reply.Value;
            if (!from.Equals(peer))
            {
                await SendErrorAsync(socket, from, 5, "Unknown transfer ID");
                continue;
            }

            if (buffer.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(buffer) == OpData &&
                BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)) == expect)
            {
                return reply;
            }
        }

        return null;
    }

    private static async Task<(byte[] Buffer, IPEndPoint From)?> ReceiveAsync(UdpClient socket, TimeSpan wait,
                                                                              CancellationToken token)
    {
        var deadline = DateTime.UtcNow + wait;
        while (deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
            timer.CancelAfter(left);
            try
            {
                var r = await socket.ReceiveAsync(timer.Token);
                return (r.Buffer, r.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // A late ICMP report about another datagram: keep waiting out the same deadline.
            }
            catch (SocketException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The options this server accepts and the values it will use.</summary>
    private static (int BlockSize, TimeSpan Timeout, Dictionary<string, string> Accepted) Negotiate(
        Dictionary<string, string> options, long size)
    {
        var accepted = new Dictionary<string, string>();
        var blockSize = 512;
        var timeout = TimeSpan.FromSeconds(3);

        if (options.TryGetValue("blksize", out var b) && int.TryParse(b, out var requested))
        {
            // 1468 fits an Ethernet frame without fragmentation. Bigger blocks are not granted:
            // one request with a forged sender address would answer it with 64 KB packets.
            blockSize = Math.Clamp(requested, 8, MaxBlockSize);
            accepted["blksize"] = blockSize.ToString();
        }

        if (options.TryGetValue("timeout", out var t) && int.TryParse(t, out var secs) && secs is >= 1 and <= 255)
        {
            timeout = TimeSpan.FromSeconds(secs);
            accepted["timeout"] = secs.ToString();
        }

        if (options.ContainsKey("tsize"))
        {
            // On a read the client sends 0 and gets the real size; on a write it states its size.
            accepted["tsize"] = size >= 0 ? size.ToString() : options["tsize"];
        }

        return (blockSize, timeout, accepted);
    }

    private static List<string> ParseStrings(ReadOnlySpan<byte> span)
    {
        var list = new List<string>();
        while (span.Length > 0)
        {
            var end = span.IndexOf((byte)0);
            if (end < 0)
            {
                break;
            }

            list.Add(Encoding.ASCII.GetString(span[..end]));
            span = span[(end + 1)..];
        }

        return list;
    }

    private static byte[] Ack(ushort block)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(p, OpAck);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), block);
        return p;
    }

    private static byte[] Oack(Dictionary<string, string> accepted)
    {
        using var ms = new MemoryStream();
        ms.Write([0, (byte)OpOack]);
        foreach (var (k, v) in accepted)
        {
            ms.Write(Encoding.ASCII.GetBytes(k));
            ms.WriteByte(0);
            ms.Write(Encoding.ASCII.GetBytes(v));
            ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    private static async Task SendErrorAsync(UdpClient socket, IPEndPoint peer, ushort code, string message)
    {
        var text = Encoding.ASCII.GetBytes(message);
        var p = new byte[5 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p, OpError);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), code);
        Buffer.BlockCopy(text, 0, p, 4, text.Length);
        try
        {
            await socket.SendAsync(p, peer);
        }
        catch (SocketException)
        {
        }
    }

    /// <summary>
    /// netascii received: CR LF becomes LF and CR NUL becomes CR, the way the sender wrote
    /// them. A CR ending one block waits for the next block's first byte.
    /// </summary>
    private static byte[] FromNetascii(byte[] data, ref bool pendingCr)
    {
        var o = new List<byte>(data.Length + 1);
        foreach (var c in data)
        {
            if (pendingCr)
            {
                pendingCr = false;
                if (c == (byte)'\n')
                {
                    o.Add((byte)'\n');
                    continue;
                }

                o.Add((byte)'\r');
                if (c == 0)
                {
                    continue;
                }
            }

            if (c == (byte)'\r')
            {
                pendingCr = true;
            }
            else
            {
                o.Add(c);
            }
        }

        return o.ToArray();
    }

    /// <summary>netascii: every line ends CR LF, and a bare CR is sent as CR NUL.</summary>
    private static byte[] ToNetascii(byte[] data)
    {
        var o = new List<byte>(data.Length + data.Length / 40);
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c == (byte)'\n' && (i == 0 || data[i - 1] != (byte)'\r'))
            {
                o.Add((byte)'\r');
                o.Add((byte)'\n');
            }
            else if (c == (byte)'\r' && (i + 1 >= data.Length || data[i + 1] != (byte)'\n'))
            {
                o.Add((byte)'\r');
                o.Add(0);
            }
            else
            {
                o.Add(c);
            }
        }

        return o.ToArray();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };

    private void Write(string line) => Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {line}");

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        if (_cts is not null)
        {
            Write("Stopped.");
        }

        _cts?.Dispose();
        _cts = null;
        return ValueTask.CompletedTask;
    }
}
