using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Types of hardware security tokens accepted for quorum approval.
    /// Each type uses a different cryptographic protocol for challenge-response validation.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public enum HardwareTokenType
    {
        /// <summary>YubiKey one-time password using Yubico OTP protocol (modhex-encoded 44-character OTP).</summary>
        [Description("YubiKey one-time password (Yubico OTP)")]
        YubiKeyOtp = 0,

        /// <summary>YubiKey FIDO2/WebAuthn attestation with CBOR-encoded credential.</summary>
        [Description("YubiKey FIDO2/WebAuthn")]
        YubiKeyFido2 = 1,

        /// <summary>PIV smart card with X.509 certificate for digital signature verification.</summary>
        [Description("PIV smart card with X.509 certificate")]
        SmartCardPiv = 2,

        /// <summary>DoD Common Access Card (CAC) with X.509 certificate for digital signature verification.</summary>
        [Description("DoD Common Access Card (CAC)")]
        SmartCardCac = 3,

        /// <summary>TPM-based remote attestation using a TPM quote blob for platform integrity verification.</summary>
        [Description("TPM-based remote attestation")]
        TpmAttestation = 4
    }

    /// <summary>
    /// Represents a challenge issued to a hardware token for validation.
    /// Challenges are single-use and time-bounded (default 5 minutes).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public sealed record HardwareTokenChallenge
    {
        /// <summary>Unique identifier for this challenge (GUID string).</summary>
        public required string ChallengeId { get; init; }

        /// <summary>The type of hardware token this challenge targets.</summary>
        public required HardwareTokenType TokenType { get; init; }

        /// <summary>Challenge data (nonce) as base64-encoded bytes sent to the hardware token.</summary>
        public required string ChallengeData { get; init; }

        /// <summary>UTC timestamp when the challenge was issued.</summary>
        public required DateTimeOffset IssuedAt { get; init; }

        /// <summary>Duration for which this challenge remains valid. Default: 5 minutes.</summary>
        public required TimeSpan ValidFor { get; init; }
    }

    /// <summary>
    /// Result of validating a hardware token's response to a challenge.
    /// Contains validation outcome, token identity information, and failure details.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public sealed record TokenValidationResult
    {
        /// <summary>Whether the token response was successfully validated.</summary>
        public required bool IsValid { get; init; }

        /// <summary>The type of hardware token that was validated.</summary>
        public required HardwareTokenType TokenType { get; init; }

        /// <summary>Serial number of the hardware token, if available (e.g., YubiKey serial extracted from OTP).</summary>
        public string? TokenSerialNumber { get; init; }

        /// <summary>X.509 certificate subject for smart card tokens, or null for non-certificate tokens.</summary>
        public string? CertificateSubject { get; init; }

        /// <summary>Human-readable failure reason when <see cref="IsValid"/> is false; null on success.</summary>
        public string? FailureReason { get; init; }

        /// <summary>UTC timestamp when the validation was performed.</summary>
        public required DateTimeOffset ValidatedAt { get; init; }

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        /// <param name="type">The validated hardware token type.</param>
        /// <param name="serial">Optional token serial number.</param>
        /// <param name="certSubject">Optional certificate subject (for smart cards).</param>
        /// <returns>A successful <see cref="TokenValidationResult"/>.</returns>
        public static TokenValidationResult Success(HardwareTokenType type, string? serial = null, string? certSubject = null) => new()
        {
            IsValid = true,
            TokenType = type,
            TokenSerialNumber = serial,
            CertificateSubject = certSubject,
            ValidatedAt = DateTimeOffset.UtcNow
        };

        /// <summary>
        /// Creates a failed validation result with a descriptive reason.
        /// </summary>
        /// <param name="type">The hardware token type that failed validation.</param>
        /// <param name="reason">Human-readable description of why validation failed.</param>
        /// <returns>A failed <see cref="TokenValidationResult"/>.</returns>
        public static TokenValidationResult Failure(HardwareTokenType type, string reason) => new()
        {
            IsValid = false,
            TokenType = type,
            FailureReason = reason,
            ValidatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Defines the contract for validating hardware security tokens in quorum approval flows.
    /// Supports challenge-response protocols for YubiKey (OTP, FIDO2), smart cards (PIV, CAC),
    /// and TPM attestation.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public interface IHardwareTokenValidator
    {
        /// <summary>
        /// Creates a cryptographic challenge for the specified hardware token type.
        /// The challenge must be presented to the token and the response returned via
        /// <see cref="ValidateResponseAsync"/>.
        /// </summary>
        /// <param name="tokenType">The type of hardware token to create a challenge for.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="HardwareTokenChallenge"/> containing the nonce and metadata.</returns>
        Task<HardwareTokenChallenge> CreateChallengeAsync(HardwareTokenType tokenType, CancellationToken ct = default);

        /// <summary>
        /// Validates the hardware token's response to a previously issued challenge.
        /// Challenges are single-use: once validated (success or failure), the challenge is consumed.
        /// </summary>
        /// <param name="challengeId">The challenge ID from <see cref="HardwareTokenChallenge.ChallengeId"/>.</param>
        /// <param name="responseData">The token's response data (format varies by token type).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="TokenValidationResult"/> indicating success or failure with details.</returns>
        Task<TokenValidationResult> ValidateResponseAsync(string challengeId, string responseData, CancellationToken ct = default);

        /// <summary>
        /// Checks whether the specified hardware token type is supported by this validator.
        /// </summary>
        /// <param name="tokenType">The hardware token type to check.</param>
        /// <returns>True if the token type can be validated; false otherwise.</returns>
        bool IsTokenTypeSupported(HardwareTokenType tokenType);
    }

    /// <summary>
    /// Configuration for the dead man's switch that monitors super admin activity and
    /// auto-locks the system to maximum security after a configurable period of inactivity.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public sealed record DeadManSwitchConfiguration
    {
        /// <summary>
        /// Duration of inactivity before the system auto-locks to maximum security.
        /// Default: 30 days.
        /// </summary>
        public TimeSpan InactivityThreshold { get; init; } = TimeSpan.FromDays(30);

        /// <summary>
        /// Warning period before the lock triggers. Warnings are issued when inactivity
        /// reaches InactivityThreshold minus WarningPeriod. Default: 7 days.
        /// </summary>
        public TimeSpan WarningPeriod { get; init; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Whether the dead man's switch is enabled. When false, no monitoring or locking occurs.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Activity types that reset the inactivity timer. Only these types prevent the switch
        /// from triggering. Default: QuorumApproval, EscalationConfirm, PolicyChange, Login.
        /// </summary>
        public string[] MonitoredActivityTypes { get; init; } = new[]
        {
            "QuorumApproval",
            "EscalationConfirm",
            "PolicyChange",
            "Login"
        };
    }
}
