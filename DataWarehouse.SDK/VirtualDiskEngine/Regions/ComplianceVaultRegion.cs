using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A compliance passport record associating a data object with a compliance
/// framework assessment, including ECDSA P-256 digital signature for tamper
/// detection and non-repudiation.
/// </summary>
/// <remarks>
/// Serialized layout:
/// [PassportId:16][ObjectId:16][FrameworkId:2 LE][ComplianceStatus:2 LE]
/// [IssuedUtcTicks:8 LE][ExpiresUtcTicks:8 LE][LastVerifiedUtcTicks:8 LE]
/// [IssuerIdLength:4 LE][IssuerId:variable, max 128][NotesLength:4 LE][Notes:variable, max 256]
/// [Signature:64]
///
/// Fixed overhead: 132 bytes + variable (IssuerId + Notes, max 384 total).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Compliance Vault (VREG-09)")]
public readonly record struct CompliancePassport
{
    /// <summary>Fixed portion of the serialized size (excluding variable fields): 132 bytes.</summary>
    public const int FixedSize = 132;

    /// <summary>Maximum length of the IssuerId field in bytes.</summary>
    public const int MaxIssuerIdLength = 128;

    /// <summary>Maximum length of the Notes field in bytes.</summary>
    public const int MaxNotesLength = 256;

    /// <summary>Size of the ECDSA P-256 signature in bytes (r + s, 32 bytes each).</summary>
    public const int SignatureSize = 64;

    /// <summary>Unique identifier for this passport.</summary>
    public Guid PassportId { get; init; }

    /// <summary>The data object this passport covers.</summary>
    public Guid ObjectId { get; init; }

    /// <summary>
    /// Compliance framework identifier.
    /// 0=GDPR, 1=HIPAA, 2=SOX, 3=PCI-DSS, 4=SOC2, 5=ISO27001, 6=CCPA, 7=FISMA, 8=FedRAMP.
    /// </summary>
    public ushort FrameworkId { get; init; }

    /// <summary>
    /// Compliance status.
    /// 0=Unknown, 1=Compliant, 2=NonCompliant, 3=Exempt, 4=PendingReview.
    /// </summary>
    public ushort ComplianceStatus { get; init; }

    /// <summary>UTC ticks when this passport was issued.</summary>
    public long IssuedUtcTicks { get; init; }

    /// <summary>UTC ticks when this passport expires (0 = no expiry).</summary>
    public long ExpiresUtcTicks { get; init; }

    /// <summary>UTC ticks of the last verification.</summary>
    public long LastVerifiedUtcTicks { get; init; }

    /// <summary>Issuer identifier (UTF-8, max 128 bytes).</summary>
    public byte[] IssuerId { get; init; }

    /// <summary>Compliance notes (UTF-8, max 256 bytes).</summary>
    public byte[] Notes { get; init; }

    /// <summary>ECDSA P-256 digital signature over all fields except the signature itself (64 bytes).</summary>
    public byte[] Signature { get; init; }

    /// <summary>
    /// Serialized size of this passport in bytes.
    /// </summary>
    public int SerializedSize =>
        FixedSize + (IssuerId?.Length ?? 0) + (Notes?.Length ?? 0);

    /// <summary>Writes this passport to the buffer, returning bytes written.</summary>
    internal int WriteTo(Span<byte> buffer)
    {
        int offset = 0;

        PassportId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        ObjectId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), FrameworkId);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), ComplianceStatus);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), IssuedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), ExpiresUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), LastVerifiedUtcTicks);
        offset += 8;

        var issuerBytes = IssuerId ?? Array.Empty<byte>();
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), issuerBytes.Length);
        offset += 4;
        issuerBytes.AsSpan().CopyTo(buffer.Slice(offset));
        offset += issuerBytes.Length;

        var notesBytes = Notes ?? Array.Empty<byte>();
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), notesBytes.Length);
        offset += 4;
        notesBytes.AsSpan().CopyTo(buffer.Slice(offset));
        offset += notesBytes.Length;

        var sig = Signature ?? new byte[SignatureSize];
        sig.AsSpan(0, Math.Min(sig.Length, SignatureSize)).CopyTo(buffer.Slice(offset, SignatureSize));
        offset += SignatureSize;

        return offset;
    }

    /// <summary>Reads a passport from the buffer, returning bytes consumed.</summary>
    internal static CompliancePassport ReadFrom(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        int offset = 0;

        var passportId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        var objectId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        ushort frameworkId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        ushort complianceStatus = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        long issuedTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long expiresTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long lastVerifiedTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        int issuerLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        if (issuerLen < 0 || issuerLen > 4096 || offset + issuerLen > buffer.Length)
            throw new InvalidDataException($"CompliancePassport issuerLen {issuerLen} is invalid or exceeds buffer.");
        byte[] issuerId = buffer.Slice(offset, issuerLen).ToArray();
        offset += issuerLen;

        int notesLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        if (notesLen < 0 || notesLen > 65536 || offset + notesLen > buffer.Length)
            throw new InvalidDataException($"CompliancePassport notesLen {notesLen} is invalid or exceeds buffer.");
        byte[] notes = buffer.Slice(offset, notesLen).ToArray();
        offset += notesLen;

        byte[] signature = buffer.Slice(offset, SignatureSize).ToArray();
        offset += SignatureSize;

        bytesRead = offset;
        return new CompliancePassport
        {
            PassportId = passportId,
            ObjectId = objectId,
            FrameworkId = frameworkId,
            ComplianceStatus = complianceStatus,
            IssuedUtcTicks = issuedTicks,
            ExpiresUtcTicks = expiresTicks,
            LastVerifiedUtcTicks = lastVerifiedTicks,
            IssuerId = issuerId,
            Notes = notes,
            Signature = signature
        };
    }
}

