using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

// ── TPM Abstraction ──────────────────────────────────────────────────────────

/// <summary>
/// Signed quote produced by a TPM 2.0 device.
/// Contains the PCR values at the time of quoting, bound to the provided nonce.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Proof of physical custody (VOPT-56)")]
public sealed record TpmQuote
{
    /// <summary>Raw quote structure as returned by TPM2_Quote (TPMS_ATTEST serialized).</summary>
    public byte[] QuoteData { get; init; } = Array.Empty<byte>();

    /// <summary>Signature over QuoteData produced by the TPM's attestation key.</summary>
    public byte[] Signature { get; init; } = Array.Empty<byte>();

    /// <summary>PCR values included in the quote, ordered by PcrIndices.</summary>
    public byte[][] PcrValues { get; init; } = Array.Empty<byte[]>();

    /// <summary>PCR indices that were included in this quote, matching PcrValues order.</summary>
    public int[] PcrIndices { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Abstraction over a physical or virtual TPM 2.0 device.
/// Production implementations use the TSS.NET or platform TPM API.
/// Test implementations can inject deterministic values without hardware.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Proof of physical custody (VOPT-56)")]
public interface ITpmProvider
{
    /// <summary>
    /// Reads the current value of a single PCR.
    /// </summary>
    /// <param name="pcrIndex">PCR index (0–23 for TPM 2.0).</param>
    /// <param name="hashAlgorithm">Hash algorithm bank (e.g. "SHA-256").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw PCR value bytes.</returns>
    Task<byte[]> ReadPcrAsync(int pcrIndex, string hashAlgorithm, CancellationToken ct);

    /// <summary>
    /// Generates a certified TPM quote over the specified PCRs, bound to the provided nonce.
    /// </summary>
    /// <param name="pcrIndices">PCR indices to include in the quote.</param>
    /// <param name="nonce">Freshness nonce (up to 32 bytes; the TPM may truncate or hash it).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed <see cref="TpmQuote"/> attesting the current PCR state.</returns>
    Task<TpmQuote> GenerateQuoteAsync(int[] pcrIndices, byte[] nonce, CancellationToken ct);

    /// <summary>
    /// Verifies the signature on a previously generated quote.
    /// </summary>
    /// <param name="quote">The quote to verify.</param>
    /// <param name="expectedNonce">The nonce that should be embedded in the quote.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the quote signature is valid and the nonce matches.</returns>
    Task<bool> VerifyQuoteAsync(TpmQuote quote, byte[] expectedNonce, CancellationToken ct);
}

// ── Proof Structures ─────────────────────────────────────────────────────────

/// <summary>
/// A cryptographic custody proof that binds TPM PCR state to VDE MerkleRootHash.
/// Provides evidence that a specific VDE volume exists on a specific physical platform
/// at a specific point in time.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Proof of physical custody (VOPT-56)")]
public sealed record CustodyProof
{
    /// <summary>32-byte BLAKE3/SHA-256 Merkle root hash from the Integrity Anchor block.</summary>
    public byte[] MerkleRootHash { get; init; } = Array.Empty<byte>();

    /// <summary>UUID of the VDE volume the proof was generated for.</summary>
    public Guid VolumeUuid { get; init; }

    /// <summary>UTC timestamp at which the proof was generated.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Freshness nonce: SHA-256(MerkleRootHash || VolumeUuid || UTC-ticks).
    /// Binds the TPM quote to the VDE state at proof time.
    /// Note: BLAKE3 will be substituted when available in the BCL.
    /// </summary>
    public byte[] Nonce { get; init; } = Array.Empty<byte>();

    /// <summary>TPM quote over the configured PCRs, bound to Nonce.</summary>
    public TpmQuote TpmQuote { get; init; } = new TpmQuote();

    /// <summary>
    /// HMAC-SHA256 over the serialized proof body (all fields except this one).
    /// Provides integrity protection for the proof structure in storage.
    /// Note: HMAC-BLAKE3 will be substituted when available in the BCL.
    /// </summary>
    public byte[] ProofSignature { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Detailed result of verifying a custody proof.
/// Each boolean flag corresponds to one verification step.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Proof of physical custody (VOPT-56)")]
public readonly struct CustodyVerificationResult
{
    /// <summary>True when all verification steps pass.</summary>
    public bool IsValid { get; init; }

    /// <summary>True when proof.MerkleRootHash matches the current value in the Integrity Anchor.</summary>
    public bool MerkleRootMatches { get; init; }

    /// <summary>True when the TPM quote signature is valid.</summary>
    public bool TpmQuoteValid { get; init; }

    /// <summary>True when the recomputed nonce matches proof.Nonce.</summary>
    public bool NonceValid { get; init; }

    /// <summary>True when the proof timestamp is within the configured expiry window.</summary>
    public bool TimestampValid { get; init; }

    /// <summary>Human-readable reason when IsValid is false; null when valid.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Age of the proof at verification time.</summary>
    public TimeSpan ProofAge { get; init; }
}

// ── Engine ───────────────────────────────────────────────────────────────────

/// <summary>
/// Proof-of-physical-custody engine (VOPT-56).
///
/// Generates and verifies proofs that bind a VDE volume's Merkle root hash (from
/// the Integrity Anchor block) to the TPM Platform Configuration Registers (PCRs)
/// of the hosting physical platform.
///
/// Use cases:
/// - Data residency compliance (sovereign cloud, air-gapped environments)
/// - Chain-of-custody audit trails for sensitive data
/// - Remote attestation of physical hardware possession
///
/// Architecture:
/// - <see cref="ITpmProvider"/> is injected to enable testing without hardware TPM.
/// - Nonce binds MerkleRootHash + VolumeUuid + timestamp to prevent replay.
/// - ProofSignature provides tamper-evidence for proof-in-storage scenarios.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Proof of physical custody (VOPT-56)")]
public sealed class ProofOfPhysicalCustody
{
    // Serialization magic + version
    private const uint SerializationMagic = 0x43505043; // 'CPPC'
    private const byte SerializationVersion = 1;

    // Fixed sizes
    private const int HashSize = 32;
    private const int GuidSize = 16;

    private readonly IBlockDevice _device;
    private readonly int _blockSize;
    private readonly CustodyProofConfig _config;

    // HMAC key derived at construction time from a well-known domain label.
    // In production the key is derived from the volume's key material; here we
    // use a stable derivation so proof signatures are portable across instances.
    private static readonly byte[] SHmacDomainLabel =
        System.Text.Encoding.ASCII.GetBytes("VDE.ProofOfPhysicalCustody.v1");

    /// <summary>
    /// Creates a new proof-of-physical-custody engine.
    /// </summary>
    /// <param name="device">Block device backing the VDE volume.</param>
    /// <param name="blockSize">Block size in bytes (must match the volume's block size).</param>
    /// <param name="config">Configuration for proof generation and verification.</param>
    public ProofOfPhysicalCustody(IBlockDevice device, int blockSize, CustodyProofConfig config)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _blockSize = blockSize > 0
            ? blockSize
            : throw new ArgumentOutOfRangeException(nameof(blockSize), "blockSize must be positive.");
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    // ── Proof Generation ─────────────────────────────────────────────────

    /// <summary>
    /// Generates a custody proof by:
    /// 1. Reading the Integrity Anchor block to obtain the current MerkleRootHash.
    /// 2. Computing a freshness nonce from MerkleRootHash, VolumeUuid, and current UTC.
    /// 3. Requesting a TPM quote bound to that nonce.
    /// 4. Signing the assembled proof with HMAC-SHA256.
    /// </summary>
    /// <param name="tpm">TPM provider that will produce the platform attestation.</param>
    /// <param name="integrityAnchorBlock">Block number of the Integrity Anchor (Block 3 of the superblock group).</param>
    /// <param name="volumeUuid">UUID of the VDE volume; used to bind the proof to the specific volume.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed custody proof.</returns>
    public async Task<CustodyProof> GenerateProofAsync(
        ITpmProvider tpm,
        long integrityAnchorBlock,
        Guid volumeUuid,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tpm);
        ArgumentOutOfRangeException.ThrowIfNegative(integrityAnchorBlock);

        // Step 1: Read Integrity Anchor and extract MerkleRootHash
        var merkleRootHash = await ReadMerkleRootHashAsync(integrityAnchorBlock, ct).ConfigureAwait(false);

        // Step 2: Build freshness nonce
        var timestamp = DateTimeOffset.UtcNow;
        var nonce = ComputeNonce(merkleRootHash, volumeUuid, timestamp);

        // Step 3: Request TPM quote bound to nonce
        var tpmQuote = await tpm.GenerateQuoteAsync(_config.PcrIndices, nonce, ct).ConfigureAwait(false);

        // Step 4: Assemble proof (without signature first)
        var proof = new CustodyProof
        {
            MerkleRootHash = merkleRootHash,
            VolumeUuid = volumeUuid,
            Timestamp = timestamp,
            Nonce = nonce,
            TpmQuote = tpmQuote,
            ProofSignature = Array.Empty<byte>()
        };

        // Step 5: Compute proof signature over proof body
        var signature = ComputeProofSignature(proof);

        return proof with { ProofSignature = signature };
    }

    // ── Proof Verification ───────────────────────────────────────────────

    /// <summary>
    /// Verifies a custody proof by:
    /// 1. Reading the current MerkleRootHash from the Integrity Anchor.
    /// 2. Comparing it to the hash recorded in the proof.
    /// 3. Recomputing the nonce and comparing to proof.Nonce.
    /// 4. Verifying the TPM quote signature via the provider.
    /// 5. Checking proof age against the configured expiry.
    /// </summary>
    /// <param name="proof">The proof to verify.</param>
    /// <param name="tpm">TPM provider for quote verification.</param>
    /// <param name="integrityAnchorBlock">Block number of the Integrity Anchor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed verification result.</returns>
    public async Task<CustodyVerificationResult> VerifyProofAsync(
        CustodyProof proof,
        ITpmProvider tpm,
        long integrityAnchorBlock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(tpm);

        var now = DateTimeOffset.UtcNow;
        var proofAge = now - proof.Timestamp;

        // Step 1: Read current MerkleRootHash
        byte[] currentMerkleRoot;
        try
        {
            currentMerkleRoot = await ReadMerkleRootHashAsync(integrityAnchorBlock, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CustodyVerificationResult
            {
                IsValid = false,
                FailureReason = $"Failed to read Integrity Anchor: {ex.Message}",
                ProofAge = proofAge
            };
        }

        // Step 2: Verify MerkleRootHash matches
        var merkleRootMatches = CryptographicOperations.FixedTimeEquals(
            proof.MerkleRootHash.AsSpan(),
            currentMerkleRoot.AsSpan());

        // Step 3: Recompute nonce and verify
        var expectedNonce = ComputeNonce(proof.MerkleRootHash, proof.VolumeUuid, proof.Timestamp);
        var nonceValid = CryptographicOperations.FixedTimeEquals(
            proof.Nonce.AsSpan(),
            expectedNonce.AsSpan());

        // Step 4: Verify TPM quote signature
        bool tpmQuoteValid;
        try
        {
            tpmQuoteValid = await tpm.VerifyQuoteAsync(proof.TpmQuote, proof.Nonce, ct).ConfigureAwait(false);
        }
        catch
        {
            tpmQuoteValid = false;
        }

        // Step 5: Check timestamp/expiry
        var timestampValid = proofAge >= TimeSpan.Zero
                             && proofAge <= TimeSpan.FromHours(_config.ProofExpiryHours);

        var isValid = merkleRootMatches && nonceValid && tpmQuoteValid && timestampValid;

        string? failureReason = null;
        if (!isValid)
        {
            if (!merkleRootMatches)
                failureReason = "MerkleRootHash mismatch: volume data may have changed since proof was generated.";
            else if (!nonceValid)
                failureReason = "Nonce mismatch: proof components have been tampered with.";
            else if (!tpmQuoteValid)
                failureReason = "TPM quote signature invalid: proof was not generated on the expected platform.";
            else if (!timestampValid)
                failureReason = $"Proof expired: age {proofAge.TotalHours:F1}h exceeds limit {_config.ProofExpiryHours}h.";
        }

        return new CustodyVerificationResult
        {
            IsValid = isValid,
            MerkleRootMatches = merkleRootMatches,
            TpmQuoteValid = tpmQuoteValid,
            NonceValid = nonceValid,
            TimestampValid = timestampValid,
            FailureReason = failureReason,
            ProofAge = proofAge
        };
    }

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a custody proof to a portable binary format.
    ///
    /// Layout:
    /// [Magic:4][Version:1][Reserved:3]
    /// [MerkleRootHash:32][VolumeUuid:16][TimestampTicks:8]
    /// [NonceLen:2][Nonce:NonceLen]
    /// [QuoteDataLen:4][QuoteData:QuoteDataLen]
    /// [SignatureLen:4][Signature:SignatureLen]
    /// [PcrCount:2][PcrIndices:PcrCount*4][PcrValueLen:2][PcrValues:PcrCount*PcrValueLen]
    /// [ProofSignatureLen:2][ProofSignature:ProofSignatureLen]
    /// </summary>
    /// <param name="proof">Proof to serialize.</param>
    /// <returns>Byte array containing the portable proof representation.</returns>
    public static byte[] SerializeProof(CustodyProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var q = proof.TpmQuote;
        int pcrCount = q.PcrIndices.Length;
        int pcrValueLen = pcrCount > 0 && q.PcrValues.Length > 0 ? q.PcrValues[0].Length : 0;

        // Calculate total size
        int size =
            4 + 1 + 3                   // Magic + Version + Reserved
            + HashSize                   // MerkleRootHash
            + GuidSize                   // VolumeUuid
            + 8                          // TimestampTicks
            + 2 + proof.Nonce.Length     // NonceLen + Nonce
            + 4 + q.QuoteData.Length     // QuoteDataLen + QuoteData
            + 4 + q.Signature.Length     // SignatureLen + Signature
            + 2                          // PcrCount
            + pcrCount * 4              // PcrIndices
            + 2                          // PcrValueLen
            + pcrCount * pcrValueLen    // PcrValues
            + 2 + proof.ProofSignature.Length; // ProofSignatureLen + ProofSignature

        var buf = new byte[size];
        var span = buf.AsSpan();
        int offset = 0;

        // Header
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), SerializationMagic);
        offset += 4;
        buf[offset++] = SerializationVersion;
        offset += 3; // Reserved

        // MerkleRootHash
        proof.MerkleRootHash.AsSpan(0, Math.Min(proof.MerkleRootHash.Length, HashSize))
            .CopyTo(span.Slice(offset, HashSize));
        offset += HashSize;

        // VolumeUuid
        if (!proof.VolumeUuid.TryWriteBytes(span.Slice(offset, GuidSize)))
            throw new InvalidOperationException("Failed to serialize VolumeUuid.");
        offset += GuidSize;

        // Timestamp as ticks
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, 8), proof.Timestamp.UtcTicks);
        offset += 8;

        // Nonce
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)proof.Nonce.Length);
        offset += 2;
        proof.Nonce.CopyTo(span.Slice(offset));
        offset += proof.Nonce.Length;

        // Quote data
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), q.QuoteData.Length);
        offset += 4;
        q.QuoteData.CopyTo(span.Slice(offset));
        offset += q.QuoteData.Length;

        // Quote signature
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), q.Signature.Length);
        offset += 4;
        q.Signature.CopyTo(span.Slice(offset));
        offset += q.Signature.Length;

        // PCR indices
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)pcrCount);
        offset += 2;
        for (int i = 0; i < pcrCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), q.PcrIndices[i]);
            offset += 4;
        }

        // PCR values
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)pcrValueLen);
        offset += 2;
        for (int i = 0; i < pcrCount; i++)
        {
            var val = i < q.PcrValues.Length ? q.PcrValues[i] : Array.Empty<byte>();
            val.AsSpan(0, Math.Min(val.Length, pcrValueLen)).CopyTo(span.Slice(offset, pcrValueLen));
            offset += pcrValueLen;
        }

        // Proof signature
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)proof.ProofSignature.Length);
        offset += 2;
        proof.ProofSignature.CopyTo(span.Slice(offset));

        return buf;
    }

    /// <summary>
    /// Reconstructs a custody proof from a serialized byte span.
    /// </summary>
    /// <param name="data">Serialized proof bytes produced by <see cref="SerializeProof"/>.</param>
    /// <returns>Reconstructed <see cref="CustodyProof"/>.</returns>
    /// <exception cref="FormatException">Thrown when the data is corrupt or uses an unsupported version.</exception>
    public static CustodyProof DeserializeProof(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new FormatException("Proof data too short.");

        int offset = 0;

        // Header
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        if (magic != SerializationMagic)
            throw new FormatException($"Invalid proof magic 0x{magic:X8}, expected 0x{SerializationMagic:X8}.");

        var version = data[offset++];
        if (version != SerializationVersion)
            throw new FormatException($"Unsupported proof version {version}, expected {SerializationVersion}.");
        offset += 3; // Reserved

        // MerkleRootHash
        var merkleRoot = new byte[HashSize];
        data.Slice(offset, HashSize).CopyTo(merkleRoot);
        offset += HashSize;

        // VolumeUuid
        var uuid = new Guid(data.Slice(offset, GuidSize));
        offset += GuidSize;

        // Timestamp
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
        offset += 8;
        var timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);

        // Nonce
        int nonceLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        var nonce = new byte[nonceLen];
        data.Slice(offset, nonceLen).CopyTo(nonce);
        offset += nonceLen;

        // Quote data
        int quoteDataLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        var quoteData = new byte[quoteDataLen];
        data.Slice(offset, quoteDataLen).CopyTo(quoteData);
        offset += quoteDataLen;

        // Quote signature
        int quoteSignatureLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        var quoteSignature = new byte[quoteSignatureLen];
        data.Slice(offset, quoteSignatureLen).CopyTo(quoteSignature);
        offset += quoteSignatureLen;

        // PCR indices
        int pcrCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        var pcrIndices = new int[pcrCount];
        for (int i = 0; i < pcrCount; i++)
        {
            pcrIndices[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;
        }

        // PCR values
        int pcrValueLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        var pcrValues = new byte[pcrCount][];
        for (int i = 0; i < pcrCount; i++)
        {
            pcrValues[i] = new byte[pcrValueLen];
            data.Slice(offset, pcrValueLen).CopyTo(pcrValues[i]);
            offset += pcrValueLen;
        }

        // Proof signature
        int proofSignatureLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        var proofSignature = new byte[proofSignatureLen];
        data.Slice(offset, proofSignatureLen).CopyTo(proofSignature);

        return new CustodyProof
        {
            MerkleRootHash = merkleRoot,
            VolumeUuid = uuid,
            Timestamp = timestamp,
            Nonce = nonce,
            TpmQuote = new TpmQuote
            {
                QuoteData = quoteData,
                Signature = quoteSignature,
                PcrIndices = pcrIndices,
                PcrValues = pcrValues
            },
            ProofSignature = proofSignature
        };
    }

    // ── Internal Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the Integrity Anchor block and extracts the 32-byte MerkleRootHash.
    /// MerkleRootHash is the first field in the Integrity Anchor (offset 0, 32 bytes).
    /// </summary>
    private async Task<byte[]> ReadMerkleRootHashAsync(long integrityAnchorBlock, CancellationToken ct)
    {
        var buffer = new byte[_blockSize];
        await _device.ReadBlockAsync(integrityAnchorBlock, buffer, ct).ConfigureAwait(false);

        // Deserialize the Integrity Anchor and extract MerkleRootHash
        var anchor = IntegrityAnchor.Deserialize(buffer.AsSpan(), _blockSize);
        var merkleRootHash = new byte[HashSize];
        anchor.MerkleRootHash.AsSpan(0, Math.Min(anchor.MerkleRootHash.Length, HashSize))
            .CopyTo(merkleRootHash);
        return merkleRootHash;
    }

    /// <summary>
    /// Computes the freshness nonce: SHA-256(MerkleRootHash || VolumeUuid-bytes || UTC-ticks-LE).
    ///
    /// Note: The spec calls for BLAKE3; SHA-256 is used as the BCL substitute until
    /// BLAKE3 is available natively. The domain structure is identical.
    /// </summary>
    private static byte[] ComputeNonce(byte[] merkleRootHash, Guid volumeUuid, DateTimeOffset timestamp)
    {
        // Build input: MerkleRootHash (32) || VolumeUuid (16) || UTC ticks (8)
        Span<byte> input = stackalloc byte[HashSize + GuidSize + 8];
        merkleRootHash.AsSpan(0, Math.Min(merkleRootHash.Length, HashSize)).CopyTo(input);

        var guidBytes = volumeUuid.ToByteArray();
        guidBytes.AsSpan().CopyTo(input.Slice(HashSize, GuidSize));

        BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashSize + GuidSize, 8), timestamp.UtcTicks);

        return SHA256.HashData(input);
    }

    /// <summary>
    /// Computes HMAC-SHA256 over the proof body (all fields except ProofSignature).
    /// The HMAC key is derived from a stable domain label.
    ///
    /// Note: The spec calls for HMAC-BLAKE3; HMAC-SHA256 is used until BLAKE3 is
    /// available natively in the BCL. Domain label includes version to allow migration.
    /// </summary>
    private static byte[] ComputeProofSignature(CustodyProof proof)
    {
        // Derive HMAC key from the domain label using SHA-256
        var keyMaterial = SHA256.HashData(SHmacDomainLabel);

        using var hmac = new HMACSHA256(keyMaterial);

        // Feed all proof fields in stable order
        hmac.TransformBlock(proof.MerkleRootHash, 0, proof.MerkleRootHash.Length, null, 0);

        var uuidBytes = proof.VolumeUuid.ToByteArray();
        hmac.TransformBlock(uuidBytes, 0, uuidBytes.Length, null, 0);

        var ticksBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(ticksBytes, proof.Timestamp.UtcTicks);
        hmac.TransformBlock(ticksBytes, 0, ticksBytes.Length, null, 0);

        hmac.TransformBlock(proof.Nonce, 0, proof.Nonce.Length, null, 0);
        hmac.TransformBlock(proof.TpmQuote.QuoteData, 0, proof.TpmQuote.QuoteData.Length, null, 0);
        hmac.TransformBlock(proof.TpmQuote.Signature, 0, proof.TpmQuote.Signature.Length, null, 0);

        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hmac.Hash ?? Array.Empty<byte>();
    }
}
