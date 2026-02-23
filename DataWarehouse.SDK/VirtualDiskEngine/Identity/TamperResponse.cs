using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Regions;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Defines the five configurable severity levels for tamper detection response.
/// When a VDE integrity check fails, the configured level determines the action taken.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper response (VTMP-07)")]
public enum TamperResponse : byte
{
    /// <summary>Record the tamper event, allow normal access.</summary>
    Log = 0,

    /// <summary>Record + raise an alert/event, allow normal access.</summary>
    Alert = 1,

    /// <summary>Record + open VDE in read-only mode (no writes permitted).</summary>
    ReadOnly = 2,

    /// <summary>Record + move VDE to quarantine state (accessible only to recovery tools).</summary>
    Quarantine = 3,

    /// <summary>Record + refuse to open the VDE entirely (throw exception). This is the safest default.</summary>
    Reject = 4
}

/// <summary>
/// Result of a single tamper detection check.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper response (VTMP-07)")]
public readonly record struct TamperCheckResult(string CheckName, bool Passed, string? FailureReason);

/// <summary>
/// Aggregated result of running all tamper detection checks during VDE open.
/// Contains individual check results and provides a human-readable summary.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper response (VTMP-07)")]
public sealed class TamperDetectionResult
{
    /// <summary>Check name constant: namespace signature verification.</summary>
    public const string CheckNamespaceSignature = "NamespaceSignature";

    /// <summary>Check name constant: format fingerprint verification.</summary>
    public const string CheckFormatFingerprint = "FormatFingerprint";

    /// <summary>Check name constant: header integrity seal verification.</summary>
    public const string CheckHeaderIntegritySeal = "HeaderIntegritySeal";

    /// <summary>Check name constant: metadata chain hash verification.</summary>
    public const string CheckMetadataChainHash = "MetadataChainHash";

    /// <summary>Check name constant: file size sentinel verification.</summary>
    public const string CheckFileSizeSentinel = "FileSizeSentinel";

    /// <summary>The individual check results in execution order.</summary>
    public TamperCheckResult[] Checks { get; }

    /// <summary>True if all checks passed (no tamper detected).</summary>
    public bool IsClean
    {
        get
        {
            for (int i = 0; i < Checks.Length; i++)
            {
                if (!Checks[i].Passed) return false;
            }
            return true;
        }
    }

    /// <summary>Returns only the checks that failed.</summary>
    public IReadOnlyList<TamperCheckResult> FailedChecks
    {
        get
        {
            var failed = new List<TamperCheckResult>();
            for (int i = 0; i < Checks.Length; i++)
            {
                if (!Checks[i].Passed) failed.Add(Checks[i]);
            }
            return failed;
        }
    }

    /// <summary>
    /// Human-readable summary of check results.
    /// Examples: "All 5 integrity checks passed" or
    /// "2 of 5 integrity checks failed: HeaderIntegritySeal, FileSizeSentinel".
    /// </summary>
    public string Summary
    {
        get
        {
            var failed = FailedChecks;
            if (failed.Count == 0)
                return $"All {Checks.Length} integrity checks passed";

            var names = string.Join(", ", failed.Select(f => f.CheckName));
            return $"{failed.Count} of {Checks.Length} integrity checks failed: {names}";
        }
    }

    /// <summary>Creates a new detection result from the given check results.</summary>
    /// <param name="checks">The individual check results.</param>
    public TamperDetectionResult(TamperCheckResult[] checks)
    {
        Checks = checks ?? throw new ArgumentNullException(nameof(checks));
    }
}

/// <summary>
/// Serializes and deserializes the tamper response policy level to and from
/// <see cref="PolicyDefinition"/> for storage in the Policy Vault.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper response (VTMP-07)")]
public sealed class TamperResponsePolicy
{
    /// <summary>Unique policy type identifier for tamper response policies.</summary>
    public const ushort PolicyTypeId = 0x0074;

    /// <summary>Schema version of the tamper response policy payload.</summary>
    public const ushort PolicyVersion = 1;

    /// <summary>The configured tamper response severity level.</summary>
    public TamperResponse Level { get; set; }

    /// <summary>
    /// Serializes the tamper response level as a single-byte payload.
    /// </summary>
    /// <returns>A 1-byte array containing the <see cref="Level"/> value.</returns>
    public byte[] Serialize()
    {
        return [(byte)Level];
    }

    /// <summary>
    /// Deserializes a tamper response policy from a byte array.
    /// </summary>
    /// <param name="data">The serialized policy data (at least 1 byte).</param>
    /// <returns>A new <see cref="TamperResponsePolicy"/> with the deserialized level.</returns>
    /// <exception cref="ArgumentException">The data is null or empty.</exception>
    public static TamperResponsePolicy Deserialize(byte[] data)
    {
        if (data is null || data.Length < 1)
            throw new ArgumentException("Tamper response policy data must be at least 1 byte.", nameof(data));

        return new TamperResponsePolicy { Level = (TamperResponse)data[0] };
    }

