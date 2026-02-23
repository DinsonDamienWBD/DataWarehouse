using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Context required for tamper detection validation during VDE open.
/// Carries pre-parsed superblock group data and configuration for all integrity checks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper detection orchestrator (VTMP-03/04/05/07)")]
public readonly struct TamperDetectionContext
{
    /// <summary>The open VDE file stream, positioned at byte 0.</summary>
    public Stream VdeStream { get; }

    /// <summary>Block size in bytes.</summary>
    public int BlockSize { get; }

    /// <summary>The deserialized primary superblock.</summary>
    public SuperblockV2 Superblock { get; }

    /// <summary>The deserialized integrity anchor.</summary>
    public IntegrityAnchor Integrity { get; }

    /// <summary>The deserialized extended metadata.</summary>
    public ExtendedMetadata Extended { get; }

    /// <summary>
    /// Region directory mapping region name to (StartBlock, BlockCount).
    /// Used by MetadataChainHasher to locate metadata region headers.
    /// </summary>
    public IReadOnlyDictionary<string, (long StartBlock, long BlockCount)> Regions { get; }

    /// <summary>HMAC key for header integrity seal verification.</summary>
    public byte[] HmacKey { get; }

    /// <summary>
    /// Public key for namespace signature verification.
    /// Null to skip namespace verification (recorded as passed with "Skipped" reason).
    /// </summary>
    public byte[]? NamespacePublicKey { get; }

    /// <summary>
    /// Signature provider for namespace verification. Null to use the default HMAC provider.
    /// </summary>
    public INamespaceSignatureProvider? SignatureProvider { get; }

    /// <summary>
    /// The configured tamper response level from the policy vault.
    /// Defaults to <see cref="TamperResponse.Reject"/>.
    /// </summary>
    public TamperResponse ConfiguredResponse { get; }

    /// <summary>Creates a new tamper detection context with all required fields.</summary>
    public TamperDetectionContext(
        Stream vdeStream,
        int blockSize,
        SuperblockV2 superblock,
        IntegrityAnchor integrity,
        ExtendedMetadata extended,
        IReadOnlyDictionary<string, (long StartBlock, long BlockCount)> regions,
        byte[] hmacKey,
        byte[]? namespacePublicKey = null,
        INamespaceSignatureProvider? signatureProvider = null,
        TamperResponse configuredResponse = TamperResponse.Reject)
    {
        VdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        BlockSize = blockSize;
        Superblock = superblock;
        Integrity = integrity;
        Extended = extended;
        Regions = regions ?? throw new ArgumentNullException(nameof(regions));
        HmacKey = hmacKey ?? throw new ArgumentNullException(nameof(hmacKey));
        NamespacePublicKey = namespacePublicKey;
        SignatureProvider = signatureProvider;
        ConfiguredResponse = configuredResponse;
    }
}

/// <summary>
/// Main entry point for VDE tamper detection during file open.
/// Runs all five integrity checks in sequence (format fingerprint, namespace signature,
/// header integrity seal, file size sentinel, metadata chain hash), aggregates results
/// into a <see cref="TamperDetectionResult"/>, and applies the configured
/// <see cref="TamperResponse"/> via <see cref="TamperResponseExecutor"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper detection orchestrator (VTMP-03/04/05/07)")]
public static class TamperDetectionOrchestrator
{
    /// <summary>
    /// Runs all tamper detection checks and applies the configured response.
    /// This is the primary validation entry point called during VDE open.
    /// </summary>
    /// <param name="context">The tamper detection context with all required data.</param>
    /// <returns>
    /// A <see cref="TamperResponseAction"/> describing whether the VDE should be opened,
    /// opened read-only, quarantined, or rejected.
    /// </returns>
    /// <exception cref="VdeTamperDetectedException">
    /// Thrown when tamper is detected and the configured response is <see cref="TamperResponse.Reject"/>.
    /// </exception>
    public static TamperResponseAction ValidateOnOpen(TamperDetectionContext context)
    {
        var checks = new List<TamperCheckResult>(5);

        // Check 1: Format Fingerprint
        checks.Add(RunFormatFingerprintCheck(context));

        // Check 2: Namespace Signature
        checks.Add(RunNamespaceSignatureCheck(context));

        // Check 3: Header Integrity Seal
        checks.Add(RunHeaderIntegritySealCheck(context));

        // Check 4: File Size Sentinel
        checks.Add(RunFileSizeSentinelCheck(context));

        // Check 5: Metadata Chain Hash
        checks.Add(RunMetadataChainHashCheck(context));

        var result = new TamperDetectionResult(checks.ToArray());
        return TamperResponseExecutor.Execute(result, context.ConfiguredResponse);
    }

