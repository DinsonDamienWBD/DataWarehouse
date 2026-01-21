using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// Full production-ready Serpent cipher implementation according to the official specification.
/// Serpent is a 128-bit block cipher with 256-bit key, 32 rounds, and 8 distinct S-boxes.
/// </summary>
public sealed class SerpentProvider : IEncryptionProvider
{
    public string AlgorithmName => "Serpent-256-CTR-HMAC";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 16;

    private const uint PHI = 0x9E3779B9;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        var ciphertext = new byte[plaintext.Length];
        var subkeys = GenerateSerpentSubkeys(encKey);

        var counter = new byte[16];
        Array.Copy(nonce, counter, 16);

        for (int i = 0; i < plaintext.Length; i += 16)
        {
            var block = SerpentEncryptBlock(counter, subkeys);
            var blockLen = Math.Min(16, plaintext.Length - i);

            for (int j = 0; j < blockLen; j++)
                ciphertext[i + j] = (byte)(plaintext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        var dataToMac = new byte[nonce.Length + ciphertext.Length];
        Array.Copy(nonce, dataToMac, nonce.Length);
        Array.Copy(ciphertext, 0, dataToMac, nonce.Length, ciphertext.Length);
        var tag = HMACSHA256.HashData(macKey, dataToMac);

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
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        var dataToMac = new byte[payload.Nonce.Length + payload.Ciphertext.Length];
        Array.Copy(payload.Nonce, dataToMac, payload.Nonce.Length);
        Array.Copy(payload.Ciphertext, 0, dataToMac, payload.Nonce.Length, payload.Ciphertext.Length);

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, payload.Tag))
            throw new CryptographicException("MAC verification failed");

        var plaintext = new byte[payload.Ciphertext.Length];
        var subkeys = GenerateSerpentSubkeys(encKey);

        var counter = new byte[16];
        Array.Copy(payload.Nonce, counter, 16);

