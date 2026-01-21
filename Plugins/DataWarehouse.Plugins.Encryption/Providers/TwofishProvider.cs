using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// Twofish encryption provider implementing the full Twofish block cipher per the official specification.
/// Supports 256-bit keys with CTR mode and HMAC authentication.
/// </summary>
public sealed class TwofishProvider : IEncryptionProvider
{
    public string AlgorithmName => "Twofish-256-CTR-HMAC";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 16;

    // q0 permutation table from Twofish specification
    private static readonly byte[] Q0 = new byte[]
    {
        0xA9, 0x67, 0xB3, 0xE8, 0x04, 0xFD, 0xA3, 0x76, 0x9A, 0x92, 0x80, 0x78, 0xE4, 0xDD, 0xD1, 0x38,
        0x0D, 0xC6, 0x35, 0x98, 0x18, 0xF7, 0xEC, 0x6C, 0x43, 0x75, 0x37, 0x26, 0xFA, 0x13, 0x94, 0x48,
        0xF2, 0xD0, 0x8B, 0x30, 0x84, 0x54, 0xDF, 0x23, 0x19, 0x5B, 0x3D, 0x59, 0xF3, 0xAE, 0xA2, 0x82,
        0x63, 0x01, 0x83, 0x2E, 0xD9, 0x51, 0x9B, 0x7C, 0xA6, 0xEB, 0xA5, 0xBE, 0x16, 0x0C, 0xE3, 0x61,
        0xC0, 0x8C, 0x3A, 0xF5, 0x73, 0x2C, 0x25, 0x0B, 0xBB, 0x4E, 0x89, 0x6B, 0x53, 0x6A, 0xB4, 0xF1,
        0xE1, 0xE6, 0xBD, 0x45, 0xE2, 0xF4, 0xB6, 0x66, 0xCC, 0x95, 0x03, 0x56, 0xD4, 0x1C, 0x1E, 0xD7,
        0xFB, 0xC3, 0x8E, 0xB5, 0xE9, 0xCF, 0xBF, 0xBA, 0xEA, 0x77, 0x39, 0xAF, 0x33, 0xC9, 0x62, 0x71,
        0x81, 0x79, 0x09, 0xAD, 0x24, 0xCD, 0xF9, 0xD8, 0xE5, 0xC5, 0xB9, 0x4D, 0x44, 0x08, 0x86, 0xE7,
        0xA1, 0x1D, 0xAA, 0xED, 0x06, 0x70, 0xB2, 0xD2, 0x41, 0x7B, 0xA0, 0x11, 0x31, 0xC2, 0x27, 0x90,
        0x20, 0xF6, 0x60, 0xFF, 0x96, 0x5C, 0xB1, 0xAB, 0x9E, 0x9C, 0x52, 0x1B, 0x5F, 0x93, 0x0A, 0xEF,
        0x91, 0x85, 0x49, 0xEE, 0x2D, 0x4F, 0x8F, 0x3B, 0x47, 0x87, 0x6D, 0x46, 0xD6, 0x3E, 0x69, 0x64,
        0x2A, 0xCE, 0xCB, 0x2F, 0xFC, 0x97, 0x05, 0x7A, 0xAC, 0x7F, 0xD5, 0x1A, 0x4B, 0x0E, 0xA7, 0x5A,
        0x28, 0x14, 0x3F, 0x29, 0x88, 0x3C, 0x4C, 0x02, 0xB8, 0xDA, 0xB0, 0x17, 0x55, 0x1F, 0x8A, 0x7D,
        0x57, 0xC7, 0x8D, 0x74, 0xB7, 0xC4, 0x9F, 0x72, 0x7E, 0x15, 0x22, 0x12, 0x58, 0x07, 0x99, 0x34,
        0x6E, 0x50, 0xDE, 0x68, 0x65, 0xBC, 0xDB, 0xF8, 0xC8, 0xA8, 0x2B, 0x40, 0xDC, 0xFE, 0x32, 0xA4,
        0xCA, 0x10, 0x21, 0xF0, 0xD3, 0x5D, 0x0F, 0x00, 0x6F, 0x9D, 0x36, 0x42, 0x4A, 0x5E, 0xC1, 0xE0
    };

