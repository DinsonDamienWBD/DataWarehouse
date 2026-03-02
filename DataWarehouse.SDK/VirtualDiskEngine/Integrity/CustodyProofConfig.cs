using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Configuration for proof-of-physical-custody generation and verification.
/// Controls which TPM PCR registers are included, hash algorithm selection,
/// freshness enforcement, and proof expiry.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Proof of physical custody (VOPT-56)")]
public sealed class CustodyProofConfig
{
    /// <summary>
    /// PCR indices to include in the TPM quote.
    /// Default: PCRs 0, 1, 2, 7 (firmware, firmware config, option ROMs, secure boot).
    /// </summary>
    public int[] PcrIndices { get; set; } = new[] { 0, 1, 2, 7 };

    /// <summary>
    /// Hash algorithm for the TPM PCR bank.
    /// Must match the bank configured on the TPM device.
    /// Default: "SHA-256".
    /// </summary>
    public string HashAlgorithm { get; set; } = "SHA-256";

    /// <summary>
    /// Whether to include a freshness nonce (bound to current timestamp) in the proof.
    /// When true, the nonce incorporates UTC timestamp to prevent replay attacks.
    /// Default: true.
    /// </summary>
    public bool IncludeTimestamp { get; set; } = true;

    /// <summary>
    /// Whether to bind the proof to the specific volume UUID.
    /// When true, proof cannot be transplanted to a different VDE volume.
    /// Default: true.
    /// </summary>
    public bool IncludeVolumeUuid { get; set; } = true;

    /// <summary>
    /// Proof validity window in hours.
    /// Proofs older than this are considered expired during verification.
    /// Default: 24 hours.
    /// </summary>
    public int ProofExpiryHours { get; set; } = 24;
}