    /// <summary>
    /// Convenience overload that reads and parses the superblock group from the stream,
    /// builds the region map, constructs a <see cref="TamperDetectionContext"/>,
    /// and calls the primary <see cref="ValidateOnOpen(TamperDetectionContext)"/>.
    /// </summary>
    /// <param name="stream">Seekable VDE file stream.</param>
    /// <param name="hmacKey">HMAC key for header seal verification.</param>
    /// <param name="response">The configured tamper response level.</param>
    /// <returns>A <see cref="TamperResponseAction"/> describing the action to take.</returns>
    public static TamperResponseAction ValidateOnOpen(
        Stream stream,
        byte[] hmacKey,
        TamperResponse response = TamperResponse.Reject)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hmacKey);

        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable.", nameof(stream));

        // Read the superblock group (blocks 0-3) to determine block size
        // First, read a minimal header to get the block size from the superblock
        stream.Seek(0, SeekOrigin.Begin);

        // Read enough for minimum block size superblock group (4 * MinBlockSize)
        var minGroupSize = SuperblockGroup.BlockCount * FormatConstants.MinBlockSize;
        var headerBuffer = new byte[minGroupSize];
        ReadFully(stream, headerBuffer, 0, headerBuffer.Length);

        // Deserialize with min block size to get the actual block size
        var tempGroup = SuperblockGroup.DeserializeFromBlocks(headerBuffer, FormatConstants.MinBlockSize);
        var blockSize = tempGroup.PrimarySuperblock.BlockSize;

        // If the actual block size is larger, re-read with the correct size
        SuperblockGroup group;
        if (blockSize > FormatConstants.MinBlockSize)
        {
            var fullGroupSize = SuperblockGroup.BlockCount * blockSize;
            var fullBuffer = new byte[fullGroupSize];
            stream.Seek(0, SeekOrigin.Begin);
            ReadFully(stream, fullBuffer, 0, fullBuffer.Length);
            group = SuperblockGroup.DeserializeFromBlocks(fullBuffer, blockSize);
        }
        else
        {
            group = tempGroup;
        }

        // Build region map from the Region Pointer Table
        var regions = BuildRegionMap(group.RegionPointers);

        var context = new TamperDetectionContext(
            stream,
            blockSize,
            group.PrimarySuperblock,
            group.Integrity,
            group.Extended,
            regions,
            hmacKey,
            namespacePublicKey: null,
            signatureProvider: null,
            configuredResponse: response);

        return ValidateOnOpen(context);
    }

    // ── Individual check runners ────────────────────────────────────────

    private static TamperCheckResult RunFormatFingerprintCheck(TamperDetectionContext context)
    {
        try
        {
            bool passed = FormatFingerprintValidator.ValidateFingerprint(context.Integrity.FormatFingerprint);
            return new TamperCheckResult(
                TamperDetectionResult.CheckFormatFingerprint,
                passed,
                passed ? null : "Format fingerprint does not match current specification revision");
        }
        catch (Exception ex)
        {
            return new TamperCheckResult(
                TamperDetectionResult.CheckFormatFingerprint,
                false,
                $"Format fingerprint check threw exception: {ex.Message}");
        }
    }

    private static TamperCheckResult RunNamespaceSignatureCheck(TamperDetectionContext context)
    {
        if (context.NamespacePublicKey is null)
        {
            return new TamperCheckResult(
                TamperDetectionResult.CheckNamespaceSignature,
                true,
                "Skipped: no public key");
        }

        try
        {
            var provider = context.SignatureProvider ?? NamespaceAuthority.GetDefaultProvider();
            bool passed = NamespaceAuthority.VerifyRegistration(
                context.Extended.Namespace,
                context.NamespacePublicKey,
                provider);

            return new TamperCheckResult(
                TamperDetectionResult.CheckNamespaceSignature,
                passed,
                passed ? null : "Namespace signature verification failed");
        }
        catch (Exception ex)
        {
            return new TamperCheckResult(
                TamperDetectionResult.CheckNamespaceSignature,
                false,
                $"Namespace signature check threw exception: {ex.Message}");
        }
    }

    private static TamperCheckResult RunHeaderIntegritySealCheck(TamperDetectionContext context)
    {
        try
        {
            // Read block 0 from the VDE stream
            var block0 = new byte[context.BlockSize];
            context.VdeStream.Seek(0, SeekOrigin.Begin);
            ReadFully(context.VdeStream, block0, 0, context.BlockSize);

            bool passed = HeaderIntegritySeal.VerifySeal(block0, context.BlockSize, context.HmacKey);
            return new TamperCheckResult(
                TamperDetectionResult.CheckHeaderIntegritySeal,
                passed,
                passed ? null : "Header integrity seal mismatch: superblock has been modified");
        }
        catch (Exception ex)
        {
            return new TamperCheckResult(
                TamperDetectionResult.CheckHeaderIntegritySeal,
                false,
                $"Header integrity seal check threw exception: {ex.Message}");
        }
    }

    private static TamperCheckResult RunFileSizeSentinelCheck(TamperDetectionContext context)
    {
        try
        {
            bool passed = FileSizeSentinel.Validate(context.VdeStream.Length, context.Superblock);
            return new TamperCheckResult(
                TamperDetectionResult.CheckFileSizeSentinel,
                passed,
                passed ? null : $"File size mismatch: expected {context.Superblock.ExpectedFileSize} bytes, actual {context.VdeStream.Length} bytes");
        }
        catch (Exception ex)
        {
            return new TamperCheckResult(
                TamperDetectionResult.CheckFileSizeSentinel,
                false,
                $"File size sentinel check threw exception: {ex.Message}");
        }
    }

    private static TamperCheckResult RunMetadataChainHashCheck(TamperDetectionContext context)
    {
        try
        {
            bool passed = MetadataChainHasher.ValidateChainHash(
                context.VdeStream,
                context.BlockSize,
                context.Regions,
                context.Integrity.MetadataChainHash);

            return new TamperCheckResult(
                TamperDetectionResult.CheckMetadataChainHash,
                passed,
                passed ? null : "Metadata chain hash mismatch: one or more metadata regions have been modified");
        }
        catch (Exception ex)
        {
            return new TamperCheckResult(
                TamperDetectionResult.CheckMetadataChainHash,
                false,
                $"Metadata chain hash check threw exception: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a region name-to-location map from the <see cref="RegionPointerTable"/>.
    /// Uses <see cref="BlockTypeTags.TagToString"/> for region names.
    /// Only includes active regions (with <see cref="RegionFlags.Active"/> flag).
    /// </summary>
    private static Dictionary<string, (long StartBlock, long BlockCount)> BuildRegionMap(
        RegionPointerTable regionPointers)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < RegionPointerTable.MaxSlots; i++)
        {
            var slot = regionPointers.GetSlot(i);
            if ((slot.Flags & RegionFlags.Active) == 0)
                continue;

            var name = BlockTypeTags.TagToString(slot.RegionTypeId);
            // Use first occurrence for each name (duplicates are invalid but defensive)
            map.TryAdd(name, (slot.StartBlock, slot.BlockCount));
        }
        return map;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes from the stream, throwing on short read.</summary>
    private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Unexpected end of VDE stream: needed {count} bytes but got {totalRead}.");
            totalRead += read;
        }
    }
}