    // q1 permutation table from Twofish specification
    private static readonly byte[] Q1 = new byte[]
    {
        0x75, 0xF3, 0xC6, 0xF4, 0xDB, 0x7B, 0xFB, 0xC8, 0x4A, 0xD3, 0xE6, 0x6B, 0x45, 0x7D, 0xE8, 0x4B,
        0xD6, 0x32, 0xD8, 0xFD, 0x37, 0x71, 0xF1, 0xE1, 0x30, 0x0F, 0xF8, 0x1B, 0x87, 0xFA, 0x06, 0x3F,
        0x5E, 0xBA, 0xAE, 0x5B, 0x8A, 0x00, 0xBC, 0x9D, 0x6D, 0xC1, 0xB1, 0x0E, 0x80, 0x5D, 0xD2, 0xD5,
        0xA0, 0x84, 0x07, 0x14, 0xB5, 0x90, 0x2C, 0xA3, 0xB2, 0x73, 0x4C, 0x54, 0x92, 0x74, 0x36, 0x51,
        0x38, 0xB0, 0xBD, 0x5A, 0xFC, 0x60, 0x62, 0x96, 0x6C, 0x42, 0xF7, 0x10, 0x7C, 0x28, 0x27, 0x8C,
        0x13, 0x95, 0x9C, 0xC7, 0x24, 0x46, 0x3B, 0x70, 0xCA, 0xE3, 0x85, 0xCB, 0x11, 0xD0, 0x93, 0xB8,
        0xA6, 0x83, 0x20, 0xFF, 0x9F, 0x77, 0xC3, 0xCC, 0x03, 0x6F, 0x08, 0xBF, 0x40, 0xE7, 0x2B, 0xE2,
        0x79, 0x0C, 0xAA, 0x82, 0x41, 0x3A, 0xEA, 0xB9, 0xE4, 0x9A, 0xA4, 0x97, 0x7E, 0xDA, 0x7A, 0x17,
        0x66, 0x94, 0xA1, 0x1D, 0x3D, 0xF0, 0xDE, 0xB3, 0x0B, 0x72, 0xA7, 0x1C, 0xEF, 0xD1, 0x53, 0x3E,
        0x8F, 0x33, 0x26, 0x5F, 0xEC, 0x76, 0x2A, 0x49, 0x81, 0x88, 0xEE, 0x21, 0xC4, 0x1A, 0xEB, 0xD9,
        0xC5, 0x39, 0x99, 0xCD, 0xAD, 0x31, 0x8B, 0x01, 0x18, 0x23, 0xDD, 0x1F, 0x4E, 0x2D, 0xF9, 0x48,
        0x4F, 0xF2, 0x65, 0x8E, 0x78, 0x5C, 0x58, 0x19, 0x8D, 0xE5, 0x98, 0x57, 0x67, 0x7F, 0x05, 0x64,
        0xAF, 0x63, 0xB6, 0xFE, 0xF5, 0xB7, 0x3C, 0xA5, 0xCE, 0xE9, 0x68, 0x44, 0xE0, 0x4D, 0x43, 0x69,
        0x29, 0x2E, 0xAC, 0x15, 0x59, 0xA8, 0x0A, 0x9E, 0x6E, 0x47, 0xDF, 0x34, 0x35, 0x6A, 0xCF, 0xDC,
        0x22, 0xC9, 0xC0, 0x9B, 0x89, 0xD4, 0xED, 0xAB, 0x12, 0xA2, 0x0D, 0x52, 0xBB, 0x02, 0x2F, 0xA9,
        0xD7, 0x61, 0x1E, 0xB4, 0x50, 0x04, 0xF6, 0xC2, 0x16, 0x25, 0x86, 0x56, 0x55, 0x09, 0xBE, 0x91
    };

    // Reed-Solomon matrix for key schedule (over GF(2^8) with poly 0x14D)
    private static readonly byte[,] RS = new byte[,]
    {
        { 0x01, 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E },
        { 0xA4, 0x56, 0x82, 0xF3, 0x1E, 0xC6, 0x68, 0xE5 },
        { 0x02, 0xA1, 0xFC, 0xC1, 0x47, 0xAE, 0x3D, 0x19 },
        { 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E, 0x03 }
    };

    // Twofish key schedule context
    private sealed class TwofishContext
    {
        public uint[] SubKeys = new uint[40];
        public uint[] SBoxKeys = new uint[4];
        public int KeyLength;
    }

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        var ctx = InitializeContext(encKey);

        var ciphertext = new byte[plaintext.Length];
        var counter = new byte[16];
        Array.Copy(nonce, counter, 16);

