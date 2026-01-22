using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// Full Blake2b implementation per RFC 7693 for Argon2.
/// Supports variable output length (1-64 bytes), keyed hashing, and proper compression.
/// </summary>
public sealed class Blake2bHasher
{
    private static readonly ulong[] IV = new ulong[]
    {
        0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL,
        0x3C6EF372FE94F82BUL, 0xA54FF53A5F1D36F1UL,
        0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL,
        0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL
    };

    private static readonly byte[,] SIGMA = new byte[,]
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 }
    };

    public static byte[] Hash(byte[] data, int outputLength) => Hash(data, outputLength, null);

    public static byte[] Hash(byte[] data, int outputLength, byte[]? key)
    {
        if (outputLength < 1 || outputLength > 64)
            throw new ArgumentOutOfRangeException(nameof(outputLength), "Output length must be 1-64 bytes");

        int keyLen = key?.Length ?? 0;
        if (keyLen > 64)
            throw new ArgumentException("Key length must be 0-64 bytes", nameof(key));

        var h = new ulong[8];
        Array.Copy(IV, h, 8);
        h[0] ^= 0x01010000UL ^ ((ulong)keyLen << 8) ^ (ulong)outputLength;

        ulong bytesCompressed = 0;
        int dataOffset = 0;
        int dataLen = data.Length;

        byte[] block = new byte[128];
        if (keyLen > 0)
        {
            Array.Copy(key!, block, keyLen);
            bytesCompressed = 128;
            Compress(h, block, bytesCompressed, false);
        }

        while (dataLen - dataOffset > 128)
        {
            Array.Copy(data, dataOffset, block, 0, 128);
            dataOffset += 128;
            bytesCompressed += 128;
            Compress(h, block, bytesCompressed, false);
        }

        int remaining = dataLen - dataOffset;
        Array.Clear(block, 0, 128);
        if (remaining > 0)
            Array.Copy(data, dataOffset, block, 0, remaining);
        bytesCompressed += (ulong)remaining;
        Compress(h, block, bytesCompressed, true);

        byte[] output = new byte[outputLength];
        for (int i = 0; i < outputLength; i++)
            output[i] = (byte)(h[i / 8] >> (8 * (i % 8)));

        return output;
    }

    public static byte[] HashLong(byte[] data, int outputLength)
    {
        if (outputLength <= 64)
        {
            var input = new byte[4 + data.Length];
            BitConverter.GetBytes(outputLength).CopyTo(input, 0);
            Array.Copy(data, 0, input, 4, data.Length);
            return Hash(input, outputLength);
        }

        var result = new byte[outputLength];
        int pos = 0;

        var firstInput = new byte[4 + data.Length];
        BitConverter.GetBytes(outputLength).CopyTo(firstInput, 0);
        Array.Copy(data, 0, firstInput, 4, data.Length);
        var v = Hash(firstInput, 64);

        Array.Copy(v, 0, result, pos, 32);
        pos += 32;

        while (outputLength - pos > 64)
        {
            v = Hash(v, 64);
            Array.Copy(v, 0, result, pos, 32);
            pos += 32;
        }

        int remaining = outputLength - pos;
        if (remaining > 0)
        {
            v = Hash(v, remaining);
            Array.Copy(v, 0, result, pos, remaining);
        }

        return result;
    }

    private static void Compress(ulong[] h, byte[] block, ulong bytesCompressed, bool isLast)
    {
        var v = new ulong[16];
        Array.Copy(h, 0, v, 0, 8);
        Array.Copy(IV, 0, v, 8, 8);

        v[12] ^= bytesCompressed;
        if (isLast)
            v[14] = ~v[14];

        var m = new ulong[16];
        for (int i = 0; i < 16; i++)
            m[i] = BitConverter.ToUInt64(block, i * 8);

        for (int round = 0; round < 12; round++)
        {
            G(ref v[0], ref v[4], ref v[8], ref v[12], m[SIGMA[round, 0]], m[SIGMA[round, 1]]);
            G(ref v[1], ref v[5], ref v[9], ref v[13], m[SIGMA[round, 2]], m[SIGMA[round, 3]]);
            G(ref v[2], ref v[6], ref v[10], ref v[14], m[SIGMA[round, 4]], m[SIGMA[round, 5]]);
            G(ref v[3], ref v[7], ref v[11], ref v[15], m[SIGMA[round, 6]], m[SIGMA[round, 7]]);

            G(ref v[0], ref v[5], ref v[10], ref v[15], m[SIGMA[round, 8]], m[SIGMA[round, 9]]);
            G(ref v[1], ref v[6], ref v[11], ref v[12], m[SIGMA[round, 10]], m[SIGMA[round, 11]]);
            G(ref v[2], ref v[7], ref v[8], ref v[13], m[SIGMA[round, 12]], m[SIGMA[round, 13]]);
            G(ref v[3], ref v[4], ref v[9], ref v[14], m[SIGMA[round, 14]], m[SIGMA[round, 15]]);
        }

        for (int i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G(ref ulong a, ref ulong b, ref ulong c, ref ulong d, ulong x, ulong y)
    {
        a = a + b + x;
        d = RotateRight(d ^ a, 32);
        c = c + d;
        b = RotateRight(b ^ c, 24);
        a = a + b + y;
        d = RotateRight(d ^ a, 16);
        c = c + d;
        b = RotateRight(b ^ c, 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RotateRight(ulong value, int bits) => (value >> bits) | (value << (64 - bits));
}
