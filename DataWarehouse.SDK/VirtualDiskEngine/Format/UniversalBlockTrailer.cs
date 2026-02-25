using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Universal Block Trailer: exactly 16 bytes appended to the end of every block
/// in the DWVD v2.0 format. Provides self-verification via XxHash64 checksum
/// and torn-write detection via monotonic generation numbers.
/// Layout: [BlockTypeTag:4 LE][GenerationNumber:4 LE][XxHash64Checksum:8 LE]
/// The checksum covers bytes [0..blockSize-16), i.e. all payload before the trailer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 universal block trailer (VDE2-03)")]
public readonly struct UniversalBlockTrailer : IEquatable<UniversalBlockTrailer>
{
    /// <summary>Size of the trailer in bytes (always 16).</summary>
    public const int Size = FormatConstants.UniversalBlockTrailerSize;

    /// <summary>Block type tag identifying the block's purpose (from <see cref="BlockTypeTags"/>).</summary>
    public uint BlockTypeTag { get; }

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint GenerationNumber { get; }

    /// <summary>XxHash64 checksum of the block payload bytes [0..blockSize-16).</summary>
    public ulong XxHash64Checksum { get; }

    /// <summary>Creates a trailer with explicit field values.</summary>
    public UniversalBlockTrailer(uint blockTypeTag, uint generationNumber, ulong xxHash64Checksum)
    {
        BlockTypeTag = blockTypeTag;
        GenerationNumber = generationNumber;
        XxHash64Checksum = xxHash64Checksum;
    }

    /// <summary>
    /// Creates a trailer by computing XxHash64 over the provided block payload.
    /// The payload should be bytes [0..blockSize-16) of the block.
    /// </summary>
    /// <param name="blockTypeTag">Block type tag constant from <see cref="BlockTypeTags"/>.</param>
    /// <param name="generation">Monotonic generation number.</param>
    /// <param name="blockPayload">Block content excluding the trailer area (bytes [0..blockSize-16)).</param>
    /// <returns>A populated trailer with computed checksum.</returns>
    public static UniversalBlockTrailer Create(uint blockTypeTag, uint generation, ReadOnlySpan<byte> blockPayload)
    {
        var hash = XxHash64.HashToUInt64(blockPayload);
        return new UniversalBlockTrailer(blockTypeTag, generation, hash);
    }

    /// <summary>
    /// Computes the XxHash64 checksum of block[0..blockSize-16) and writes the complete
    /// 16-byte trailer into block[blockSize-16..blockSize).
    /// </summary>
    /// <param name="block">The full block buffer (must be at least <paramref name="blockSize"/> bytes).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <param name="blockTypeTag">Block type tag constant from <see cref="BlockTypeTags"/>.</param>
    /// <param name="generation">Monotonic generation number.</param>
    public static void Write(Span<byte> block, int blockSize, uint blockTypeTag, uint generation)
    {
        if (block.Length < blockSize)
            throw new ArgumentException($"Block buffer must be at least {blockSize} bytes.", nameof(block));
        if (blockSize < Size)
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be at least {Size} bytes.");

        int trailerOffset = blockSize - Size;
        var payload = block.Slice(0, trailerOffset);
        var hash = XxHash64.HashToUInt64(payload);

        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(trailerOffset), blockTypeTag);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(trailerOffset + 4), generation);
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(trailerOffset + 8), hash);
    }

    /// <summary>
    /// Reads the 16-byte trailer from the last 16 bytes of a block.
    /// </summary>
    /// <param name="block">The full block buffer (must be at least <paramref name="blockSize"/> bytes).</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The deserialized trailer.</returns>
    public static UniversalBlockTrailer Read(ReadOnlySpan<byte> block, int blockSize)
    {
        if (block.Length < blockSize)
            throw new ArgumentException($"Block buffer must be at least {blockSize} bytes.", nameof(block));
        if (blockSize < Size)
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be at least {Size} bytes.");

        int trailerOffset = blockSize - Size;
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(trailerOffset));
        var gen = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(trailerOffset + 4));
        var hash = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(trailerOffset + 8));

        return new UniversalBlockTrailer(tag, gen, hash);
    }

    /// <summary>
    /// Verifies the XxHash64 checksum of a block by recomputing it from bytes [0..blockSize-16)
    /// and comparing with the stored checksum in the trailer.
    /// </summary>
    /// <param name="block">The full block buffer.</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>True if the stored checksum matches the recomputed checksum.</returns>
    public static bool Verify(ReadOnlySpan<byte> block, int blockSize)
    {
        if (block.Length < blockSize || blockSize < Size)
            return false;

        int trailerOffset = blockSize - Size;
        var storedHash = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(trailerOffset + 8));
        var computedHash = XxHash64.HashToUInt64(block.Slice(0, trailerOffset));

        return storedHash == computedHash;
    }

    /// <summary>
    /// Verifies the XxHash64 checksum and outputs the trailer on success.
    /// </summary>
    /// <param name="block">The full block buffer.</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <param name="trailer">The deserialized trailer if verification succeeds.</param>
    /// <returns>True if the stored checksum matches the recomputed checksum.</returns>
    public static bool Verify(ReadOnlySpan<byte> block, int blockSize, out UniversalBlockTrailer trailer)
    {
        trailer = default;

        if (block.Length < blockSize || blockSize < Size)
            return false;

        int trailerOffset = blockSize - Size;
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(trailerOffset));
        var gen = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(trailerOffset + 4));
        var storedHash = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(trailerOffset + 8));
        var computedHash = XxHash64.HashToUInt64(block.Slice(0, trailerOffset));

        if (storedHash != computedHash)
            return false;

        trailer = new UniversalBlockTrailer(tag, gen, storedHash);
        return true;
    }

    /// <summary>
    /// Returns the usable payload size per block (block size minus trailer size).
    /// </summary>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <returns>The number of usable payload bytes.</returns>
    public static int PayloadSize(int blockSize) => blockSize - Size;

    /// <inheritdoc />
    public bool Equals(UniversalBlockTrailer other) =>
        BlockTypeTag == other.BlockTypeTag
        && GenerationNumber == other.GenerationNumber
        && XxHash64Checksum == other.XxHash64Checksum;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UniversalBlockTrailer other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BlockTypeTag, GenerationNumber, XxHash64Checksum);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(UniversalBlockTrailer left, UniversalBlockTrailer right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(UniversalBlockTrailer left, UniversalBlockTrailer right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"Trailer({BlockTypeTags.TagToString(BlockTypeTag)}, Gen={GenerationNumber}, Hash=0x{XxHash64Checksum:X16})";
}
