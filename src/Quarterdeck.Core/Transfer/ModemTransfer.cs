using System.Text;
using System.Threading.Channels;

namespace Quarterdeck.Core.Transfer;

/// <summary>A raw byte path to the far end, borrowed from a session for one transfer.</summary>
public interface IByteLink
{
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token);

    /// <summary>Next byte, or -1 when nothing arrives within the timeout.</summary>
    ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken token);

    /// <summary>Drop anything already received (line noise, a prompt's echo).</summary>
    void DiscardInput();
}

/// <summary>An <see cref="IByteLink"/> fed by pushing received bytes into it.</summary>
public sealed class QueuedByteLink(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write) : IByteLink
{
    private readonly Channel<byte> _in = Channel.CreateUnbounded<byte>();

    public void Push(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _in.Writer.TryWrite(b);
        }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => write(data, token);

    public async ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken token)
    {
        if (_in.Reader.TryRead(out var now))
        {
            return now;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(timeout);
        try
        {
            return await _in.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return -1;
        }
    }

    public void DiscardInput()
    {
        while (_in.Reader.TryRead(out _))
        {
        }
    }
}

public enum ModemProtocol
{
    /// <summary>128-byte blocks; checksum or CRC as the receiver asks.</summary>
    Xmodem,

    /// <summary>1024-byte blocks with CRC (U-Boot loadx, most boot loaders).</summary>
    Xmodem1K,

    /// <summary>Batch: the file's name and size travel with it (U-Boot loady, lrz/lsz).</summary>
    Ymodem
}

public sealed record ModemProgress(string File, long Done, long Total);

/// <summary>
/// XMODEM (checksum, CRC, 1K) and YMODEM batch, both directions, over any <see cref="IByteLink"/>.
/// The far end is a boot loader, a BaudHound target or lrzsz; this side waits for it to start,
/// so the order is: start the device's receive (loady, rx, rb), then send here; or start a
/// receive here, then the device's send.
/// </summary>
public sealed class ModemTransfer(IByteLink link)
{
    private const byte Soh = 0x01, Stx = 0x02, Eot = 0x04, Ack = 0x06, Nak = 0x15, Can = 0x18, Sub = 0x1A, C = (byte)'C';

    public event Action<ModemProgress>? Progress;
    public event Action<string>? Log;

    /// <summary>How long to wait for the far end to start, in seconds.</summary>
    public int StartTimeoutSeconds { get; set; } = 60;

    public int Retries { get; set; } = 10;

    // ---------- send ----------

    /// <summary>Sends files. XMODEM sends exactly one; YMODEM sends them as a batch.</summary>
    public async Task SendAsync(ModemProtocol protocol, IReadOnlyList<string> paths, CancellationToken token)
    {
        if (protocol != ModemProtocol.Ymodem && paths.Count != 1)
        {
            throw new ArgumentException("XMODEM sends one file at a time; use YMODEM for several.");
        }

        try
        {
            if (protocol == ModemProtocol.Ymodem)
            {
                foreach (var path in paths)
                {
                    var crc = await WaitForStartAsync(token);
                    if (!crc)
                    {
                        throw new IOException("The receiver asked for checksum mode; YMODEM needs CRC.");
                    }

                    var info = new FileInfo(path);
                    var header = Encoding.ASCII.GetBytes($"{info.Name}\0{info.Length} {ToOctal(info.LastWriteTimeUtc)} 100644");
                    await SendBlockAsync(0, Pad(header, header.Length > 128 ? 1024 : 128, 0), crc: true, token);
                    await SendDataAsync(info.Name, await File.ReadAllBytesAsync(path, token), oneK: true, crc: true,
                        waitForStart: true, token);
                }

                await EndBatchAsync(token);
            }
            else
            {
                var crc = await WaitForStartAsync(token);
                var data = await File.ReadAllBytesAsync(paths[0], token);
                await SendDataAsync(Path.GetFileName(paths[0]), data, protocol == ModemProtocol.Xmodem1K && crc, crc,
                    waitForStart: false, token);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelFarEndAsync();
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // A failed transfer leaves the far end mid-protocol, its line still raw; abort it there too.
            await CancelFarEndAsync(announce: false);
            throw;
        }
    }

    /// <summary>
    /// An empty block 0 ends a YMODEM batch. Every file is already delivered and acknowledged by
    /// now, and some receivers (lrzsz rb on a tty, a few boot loaders) exit without answering
    /// this block. So a short, quiet wait: an answer is welcome, silence is not a failure.
    /// </summary>
    private async Task EndBatchAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var asked = await link.ReadByteAsync(TimeSpan.FromSeconds(3), token);
            if (asked is not (C or Nak))
            {
                continue;
            }

            var packet = new byte[133];
            packet[0] = Soh;
            packet[2] = 0xFF;
            if (asked == C)
            {
                var crc = Crc16(new byte[128]);
                packet = packet[..131].Concat(new[] { (byte)(crc >> 8), (byte)crc }).ToArray();
            }
            else
            {
                packet = packet[..132];   // checksum of 128 zero bytes is 0
            }

            await link.WriteAsync(packet, token);
            if (await link.ReadByteAsync(TimeSpan.FromSeconds(3), token) == Ack)
            {
                Write("Batch finished.");
                return;
            }
        }

