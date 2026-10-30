using System.Buffers.Binary;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Quarterdeck.Core.Security;

namespace Quarterdeck.Core.Terminal;

/// <summary>A VNC server's certificate, for the user to accept or refuse (see <see cref="VncTls.Ask"/>).</summary>
public sealed record VncCertificateQuestion(string HostPort, string Subject, string Issuer, DateTime ValidFrom, DateTime ValidTo,
                                            string Sha256, string? PreviousSha256)
{
    public bool Changed => PreviousSha256 is not null;
}

/// <summary>
/// TLS for a VNC connection: the VeNCrypt security type (RFB 3.7+), as TigerVNC, libvncserver and
/// x11vnc offer it. The bridge runs the outer handshake up to the TLS session itself, then shows
/// noVNC the inner handshake it would have had without TLS (None, VNC authentication or Plain),
/// so the page needs no TLS of its own. Certificates are trusted on first use like SSH host keys
/// and the Web tab's: the first is shown and kept on a yes, the same one later passes, a different
/// one is refused. The anonymous subtypes (TLSNone, TLSVnc, TLSPlain) use a key exchange without a
/// certificate that Windows' TLS does not offer, so only the X509 subtypes are taken.
/// </summary>
public static class VncTls
{
    private const byte TypeNone = 1, TypeVncAuth = 2, TypeVeNCrypt = 19;
    private const uint SubPlain = 256, SubX509None = 260, SubX509Vnc = 261, SubX509Plain = 262;

    /// <summary>Set by the app to ask the user. Unset (tests, no UI) means unknown certificates are refused.</summary>
    public static Func<VncCertificateQuestion, bool>? Ask { get; set; }

    /// <summary>What the bridge presents to the page after TLS: the inner type, and the stream to pump.</summary>
    public sealed record Session(Stream Stream, uint Subtype, string Fingerprint)
    {
        public string SubtypeName => Subtype switch
        {
            SubX509None => "X509None",
            SubX509Vnc => "X509Vnc",
            SubX509Plain => "X509Plain",
            _ => Subtype.ToString()
        };
    }

