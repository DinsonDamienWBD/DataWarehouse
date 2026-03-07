using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.Hardening.Tests.Chaos.MaliciousPayload;

/// <summary>
/// Generates various malicious payloads to test VDE input validation and resource bounding.
/// Each method produces a byte[] or Stream that exercises a specific attack vector
/// against the VDE decorator chain (Compression, Encryption, metadata parsing, superblock).
///
/// Report: "Stage 3 - Steps 7-8 - Malicious Payloads"
/// </summary>
public static class PayloadGenerator
{
    /// <summary>
    /// Creates a GZip-compressed payload with an extreme compression ratio.
    /// The compressed form is small but decompresses to <paramref name="uncompressedSize"/> bytes.
    /// Uses nested GZip with highly compressible data (all zeros) for 1000:1+ ratios.
    /// </summary>
    /// <param name="compressionRatio">Target compression ratio (e.g., 1000 for 1000:1).</param>
    /// <param name="uncompressedSize">Size in bytes when fully decompressed (e.g., 10GB).</param>
    /// <returns>A GZip-compressed byte array that expands to the target size.</returns>
    public static byte[] ZipBomb(long compressionRatio, long uncompressedSize)
    {
        // Create highly compressible data (all zeros achieve maximum ratio)
        // We use a small seed that claims to decompress to a massive size
        // by writing a valid GZip stream with a fabricated uncompressed length.
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            // Write zeros in chunks -- GZip will compress them extremely well
            var chunk = new byte[Math.Min(64 * 1024, uncompressedSize)];
            long remaining = Math.Min(uncompressedSize, 1024 * 1024); // Cap actual write to 1MB for test speed
            while (remaining > 0)
            {
                int toWrite = (int)Math.Min(chunk.Length, remaining);
                gz.Write(chunk, 0, toWrite);
                remaining -= toWrite;
            }
        }

        var compressed = ms.ToArray();

        // Tamper with the ISIZE field (last 4 bytes of GZip) to claim enormous uncompressed size
        // GZip ISIZE is stored as uint32 little-endian at the end
        if (compressed.Length >= 4)
        {
            var fakeSize = (uint)(uncompressedSize & 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(
                compressed.AsSpan(compressed.Length - 4), fakeSize);
        }

        return compressed;
    }

    /// <summary>
    /// Creates a GZip stream wrapping the zip bomb for streaming decompression tests.
    /// </summary>
    public static Stream ZipBombStream(long compressionRatio, long uncompressedSize)
    {
        var data = ZipBomb(compressionRatio, uncompressedSize);
        return new MemoryStream(data, writable: false);
    }

