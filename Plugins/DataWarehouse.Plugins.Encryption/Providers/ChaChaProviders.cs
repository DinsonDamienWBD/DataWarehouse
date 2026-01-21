using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// ChaCha20-Poly1305 encryption provider.
/// </summary>
public sealed class ChaCha20Poly1305Provider : IEncryptionProvider
{
    public string AlgorithmName => "ChaCha20-Poly1305";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 12;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var chacha = new ChaCha20Poly1305(key);
        chacha.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var plaintext = new byte[payload.Ciphertext.Length];

        using var chacha = new ChaCha20Poly1305(key);
        chacha.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);

        return plaintext;
    }
}

/// <summary>
/// XChaCha20-Poly1305 encryption provider with extended nonce.
/// </summary>
public sealed class XChaCha20Poly1305Provider : IEncryptionProvider
{
    public string AlgorithmName => "XChaCha20-Poly1305";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 24;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        // XChaCha20 uses HChaCha20 to derive a subkey from the first 16 bytes of the nonce
        var subkey = HChaCha20(key, nonce.AsSpan(0, 16).ToArray());
        var shortNonce = new byte[12];
        Array.Copy(nonce, 16, shortNonce, 4, 8);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Encrypt(shortNonce, plaintext, ciphertext, tag);

        CryptographicOperations.ZeroMemory(subkey);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var subkey = HChaCha20(key, payload.Nonce.AsSpan(0, 16).ToArray());
        var shortNonce = new byte[12];
        Array.Copy(payload.Nonce, 16, shortNonce, 4, 8);

        var plaintext = new byte[payload.Ciphertext.Length];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Decrypt(shortNonce, payload.Ciphertext, payload.Tag, plaintext);

        CryptographicOperations.ZeroMemory(subkey);

        return plaintext;
    }

    private static byte[] HChaCha20(byte[] key, byte[] nonce)
    {
        // HChaCha20 initialization
        var state = new uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;

        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);

        for (int i = 0; i < 4; i++)
            state[12 + i] = BitConverter.ToUInt32(nonce, i * 4);

        // 20 rounds
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref state[0], ref state[4], ref state[8], ref state[12]);
            QuarterRound(ref state[1], ref state[5], ref state[9], ref state[13]);
            QuarterRound(ref state[2], ref state[6], ref state[10], ref state[14]);
            QuarterRound(ref state[3], ref state[7], ref state[11], ref state[15]);
            QuarterRound(ref state[0], ref state[5], ref state[10], ref state[15]);
            QuarterRound(ref state[1], ref state[6], ref state[11], ref state[12]);
            QuarterRound(ref state[2], ref state[7], ref state[8], ref state[13]);
            QuarterRound(ref state[3], ref state[4], ref state[9], ref state[14]);
        }

        // Output first and last 4 words
        var output = new byte[32];
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(state[i]).CopyTo(output, i * 4);
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(state[12 + i]).CopyTo(output, 16 + i * 4);

        return output;
    }

    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = (d << 16) | (d >> 16);
        c += d; b ^= c; b = (b << 12) | (b >> 20);
        a += b; d ^= a; d = (d << 8) | (d >> 24);
        c += d; b ^= c; b = (b << 7) | (b >> 25);
    }
}