        Write("Batch finished (the receiver closed without acknowledging the end of the batch, which some do).");
    }

    /// <summary>Waits for 'C' (CRC) or NAK (checksum). True means CRC.</summary>
    private async Task<bool> WaitForStartAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(StartTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var b = await link.ReadByteAsync(TimeSpan.FromSeconds(1), token);
            switch (b)
            {
                case C:
                    return true;
                case Nak:
                    return false;
                case Can:
                    if (await link.ReadByteAsync(TimeSpan.FromSeconds(1), token) == Can)
                    {
                        throw new IOException("The receiver cancelled.");
                    }

                    break;
            }
        }

        throw new TimeoutException("The receiver never started. Start its receive first (loadx, loady, rx, rb), then send.");
    }

    private async Task SendDataAsync(string name, byte[] data, bool oneK, bool crc, bool waitForStart, CancellationToken token)
    {
        if (waitForStart)
        {
            // YMODEM: after the header block is acknowledged, the receiver asks again with 'C'.
            await WaitForStartAsync(token);
        }

        byte block = 1;
        long offset = 0;
        Progress?.Invoke(new ModemProgress(name, 0, data.Length));

        while (offset < data.Length)
        {
            token.ThrowIfCancellationRequested();
            var remaining = data.Length - offset;

            // A short tail goes in a 128-byte block rather than a mostly-padding 1K one.
            var size = oneK && remaining > 128 ? 1024 : 128;
            var chunk = new byte[size];
            Array.Fill(chunk, Sub);
            Array.Copy(data, offset, chunk, 0, Math.Min(size, remaining));

            await SendBlockAsync(block, chunk, crc, token);
            offset += Math.Min(size, remaining);
            block++;
            Progress?.Invoke(new ModemProgress(name, offset, data.Length));
        }

        // End of file: EOT until acknowledged (YMODEM receivers NAK the first one on purpose).
        for (var attempt = 0; attempt < Retries; attempt++)
        {
            await link.WriteAsync(new[] { Eot }, token);
            var r = await link.ReadByteAsync(TimeSpan.FromSeconds(10), token);
            if (r == Ack)
            {
                Write($"Sent {name}: {data.Length:N0} bytes.");
                return;
            }
        }

        throw new IOException("The receiver never acknowledged the end of the file.");
    }

    private async Task SendBlockAsync(byte number, byte[] payload, bool crc, CancellationToken token)
    {
        var packet = new List<byte>(payload.Length + 5)
        {
            payload.Length == 1024 ? Stx : Soh, number, (byte)~number
        };
        packet.AddRange(payload);

        if (crc)
        {
            var c = Crc16(payload);
            packet.Add((byte)(c >> 8));
            packet.Add((byte)c);
        }
        else
        {
            packet.Add((byte)payload.Sum(b => b));
        }

        var bytes = packet.ToArray();
        for (var attempt = 0; attempt < Retries; attempt++)
        {
            link.DiscardInput();
            await link.WriteAsync(bytes, token);

            var r = await link.ReadByteAsync(TimeSpan.FromSeconds(10), token);
            if (r == Ack)
            {
                return;
            }

            if (r == Can && await link.ReadByteAsync(TimeSpan.FromSeconds(1), token) == Can)
            {
                throw new IOException("The receiver cancelled.");
            }

            // NAK, garbage or silence: send the block again.
        }

        throw new IOException($"Block {number} was not acknowledged after {Retries} tries.");
    }

    // ---------- receive ----------

    /// <summary>
    /// Receives into <paramref name="folder"/>. XMODEM has no file name or size, so it is
    /// saved as <paramref name="xmodemName"/> with trailing padding (0x1A) removed. Returns the
    /// paths written.
    /// </summary>
    public async Task<List<string>> ReceiveAsync(ModemProtocol protocol, string folder, string xmodemName,
                                                 CancellationToken token)
    {
        var written = new List<string>();
        try
        {
            if (protocol != ModemProtocol.Ymodem)
            {
                var (data, _) = await ReceiveFileAsync(expectHeader: false, token);
                var path = Unique(Path.Combine(folder, xmodemName));
                await File.WriteAllBytesAsync(path, TrimPadding(data), token);
                written.Add(path);
                Write($"Received {Path.GetFileName(path)}: {new FileInfo(path).Length:N0} bytes (padding removed).");
                return written;
            }

            while (true)
            {
                var (data, header) = await ReceiveFileAsync(expectHeader: true, token);
                if (header is null)
                {
                    Write("Batch finished.");
                    return written;
                }

                var path = Unique(Path.Combine(folder, Path.GetFileName(header.Value.Name)));
                var length = header.Value.Size >= 0 ? (int)Math.Min(header.Value.Size, data.Length) : data.Length;
                await File.WriteAllBytesAsync(path, data.AsMemory(0, length), token);
                written.Add(path);
                Write($"Received {Path.GetFileName(path)}: {length:N0} bytes.");
            }
        }
        catch (OperationCanceledException)
        {
            await CancelFarEndAsync();
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // A failed transfer leaves the far end mid-protocol, its line still raw; abort it there too.
            await CancelFarEndAsync(announce: false);
            throw;
        }
    }

    /// <summary>One file. For YMODEM a null header (empty name) ends the batch: Header is null.</summary>
    private async Task<(byte[] Data, (string Name, long Size)? Header)> ReceiveFileAsync(bool expectHeader, CancellationToken token)
    {
        var crc = true;
        var data = new MemoryStream();
        (string Name, long Size)? header = null;
        byte expect = expectHeader ? (byte)0 : (byte)1;
        var started = false;
        var errors = 0;
        var eots = 0;

        // Ask to start: 'C' for CRC; after a few unanswered asks, fall back to NAK (checksum),
        // which only plain XMODEM senders need.
        for (var ask = 0; !started; ask++)
        {
            token.ThrowIfCancellationRequested();
            if (ask >= StartTimeoutSeconds / 3)
            {
                throw new TimeoutException("The sender never started. Start its send (sx, sb, loadb...) after this.");
            }

            if (!expectHeader && ask == 4)
            {
                crc = false;
            }

            await link.WriteAsync(new[] { crc ? C : Nak }, token);
            var first = await link.ReadByteAsync(TimeSpan.FromSeconds(3), token);
            if (first is Soh or Stx)
            {
                started = true;
                var blk = await ReadBlockAsync(first, crc, token);
                if (blk is null)
                {
                    await link.WriteAsync(new[] { Nak }, token);
                    started = false;
                    continue;
                }

                if (!await HandleBlockAsync(blk.Value))
                {
                    return (data.ToArray(), header);
                }
            }
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var b = await link.ReadByteAsync(TimeSpan.FromSeconds(10), token);

            switch (b)
            {
                case Soh or Stx:
                    var blk = await ReadBlockAsync((byte)b, crc, token);
                    if (blk is null)
                    {
                        if (++errors > Retries)
                        {
                            throw new IOException("Too many damaged blocks.");
                        }

                        link.DiscardInput();
                        await link.WriteAsync(new[] { Nak }, token);
                        continue;
                    }

                    if (!await HandleBlockAsync(blk.Value))
                    {
                        return (data.ToArray(), header);
                    }

                    break;

                case Eot:
                    // YMODEM: NAK the first EOT and wait for it again, as senders expect.
                    if (expectHeader && eots++ == 0)
                    {
                        await link.WriteAsync(new[] { Nak }, token);
                        continue;
                    }

                    await link.WriteAsync(new[] { Ack }, token);   // the caller asks for the next header
                    return (data.ToArray(), header ?? ("", data.Length));

                case Can:
                    if (await link.ReadByteAsync(TimeSpan.FromSeconds(1), token) == Can)
                    {
                        throw new IOException("The sender cancelled.");
                    }

                    break;

                case -1:
                    if (++errors > Retries)
                    {
                        throw new TimeoutException("The sender went quiet.");
                    }

                    await link.WriteAsync(new[] { Nak }, token);
                    break;
            }
        }

        // Returns false when the transfer of this file is over (a null YMODEM header).
        async Task<bool> HandleBlockAsync((byte Number, byte[] Payload) block)
        {
            if (block.Number == expect)
            {
                if (expectHeader && expect == 0 && header is null)
                {
                    var text = Encoding.ASCII.GetString(block.Payload);
                    var end = text.IndexOf('\0');
                    if (end < 0)
                    {
                        // Not the empty end-of-batch block (that starts with NUL): a damaged or
                        // overlong header, which must not pass for "no more files".
                        throw new IOException("The YMODEM header has no end to its file name.");
                    }

                    var name = text[..end];
                    if (name.Length == 0)
                    {
                        await link.WriteAsync(new[] { Ack }, token);
                        header = null;
                        return false;   // empty header: no more files
                    }

                    var rest = text[(name.Length + 1)..].TrimEnd('\0');
                    var size = long.TryParse(rest.Split(' ')[0], out var s) ? s : -1;
                    header = (name, size);
                    await link.WriteAsync(new[] { Ack, C }, token);   // ACK, then ask for data
                    Progress?.Invoke(new ModemProgress(name, 0, size));
                }
                else
                {
                    data.Write(block.Payload);
                    await link.WriteAsync(new[] { Ack }, token);
                    Progress?.Invoke(new ModemProgress(header?.Name ?? "file", data.Length, header?.Size ?? -1));
                }

                expect++;
                errors = 0;
            }
            else if (block.Number == (byte)(expect - 1))
            {
                // A repeat of one we have: our ACK was lost. A repeated YMODEM header also needs
                // the C that asks for data, or the sender waits for it until it times out.
                await link.WriteAsync(expectHeader && block.Number == 0 ? new[] { Ack, C } : new[] { Ack }, token);
            }
            else
            {
                throw new IOException($"Out of step: expected block {expect}, got {block.Number}.");
            }

            return true;
        }
    }

    /// <summary>Reads the rest of a block after its first byte; null if it is damaged.</summary>
    private async Task<(byte Number, byte[] Payload)?> ReadBlockAsync(int first, bool crc, CancellationToken token)
    {
        var size = first == Stx ? 1024 : 128;
        var total = 2 + size + (crc ? 2 : 1);
        var buffer = new byte[total];

        for (var i = 0; i < total; i++)
        {
            var b = await link.ReadByteAsync(TimeSpan.FromSeconds(1), token);
            if (b < 0)
            {
                return null;
            }

            buffer[i] = (byte)b;
        }

        if (buffer[0] != (byte)~buffer[1])
        {
            return null;
        }

        var payload = buffer.AsSpan(2, size).ToArray();
        if (crc)
        {
            var expected = (ushort)((buffer[2 + size] << 8) | buffer[3 + size]);
            if (Crc16(payload) != expected)
            {
                return null;
            }
        }
        else if ((byte)payload.Sum(b => b) != buffer[2 + size])
        {
            return null;
        }

        return (buffer[0], payload);
    }

    // ---------- helpers ----------

    private async Task CancelFarEndAsync(bool announce = true)
    {
        try
        {
            // Five CANs then backspaces: the conventional abort that every implementation honours.
            await link.WriteAsync(new byte[] { Can, Can, Can, Can, Can, 8, 8, 8, 8, 8 }, CancellationToken.None);
        }
        catch
        {
            // The link may already be gone.
        }

        if (announce)
        {
            Write("Cancelled.");
        }
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    private static byte[] Pad(byte[] data, int size, byte fill)
    {
        var o = new byte[size];
        Array.Fill(o, fill);
        Array.Copy(data, o, Math.Min(size, data.Length));
        return o;
    }

    private static byte[] TrimPadding(byte[] data)
    {
        var end = data.Length;
        while (end > 0 && data[end - 1] == Sub)
        {
            end--;
        }

        return data[..end];
    }

    private static string ToOctal(DateTime utc) =>
        Convert.ToString((long)(utc - DateTime.UnixEpoch).TotalSeconds, 8);

    /// <summary>Never overwrite: "fw.bin" becomes "fw (1).bin".</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void Write(string line) => Log?.Invoke(line);
}
