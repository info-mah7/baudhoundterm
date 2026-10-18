using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Quarterdeck.Core.Security.Agent;

/// <summary>One key an agent offers. The private half never leaves the agent.</summary>
public sealed record AgentIdentity(byte[] Blob, string Comment, string KeyType, string Source)
{
    /// <summary>OpenSSH-style fingerprint, "SHA256:..." without padding.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    internal IAgentTransport Transport { get; init; } = null!;
}

/// <summary>
/// An SSH agent client (draft-miller-ssh-agent): lists keys and asks for signatures. Reaches
/// every agent KeePassXC can load keys into on Windows: the OpenSSH agent's named pipe and
/// PuTTY's Pageant. Keys from both are merged; each remembers which agent signs for it.
/// </summary>
public static class SshAgentClient
{
    private const byte Failure = 5;
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    public const uint RsaSha256 = 2;
    public const uint RsaSha512 = 4;

    /// <summary>Agents found on this machine, in the order they are asked.</summary>
    public static IReadOnlyList<IAgentTransport> FindAgents()
    {
        var agents = new List<IAgentTransport>();

        if (OperatingSystem.IsWindows())
        {
            // Enumerate rather than probe: opening the pipe to test it would use up a connection.
            if (Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith(@"\openssh-ssh-agent", StringComparison.OrdinalIgnoreCase)))
            {
                agents.Add(new PipeTransport("openssh-ssh-agent", "OpenSSH agent"));
            }

            if (PageantTransport.IsRunning)
            {
                agents.Add(new PageantTransport());
            }
        }
        else if (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } socket)
        {
            agents.Add(new UnixSocketTransport(socket));
        }

        return agents;
    }

    /// <summary>All keys from all agents. Agents that fail are reported in <paramref name="problems"/>.</summary>
    public static List<AgentIdentity> ListIdentities(out List<string> problems)
    {
        problems = new List<string>();
        var keys = new List<AgentIdentity>();

        foreach (var agent in FindAgents())
        {
            try
            {
                keys.AddRange(ListIdentities(agent));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add($"{agent.Name}: {ex.Message}");
            }
        }

        return keys;
    }

    public static List<AgentIdentity> ListIdentities(IAgentTransport agent)
    {
        var reply = agent.Request(new[] { RequestIdentities });
        var r = new SshReader(reply);

        if (r.Byte() != IdentitiesAnswer)
        {
            throw new InvalidDataException("the agent refused to list its keys.");
        }

        var count = r.UInt32();
        var keys = new List<AgentIdentity>();
        for (var i = 0; i < count; i++)
        {
            var blob = r.Bytes();
            var comment = Encoding.UTF8.GetString(r.Bytes());
            var type = Encoding.ASCII.GetString(new SshReader(blob).Bytes());
            keys.Add(new AgentIdentity(blob, comment, type, agent.Name) { Transport = agent });
        }

        return keys;
    }

    /// <summary>Asks the key's agent to sign; returns the SSH signature blob (algorithm name + signature).</summary>
    public static byte[] Sign(AgentIdentity key, byte[] data, uint flags)
    {
        var w = new MemoryStream();
        w.WriteByte(SignRequest);
        WriteBytes(w, key.Blob);
        WriteBytes(w, data);
        WriteUInt32(w, flags);

        var reply = key.Transport.Request(w.ToArray());
        var r = new SshReader(reply);
        var type = r.Byte();

        if (type == Failure)
        {
            throw new InvalidOperationException($"{key.Source} declined to sign with {key.Comment} (locked, or the use was refused).");
        }

        if (type != SignResponse)
        {
            throw new InvalidDataException($"{key.Source} answered a signature request with message {type}.");
        }

        return r.Bytes();
    }

    private static void WriteBytes(Stream s, byte[] data)
    {
        WriteUInt32(s, (uint)data.Length);
        s.Write(data);
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }

    /// <summary>Wraps a message in its length prefix, sends it, and returns the reply payload.</summary>
    internal static byte[] Exchange(Stream stream, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        stream.Write(frame);
        stream.Flush();

        var header = new byte[4];
        stream.ReadExactly(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > 256 * 1024)
        {
            throw new InvalidDataException($"agent reply of {length} bytes.");
        }

        var body = new byte[length];
        stream.ReadExactly(body);
        return body;
    }

    private ref struct SshReader
    {
        private ReadOnlySpan<byte> _data;

        public SshReader(ReadOnlySpan<byte> data) => _data = data;

        public byte Byte()
        {
            if (_data.Length < 1) throw new InvalidDataException("truncated agent reply.");
            var b = _data[0];
            _data = _data[1..];
            return b;
        }

        public uint UInt32()
        {
            if (_data.Length < 4) throw new InvalidDataException("truncated agent reply.");
            var v = BinaryPrimitives.ReadUInt32BigEndian(_data);
            _data = _data[4..];
            return v;
        }

        public byte[] Bytes()
        {
            var n = (int)UInt32();
            if (n < 0 || n > _data.Length) throw new InvalidDataException("truncated agent reply.");
            var v = _data[..n].ToArray();
            _data = _data[n..];
            return v;
        }
    }
}

