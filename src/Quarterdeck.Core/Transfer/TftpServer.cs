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
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
        Write($"Serving {_root} on {_bind}{(_allowUploads ? " (uploads allowed)" : "")}");
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _listener.ReceiveAsync(token);
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
                continue;   // an ICMP "port unreachable" from an earlier peer surfaces here on Windows
            }

            _ = Task.Run(() => HandleRequestAsync(packet.Buffer, packet.RemoteEndPoint, token), token);
        }
    }

    private async Task HandleRequestAsync(byte[] request, IPEndPoint peer, CancellationToken token)
    {
        // Every transfer answers from a fresh port (its TID), on the same address as the listener.
        using var socket = new UdpClient(new IPEndPoint(_bind.Address, 0));

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
                await ReceiveFileAsync(socket, peer, name, path, options, token);
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

        var full = Path.GetFullPath(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? full : null;
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
                                        Dictionary<string, string> options, CancellationToken token)
    {
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
        var temp = path + ".part";
        var done = false;
        long written = 0;
        byte[] answer = accepted.Count > 0 ? Oack(accepted) : Ack(0);

        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
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
                await file.WriteAsync(payload, token);
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
            File.Move(temp, path);
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
        if (wait <= TimeSpan.Zero)
        {
            return null;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(wait);
        try
        {
            var r = await socket.ReceiveAsync(timer.Token);
            return (r.Buffer, r.RemoteEndPoint);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
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
            // 1468 fits an Ethernet frame without fragmentation; larger asks are honoured to 65464.
            blockSize = Math.Clamp(requested, 8, 65464);
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
