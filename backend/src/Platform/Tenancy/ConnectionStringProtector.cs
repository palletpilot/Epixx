using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.Platform.Tenancy;

public sealed class ConnectionStringProtector
{
    private readonly byte[] _kek;

    public ConnectionStringProtector(IConfiguration configuration)
        : this(ParseKek(configuration["Crypto:Kek"]))
    {
    }

    public ConnectionStringProtector(byte[] kek)
    {
        if (kek.Length != 32)
        {
            throw new ArgumentException("KEK must be 32 bytes.", nameof(kek));
        }

        _kek = kek;
    }

    public byte[] Protect(string plaintext) => Seal(_kek, Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(byte[] packed) => Encoding.UTF8.GetString(Open(_kek, packed));

    public (byte[] WrappedDek, byte[] Ciphertext) Encrypt(string connectionString)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        return (Seal(_kek, dek), Seal(dek, Encoding.UTF8.GetBytes(connectionString)));
    }

    public string Decrypt(byte[] wrappedDek, byte[] ciphertext)
    {
        var dek = Open(_kek, wrappedDek);
        return Encoding.UTF8.GetString(Open(dek, ciphertext));
    }

    private static byte[] ParseKek(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Crypto:Kek is not configured.");
        }

        if (value.Length == 32)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        var kek = Convert.FromBase64String(value);
        if (kek.Length != 32)
        {
            throw new InvalidOperationException("Crypto:Kek must be 32 bytes.");
        }

        return kek;
    }

    private static byte[] Seal(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var packed = new byte[12 + 16 + ciphertext.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, 12);
        ciphertext.CopyTo(packed, 28);
        return packed;
    }

    private static byte[] Open(byte[] key, byte[] packed)
    {
        var nonce = packed.AsSpan(0, 12);
        var tag = packed.AsSpan(12, 16);
        var ciphertext = packed.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