public interface IAgentTransport
{
    string Name { get; }

    /// <summary>Sends one agent message (without its length prefix) and returns the reply payload.</summary>
    byte[] Request(byte[] payload);
}

/// <summary>The Windows OpenSSH agent (and anything that imitates it, such as KeePassXC's own pipe mode).</summary>
internal sealed class PipeTransport(string pipe, string name) : IAgentTransport
{
    public string Name => name;

    public byte[] Request(byte[] payload)
    {
        using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
        stream.Connect(3000);
        return SshAgentClient.Exchange(stream, payload);
    }
}

/// <summary>An agent on a Unix socket (SSH_AUTH_SOCK). Used by the tests on Linux.</summary>
internal sealed class UnixSocketTransport(string path) : IAgentTransport
{
    public string Name => "ssh-agent";

    public byte[] Request(byte[] payload)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(socket);
        return SshAgentClient.Exchange(stream, payload);
    }
}

/// <summary>
/// PuTTY's Pageant: the request goes into a named shared-memory section, and a WM_COPYDATA
/// message carrying the section's name tells Pageant to answer in place. Pageant only serves
/// a section owned by the same user, so the section gets that owner explicitly: an elevated
/// process would otherwise create it owned by Administrators.
/// </summary>
internal sealed class PageantTransport : IAgentTransport
{
    private const int MaxMessage = 8192;
    private const uint CopyDataId = 0x804e50ba;
    private const int WmCopyData = 0x004A;

    public string Name => "Pageant";

    public static bool IsRunning => OperatingSystem.IsWindows() && FindWindow("Pageant", "Pageant") != IntPtr.Zero;

    public byte[] Request(byte[] payload)
    {
        if (payload.Length + 4 > MaxMessage)
        {
            throw new InvalidDataException("request too large for Pageant.");
        }

        var window = FindWindow("Pageant", "Pageant");
        if (window == IntPtr.Zero)
        {
            throw new IOException("Pageant is not running.");
        }

        var mapName = $"PageantRequest{GetCurrentThreadId():x8}";
        using var security = new OwnerSecurity();
        var section = CreateFileMapping(new IntPtr(-1), security.Attributes, 0x04 /* PAGE_READWRITE */, 0, MaxMessage, mapName);
        if (section == IntPtr.Zero)
        {
            throw new IOException($"could not create the Pageant request section ({Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var view = MapViewOfFile(section, 0x0002 | 0x0004 /* FILE_MAP_WRITE | READ */, 0, 0, IntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                throw new IOException($"could not map the Pageant request section ({Marshal.GetLastWin32Error()}).");
            }

            try
            {
                var frame = new byte[4 + payload.Length];
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
                payload.CopyTo(frame, 4);
                Marshal.Copy(frame, 0, view, frame.Length);

                var name = Encoding.ASCII.GetBytes(mapName + "\0");
                var namePtr = Marshal.AllocHGlobal(name.Length);
                try
                {
                    Marshal.Copy(name, 0, namePtr, name.Length);
                    // Widen the id without sign extension: through an int it became
                    // 0xFFFFFFFF804E50BA on x64, and Pageant ignores any other value.
                    var data = new CopyDataStruct { dwData = unchecked((nint)(nuint)CopyDataId), cbData = name.Length, lpData = namePtr };
                    if (SendMessage(window, WmCopyData, IntPtr.Zero, ref data) == IntPtr.Zero)
                    {
                        throw new IOException("Pageant refused the request.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }

                var header = new byte[4];
                Marshal.Copy(view, header, 0, 4);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length is 0 || length > MaxMessage - 4)   // length + 4 wrapped for values near uint.MaxValue
                {
                    throw new InvalidDataException($"Pageant reply of {length} bytes.");
                }

                var body = new byte[length];
                Marshal.Copy(view + 4, body, 0, (int)length);
                return body;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(section);
        }
    }

    /// <summary>A security descriptor that makes the current user the owner, for Pageant's check.</summary>
    private sealed class OwnerSecurity : IDisposable
    {
        private readonly IntPtr _descriptor;
        private readonly IntPtr _attributes;

        public OwnerSecurity()
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"O:{sid}D:(A;;GA;;;{sid})", 1, out _descriptor, IntPtr.Zero))
            {
                throw new IOException($"could not build the Pageant security descriptor ({Marshal.GetLastWin32Error()}).");
            }

            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), lpSecurityDescriptor = _descriptor };
            _attributes = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
            Marshal.StructureToPtr(sa, _attributes, false);
        }

        public IntPtr Attributes => _attributes;

        public void Dispose()
        {
            Marshal.FreeHGlobal(_attributes);
            LocalFree(_descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, IntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int revision, out IntPtr descriptor, IntPtr size);
}