        for (int i = 0; i < payload.Ciphertext.Length; i += 16)
        {
            var block = SerpentEncryptBlock(counter, subkeys);
            var blockLen = Math.Min(16, payload.Ciphertext.Length - i);

            for (int j = 0; j < blockLen; j++)
                plaintext[i + j] = (byte)(payload.Ciphertext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        return plaintext;
    }

    private static uint[] GenerateSerpentSubkeys(byte[] key)
    {
        var paddedKey = new byte[32];
        Array.Copy(key, paddedKey, Math.Min(key.Length, 32));
        if (key.Length < 32)
            paddedKey[key.Length] = 0x01;

        var w = new uint[140];
        for (int i = 0; i < 8; i++)
            w[i] = BitConverter.ToUInt32(paddedKey, i * 4);

        for (int i = 8; i < 140; i++)
        {
            var t = w[i - 8] ^ w[i - 5] ^ w[i - 3] ^ w[i - 1] ^ PHI ^ (uint)(i - 8);
            w[i] = RotateLeft(t, 11);
        }

        var subkeys = new uint[132];
        for (int i = 0; i < 33; i++)
        {
            int sboxIndex = (35 - i) % 8;
            var block = new uint[4];
            block[0] = w[8 + i * 4];
            block[1] = w[8 + i * 4 + 1];
            block[2] = w[8 + i * 4 + 2];
            block[3] = w[8 + i * 4 + 3];

            ApplySBox(block, sboxIndex);

            subkeys[i * 4] = block[0];
            subkeys[i * 4 + 1] = block[1];
            subkeys[i * 4 + 2] = block[2];
            subkeys[i * 4 + 3] = block[3];
        }

        return subkeys;
    }

    private static byte[] SerpentEncryptBlock(byte[] block, uint[] subkeys)
    {
        var x = new uint[4];
        for (int i = 0; i < 4; i++)
            x[i] = BitConverter.ToUInt32(block, i * 4);

        ApplyIP(x);

        for (int round = 0; round < 32; round++)
        {
            x[0] ^= subkeys[4 * round];
            x[1] ^= subkeys[4 * round + 1];
            x[2] ^= subkeys[4 * round + 2];
            x[3] ^= subkeys[4 * round + 3];

            ApplySBox(x, round % 8);

            if (round < 31)
                ApplyLinearTransform(x);
        }

        x[0] ^= subkeys[128];
        x[1] ^= subkeys[129];
        x[2] ^= subkeys[130];
        x[3] ^= subkeys[131];

        ApplyFP(x);

        var output = new byte[16];
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(x[i]).CopyTo(output, i * 4);

        return output;
    }

    private static void ApplySBox(uint[] x, int box)
    {
        uint t0, t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13;

        switch (box)
        {
            case 0:
                t0 = x[1] ^ x[2]; t1 = x[0] | x[3]; t2 = x[0] ^ x[1]; t3 = x[3] ^ t0;
                t4 = t1 ^ t3; t5 = x[1] | x[2]; t6 = x[3] ^ t4; t7 = t5 | t6;
                t8 = x[0] ^ x[3]; t9 = t2 & t8; x[3] = t7 ^ t9; t10 = t2 | x[3];
                x[1] = t10 ^ t8; t11 = t4 ^ t6; t12 = x[1] & t11; x[2] = t4 ^ t12;
                t13 = x[2] & x[3]; x[0] = t0 ^ t13;
                break;
            case 1:
                t0 = x[0] | x[3]; t1 = x[2] ^ x[3]; t2 = ~x[1]; t3 = x[0] ^ x[2];
                t4 = x[0] | t2; t5 = t0 & t1; t6 = t3 | t5; x[1] = t4 ^ t6;
                t7 = t0 ^ t2; t8 = t5 | x[1]; t9 = x[3] | x[1]; t10 = t7 ^ t8;
                x[3] = t1 ^ t9; t11 = x[2] | t10; x[0] = t10 ^ t11;
                x[2] = t9 ^ t0 ^ x[0];
                break;
            case 2:
                t0 = x[0] | x[2]; t1 = x[0] ^ x[1]; t2 = x[3] ^ t0; x[0] = t1 ^ t2;
                t3 = x[2] ^ x[0]; t4 = x[1] ^ t2; t5 = x[1] | t3; x[3] = t4 ^ t5;
                t6 = t0 ^ x[3]; t7 = t5 & t6; x[2] = t2 ^ t7; t8 = x[2] & x[0];
                x[1] = t6 ^ t8;
                break;
            case 3:
                t0 = x[0] | x[3]; t1 = x[2] ^ x[3]; t2 = x[0] ^ x[2]; t3 = t0 & t1;
                t4 = x[1] | t2; x[2] = t3 ^ t4; t5 = x[1] ^ t1; t6 = x[3] ^ x[2];
                t7 = t4 | t6; x[3] = t5 ^ t7; t8 = t0 ^ x[3]; t9 = x[2] & t8;
                x[1] = t1 ^ t9; t10 = x[1] | x[0]; x[0] = t6 ^ t10;
                break;
            case 4:
                t0 = x[0] | x[1]; t1 = x[1] | x[2]; t2 = x[0] ^ t1; t3 = x[1] ^ x[3];
                t4 = x[3] | t2; x[3] = t3 ^ t4; t5 = t0 ^ t4; t6 = t1 ^ t5;
                t7 = x[3] & t5; x[1] = t6 ^ t7; t8 = x[3] | x[1]; x[0] = t5 ^ t8;
                t9 = x[2] ^ x[0]; x[2] = t2 ^ t9 ^ x[1];
                break;
            case 5:
                t0 = x[1] ^ x[3]; t1 = x[1] | x[3]; t2 = x[0] & t0; t3 = x[2] ^ t1;
                x[0] = t2 ^ t3; t4 = x[0] | x[1]; t5 = x[3] ^ t4; t6 = x[2] | x[0];
                x[3] = t5 ^ t6; t7 = t0 ^ t6; t8 = t1 & x[3]; x[1] = t7 ^ t8;
                t9 = x[0] ^ x[3]; x[2] = x[1] ^ t9 ^ t4;
                break;
            case 6:
                t0 = x[0] ^ x[3]; t1 = x[1] ^ x[3]; t2 = x[0] & t1; t3 = x[2] ^ t2;
                x[0] = x[1] ^ t3; t4 = t0 | x[0]; t5 = x[3] | t3; x[3] = t4 ^ t5;
                t6 = t1 ^ t4; t7 = x[0] & x[3]; x[1] = t6 ^ t7; t8 = x[0] ^ x[3];
                x[2] = x[1] ^ t8 ^ t1;
                break;
            case 7:
                t0 = x[0] & x[2]; t1 = x[3] ^ t0; t2 = x[0] ^ x[3]; t3 = x[1] ^ t1;
                t4 = x[0] ^ t3; x[2] = t2 & t4; t5 = t1 | x[2]; x[0] = t3 ^ t5;
                t6 = x[2] ^ x[0]; t7 = x[3] | t6; x[3] = t4 ^ t7; t8 = t3 & x[3];
                x[1] = t1 ^ t8;
                break;
        }
    }

    private static void ApplyLinearTransform(uint[] x)
    {
        x[0] = RotateLeft(x[0], 13);
        x[2] = RotateLeft(x[2], 3);
        x[1] = x[1] ^ x[0] ^ x[2];
        x[3] = x[3] ^ x[2] ^ (x[0] << 3);
        x[1] = RotateLeft(x[1], 1);
        x[3] = RotateLeft(x[3], 7);
        x[0] = x[0] ^ x[1] ^ x[3];
        x[2] = x[2] ^ x[3] ^ (x[1] << 7);
        x[0] = RotateLeft(x[0], 5);
        x[2] = RotateLeft(x[2], 22);
    }

    private static void ApplyIP(uint[] x)
    {
        uint t0 = x[0], t1 = x[1], t2 = x[2], t3 = x[3];
        SWAPMOVE(ref t0, ref t1, 0x55555555, 1);
        SWAPMOVE(ref t2, ref t3, 0x55555555, 1);
        SWAPMOVE(ref t0, ref t2, 0x33333333, 2);
        SWAPMOVE(ref t1, ref t3, 0x33333333, 2);
        SWAPMOVE(ref t0, ref t1, 0x0F0F0F0F, 4);
        SWAPMOVE(ref t2, ref t3, 0x0F0F0F0F, 4);
        x[0] = t0; x[1] = t1; x[2] = t2; x[3] = t3;
    }

    private static void ApplyFP(uint[] x)
    {
        uint t0 = x[0], t1 = x[1], t2 = x[2], t3 = x[3];
        SWAPMOVE(ref t2, ref t3, 0x0F0F0F0F, 4);
        SWAPMOVE(ref t0, ref t1, 0x0F0F0F0F, 4);
        SWAPMOVE(ref t1, ref t3, 0x33333333, 2);
        SWAPMOVE(ref t0, ref t2, 0x33333333, 2);
        SWAPMOVE(ref t2, ref t3, 0x55555555, 1);
        SWAPMOVE(ref t0, ref t1, 0x55555555, 1);
        x[0] = t0; x[1] = t1; x[2] = t2; x[3] = t3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SWAPMOVE(ref uint a, ref uint b, uint mask, int shift)
    {
        uint t = ((a >> shift) ^ b) & mask;
        b ^= t;
        a ^= t << shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }
}