/// <summary>
/// Compliance Vault Region: stores <see cref="CompliancePassport"/> records with
/// ECDSA P-256 digital signatures for tamper detection and non-repudiation.
/// Supports querying passports by object ID, framework, or passport ID.
/// Serialized using <see cref="BlockTypeTags.CMVT"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [PassportCount:4 LE][Reserved:12][CompliancePassport entries...]
///     Entries are packed sequentially and overflow to block 1+ if needed.
///   Each block ends with [UniversalBlockTrailer].
///
/// Signature scheme: ECDSA with NIST P-256 (64-byte raw r||s signature).
/// The signature covers the concatenation of all passport fields EXCEPT the
/// Signature field itself. Signing/verification keys are provided externally.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Compliance Vault (VREG-09)")]
public sealed class ComplianceVaultRegion
{
    /// <summary>Size of the header fields at the start of block 0: PassportCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<CompliancePassport> _passports = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of passports stored in this vault.</summary>
    public int PassportCount => _passports.Count;

    /// <summary>
    /// Creates a new empty compliance vault region.
    /// </summary>
    public ComplianceVaultRegion()
    {
    }

    /// <summary>
    /// Adds a compliance passport to the vault.
    /// </summary>
    /// <param name="passport">The passport to add.</param>
    public void AddPassport(CompliancePassport passport)
    {
        ValidatePassport(passport);
        _passports.Add(passport);
    }