        for (int i = 0; i < plaintext.Length; i += 16)
        {
            var block = TwofishEncryptBlock(counter, ctx);
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

        var ctx = InitializeContext(encKey);

        var plaintext = new byte[payload.Ciphertext.Length];
        var counter = new byte[16];
        Array.Copy(payload.Nonce, counter, 16);

        for (int i = 0; i < payload.Ciphertext.Length; i += 16)
        {
            var block = TwofishEncryptBlock(counter, ctx);
            var blockLen = Math.Min(16, payload.Ciphertext.Length - i);

            for (int j = 0; j < blockLen; j++)
                plaintext[i + j] = (byte)(payload.Ciphertext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        return plaintext;
    }

    private static TwofishContext InitializeContext(byte[] key)
    {
        var ctx = new TwofishContext();
        int keyLen = key.Length;
        ctx.KeyLength = keyLen / 8;

        var Me = new uint[ctx.KeyLength];
        var Mo = new uint[ctx.KeyLength];

        for (int i = 0; i < ctx.KeyLength; i++)
        {
            Me[i] = BitConverter.ToUInt32(key, 8 * i);
            Mo[i] = BitConverter.ToUInt32(key, 8 * i + 4);
        }

        for (int i = 0; i < ctx.KeyLength; i++)
        {
            ctx.SBoxKeys[ctx.KeyLength - 1 - i] = ComputeReedSolomon(key, 8 * i);
        }

        uint rho = 0x01010101;
        for (int i = 0; i < 20; i++)
        {
            uint A = HFunction(2 * (uint)i * rho, Me, ctx.KeyLength);
            uint B = HFunction((2 * (uint)i + 1) * rho, Mo, ctx.KeyLength);
            B = RotateLeft(B, 8);

            ctx.SubKeys[2 * i] = A + B;
            ctx.SubKeys[2 * i + 1] = RotateLeft(A + 2 * B, 9);
        }

        return ctx;
    }

    private static uint ComputeReedSolomon(byte[] key, int offset)
    {
        var result = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = 0;
            for (int j = 0; j < 8; j++)
            {
                result[i] ^= GfMult(RS[i, j], key[offset + j], 0x14D);
            }
        }
        return BitConverter.ToUInt32(result, 0);
    }

    private static uint HFunction(uint X, uint[] L, int k)
    {
        byte[] y = new byte[4];
        y[0] = (byte)X;
        y[1] = (byte)(X >> 8);
        y[2] = (byte)(X >> 16);
        y[3] = (byte)(X >> 24);

        if (k == 4)
        {
            y[0] = Q1[y[0] ^ (byte)L[3]];
            y[1] = Q0[y[1] ^ (byte)(L[3] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(L[3] >> 16)];
            y[3] = Q1[y[3] ^ (byte)(L[3] >> 24)];
        }

        if (k >= 3)
        {
            y[0] = Q1[y[0] ^ (byte)L[2]];
            y[1] = Q1[y[1] ^ (byte)(L[2] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(L[2] >> 16)];
            y[3] = Q0[y[3] ^ (byte)(L[2] >> 24)];
        }

        y[0] = Q1[Q0[Q0[y[0] ^ (byte)L[1]] ^ (byte)L[0]]];
        y[1] = Q0[Q0[Q1[y[1] ^ (byte)(L[1] >> 8)] ^ (byte)(L[0] >> 8)]];
        y[2] = Q1[Q1[Q0[y[2] ^ (byte)(L[1] >> 16)] ^ (byte)(L[0] >> 16)]];
        y[3] = Q0[Q1[Q1[y[3] ^ (byte)(L[1] >> 24)] ^ (byte)(L[0] >> 24)]];

        return ApplyMDS(y);
    }

    private static uint GFunction(uint X, uint[] sBoxKeys, int k)
    {
        byte[] y = new byte[4];
        y[0] = (byte)X;
        y[1] = (byte)(X >> 8);
        y[2] = (byte)(X >> 16);
        y[3] = (byte)(X >> 24);

        if (k == 4)
        {
            y[0] = Q1[y[0] ^ (byte)sBoxKeys[3]];
            y[1] = Q0[y[1] ^ (byte)(sBoxKeys[3] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(sBoxKeys[3] >> 16)];
            y[3] = Q1[y[3] ^ (byte)(sBoxKeys[3] >> 24)];
        }

        if (k >= 3)
        {
            y[0] = Q1[y[0] ^ (byte)sBoxKeys[2]];
            y[1] = Q1[y[1] ^ (byte)(sBoxKeys[2] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(sBoxKeys[2] >> 16)];
            y[3] = Q0[y[3] ^ (byte)(sBoxKeys[2] >> 24)];
        }

        y[0] = Q1[Q0[Q0[y[0] ^ (byte)sBoxKeys[1]] ^ (byte)sBoxKeys[0]]];
        y[1] = Q0[Q0[Q1[y[1] ^ (byte)(sBoxKeys[1] >> 8)] ^ (byte)(sBoxKeys[0] >> 8)]];
        y[2] = Q1[Q1[Q0[y[2] ^ (byte)(sBoxKeys[1] >> 16)] ^ (byte)(sBoxKeys[0] >> 16)]];
        y[3] = Q0[Q1[Q1[y[3] ^ (byte)(sBoxKeys[1] >> 24)] ^ (byte)(sBoxKeys[0] >> 24)]];

        return ApplyMDS(y);
    }

    private static uint ApplyMDS(byte[] y)
    {
        byte[] result = new byte[4];

        result[0] = (byte)(GfMult(0x01, y[0], 0x169) ^ GfMult(0xEF, y[1], 0x169) ^
                          GfMult(0x5B, y[2], 0x169) ^ GfMult(0x5B, y[3], 0x169));
        result[1] = (byte)(GfMult(0x5B, y[0], 0x169) ^ GfMult(0xEF, y[1], 0x169) ^
                          GfMult(0xEF, y[2], 0x169) ^ GfMult(0x01, y[3], 0x169));
        result[2] = (byte)(GfMult(0xEF, y[0], 0x169) ^ GfMult(0x5B, y[1], 0x169) ^
                          GfMult(0x01, y[2], 0x169) ^ GfMult(0xEF, y[3], 0x169));
        result[3] = (byte)(GfMult(0xEF, y[0], 0x169) ^ GfMult(0x01, y[1], 0x169) ^
                          GfMult(0xEF, y[2], 0x169) ^ GfMult(0x5B, y[3], 0x169));

        return BitConverter.ToUInt32(result, 0);
    }

    private static byte GfMult(byte a, byte b, int poly)
    {
        int result = 0;
        int aa = a;
        int bb = b;

        while (aa != 0)
        {
            if ((aa & 1) != 0)
                result ^= bb;

            bb <<= 1;
            if ((bb & 0x100) != 0)
                bb ^= poly;

            aa >>= 1;
        }

        return (byte)result;
    }

    private static void FFunction(uint R0, uint R1, uint[] sBoxKeys, int k,
                                  uint subKey0, uint subKey1, out uint F0, out uint F1)
    {
        uint T0 = GFunction(R0, sBoxKeys, k);
        uint T1 = GFunction(RotateLeft(R1, 8), sBoxKeys, k);

        F0 = T0 + T1 + subKey0;
        F1 = T0 + 2 * T1 + subKey1;
    }

    private static byte[] TwofishEncryptBlock(byte[] block, TwofishContext ctx)
    {
        uint R0 = BitConverter.ToUInt32(block, 0) ^ ctx.SubKeys[0];
        uint R1 = BitConverter.ToUInt32(block, 4) ^ ctx.SubKeys[1];
        uint R2 = BitConverter.ToUInt32(block, 8) ^ ctx.SubKeys[2];
        uint R3 = BitConverter.ToUInt32(block, 12) ^ ctx.SubKeys[3];

        for (int round = 0; round < 16; round++)
        {
            FFunction(R0, R1, ctx.SBoxKeys, ctx.KeyLength,
                     ctx.SubKeys[8 + 2 * round], ctx.SubKeys[9 + 2 * round],
                     out uint F0, out uint F1);

            R2 = RotateRight(R2 ^ F0, 1);
            R3 = RotateLeft(R3, 1) ^ F1;

            if (round < 15)
            {
                (R0, R2) = (R2, R0);
                (R1, R3) = (R3, R1);
            }
        }

        (R0, R2) = (R2, R0);
        (R1, R3) = (R3, R1);

        R0 ^= ctx.SubKeys[4];
        R1 ^= ctx.SubKeys[5];
        R2 ^= ctx.SubKeys[6];
        R3 ^= ctx.SubKeys[7];

        var output = new byte[16];
        BitConverter.GetBytes(R0).CopyTo(output, 0);
        BitConverter.GetBytes(R1).CopyTo(output, 4);
        BitConverter.GetBytes(R2).CopyTo(output, 8);
        BitConverter.GetBytes(R3).CopyTo(output, 12);

        return output;
    }

    private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
    private static uint RotateRight(uint value, int count) => (value >> count) | (value << (32 - count));

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }
}
