using System.Buffers.Binary;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single policy definition stored in the Policy Vault.
/// Fixed header: 40 bytes, followed by variable-length <see cref="Data"/> payload.
/// </summary>
public sealed class PolicyDefinition
{
    /// <summary>Fixed-size header portion in bytes (16+2+2+8+8+4 = 40).</summary>
    public const int FixedHeaderSize = 40;

    /// <summary>Unique identifier for this policy.</summary>
    public Guid PolicyId { get; }

    /// <summary>Enum-like identifier for the policy category.</summary>
    public ushort PolicyType { get; }

    /// <summary>Schema version of the policy payload.</summary>
    public ushort Version { get; }

    /// <summary>UTC ticks when the policy was created.</summary>
    public long CreatedUtcTicks { get; }

    /// <summary>UTC ticks when the policy was last modified.</summary>
    public long ModifiedUtcTicks { get; }

    /// <summary>Length of the variable-length policy payload in bytes.</summary>
    public int DataLength => Data.Length;

    /// <summary>The serialized policy content (variable length).</summary>
    public byte[] Data { get; }

    /// <summary>Total serialized size of this definition (header + payload).</summary>
    public int SerializedSize => FixedHeaderSize + Data.Length;

    public PolicyDefinition(
        Guid policyId,
        ushort policyType,
        ushort version,
        long createdUtcTicks,
        long modifiedUtcTicks,
        byte[] data)
    {
        PolicyId = policyId;
        PolicyType = policyType;
        Version = version;
        CreatedUtcTicks = createdUtcTicks;
        ModifiedUtcTicks = modifiedUtcTicks;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    /// <summary>Writes this definition into the target span at the given offset.</summary>
    /// <returns>Number of bytes written.</returns>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        PolicyId.TryWriteBytes(buffer.Slice(offset));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 16), PolicyType);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 18), Version);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 20), CreatedUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 28), ModifiedUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 36), Data.Length);
        Data.AsSpan().CopyTo(buffer.Slice(offset + FixedHeaderSize));
        return FixedHeaderSize + Data.Length;
    }

    /// <summary>Reads a definition from the source span at the given offset.</summary>
    /// <returns>The deserialized definition and the number of bytes consumed.</returns>
    internal static (PolicyDefinition Definition, int BytesRead) ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var policyId = new Guid(buffer.Slice(offset, 16));
        var policyType = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 16));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 18));
        var createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 20));
        var modifiedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 28));
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 36));
        var data = buffer.Slice(offset + FixedHeaderSize, dataLength).ToArray();

        return (new PolicyDefinition(policyId, policyType, version, createdUtcTicks, modifiedUtcTicks, data),
                FixedHeaderSize + dataLength);
    }
}

/// <summary>
/// Policy Vault region: a 2-block HMAC-SHA256 sealed vault storing serialized policy
/// definitions. Block 0 holds the policy count and serialized entries. Block 1 holds
/// the 32-byte HMAC that covers block 0's payload bytes [0..blockSize-16).
/// Tampered data is detected on deserialization via HMAC verification.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Policy Vault (VREG-01)")]
public sealed class PolicyVaultRegion
{
    /// <summary>Number of blocks this region occupies.</summary>
    public const int BlockCount = 2;

    /// <summary>Size of the policy count field at the start of block 0.</summary>
    private const int PolicyCountFieldSize = 4;

    /// <summary>Size of the HMAC hash stored at the start of block 1.</summary>
    private const int HmacHashSize = 32;

    private readonly List<PolicyDefinition> _policies = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>32-byte HMAC-SHA256 key used to seal/verify the vault.</summary>
    public byte[] HmacKey { get; }

    /// <summary>Number of policies currently in the vault.</summary>
    public int PolicyCount => _policies.Count;

    /// <summary>
    /// Creates a new Policy Vault region with the specified HMAC key.
    /// </summary>
    /// <param name="hmacKey">32-byte HMAC-SHA256 key.</param>
    /// <exception cref="ArgumentException">Key is not exactly 32 bytes.</exception>
    public PolicyVaultRegion(byte[] hmacKey)
    {
        if (hmacKey is null) throw new ArgumentNullException(nameof(hmacKey));
        if (hmacKey.Length != HmacHashSize)
            throw new ArgumentException($"HMAC key must be exactly {HmacHashSize} bytes.", nameof(hmacKey));
        HmacKey = hmacKey;
    }