    /// <summary>
    /// Runs the RFB version and security negotiation on <paramref name="raw"/>, picks a VeNCrypt
    /// X509 subtype and brings up TLS. Throws <see cref="VncTlsException"/> with a reason the tab can show.
    /// </summary>
    public static async Task<Session> NegotiateAsync(Stream raw, string host, int port, bool hasUsername, CancellationToken token)
    {
        var hostPort = $"{host}:{port}";
        var version = await ReadExactAsync(raw, 12, token).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(version);
        if (!text.StartsWith("RFB 003.", StringComparison.Ordinal) || !int.TryParse(text.AsSpan(8, 3), out var minor))
        {
            throw new VncTlsException($"{hostPort} did not answer as a VNC server ({text.Trim()}).");
        }

        if (minor < 7)
        {
            throw new VncTlsException($"{hostPort} speaks RFB 3.{minor}, which has no TLS: VeNCrypt needs 3.7 or later.");
        }

        await raw.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), token).ConfigureAwait(false);

        var count = (await ReadExactAsync(raw, 1, token).ConfigureAwait(false))[0];
        if (count == 0)
        {
            var reason = await ReadReasonAsync(raw, token).ConfigureAwait(false);
            throw new VncTlsException($"{hostPort} refused the connection: {reason}");
        }

        var types = await ReadExactAsync(raw, count, token).ConfigureAwait(false);
        if (!types.Contains(TypeVeNCrypt))
        {
            throw new VncTlsException($"{hostPort} offers no TLS (security types {string.Join(", ", types.Select(Name))}). " +
                                      "Turn on VeNCrypt with a certificate on the server (TigerVNC: SecurityTypes=X509Vnc), or untick TLS on the shortcut.");
        }

        await raw.WriteAsync(new[] { TypeVeNCrypt }, token).ConfigureAwait(false);

        // VeNCrypt version: the server's, then ours (0.2), then its yes.
        var ver = await ReadExactAsync(raw, 2, token).ConfigureAwait(false);
        if (ver[0] != 0 || ver[1] < 2)
        {
            throw new VncTlsException($"{hostPort} speaks VeNCrypt {ver[0]}.{ver[1]}; 0.2 is needed.");
        }

        await raw.WriteAsync(new byte[] { 0, 2 }, token).ConfigureAwait(false);
        if ((await ReadExactAsync(raw, 1, token).ConfigureAwait(false))[0] != 0)
        {
            throw new VncTlsException($"{hostPort} did not accept VeNCrypt 0.2.");
        }

        var subCount = (await ReadExactAsync(raw, 1, token).ConfigureAwait(false))[0];
        var subBytes = await ReadExactAsync(raw, 4 * subCount, token).ConfigureAwait(false);
        var subtypes = Enumerable.Range(0, subCount).Select(i => BinaryPrimitives.ReadUInt32BigEndian(subBytes.AsSpan(4 * i))).ToList();

        // A password challenge first (the usual), then nothing, then a login (needs a user name).
        var wanted = new List<uint> { SubX509Vnc, SubX509None };
        if (hasUsername)
        {
            wanted.Add(SubX509Plain);
        }

        var chosen = wanted.FirstOrDefault(subtypes.Contains);
        if (chosen == 0)
        {
            var anonymous = subtypes.Any(s => s is 257 or 258 or 259);
            throw new VncTlsException($"{hostPort} offers TLS only in forms this PC cannot use ({string.Join(", ", subtypes.Select(SubName))}). " +
                                      (anonymous ? "Its TLS has no certificate (anonymous Diffie-Hellman), which Windows does not do: give the server a certificate (TigerVNC: X509Cert and X509Key). "
                                                 : "") +
                                      (subtypes.Contains(SubX509Plain) && !hasUsername ? "X509Plain needs a user name on the shortcut." : ""));
        }

        var pick = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(pick, chosen);
        await raw.WriteAsync(pick, token).ConfigureAwait(false);
        if ((await ReadExactAsync(raw, 1, token).ConfigureAwait(false))[0] != 1)
        {
            throw new VncTlsException($"{hostPort} refused {SubName(chosen)}.");
        }

        string fingerprint = "";
        string? refusal = null;
        var ssl = new SslStream(raw, leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            fingerprint = Fingerprint(certificate);
            var ok = Trust(hostPort, new X509Certificate2(certificate), fingerprint, out refusal);
            return ok;
        });

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                // Checked by the callback above (trust on first use), not by the system's roots.
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token).ConfigureAwait(false);
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new VncTlsException(refusal ?? $"TLS to {hostPort} failed: {ex.Message}");
        }

        return new Session(ssl, chosen, fingerprint);
    }

    // The page opens the socket twice in a row (a probe, then noVNC): one decision serves both,
    // so the user is asked once, and a no is not asked again within the same attempt.
    private static readonly object TrustGate = new();
    private static readonly Dictionary<string, DateTime> RefusedUntil = new();

    private static bool Trust(string hostPort, X509Certificate2 cert, string fingerprint, out string? refusal)
    {
        lock (TrustGate)
        {
            return TrustLocked(hostPort, cert, fingerprint, out refusal);
        }
    }

    private static bool TrustLocked(string hostPort, X509Certificate2 cert, string fingerprint, out string? refusal)
    {
        var key = "vnc " + hostPort;
        refusal = null;
        if (RefusedUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow)
        {
            refusal = $"The certificate of {hostPort} ({fingerprint}) was not trusted, so the connection was not made.";
            return false;
        }

        switch (WebCertificates.Check(key, fingerprint))
        {
            case WebCertificates.Verdict.Known:
                return true;
            case WebCertificates.Verdict.Changed:
                refusal = $"Refused: {hostPort} now shows a different certificate from the one you accepted ({fingerprint}). " +
                           "That is what an intercepted connection looks like. If it was replaced on purpose, forget the old one in the shortcut's properties.";
                return false;
        }

        var question = new VncCertificateQuestion(hostPort, cert.Subject, cert.Issuer, cert.NotBefore, cert.NotAfter, fingerprint, null);
        if (Ask?.Invoke(question) == true)
        {
            WebCertificates.Remember(key, fingerprint);
            return true;
        }

        RefusedUntil[key] = DateTime.UtcNow.AddSeconds(10);
        refusal = $"The certificate of {hostPort} ({fingerprint}) was not trusted, so the connection was not made.";
        return false;
    }

    /// <summary>Forgets the certificate kept for a server, so the next connection asks again.</summary>
    public static void Forget(string host, int port) => WebCertificates.Forget($"vnc {host}:{port}");

    /// <summary>
    /// The name a server's certificate is kept under. Through a jump host the VNC host is often
    /// "localhost", which would be the same name for every server behind a different jump.
    /// </summary>
    public static string TrustHost(string host, string jumpHost) =>
        jumpHost.Trim().Length == 0 ? host : $"{host} via {jumpHost.Trim()}";

    public static string? Known(string host, int port) => WebCertificates.Fingerprint($"vnc {host}:{port}");

    /// <summary>
    /// The handshake noVNC gets instead of the server's: RFB 3.8, one security type standing for
    /// the VeNCrypt subtype already agreed, up to the point where the bytes it sends next are the
    /// ones the server expects inside TLS (a challenge answer, a login, or its ClientInit).
    /// </summary>
    public static async Task PresentAsync(WebSocket page, uint subtype, CancellationToken token)
    {
        await SendAsync(page, Encoding.ASCII.GetBytes("RFB 003.008\n"), token).ConfigureAwait(false);
        var clientVersion = await ReceiveAsync(page, 12, token).ConfigureAwait(false);
        if (!Encoding.ASCII.GetString(clientVersion).StartsWith("RFB 003.", StringComparison.Ordinal))
        {
            throw new VncTlsException("The viewer did not answer with an RFB version.");
        }

        if (subtype == SubX509Plain)
        {
            // Plain lives inside VeNCrypt for the viewer too: a short VeNCrypt of its own, Plain the only subtype.
            await SendAsync(page, new byte[] { 1, TypeVeNCrypt }, token).ConfigureAwait(false);
            await ExpectAsync(page, TypeVeNCrypt, token).ConfigureAwait(false);
            await SendAsync(page, new byte[] { 0, 2 }, token).ConfigureAwait(false);
            await ReceiveAsync(page, 2, token).ConfigureAwait(false);
            await SendAsync(page, new byte[] { 0 }, token).ConfigureAwait(false);
            var offer = new byte[5];
            offer[0] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(offer.AsSpan(1), SubPlain);
            await SendAsync(page, offer, token).ConfigureAwait(false);
            var picked = await ReceiveAsync(page, 4, token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(picked) != SubPlain)
            {
                throw new VncTlsException("The viewer did not pick Plain.");
            }

            return;   // the login it sends next is what the server waits for
        }

        var inner = subtype == SubX509Vnc ? TypeVncAuth : TypeNone;
        await SendAsync(page, new byte[] { 1, inner }, token).ConfigureAwait(false);
        await ExpectAsync(page, inner, token).ConfigureAwait(false);
        // Next from the server: the 16-byte challenge (VncAuth) or SecurityResult (None); both pass straight through.
    }

    private static async Task ExpectAsync(WebSocket page, byte type, CancellationToken token)
    {
        var got = await ReceiveAsync(page, 1, token).ConfigureAwait(false);
        if (got[0] != type)
        {
            throw new VncTlsException($"The viewer chose security type {got[0]} where {type} was offered.");
        }
    }

    private static Task SendAsync(WebSocket page, byte[] bytes, CancellationToken token) =>
        page.SendAsync(bytes, WebSocketMessageType.Binary, true, token);

    private static async Task<byte[]> ReceiveAsync(WebSocket page, int count, CancellationToken token)
    {
        var bytes = new byte[count];
        var have = 0;
        while (have < count)
        {
            var result = await page.ReceiveAsync(bytes.AsMemory(have), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close || result.Count == 0)
            {
                throw new VncTlsException("The viewer closed during the handshake.");
            }

            have += result.Count;
        }

        return bytes;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var bytes = new byte[count];
        var have = 0;
        while (have < count)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(have), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new VncTlsException("The server closed the connection during the handshake.");
            }

            have += read;
        }

        return bytes;
    }

    private static async Task<string> ReadReasonAsync(Stream stream, CancellationToken token)
    {
        var len = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(stream, 4, token).ConfigureAwait(false));
        return Encoding.UTF8.GetString(await ReadExactAsync(stream, (int)Math.Min(len, 1024), token).ConfigureAwait(false));
    }

    private static string Fingerprint(X509Certificate certificate) =>
        string.Join(':', SHA256.HashData(certificate.GetRawCertData()).Select(b => b.ToString("X2")));

    private static string Name(byte type) => type switch
    {
        TypeNone => "None",
        TypeVncAuth => "VNC authentication",
        5 => "RA2",
        6 => "RA2ne",
        16 => "Tight",
        18 => "TLS",
        TypeVeNCrypt => "VeNCrypt",
        30 => "Apple",
        _ => type.ToString()
    };

    private static string SubName(uint s) => s switch
    {
        SubPlain => "Plain",
        257 => "TLSNone",
        258 => "TLSVnc",
        259 => "TLSPlain",
        SubX509None => "X509None",
        SubX509Vnc => "X509Vnc",
        SubX509Plain => "X509Plain",
        _ => s.ToString()
    };
}

public sealed class VncTlsException(string message) : Exception(message);