    /// <summary>
    /// Creates a <see cref="PolicyDefinition"/> suitable for storage in the Policy Vault.
    /// </summary>
    /// <returns>A <see cref="PolicyDefinition"/> with <see cref="PolicyTypeId"/> and serialized level.</returns>
    public PolicyDefinition ToPolicyDefinition()
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        return new PolicyDefinition(
            Guid.NewGuid(),
            PolicyTypeId,
            PolicyVersion,
            now,
            now,
            Serialize());
    }

    /// <summary>
    /// Extracts a <see cref="TamperResponsePolicy"/> from a <see cref="PolicyDefinition"/>.
    /// </summary>
    /// <param name="pd">The policy definition to extract from.</param>
    /// <returns>A new <see cref="TamperResponsePolicy"/> with the deserialized level.</returns>
    /// <exception cref="ArgumentException">The policy definition has an unexpected type or version.</exception>
    public static TamperResponsePolicy FromPolicyDefinition(PolicyDefinition pd)
    {
        ArgumentNullException.ThrowIfNull(pd);

        if (pd.PolicyType != PolicyTypeId)
            throw new ArgumentException(
                $"Expected PolicyType 0x{PolicyTypeId:X4} but got 0x{pd.PolicyType:X4}.", nameof(pd));

        return Deserialize(pd.Data);
    }

    /// <summary>
    /// Gets the default tamper response policy: <see cref="TamperResponse.Reject"/> (safest default).
    /// </summary>
    public static TamperResponsePolicy Default => new() { Level = TamperResponse.Reject };
}

/// <summary>
/// The action to take after tamper detection, produced by <see cref="TamperResponseExecutor"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper response (VTMP-07)")]
public readonly struct TamperResponseAction
{
    /// <summary>The tamper response level that was applied.</summary>
    public TamperResponse AppliedLevel { get; }

    /// <summary>True if the VDE should be opened (or remain open).</summary>
    public bool AllowOpen { get; }

    /// <summary>True if the VDE should be opened in read-only mode.</summary>
    public bool ReadOnlyMode { get; }

    /// <summary>True if the VDE should be moved to quarantine.</summary>
    public bool Quarantined { get; }

    /// <summary>Human-readable message describing the action taken.</summary>
    public string Message { get; }

    /// <summary>Creates a new tamper response action.</summary>
    public TamperResponseAction(TamperResponse appliedLevel, bool allowOpen, bool readOnlyMode, bool quarantined, string message)
    {
        AppliedLevel = appliedLevel;
        AllowOpen = allowOpen;
        ReadOnlyMode = readOnlyMode;
        Quarantined = quarantined;
        Message = message;
    }
}

/// <summary>
/// Evaluates a <see cref="TamperDetectionResult"/> against a configured <see cref="TamperResponse"/>
/// level and produces the appropriate <see cref="TamperResponseAction"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- tamper response (VTMP-07)")]
public static class TamperResponseExecutor
{
    /// <summary>
    /// Evaluates the detection result and returns the action to take based on the configured level.
    /// If the result is clean (no tamper detected), always returns AllowOpen=true regardless of level.
    /// For <see cref="TamperResponse.Reject"/>, throws <see cref="VdeTamperDetectedException"/>.
    /// </summary>
    /// <param name="result">The tamper detection result from the orchestrator.</param>
    /// <param name="configuredLevel">The configured tamper response severity level.</param>
    /// <returns>A <see cref="TamperResponseAction"/> describing the action to take.</returns>
    /// <exception cref="VdeTamperDetectedException">
    /// Thrown when the configured level is <see cref="TamperResponse.Reject"/> and tamper is detected.
    /// </exception>
    public static TamperResponseAction Execute(TamperDetectionResult result, TamperResponse configuredLevel)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Clean result: always allow open regardless of configured level
        if (result.IsClean)
        {
            return new TamperResponseAction(
                configuredLevel,
                allowOpen: true,
                readOnlyMode: false,
                quarantined: false,
                message: result.Summary);
        }

        var summary = result.Summary;

        return configuredLevel switch
        {
            TamperResponse.Log => new TamperResponseAction(
                TamperResponse.Log,
                allowOpen: true,
                readOnlyMode: false,
                quarantined: false,
                message: $"Tamper detected (logged): {summary}"),

            TamperResponse.Alert => new TamperResponseAction(
                TamperResponse.Alert,
                allowOpen: true,
                readOnlyMode: false,
                quarantined: false,
                message: $"ALERT: Tamper detected: {summary}"),

            TamperResponse.ReadOnly => new TamperResponseAction(
                TamperResponse.ReadOnly,
                allowOpen: true,
                readOnlyMode: true,
                quarantined: false,
                message: $"Tamper detected: VDE opened in read-only mode: {summary}"),

            TamperResponse.Quarantine => new TamperResponseAction(
                TamperResponse.Quarantine,
                allowOpen: false,
                readOnlyMode: false,
                quarantined: true,
                message: $"Tamper detected: VDE quarantined: {summary}"),

            TamperResponse.Reject => throw new VdeTamperDetectedException(
                $"Tamper detected: VDE rejected: {summary}"),

            _ => throw new ArgumentOutOfRangeException(nameof(configuredLevel),
                $"Unknown TamperResponse level: {configuredLevel}")
        };
    }
}