    /// <summary>
    /// Adds a policy to the vault. Validates that the total serialized size fits
    /// within block 0 payload capacity (blockSize-16 minus the 4-byte count field).
    /// </summary>
    /// <exception cref="InvalidOperationException">Adding this policy would exceed block 0 capacity.</exception>
    public void AddPolicy(PolicyDefinition policy)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        _policies.Add(policy);
    }

    /// <summary>Removes a policy by its ID. Returns true if found and removed.</summary>
    public bool RemovePolicy(Guid policyId)
    {
        for (int i = 0; i < _policies.Count; i++)
        {
            if (_policies[i].PolicyId == policyId)
            {
                _policies.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Looks up a policy by its ID. Returns null if not found.</summary>
    public PolicyDefinition? GetPolicy(Guid policyId)
    {
        for (int i = 0; i < _policies.Count; i++)
        {
            if (_policies[i].PolicyId == policyId)
                return _policies[i];
        }
        return null;
    }

    /// <summary>Returns a snapshot of all policies in the vault.</summary>
    public IReadOnlyList<PolicyDefinition> GetAllPolicies() => _policies.AsReadOnly();

    /// <summary>
    /// Serializes the vault into a 2-block buffer with HMAC sealing and
    /// <see cref="UniversalBlockTrailer"/> on each block.
    /// </summary>
    /// <param name="buffer">Target buffer, must be at least <paramref name="blockSize"/> * 2 bytes.</param>
    /// <param name="blockSize">Block size in bytes (minimum <see cref="FormatConstants.MinBlockSize"/>).</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int totalSize = blockSize * BlockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Validate total policy data fits in block 0 payload
        int totalPolicyBytes = PolicyCountFieldSize;
        for (int i = 0; i < _policies.Count; i++)
            totalPolicyBytes += _policies[i].SerializedSize;

        if (totalPolicyBytes > payloadSize)
            throw new InvalidOperationException(
                $"Policy data ({totalPolicyBytes} bytes) exceeds block 0 payload capacity ({payloadSize} bytes).");

        // Clear entire buffer
        buffer.Slice(0, totalSize).Clear();

        // ── Block 0: [PolicyCount:4 LE][PolicyDefinition entries...][zero-fill][Trailer] ──
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _policies.Count);

        int offset = PolicyCountFieldSize;
        for (int i = 0; i < _policies.Count; i++)
            offset += _policies[i].WriteTo(block0, offset);

        // Write trailer on block 0
        UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.POLV, Generation);

        // ── Block 1: [HmacHash:32][Reserved:zero-fill][Trailer] ──
        var block1 = buffer.Slice(blockSize, blockSize);

        // Compute HMAC-SHA256 over block 0 payload [0..blockSize-16)
        using var hmac = new HMACSHA256(HmacKey);
        byte[] hash = hmac.ComputeHash(block0.Slice(0, payloadSize).ToArray());
        hash.AsSpan().CopyTo(block1);

        // Write trailer on block 1
        UniversalBlockTrailer.Write(block1, blockSize, BlockTypeTags.POLV, Generation);
    }

    /// <summary>
    /// Deserializes a 2-block buffer, verifying block trailers and HMAC integrity.
    /// </summary>
    /// <param name="buffer">Source buffer containing 2 blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hmacKey">32-byte HMAC-SHA256 key for verification.</param>
    /// <returns>A populated <see cref="PolicyVaultRegion"/>.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown if block trailers fail verification or HMAC does not match (tampered data).
    /// </exception>
    public static PolicyVaultRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, byte[] hmacKey)
    {
        int totalSize = blockSize * BlockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        var block0 = buffer.Slice(0, blockSize);
        var block1 = buffer.Slice(blockSize, blockSize);

        // Verify block trailers
        if (!UniversalBlockTrailer.Verify(block0, blockSize))
            throw new InvalidDataException("Policy Vault block 0 trailer verification failed.");
        if (!UniversalBlockTrailer.Verify(block1, blockSize))
            throw new InvalidDataException("Policy Vault block 1 trailer verification failed.");

        // Read HMAC from block 1 and verify against block 0 payload
        var storedHmac = block1.Slice(0, HmacHashSize);
        using var hmacAlg = new HMACSHA256(hmacKey);
        byte[] computedHmac = hmacAlg.ComputeHash(block0.Slice(0, payloadSize).ToArray());

        if (!CryptographicOperations.FixedTimeEquals(storedHmac, computedHmac))
            throw new InvalidDataException("Policy Vault HMAC verification failed: data has been tampered with.");

        // Read generation from block 0 trailer
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        // Deserialize policies from block 0
        var region = new PolicyVaultRegion(hmacKey)
        {
            Generation = trailer.GenerationNumber
        };

        int policyCount = BinaryPrimitives.ReadInt32LittleEndian(block0);
        int offset = PolicyCountFieldSize;

        for (int i = 0; i < policyCount; i++)
        {
            var (definition, bytesRead) = PolicyDefinition.ReadFrom(block0, offset);
            region._policies.Add(definition);
            offset += bytesRead;
        }

        return region;
    }
}
