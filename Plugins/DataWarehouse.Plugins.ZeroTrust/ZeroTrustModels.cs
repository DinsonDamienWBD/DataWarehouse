using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.ZeroTrust
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Zero-Trust Architecture plugin.
    /// </summary>
    public sealed class ZeroTrustConfig
    {
        /// <summary>
        /// Gets or sets the storage path for persisted state.
        /// </summary>
        public string StoragePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "zerotrust");

        /// <summary>
        /// Gets or sets whether identity verification is required for all requests.
        /// </summary>
        public bool RequireIdentityVerification { get; set; } = true;

        /// <summary>
        /// Gets or sets whether device compliance is required.
        /// </summary>
        public bool RequireDeviceCompliance { get; set; } = false;

        /// <summary>
        /// Gets or sets whether full device compliance is required (vs partial).
        /// </summary>
        public bool RequireFullDeviceCompliance { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to block requests when anomalies are detected.
        /// </summary>
        public bool BlockOnAnomaly { get; set; } = false;

        /// <summary>
        /// Gets or sets the minimum trust score required for access.
        /// </summary>
        public double MinimumTrustScore { get; set; } = 50.0;

        /// <summary>
        /// Gets or sets the default trust score for new identities.
        /// </summary>
        public double DefaultTrustScore { get; set; } = 60.0;

        /// <summary>
        /// Gets or sets the session timeout duration.
        /// </summary>
        public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(8);

        /// <summary>
        /// Gets or sets whether session renewal is enabled.
        /// </summary>
        public bool EnableSessionRenewal { get; set; } = true;

        /// <summary>
        /// Gets or sets the window before expiration when session can be renewed.
        /// </summary>
        public TimeSpan SessionRenewalWindow { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Gets or sets whether to revoke session on IP change.
        /// </summary>
        public bool RevokeSessionOnIpChange { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to revoke session on device change.
        /// </summary>
        public bool RevokeSessionOnDeviceChange { get; set; } = true;

        /// <summary>
        /// Gets or sets the trust score penalty for IP changes.
        /// </summary>
        public double IpChangeTrustPenalty { get; set; } = 15.0;

        /// <summary>
        /// Gets or sets the trust score penalty for device changes.
        /// </summary>
        public double DeviceChangeTrustPenalty { get; set; } = 25.0;

        /// <summary>
        /// Gets or sets whether device encryption is required.
        /// </summary>
        public bool RequireDeviceEncryption { get; set; } = true;

        /// <summary>
        /// Gets or sets whether device firewall is required.
        /// </summary>
        public bool RequireDeviceFirewall { get; set; } = true;

        /// <summary>
        /// Gets or sets whether device antivirus is required.
        /// </summary>
        public bool RequireDeviceAntivirus { get; set; } = false;

        /// <summary>
        /// Gets or sets whether current patches are required.
        /// </summary>
        public bool RequireCurrentPatches { get; set; } = false;

        /// <summary>
        /// Gets or sets the maximum failed access attempts before blocking.
        /// </summary>
        public int MaxFailedAccessAttempts { get; set; } = 5;

        /// <summary>
        /// Gets or sets the maximum access patterns to keep per profile.
        /// </summary>
        public int MaxAccessPatternsPerProfile { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the weight for identity verification in trust score.
        /// </summary>
        public double IdentityVerificationWeight { get; set; } = 0.4;

        /// <summary>
        /// Gets or sets the weight for device compliance in trust score.
        /// </summary>
        public double DeviceComplianceWeight { get; set; } = 0.25;

        /// <summary>
        /// Gets or sets the weight for behavior in trust score.
        /// </summary>
        public double BehaviorWeight { get; set; } = 0.25;

        /// <summary>
        /// Gets or sets the weight for session validity in trust score.
        /// </summary>
        public double SessionWeight { get; set; } = 0.1;

        /// <summary>
        /// Gets or sets whether to encrypt data at rest by default.
        /// </summary>
        public bool DefaultEncryptAtRest { get; set; } = true;

        /// <summary>
        /// Gets or sets the default minimum encryption key size.
        /// </summary>
        public int DefaultMinKeySize { get; set; } = 128;

        /// <summary>
        /// Gets or sets the minimum trust score for write operations.
        /// </summary>
        public double WriteOperationMinTrust { get; set; } = 60.0;

        /// <summary>
        /// Gets or sets the minimum trust score for delete operations.
        /// </summary>
        public double DeleteOperationMinTrust { get; set; } = 75.0;

        /// <summary>
        /// Gets or sets the minimum trust score for admin operations.
        /// </summary>
        public double AdminOperationMinTrust { get; set; } = 90.0;

        /// <summary>
        /// Gets or sets the anomaly detection sensitivity (0-1).
        /// </summary>
        public double AnomalyDetectionSensitivity { get; set; } = 0.7;

        /// <summary>
        /// Gets or sets the time window for anomaly detection analysis.
        /// </summary>
        public TimeSpan AnomalyDetectionWindow { get; set; } = TimeSpan.FromHours(24);
    }

    #endregion

    #region Request/Response Models

    /// <summary>
    /// Represents the context of a request for zero-trust verification.
    /// </summary>
    public sealed class RequestContext
    {
        /// <summary>
        /// Gets or sets the unique request identifier.
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identity ID making the request.
        /// </summary>
        public string IdentityId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device ID.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the session ID.
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Gets or sets the source IP address.
        /// </summary>
        public string? SourceIp { get; set; }

        /// <summary>
        /// Gets or sets the source microsegment.
        /// </summary>
        public string? SourceSegment { get; set; }

        /// <summary>
        /// Gets or sets the target microsegment.
        /// </summary>
        public string? TargetSegment { get; set; }

        /// <summary>
        /// Gets or sets the resource path being accessed.
        /// </summary>
        public string? ResourcePath { get; set; }

        /// <summary>
        /// Gets or sets the operation being performed.
        /// </summary>
        public string? Operation { get; set; }

        /// <summary>
        /// Gets or sets the protocol being used.
        /// </summary>
        public string? Protocol { get; set; }

        /// <summary>
        /// Gets or sets the port being used.
        /// </summary>
        public int? Port { get; set; }

        /// <summary>
        /// Gets or sets the authentication method used.
        /// </summary>
        public string? AuthenticationMethod { get; set; }

        /// <summary>
        /// Gets or sets the resource sensitivity level.
        /// </summary>
        public SensitivityLevel? ResourceSensitivity { get; set; }

        /// <summary>
        /// Gets or sets additional attributes.
        /// </summary>
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Represents the result of a zero-trust verification.
    /// </summary>
    public sealed class ZeroTrustVerificationResult
    {
        /// <summary>
        /// Gets or sets the request ID.
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the request is allowed.
        /// </summary>
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Gets or sets the calculated trust score (0-100).
        /// </summary>
        public double TrustScore { get; set; }

        /// <summary>
        /// Gets or sets whether identity was verified.
        /// </summary>
        public bool IdentityVerified { get; set; }

        /// <summary>
        /// Gets or sets identity verification details.
        /// </summary>
        public string? IdentityDetails { get; set; }

        /// <summary>
        /// Gets or sets whether device was verified.
        /// </summary>
        public bool DeviceVerified { get; set; }

        /// <summary>
        /// Gets or sets device verification details.
        /// </summary>
        public string? DeviceDetails { get; set; }

        /// <summary>
        /// Gets or sets whether session is valid.
        /// </summary>
        public bool SessionValid { get; set; }

        /// <summary>
        /// Gets or sets session validation details.
        /// </summary>
        public string? SessionDetails { get; set; }

        /// <summary>
        /// Gets or sets whether segmentation rules allow access.
        /// </summary>
        public bool SegmentationAllowed { get; set; }

        /// <summary>
        /// Gets or sets segmentation check details.
        /// </summary>
        public string? SegmentDetails { get; set; }

        /// <summary>
        /// Gets or sets whether policy evaluation passed.
        /// </summary>
        public bool PolicyEvaluationPassed { get; set; }

        /// <summary>
        /// Gets or sets the list of applied policies.
        /// </summary>
        public List<string> AppliedPolicies { get; set; } = new();

        /// <summary>
        /// Gets or sets whether an anomaly was detected.
        /// </summary>
        public bool AnomalyDetected { get; set; }

        /// <summary>
        /// Gets or sets anomaly detection details.
        /// </summary>
        public string? AnomalyDetails { get; set; }

        /// <summary>
        /// Gets or sets the denial reason if not allowed.
        /// </summary>
        public string? DenialReason { get; set; }

        /// <summary>
        /// Gets or sets the required encryption settings.
        /// </summary>
        public EncryptionRequirement? RequiredEncryption { get; set; }

        /// <summary>
        /// Gets or sets the allowed operations based on trust score.
        /// </summary>
        public List<string> AllowedOperations { get; set; } = new();

        /// <summary>
        /// Gets or sets the verification timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Represents encryption requirements for a request.
    /// </summary>
    public sealed class EncryptionRequirement
    {
        /// <summary>
        /// Gets or sets whether encryption in transit is required.
        /// </summary>
        public bool InTransit { get; set; }

        /// <summary>
        /// Gets or sets whether encryption at rest is required.
        /// </summary>
        public bool AtRest { get; set; }

        /// <summary>
        /// Gets or sets the minimum key size in bits.
        /// </summary>
        public int MinKeySize { get; set; }

        /// <summary>
        /// Gets or sets the required algorithms.
        /// </summary>
        public List<string>? RequiredAlgorithms { get; set; }
    }

    #endregion

    #region Session Models

    /// <summary>
    /// Request to create a new zero-trust session.
    /// </summary>
    public sealed class SessionCreationRequest
    {
        /// <summary>
        /// Gets or sets the identity ID.
        /// </summary>
        public string IdentityId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device ID.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the source IP address.
        /// </summary>
        public string? SourceIp { get; set; }

        /// <summary>
        /// Gets or sets additional session attributes.
        /// </summary>
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Represents a zero-trust session.
    /// </summary>
    public sealed class ZeroTrustSession
    {
        /// <summary>
        /// Gets or sets the session ID.
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identity ID.
        /// </summary>
        public string IdentityId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device ID.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the source IP address.
        /// </summary>
        public string? SourceIp { get; set; }

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the last activity timestamp.
        /// </summary>
        public DateTime LastActivityAt { get; set; }

        /// <summary>
        /// Gets or sets the expiration timestamp.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Gets or sets the initial trust score.
        /// </summary>
        public double InitialTrustScore { get; set; }

        /// <summary>
        /// Gets or sets the current trust score.
        /// </summary>
        public double CurrentTrustScore { get; set; }

        /// <summary>
        /// Gets or sets the session state.
        /// </summary>
        public SessionState State { get; set; }

        /// <summary>
        /// Gets or sets the security level.
        /// </summary>
        public SecurityLevel SecurityLevel { get; set; }

        /// <summary>
        /// Gets or sets additional attributes.
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();

        /// <summary>
        /// Gets or sets the revocation timestamp.
        /// </summary>
        public DateTime? RevokedAt { get; set; }

        /// <summary>
        /// Gets or sets the revocation reason.
        /// </summary>
        public string? RevocationReason { get; set; }
    }

    /// <summary>
    /// Session state enumeration.
    /// </summary>
    public enum SessionState
    {
        /// <summary>Session is active.</summary>
        Active,
        /// <summary>Session is suspended.</summary>
        Suspended,
        /// <summary>Session has expired.</summary>
        Expired,
        /// <summary>Session has been revoked.</summary>
        Revoked
    }

    /// <summary>
    /// Security level enumeration.
    /// </summary>
    public enum SecurityLevel
    {
        /// <summary>Minimal security level.</summary>
        Minimal,
        /// <summary>Low security level.</summary>
        Low,
        /// <summary>Medium security level.</summary>
        Medium,
        /// <summary>High security level.</summary>
        High
    }

    /// <summary>
    /// Result of session validation.
    /// </summary>
    public sealed class SessionValidationResult
    {
        /// <summary>
        /// Gets or sets whether the session is valid.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Gets or sets the validation details.
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Gets or sets the session if valid.
        /// </summary>
        public ZeroTrustSession? Session { get; set; }

        /// <summary>
        /// Gets or sets the remaining time until expiration.
        /// </summary>
        public TimeSpan? RemainingTime { get; set; }

        /// <summary>
        /// Gets or sets the current trust score.
        /// </summary>
        public double CurrentTrustScore { get; set; }
    }

    #endregion

    #region Device Models

    /// <summary>
    /// Request to register a device.
    /// </summary>
    public sealed class DeviceRegistrationRequest
    {
        /// <summary>
        /// Gets or sets the device ID (optional, will be generated if not provided).
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the device name.
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device type.
        /// </summary>
        public string DeviceType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the platform.
        /// </summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the OS version.
        /// </summary>
        public string? OsVersion { get; set; }

        /// <summary>
        /// Gets or sets the owner ID.
        /// </summary>
        public string OwnerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether encryption is enabled.
        /// </summary>
        public bool IsEncryptionEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether firewall is enabled.
        /// </summary>
        public bool IsFirewallEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether antivirus is enabled.
        /// </summary>
        public bool IsAntivirusEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether patches are current.
        /// </summary>
        public bool IsPatchCurrent { get; set; }

        /// <summary>
        /// Gets or sets additional attributes.
        /// </summary>
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Represents a registered device.
    /// </summary>
    public sealed class DeviceRegistration
    {
        /// <summary>
        /// Gets or sets the device ID.
        /// </summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device name.
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device type.
        /// </summary>
        public string DeviceType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the platform.
        /// </summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the OS version.
        /// </summary>
        public string? OsVersion { get; set; }

        /// <summary>
        /// Gets or sets the device fingerprint.
        /// </summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the registration timestamp.
        /// </summary>
        public DateTime RegisteredAt { get; set; }

        /// <summary>
        /// Gets or sets the last seen timestamp.
        /// </summary>
        public DateTime LastSeenAt { get; set; }

        /// <summary>
        /// Gets or sets the owner ID.
        /// </summary>
        public string OwnerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the compliance status.
        /// </summary>
        public ComplianceStatus ComplianceStatus { get; set; }

        /// <summary>
        /// Gets or sets the trust score.
        /// </summary>
        public double TrustScore { get; set; }

        /// <summary>
        /// Gets or sets the security posture.
        /// </summary>
        public DeviceSecurityPosture SecurityPosture { get; set; } = new();

        /// <summary>
        /// Gets or sets additional attributes.
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    /// <summary>
    /// Device security posture information.
    /// </summary>
    public sealed class DeviceSecurityPosture
    {
        /// <summary>
        /// Gets or sets whether encryption is enabled.
        /// </summary>
        public bool IsEncryptionEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether firewall is enabled.
        /// </summary>
        public bool IsFirewallEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether antivirus is enabled.
        /// </summary>
        public bool IsAntivirusEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether patches are current.
        /// </summary>
        public bool IsPatchCurrent { get; set; }

        /// <summary>
        /// Gets or sets the last security check timestamp.
        /// </summary>
        public DateTime LastSecurityCheck { get; set; }
    }

    /// <summary>
    /// Device compliance status.
    /// </summary>
    public enum ComplianceStatus
    {
        /// <summary>Compliance status unknown.</summary>
        Unknown,
        /// <summary>Device is compliant.</summary>
        Compliant,
        /// <summary>Device is partially compliant.</summary>
        PartiallyCompliant,
        /// <summary>Device is non-compliant.</summary>
        NonCompliant
    }

    /// <summary>
    /// Result of device verification.
    /// </summary>
    public sealed class DeviceVerificationResult
    {
        /// <summary>
        /// Gets or sets whether the device is compliant.
        /// </summary>
        public bool IsCompliant { get; set; }

        /// <summary>
        /// Gets or sets the verification details.
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Gets or sets the device registration.
        /// </summary>
        public DeviceRegistration? Device { get; set; }

        /// <summary>
        /// Gets or sets the trust score.
        /// </summary>
        public double TrustScore { get; set; }
    }

    #endregion

    #region Policy Models

    /// <summary>
    /// Represents a zero-trust policy.
    /// </summary>
    public sealed class ZeroTrustPolicy
    {
        /// <summary>
        /// Gets or sets the policy ID.
        /// </summary>
        public string PolicyId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the policy name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the policy description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the policy is enabled.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the policy priority (higher = evaluated first).
        /// </summary>
        public int Priority { get; set; }

        /// <summary>
        /// Gets or sets the policy conditions.
        /// </summary>
        public List<PolicyCondition> Conditions { get; set; } = new();

        /// <summary>
        /// Gets or sets the policy effect.
        /// </summary>
        public PolicyEffect Effect { get; set; }

        /// <summary>
        /// Gets or sets required conditions for AllowWithConditions effect.
        /// </summary>
        public List<string> RequiredConditions { get; set; } = new();

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the last update timestamp.
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Policy condition for evaluation.
    /// </summary>
    public sealed class PolicyCondition
    {
        /// <summary>
        /// Gets or sets the attribute to evaluate.
        /// </summary>
        public string Attribute { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the comparison operator.
        /// </summary>
        public ConditionOperator Operator { get; set; }

        /// <summary>
        /// Gets or sets the value to compare against.
        /// </summary>
        public object? Value { get; set; }
    }

    /// <summary>
    /// Condition comparison operators.
    /// </summary>
    public enum ConditionOperator
    {
        /// <summary>Equals comparison.</summary>
        Equals,
        /// <summary>Not equals comparison.</summary>
        NotEquals,
        /// <summary>Contains comparison.</summary>
        Contains,
        /// <summary>Starts with comparison.</summary>
        StartsWith,
        /// <summary>Ends with comparison.</summary>
        EndsWith,
        /// <summary>Greater than comparison.</summary>
        GreaterThan,
        /// <summary>Less than comparison.</summary>
        LessThan,
        /// <summary>In collection comparison.</summary>
        In,
        /// <summary>Regex match comparison.</summary>
        Matches
    }

    /// <summary>
    /// Policy effect enumeration.
    /// </summary>
    public enum PolicyEffect
    {
        /// <summary>Allow access.</summary>
        Allow,
        /// <summary>Deny access.</summary>
        Deny,
        /// <summary>Allow with additional conditions.</summary>
        AllowWithConditions
    }

    /// <summary>
    /// Result of policy evaluation.
    /// </summary>
    public sealed class PolicyEvaluationResult
    {
        /// <summary>
        /// Gets or sets whether access is allowed.
        /// </summary>
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Gets or sets the denial reason.
        /// </summary>
        public string? DenialReason { get; set; }

        /// <summary>
        /// Gets or sets the list of applied policies.
        /// </summary>
        public List<string> AppliedPolicies { get; set; } = new();

        /// <summary>
        /// Gets or sets required conditions.
        /// </summary>
        public List<string> RequiredConditions { get; set; } = new();
    }

    /// <summary>
    /// Detail of a single policy evaluation.
    /// </summary>
    public sealed class PolicyEvaluationDetail
    {
        /// <summary>
        /// Gets or sets the policy ID.
        /// </summary>
        public string PolicyId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the effect.
        /// </summary>
        public PolicyEffect Effect { get; set; }

        /// <summary>
        /// Gets or sets the reason.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// Gets or sets required conditions.
        /// </summary>
        public List<string> RequiredConditions { get; set; } = new();
    }

    #endregion

    #region Data Classification Models

    /// <summary>
    /// Request to classify data.
    /// </summary>
    public sealed class ClassificationRequest
    {
        /// <summary>
        /// Gets or sets the resource ID.
        /// </summary>
        public string ResourceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the content sample for analysis.
        /// </summary>
        public byte[]? ContentSample { get; set; }

        /// <summary>
        /// Gets or sets the content type.
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Gets or sets metadata for rule-based classification.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; set; }

        /// <summary>
        /// Gets or sets a suggested sensitivity level.
        /// </summary>
        public SensitivityLevel? SuggestedLevel { get; set; }

        /// <summary>
        /// Gets or sets who is classifying the data.
        /// </summary>
        public string? ClassifiedBy { get; set; }
    }

    /// <summary>
    /// Represents a data classification.
    /// </summary>
    public sealed class DataClassification
    {
        /// <summary>
        /// Gets or sets the resource ID.
        /// </summary>
        public string ResourceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the sensitivity level.
        /// </summary>
        public SensitivityLevel SensitivityLevel { get; set; }

        /// <summary>
        /// Gets or sets the classification timestamp.
        /// </summary>
        public DateTime ClassifiedAt { get; set; }

        /// <summary>
        /// Gets or sets who classified the data.
        /// </summary>
        public string ClassifiedBy { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets detected sensitive patterns.
        /// </summary>
        public List<DetectedPattern> DetectedPatterns { get; set; } = new();

        /// <summary>
        /// Gets or sets required security controls.
        /// </summary>
        public List<string> RequiredControls { get; set; } = new();

        /// <summary>
        /// Gets or sets allowed access levels.
        /// </summary>
        public List<string> AllowedAccessLevels { get; set; } = new();
    }

    /// <summary>
    /// Data sensitivity levels.
    /// </summary>
    public enum SensitivityLevel
    {
        /// <summary>Public data.</summary>
        Public = 0,
        /// <summary>Internal use only.</summary>
        Internal = 1,
        /// <summary>Confidential data.</summary>
        Confidential = 2,
        /// <summary>Restricted access.</summary>
        Restricted = 3,
        /// <summary>Top secret classification.</summary>
        TopSecret = 4
    }

    /// <summary>
    /// Represents a detected sensitive pattern.
    /// </summary>
    public sealed class DetectedPattern
    {
        /// <summary>
        /// Gets or sets the pattern type.
        /// </summary>
        public PatternType PatternType { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the confidence level.
        /// </summary>
        public double Confidence { get; set; } = 1.0;
    }

    /// <summary>
    /// Types of sensitive patterns.
    /// </summary>
    public enum PatternType
    {
        /// <summary>Personally identifiable information.</summary>
        PII,
        /// <summary>Financial information.</summary>
        Financial,
        /// <summary>Credentials or secrets.</summary>
        Credential,
        /// <summary>Cryptographic keys.</summary>
        CryptoKey,
        /// <summary>Internal reference or identifier.</summary>
        InternalReference,
        /// <summary>Health information.</summary>
        HealthInfo,
        /// <summary>Other sensitive data.</summary>
        Other
    }

    #endregion

    #region Microsegmentation Models

    /// <summary>
    /// Represents a microsegment for isolation.
    /// </summary>
    public sealed class Microsegment
    {
        /// <summary>
        /// Gets or sets the segment ID.
        /// </summary>
        public string SegmentId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the segment name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the segment description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the segment type.
        /// </summary>
        public string SegmentType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets allowed communication rules.
        /// </summary>
        public List<SegmentCommunicationRule>? AllowedCommunications { get; set; }
    }

    /// <summary>
    /// Rule for communication between segments.
    /// </summary>
    public sealed class SegmentCommunicationRule
    {
        /// <summary>
        /// Gets or sets the target segment ID.
        /// </summary>
        public string TargetSegmentId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets allowed protocols.
        /// </summary>
        public string[] AllowedProtocols { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets allowed ports.
        /// </summary>
        public int[]? AllowedPorts { get; set; }

        /// <summary>
        /// Gets or sets whether encryption is required.
        /// </summary>
        public bool RequireEncryption { get; set; } = true;

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Result of segmentation check.
    /// </summary>
    public sealed class SegmentationCheckResult
    {
        /// <summary>
        /// Gets or sets whether communication is allowed.
        /// </summary>
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Gets or sets the check details.
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Gets or sets whether encryption is required.
        /// </summary>
        public bool RequireEncryption { get; set; }
    }

    #endregion

    #region Identity and Behavior Models

    /// <summary>
    /// Identity context information.
    /// </summary>
    public sealed class IdentityContext
    {
        /// <summary>
        /// Gets or sets the identity ID.
        /// </summary>
        public string IdentityId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the first seen timestamp.
        /// </summary>
        public DateTime FirstSeenAt { get; set; }

        /// <summary>
        /// Gets or sets the last seen timestamp.
        /// </summary>
        public DateTime LastSeenAt { get; set; }

        /// <summary>
        /// Gets or sets the access count.
        /// </summary>
        public long AccessCount { get; set; }

        /// <summary>
        /// Gets or sets the failed access count.
        /// </summary>
        public int FailedAccessCount { get; set; }

        /// <summary>
        /// Gets or sets whether the identity is blocked.
        /// </summary>
        public bool IsBlocked { get; set; }

        /// <summary>
        /// Gets or sets the blocked timestamp.
        /// </summary>
        public DateTime? BlockedAt { get; set; }
    }

    /// <summary>
    /// Result of identity verification.
    /// </summary>
    public sealed class IdentityVerificationResult
    {
        /// <summary>
        /// Gets or sets the identity ID.
        /// </summary>
        public string IdentityId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the identity is valid.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Gets or sets the verification details.
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Gets or sets the trust score.
        /// </summary>
        public double TrustScore { get; set; }
    }

    /// <summary>
    /// Behavior profile for an identity.
    /// </summary>
    public sealed class BehaviorProfile
    {
        /// <summary>
        /// Gets or sets the identity ID.
        /// </summary>
        public string IdentityId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the last update timestamp.
        /// </summary>
        public DateTime LastUpdatedAt { get; set; }

        /// <summary>
        /// Gets or sets recent access patterns.
        /// </summary>
        public List<AccessPattern>? AccessPatterns { get; set; }

        /// <summary>
        /// Gets or sets the average trust score.
        /// </summary>
        public double AverageTrustScore { get; set; }

        /// <summary>
        /// Gets or sets the average accesses per hour.
        /// </summary>
        public double AverageAccessesPerHour { get; set; }

        /// <summary>
        /// Gets or sets unique resources accessed count.
        /// </summary>
        public int UniqueResourcesAccessed { get; set; }

        /// <summary>
        /// Gets or sets unique IPs used count.
        /// </summary>
        public int UniqueIpsUsed { get; set; }
    }

    /// <summary>
    /// Represents an access pattern.
    /// </summary>
    public sealed class AccessPattern
    {
        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the source IP.
        /// </summary>
        public string? SourceIp { get; set; }

        /// <summary>
        /// Gets or sets the resource path.
        /// </summary>
        public string? ResourcePath { get; set; }

        /// <summary>
        /// Gets or sets the operation.
        /// </summary>
        public string? Operation { get; set; }

        /// <summary>
        /// Gets or sets the device ID.
        /// </summary>
        public string? DeviceId { get; set; }
    }

    #endregion

    #region Trust Score Models

    /// <summary>
    /// Result of trust score calculation.
    /// </summary>
    public sealed class TrustScoreResult
    {
        /// <summary>
        /// Gets or sets the overall trust score.
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Gets or sets the identity score component.
        /// </summary>
        public double IdentityScore { get; set; }

        /// <summary>
        /// Gets or sets the device score component.
        /// </summary>
        public double DeviceScore { get; set; }

        /// <summary>
        /// Gets or sets the behavior score component.
        /// </summary>
        public double BehaviorScore { get; set; }

        /// <summary>
        /// Gets or sets the anomaly penalty.
        /// </summary>
        public double AnomalyPenalty { get; set; }

        /// <summary>
        /// Gets or sets the contributing factors.
        /// </summary>
        public Dictionary<string, double> Factors { get; set; } = new();
    }

    #endregion

    #region Anomaly Detection Models

    /// <summary>
    /// Result of anomaly analysis.
    /// </summary>
    public sealed class AnomalyAnalysisResult
    {
        /// <summary>
        /// Gets or sets whether an anomaly was detected.
        /// </summary>
        public bool IsAnomalous { get; set; }

        /// <summary>
        /// Gets or sets the anomaly details.
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Gets or sets the severity penalty to apply.
        /// </summary>
        public double SeverityPenalty { get; set; }

        /// <summary>
        /// Gets or sets detected anomaly types.
        /// </summary>
        public List<string> AnomalyTypes { get; set; } = new();
    }

    #endregion

    #region Persistence Models

    /// <summary>
    /// State data for persistence.
    /// </summary>
    internal sealed class ZeroTrustStateData
    {
        /// <summary>
        /// Gets or sets the sessions.
        /// </summary>
        public List<ZeroTrustSession> Sessions { get; set; } = new();

        /// <summary>
        /// Gets or sets the devices.
        /// </summary>
        public List<DeviceRegistration> Devices { get; set; } = new();

        /// <summary>
        /// Gets or sets the policies.
        /// </summary>
        public List<ZeroTrustPolicy> Policies { get; set; } = new();

        /// <summary>
        /// Gets or sets the segments.
        /// </summary>
        public List<Microsegment> Segments { get; set; } = new();

        /// <summary>
        /// Gets or sets the classifications.
        /// </summary>
        public List<DataClassification> Classifications { get; set; } = new();
    }

    #endregion
}
