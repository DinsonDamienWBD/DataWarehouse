using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Base exception for all VDE identity, tamper detection, and integrity failures.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- identity exception base (VTMP-01)")]
public class VdeIdentityException : Exception
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public VdeIdentityException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public VdeIdentityException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a default instance.</summary>
    protected VdeIdentityException() : base() { }
}

/// <summary>
/// Thrown when a namespace registration's Ed25519 signature is invalid or has been tampered with.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- namespace signature exception (VTMP-01)")]
public class VdeSignatureException : VdeIdentityException
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public VdeSignatureException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public VdeSignatureException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a default instance.</summary>
    protected VdeSignatureException() : base() { }
}

/// <summary>
/// Thrown when the format fingerprint stored in the VDE does not match the expected
/// fingerprint for the current specification revision.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- fingerprint mismatch exception (VTMP-01)")]
public class VdeFingerprintMismatchException : VdeIdentityException
{
    /// <summary>The expected fingerprint as a hex string.</summary>
    public string? ExpectedHex { get; }

    /// <summary>The actual (stored) fingerprint as a hex string.</summary>
    public string? ActualHex { get; }

    /// <summary>Creates a new instance with the specified message.</summary>
    public VdeFingerprintMismatchException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public VdeFingerprintMismatchException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a new instance with expected and actual fingerprint hex values.</summary>
    public VdeFingerprintMismatchException(string message, string expectedHex, string actualHex)
        : base(message)
    {
        ExpectedHex = expectedHex;
        ActualHex = actualHex;
    }

    /// <summary>Creates a default instance.</summary>
    protected VdeFingerprintMismatchException() : base() { }
}

/// <summary>
/// Thrown when generic tamper detection identifies corruption in VDE headers,
/// hash chains, or sentinel values.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper detected exception (VTMP-01)")]
public class VdeTamperDetectedException : VdeIdentityException
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public VdeTamperDetectedException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public VdeTamperDetectedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a default instance.</summary>
    protected VdeTamperDetectedException() : base() { }
}

/// <summary>
/// Thrown when a VDE nesting operation exceeds the maximum allowed depth.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- nesting depth exception (VTMP-01)")]
public class VdeNestingDepthExceededException : VdeIdentityException
{
    /// <summary>Creates a new instance with the specified message.</summary>
    public VdeNestingDepthExceededException(string message) : base(message) { }

    /// <summary>Creates a new instance with the specified message and inner exception.</summary>
    public VdeNestingDepthExceededException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a default instance.</summary>
    protected VdeNestingDepthExceededException() : base() { }
}