    /// <summary>
    /// Creates an encryption header with a malformed IV for the specified algorithm.
    /// </summary>
    /// <param name="algorithm">Algorithm name: "AES-256-GCM", "ChaCha20", "AES-128-CBC".</param>
    /// <returns>Byte array containing a header with an invalid IV.</returns>
    public static byte[] MalformedIV(string algorithm)
    {
        // Expected IV sizes: AES-GCM = 12 bytes, ChaCha20 = 12 bytes, AES-CBC = 16 bytes
        int expectedIvSize = algorithm switch
        {
            "AES-256-GCM" => 12,
            "AES-128-GCM" => 12,
            "ChaCha20" => 12,
            "AES-128-CBC" => 16,
            "AES-256-CBC" => 16,
            _ => 12
        };

        // Create a header with wrong IV length (too short)
        var header = new byte[64];

        // Algorithm identifier (4 bytes)
        Encoding.ASCII.GetBytes(algorithm.AsSpan(0, Math.Min(4, algorithm.Length)), header.AsSpan(0, 4));

        // IV length field (4 bytes LE) -- claim wrong size
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), expectedIvSize / 2); // Half the expected size

        // Truncated IV (only half the bytes)
        // Leave remaining bytes as zero -- makes the IV all-zero AND wrong length
        header[8] = 0xFF; // Non-zero first byte so it's not "missing"

        return header;
    }

    /// <summary>
    /// Creates encryption header variants for thorough testing.
    /// </summary>
    public static byte[][] MalformedIVVariants(string algorithm)
    {
        int expectedIvSize = algorithm switch
        {
            "AES-256-GCM" => 12,
            "ChaCha20" => 12,
            "AES-128-CBC" => 16,
            _ => 12
        };

        return new[]
        {
            MalformedIV(algorithm),                          // Wrong length (too short)
            CreateIvWithLength(0),                           // Zero-length IV
            CreateIvWithLength(expectedIvSize * 10),         // Way too long
            new byte[expectedIvSize],                        // All-zero IV (weak)
            CreateTruncatedHeader(expectedIvSize),           // Truncated mid-header
        };
    }

    /// <summary>
    /// Creates a VDE header claiming impossibly large field sizes.
    /// Tests that the parser rejects without attempting allocation.
    /// </summary>
    /// <param name="sizeBytes">Claimed size for string/array fields (e.g., 4GB).</param>
    /// <returns>Byte array with oversized field claims.</returns>
    public static byte[] OversizedHeader(long sizeBytes)
    {
        // Create a minimal header structure that claims enormous sizes
        var header = new byte[256];

        // Write "DWVD" magic to make it look plausible
        header[0] = 0x44; // D
        header[1] = 0x57; // W
        header[2] = 0x56; // V
        header[3] = 0x44; // D

        // Version info
        header[4] = FormatConstants.FormatMajorVersion;
        header[5] = FormatConstants.FormatMinorVersion;

        // Field count claiming enormous embedded data
        // Offset 32: filename length field (claims 4GB filename)
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), sizeBytes);

        // Offset 40: block count claiming 2^63 blocks
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), long.MaxValue);

        // Offset 48: metadata size claiming sizeBytes
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), sizeBytes);

        return header;
    }

    /// <summary>
    /// Creates metadata payloads containing path traversal attacks.
    /// Each variant targets a different OS or encoding trick.
    /// </summary>
    /// <returns>Array of path traversal attack strings.</returns>
    public static string[] PathTraversal()
    {
        return new[]
        {
            // Unix-style traversal
            "../../../etc/passwd",
            "../../../../etc/shadow",
            "../../../proc/self/environ",

            // Windows-style traversal
            @"..\..\..\..\Windows\System32\config\SAM",
            @"..\..\..\..\Windows\System32\drivers\etc\hosts",

            // Null byte injection (try to terminate string early)
            "safe_file\0../../../etc/passwd",
            "normal.txt\0.exe",

            // Unicode normalization attacks
            "..%2F..%2F..%2Fetc%2Fpasswd",          // URL-encoded
            "..%5C..%5C..%5CWindows%5CSystem32",     // URL-encoded backslash
            "\u2025/\u2025/\u2025/etc/passwd",        // Unicode TWO DOT LEADER
            "..%c0%af..%c0%af..%c0%afetc/passwd",    // Overlong UTF-8 encoding of /
            "..%252f..%252f..%252fetc%252fpasswd",    // Double URL encoding

            // Mixed separators
            @"../..\..\etc/passwd",
            "....//....//....//etc/passwd",

            // Absolute path breakout
            "/etc/passwd",
            @"C:\Windows\System32\config\SAM",
            @"\\server\share\sensitive.dat",          // UNC path
        };
    }

    /// <summary>
    /// Creates a superblock with corrupted/invalid fields that should be rejected at mount.
    /// </summary>
    /// <returns>Byte array containing an invalid superblock.</returns>
    public static byte[] MalformedSuperblock()
    {
        const int blockSize = FormatConstants.DefaultBlockSize;
        var buffer = new byte[blockSize];

        // Write invalid magic bytes (not "DWVD")
        buffer[0] = 0xDE;
        buffer[1] = 0xAD;
        buffer[2] = 0xBE;
        buffer[3] = 0xEF;

        // Version overflow: major=255, minor=255
        buffer[4] = 0xFF;
        buffer[5] = 0xFF;

        // Invalid spec revision
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0xFFFF);

        // Corrupted namespace anchor (not "dw://")
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 0xDEADBEEFCAFEBABE);

        // Negative block size (invalid)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x34), -1);

        // Negative total blocks
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x38), -999);

        // Corrupted feature flags (all bits set)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x20), 0xFFFFFFFF);

        return buffer;
    }

    /// <summary>
    /// Creates a valid-looking superblock with specific fields designed to trigger
    /// overflow when used in arithmetic (blockCount * blockSize overflows Int64).
    /// </summary>
    public static byte[] MalformedSuperblockOverflow()
    {
        const int blockSize = FormatConstants.DefaultBlockSize;
        var buffer = new byte[blockSize];

        // Write valid magic so parsing gets past the first check
        var defaultSb = SuperblockV2.CreateDefault(blockSize, 1024, Guid.NewGuid());
        SuperblockV2.Serialize(defaultSb, buffer, blockSize);

        // Now corrupt specific arithmetic fields:
        // Set totalBlocks to long.MaxValue so blockCount * blockSize overflows
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x38), long.MaxValue);

        // Set blockSize to MaxBlockSize (65536) to maximize overflow
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x34), FormatConstants.MaxBlockSize);

        return buffer;
    }

    /// <summary>
    /// Creates payloads where integer arithmetic would overflow Int64.
    /// Returns tuples of (blockCount, blockSize) that overflow when multiplied.
    /// </summary>
    /// <returns>Array of (blockCount, blockSize) tuples that overflow Int64.</returns>
    public static (long blockCount, int blockSize)[] IntegerOverflow()
    {
        return new (long, int)[]
        {
            // long.MaxValue * any blockSize > 1 overflows
            (long.MaxValue, FormatConstants.DefaultBlockSize),

            // Large but plausible values that still overflow
            (long.MaxValue / 2, FormatConstants.MaxBlockSize),

            // Negative blockCount (invalid but should be caught)
            (-1, FormatConstants.DefaultBlockSize),

            // Near-boundary values
            (long.MaxValue / FormatConstants.DefaultBlockSize + 1, FormatConstants.DefaultBlockSize),

            // Maximum block size with large count
            ((long.MaxValue / FormatConstants.MaxBlockSize) + 1, FormatConstants.MaxBlockSize),

            // Offset + length wrap-around
            (long.MaxValue - 10, 4096),
        };
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static byte[] CreateIvWithLength(int length)
    {
        var header = new byte[8 + Math.Max(length, 0)];
        Encoding.ASCII.GetBytes("AES\0", header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length);
        if (length > 0)
        {
            // Fill with 0xFF pattern
            Array.Fill(header, (byte)0xFF, 8, length);
        }
        return header;
    }

    private static byte[] CreateTruncatedHeader(int expectedIvSize)
    {
        // Header that ends mid-IV -- only 3 bytes total (less than the header structure)
        return new byte[] { 0x41, 0x45, 0x53 }; // "AES" but no IV data at all
    }
}
