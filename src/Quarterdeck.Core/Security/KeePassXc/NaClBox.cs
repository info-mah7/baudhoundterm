using System.Runtime.InteropServices;

namespace Quarterdeck.Core.Security.KeePassXc;

/// <summary>
/// NaCl crypto_box (Curve25519, XSalsa20, Poly1305) through libsodium, the cipher KeePassXC's
/// browser protocol uses. .NET has no XSalsa20, and this is not code to write by hand.
/// </summary>
internal static class NaClBox
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 24;
    private const int MacBytes = 16;

    private const string Lib = "libsodium";

    static NaClBox()
    {
        if (sodium_init() < 0)
        {
            throw new InvalidOperationException("libsodium failed to initialise.");
        }
    }

    public static (byte[] Public, byte[] Secret) KeyPair()
    {
        var pk = new byte[KeyBytes];
        var sk = new byte[KeyBytes];
        Check(crypto_box_keypair(pk, sk), "make a key pair");
        return (pk, sk);
    }

    public static byte[] Seal(byte[] message, byte[] nonce, byte[] theirPublic, byte[] mySecret)
    {
        var cipher = new byte[message.Length + MacBytes];
        Check(crypto_box_easy(cipher, message, (ulong)message.Length, nonce, theirPublic, mySecret), "encrypt");
        return cipher;
    }

    /// <summary>Returns null when the message does not authenticate.</summary>
    public static byte[]? Open(byte[] cipher, byte[] nonce, byte[] theirPublic, byte[] mySecret)
    {
        if (cipher.Length < MacBytes)
        {
            return null;
        }

        var message = new byte[cipher.Length - MacBytes];
        return crypto_box_open_easy(message, cipher, (ulong)cipher.Length, nonce, theirPublic, mySecret) == 0 ? message : null;
    }

    /// <summary>libsodium's sodium_increment: the nonce as a little-endian number, plus one.</summary>
    public static byte[] Increment(byte[] nonce)
    {
        var n = (byte[])nonce.Clone();
        for (var i = 0; i < n.Length && ++n[i] == 0; i++)
        {
        }

        return n;
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"libsodium could not {what}.");
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sodium_init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_box_keypair(byte[] pk, byte[] sk);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_box_easy(byte[] c, byte[] m, ulong mlen, byte[] n, byte[] pk, byte[] sk);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_box_open_easy(byte[] m, byte[] c, ulong clen, byte[] n, byte[] pk, byte[] sk);
}
