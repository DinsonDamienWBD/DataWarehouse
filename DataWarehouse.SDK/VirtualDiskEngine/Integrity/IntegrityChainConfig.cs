using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Integrity verification level for the cross-extent hash chain.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent integrity chain configuration (VOPT-50)")]
public enum IntegrityLevel
{
    /// <summary>Level 1: Per-block XxHash64 verification via Universal Block Trailers.</summary>
    Block = 1,

    /// <summary>Level 2: Per-extent BLAKE3 ExpectedHash verification.</summary>
    Extent = 2,

    /// <summary>Level 3: Per-file Merkle root verification via the Integrity Tree region.</summary>
    File = 3,
}

/// <summary>
/// Configuration for <c>CrossExtentIntegrityChain</c> verification behaviour.
/// Controls how deeply the chain verifies and how results are reported.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-38: Cross-extent integrity chain configuration (VOPT-50)")]
public sealed class IntegrityChainConfig
{
    /// <summary>
    /// The highest verification level to execute.
    /// Verification stops after this level even when lower levels pass.
    /// Default: <see cref="IntegrityLevel.File"/> (all three levels).
    /// </summary>
    public IntegrityLevel MaxVerificationLevel { get; set; } = IntegrityLevel.File;

    /// <summary>
    /// When <see langword="true"/>, verification stops as soon as any level reports a failure.
    /// When <see langword="false"/> (default), all levels up to <see cref="MaxVerificationLevel"/>
    /// are always executed, enabling a complete failure report in a single pass.
    /// </summary>
    public bool StopOnFirstFailure { get; set; } = false;

    /// <summary>
    /// Maximum number of block reads issued concurrently during Level 1 (block) verification.
    /// Higher values improve throughput on devices that support parallel I/O.
    /// Default: 4.
    /// </summary>
    public int MaxConcurrentBlockReads { get; set; } = 4;

    /// <summary>
    /// When <see langword="true"/> (default), the <c>ChainVerificationResult</c> and
    /// <c>BlockLevelResult</c> include the individual block-level pass/fail entries.
    /// Set to <see langword="false"/> for lightweight summary-only verification.
    /// </summary>
    public bool IncludeBlockDetails { get; set; } = true;
}