    /// <summary>
    /// Removes a passport by its unique identifier.
    /// </summary>
    /// <param name="passportId">The passport ID to remove.</param>
    /// <returns>True if the passport was found and removed; false otherwise.</returns>
    public bool RemovePassport(Guid passportId)
    {
        for (int i = 0; i < _passports.Count; i++)
        {
            if (_passports[i].PassportId == passportId)
            {
                _passports.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Looks up a passport by its unique identifier.
    /// </summary>
    /// <param name="passportId">The passport ID to find.</param>
    /// <returns>The matching passport, or null if not found.</returns>
    public CompliancePassport? GetPassport(Guid passportId)
    {
        for (int i = 0; i < _passports.Count; i++)
        {
            if (_passports[i].PassportId == passportId)
                return _passports[i];
        }
        return null;
    }

    /// <summary>
    /// Returns all passports associated with a specific data object.
    /// </summary>
    /// <param name="objectId">The object ID to search for.</param>
    /// <returns>All passports covering this object.</returns>
    public IReadOnlyList<CompliancePassport> GetPassportsByObject(Guid objectId)
    {
        var results = new List<CompliancePassport>();
        for (int i = 0; i < _passports.Count; i++)
        {
            if (_passports[i].ObjectId == objectId)
                results.Add(_passports[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all passports for a specific compliance framework.
    /// </summary>
    /// <param name="frameworkId">The framework identifier to filter by.</param>
    /// <returns>All passports for this framework.</returns>
    public IReadOnlyList<CompliancePassport> GetPassportsByFramework(ushort frameworkId)
    {
        var results = new List<CompliancePassport>();
        for (int i = 0; i < _passports.Count; i++)
        {
            if (_passports[i].FrameworkId == frameworkId)
                results.Add(_passports[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all passports in the vault.
    /// </summary>
    public IReadOnlyList<CompliancePassport> GetAllPassports() => _passports.AsReadOnly();

    /// <summary>
    /// Computes the signature payload for a passport: all fields serialized
    /// in order, EXCLUDING the Signature field.
    /// </summary>
    /// <param name="passport">The passport to compute the payload for.</param>
    /// <returns>Byte array containing the signable content.</returns>
    public static byte[] ComputeSignaturePayload(CompliancePassport passport)
    {
        var issuer = passport.IssuerId ?? Array.Empty<byte>();
        var notes = passport.Notes ?? Array.Empty<byte>();

        // 16+16+2+2+8+8+8+4+issuer+4+notes = 68 + issuer.Length + notes.Length
        int size = 68 + issuer.Length + notes.Length;
        var buffer = new byte[size];
        int offset = 0;

        passport.PassportId.TryWriteBytes(buffer.AsSpan(offset, 16));
        offset += 16;
        passport.ObjectId.TryWriteBytes(buffer.AsSpan(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), passport.FrameworkId);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), passport.ComplianceStatus);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), passport.IssuedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), passport.ExpiresUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), passport.LastVerifiedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), issuer.Length);
        offset += 4;
        issuer.AsSpan().CopyTo(buffer.AsSpan(offset));
        offset += issuer.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), notes.Length);
        offset += 4;
        notes.AsSpan().CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    /// <summary>
    /// Verifies the ECDSA P-256 digital signature on a compliance passport.
    /// </summary>
    /// <param name="passport">The passport whose signature to verify.</param>
    /// <param name="publicKey">The ECDSA public key for verification.</param>
    /// <returns>True if the signature is valid; false otherwise.</returns>
    public static bool VerifySignature(CompliancePassport passport, ECDsa publicKey)
    {
        if (publicKey is null)
            throw new ArgumentNullException(nameof(publicKey));
        if (passport.Signature is null || passport.Signature.Length != CompliancePassport.SignatureSize)
            return false;

        byte[] payload = ComputeSignaturePayload(passport);
        return publicKey.VerifyData(payload, passport.Signature, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Signs a compliance passport with an ECDSA P-256 private key and returns
    /// a new passport with the Signature field populated.
    /// </summary>
    /// <param name="passport">The passport to sign (Signature field will be replaced).</param>
    /// <param name="privateKey">The ECDSA private key for signing.</param>
    /// <returns>A new passport with a valid ECDSA P-256 signature.</returns>
    public static CompliancePassport SignPassport(CompliancePassport passport, ECDsa privateKey)
    {
        if (privateKey is null)
            throw new ArgumentNullException(nameof(privateKey));

        byte[] payload = ComputeSignaturePayload(passport);
        byte[] signature = privateKey.SignData(payload, HashAlgorithmName.SHA256);

        // ECDSA P-256 produces a 64-byte signature (r || s, 32 bytes each) in IEEE format
        // Ensure we store exactly 64 bytes
        byte[] storedSignature = new byte[CompliancePassport.SignatureSize];
        Array.Copy(signature, 0, storedSignature, 0,
            Math.Min(signature.Length, CompliancePassport.SignatureSize));

        return passport with { Signature = storedSignature };
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this vault.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_passports.Count == 0)
            return 1;

        int totalPassportBytes = 0;
        for (int i = 0; i < _passports.Count; i++)
            totalPassportBytes += _passports[i].SerializedSize;

        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalPassportBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalPassportBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the compliance vault into blocks with UniversalBlockTrailer on each block.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least RequiredBlocks * blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int requiredBlocks = RequiredBlocks(blockSize);
        int totalSize = blockSize * requiredBlocks;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        buffer.Slice(0, totalSize).Clear();

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // ── Block 0: Header + passport entries ──
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _passports.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int passportIndex = 0;

        while (passportIndex < _passports.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;
            int startOffset = currentBlock == 0 ? HeaderFieldsSize : 0;

            if (currentBlock > 0)
                offset = 0;

            while (passportIndex < _passports.Count)
            {
                int entrySize = _passports[passportIndex].SerializedSize;
                if (offset + entrySize > blockPayloadEnd)
                    break;

                _passports[passportIndex].WriteTo(block.Slice(offset));
                offset += entrySize;
                passportIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.CMVT, Generation);

            if (passportIndex < _passports.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no passports
        if (_passports.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.CMVT, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.CMVT, Generation);
        }
    }

    /// <summary>
    /// Deserializes a compliance vault region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="ComplianceVaultRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static ComplianceVaultRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"Compliance Vault block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int passportCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new ComplianceVaultRegion
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int passportsRead = 0;

        while (passportsRead < passportCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (passportsRead < passportCount && offset < blockPayloadEnd)
            {
                // Check if there's enough room for at least the fixed portion
                if (offset + CompliancePassport.FixedSize > blockPayloadEnd)
                    break;

                var passport = CompliancePassport.ReadFrom(block.Slice(offset), out int bytesRead);
                region._passports.Add(passport);
                offset += bytesRead;
                passportsRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }

    private static void ValidatePassport(CompliancePassport passport)
    {
        if (passport.IssuerId is not null && passport.IssuerId.Length > CompliancePassport.MaxIssuerIdLength)
            throw new ArgumentException(
                $"IssuerId length {passport.IssuerId.Length} exceeds maximum {CompliancePassport.MaxIssuerIdLength}.",
                nameof(passport));

        if (passport.Notes is not null && passport.Notes.Length > CompliancePassport.MaxNotesLength)
            throw new ArgumentException(
                $"Notes length {passport.Notes.Length} exceeds maximum {CompliancePassport.MaxNotesLength}.",
                nameof(passport));
    }
}
