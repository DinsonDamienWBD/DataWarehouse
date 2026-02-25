using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Binary codec for serializing and deserializing <see cref="DevicePoolDescriptor"/>
/// to a fixed 4KB block suitable for writing to reserved device sectors.
/// </summary>
/// <remarks>
/// <para>Reserved sector layout (4096 bytes total):</para>
/// <list type="bullet">
///   <item>Bytes 0-3: Magic number 0x44575030 ("DWP0")</item>
///   <item>Bytes 4-7: Metadata version (int32 LE)</item>
///   <item>Bytes 8-11: Payload length (int32 LE)</item>
///   <item>Bytes 12-43: SHA-256 checksum of payload</item>
///   <item>Bytes 44-N: Payload (UTF-8 JSON of DevicePoolDescriptor)</item>
///   <item>Remaining: zero-padded to 4096 bytes</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Pool metadata binary codec (BMDV-07)")]
public sealed class PoolMetadataCodec
{
    /// <summary>Magic number identifying a pool metadata block ("DWP0").</summary>
    private const uint MagicNumber = 0x44575030;

    /// <summary>Fixed block size for pool metadata.</summary>
    private const int BlockSize = 4096;

    /// <summary>Offset of the magic number field.</summary>
    private const int MagicOffset = 0;

    /// <summary>Offset of the metadata version field.</summary>
    private const int VersionOffset = 4;

    /// <summary>Offset of the payload length field.</summary>
    private const int LengthOffset = 8;

    /// <summary>Offset of the SHA-256 checksum field.</summary>
    private const int ChecksumOffset = 12;

    /// <summary>Size of SHA-256 hash in bytes.</summary>
    private const int ChecksumSize = 32;

    /// <summary>Offset where the JSON payload begins.</summary>
    private const int PayloadOffset = 44;

    /// <summary>Maximum payload size in bytes.</summary>
    private const int MaxPayloadSize = BlockSize - PayloadOffset;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serializes a <see cref="DevicePoolDescriptor"/> into a 4KB metadata block
    /// with magic number, version, SHA-256 checksum, and JSON payload.
    /// </summary>
    /// <param name="pool">The pool descriptor to serialize.</param>
    /// <returns>A 4096-byte block containing the serialized pool metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="pool"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the serialized payload exceeds the maximum payload size.</exception>
    public byte[] SerializePoolMetadata(DevicePoolDescriptor pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var json = JsonSerializer.Serialize(pool, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);

        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidOperationException(
                $"Pool metadata payload ({payload.Length} bytes) exceeds maximum size ({MaxPayloadSize} bytes).");
        }

        var block = new byte[BlockSize];

        // Magic number (bytes 0-3)
        BitConverter.TryWriteBytes(block.AsSpan(MagicOffset, 4), MagicNumber);
        if (!BitConverter.IsLittleEndian)
        {
            block.AsSpan(MagicOffset, 4).Reverse();
        }

        // Metadata version (bytes 4-7, int32 LE)
        BitConverter.TryWriteBytes(block.AsSpan(VersionOffset, 4), pool.MetadataVersion);
        if (!BitConverter.IsLittleEndian)
        {
            block.AsSpan(VersionOffset, 4).Reverse();
        }

        // Payload length (bytes 8-11, int32 LE)
        BitConverter.TryWriteBytes(block.AsSpan(LengthOffset, 4), payload.Length);
        if (!BitConverter.IsLittleEndian)
        {
            block.AsSpan(LengthOffset, 4).Reverse();
        }

        // SHA-256 checksum of payload (bytes 12-43)
        var hash = SHA256.HashData(payload);
        hash.CopyTo(block.AsSpan(ChecksumOffset, ChecksumSize));

        // JSON payload (bytes 44-N)
        payload.CopyTo(block.AsSpan(PayloadOffset));

        // Remaining bytes are already zero (default byte[] initialization)
        return block;
    }

    /// <summary>
    /// Deserializes a <see cref="DevicePoolDescriptor"/> from a raw metadata block.
    /// Returns null if the block does not contain valid pool metadata (wrong magic number
    /// or checksum mismatch).
    /// </summary>
    /// <param name="data">The raw block data to deserialize. Must be at least 44 bytes.</param>
    /// <returns>The deserialized pool descriptor, or null if the data is invalid.</returns>
    public DevicePoolDescriptor? DeserializePoolMetadata(ReadOnlySpan<byte> data)
    {
        if (!ValidatePoolMetadata(data))
        {
            return null;
        }

        var payloadLength = ReadInt32LE(data.Slice(LengthOffset, 4));
        var payload = data.Slice(PayloadOffset, payloadLength);
        var json = Encoding.UTF8.GetString(payload);

        try
        {
            return JsonSerializer.Deserialize<DevicePoolDescriptor>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a raw metadata block by checking the magic number and SHA-256 checksum
    /// without performing full deserialization.
    /// </summary>
    /// <param name="data">The raw block data to validate.</param>
    /// <returns>True if the magic number matches and the checksum is valid; otherwise false.</returns>
    public bool ValidatePoolMetadata(ReadOnlySpan<byte> data)
    {
        if (data.Length < PayloadOffset)
        {
            return false;
        }

        // Check magic number
        var magic = ReadUInt32LE(data.Slice(MagicOffset, 4));
        if (magic != MagicNumber)
        {
            return false;
        }

        // Read payload length
        var payloadLength = ReadInt32LE(data.Slice(LengthOffset, 4));
        if (payloadLength <= 0 || payloadLength > MaxPayloadSize || PayloadOffset + payloadLength > data.Length)
        {
            return false;
        }

        // Verify SHA-256 checksum
        var storedChecksum = data.Slice(ChecksumOffset, ChecksumSize);
        var payload = data.Slice(PayloadOffset, payloadLength);
        Span<byte> computedHash = stackalloc byte[32];
        SHA256.HashData(payload, computedHash);

        return storedChecksum.SequenceEqual(computedHash);
    }

    private static uint ReadUInt32LE(ReadOnlySpan<byte> data)
    {
        var value = BitConverter.ToUInt32(data);
        return BitConverter.IsLittleEndian ? value : ReverseEndianness(value);
    }

    private static int ReadInt32LE(ReadOnlySpan<byte> data)
    {
        var value = BitConverter.ToInt32(data);
        return BitConverter.IsLittleEndian ? value : (int)ReverseEndianness((uint)value);
    }

    private static uint ReverseEndianness(uint value)
    {
        return ((value & 0xFF) << 24) |
               ((value & 0xFF00) << 8) |
               ((value & 0xFF0000) >> 8) |
               ((value & 0xFF000000) >> 24);
    }
}
