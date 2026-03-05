using System.Security.Claims;

namespace DataWarehouse.SDK.Contracts
{
    #region Circuit Breaker

    /// <summary>
    /// Exception thrown when a circuit breaker is open.
    /// </summary>
    public class CircuitBreakerOpenException : Exception
    {
        public string PolicyId { get; }
        public TimeSpan RetryAfter { get; }

        public CircuitBreakerOpenException(string policyId, TimeSpan retryAfter)
            : base($"Circuit breaker '{policyId}' is open. Retry after {retryAfter.TotalSeconds:F1}s")
        {
            PolicyId = policyId;
            RetryAfter = retryAfter;
        }
    }

    #endregion

    #region RAID Types

    /// <summary>
    /// Supported RAID levels.
    /// Comprehensive enum supporting standard, nested, enhanced, vendor-specific, and ZFS RAID levels.
    /// </summary>
    public enum RaidLevel
    {
        // Standard RAID Levels
        Raid0,     // Striping (performance)
        Raid1,     // Mirroring (redundancy)
        Raid2,     // Bit-level striping with Hamming code
        Raid3,     // Byte-level striping with dedicated parity
        Raid4,     // Block-level striping with dedicated parity
        Raid5,     // Block-level striping with distributed parity
        Raid6,     // Block-level striping with dual distributed parity

        // Nested RAID Levels
        Raid10,    // RAID 1+0 (mirrored stripes)
        Raid01,    // RAID 0+1 (striped mirrors)
        Raid03,    // RAID 0+3 (striped dedicated parity)
        Raid50,    // RAID 5+0 (striped RAID 5 sets)
        Raid60,    // RAID 6+0 (striped RAID 6 sets)
        Raid100,   // RAID 10+0 (striped mirrors of mirrors)

        // Enhanced RAID Levels
        Raid1E,    // RAID 1 Enhanced (mirrored striping)
        Raid5E,    // RAID 5 with hot spare
        Raid5Ee,   // RAID 5 Enhanced with distributed spare
        Raid6E,    // RAID 6 Enhanced with extra parity

        // Vendor-Specific RAID
        RaidDp,    // NetApp Double Parity (RAID 6 variant)
        RaidS,     // Dell/EMC Parity RAID (RAID 5 variant)
        Raid7,     // Cached striping with parity
        RaidFr,    // Fast Rebuild (optimized RAID 6)

        // ZFS RAID Levels
        RaidZ1,    // ZFS single parity (RAID 5 equivalent)
        RaidZ2,    // ZFS double parity (RAID 6 equivalent)
        RaidZ3,    // ZFS triple parity

        // Advanced/Proprietary RAID
        RaidMd10,      // Linux MD RAID 10 (near/far/offset layouts)
        RaidAdaptive,  // IBM Adaptive RAID (auto-tuning)
        RaidBeyond,    // Drobo BeyondRAID (single/dual parity)
        RaidUnraid,    // Unraid parity system (1-2 parity disks)
        RaidDeclustered, // Declustered/Distributed RAID

        // Extended RAID Levels
        Raid71,        // RAID 7.1 - Enhanced RAID 7 with read cache
        Raid72,        // RAID 7.2 - Enhanced RAID 7 with write-back cache
        RaidNm,        // RAID N+M - Flexible N data + M parity
        RaidMatrix,    // Intel Matrix RAID - Multiple RAID types on same disks
        RaidJbod,      // Just a Bunch of Disks - Simple concatenation
        RaidCrypto,    // Crypto SoftRAID - Encrypted software RAID
        RaidDup,       // Btrfs DUP Profile - Duplicate on same device
        RaidDdp,       // NetApp Dynamic Disk Pool
        RaidSpan,      // Simple disk spanning/concatenation
        RaidBig,       // Concatenated volumes (Linux md BIG)
        RaidMaid,      // Massive Array of Idle Disks - Power managed RAID
        RaidLinear     // Linear mode - Sequential concatenation
    }

    /// <summary>
    /// Status of the RAID array.
    /// </summary>
    public enum RaidArrayStatus
    {
        Healthy,
        Degraded,
        Rebuilding,
        Failed
    }

    /// <summary>
    /// Result of a rebuild operation.
    /// </summary>
    public class RebuildResult
    {
        public bool Success { get; init; }
        public int ProviderIndex { get; init; }
        public TimeSpan Duration { get; init; }
        public long BytesRebuilt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Result of a scrub operation.
    /// </summary>
    public class ScrubResult
    {
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public long BytesScanned { get; init; }
        public int ErrorsFound { get; init; }
        public int ErrorsCorrected { get; init; }
        public List<string> UncorrectableErrors { get; init; } = new();
    }

    #endregion

    #region Compliance Types

    /// <summary>
    /// Severity level for compliance violations.
    /// </summary>
    public enum ViolationSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// A compliance violation.
    /// </summary>
    public class ComplianceViolation
    {
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Severity { get; init; } = "High";
        public string? Remediation { get; init; }
        public string? RemediationAdvice { get; init; }
        public string? Regulation { get; init; }
        public ViolationSeverity? ViolationSeverity { get; init; }
    }

    /// <summary>
    /// A compliance warning.
    /// </summary>
    public class ComplianceWarning
    {
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? Recommendation { get; init; }
    }

    /// <summary>
    /// Current compliance status.
    /// </summary>
    public class ComplianceStatus
    {
        public string Framework { get; init; } = string.Empty;
        public bool IsCompliant { get; init; }
        public double ComplianceScore { get; init; }
        public int TotalControls { get; init; }
        public int PassingControls { get; init; }
        public int FailingControls { get; init; }
        public DateTime LastAssessment { get; init; }
        public DateTime LastChecked { get; init; }
        public DateTime? NextAssessmentDue { get; init; }
    }

    /// <summary>
    /// Compliance report.
    /// </summary>
    public class ComplianceReport
    {
        public string Framework { get; init; } = string.Empty;
        public DateTime GeneratedAt { get; init; }
        public DateTime ReportingPeriodStart { get; init; }
        public DateTime ReportingPeriodEnd { get; init; }
        public ComplianceStatus Status { get; init; } = new();
        public List<ControlAssessment> ControlAssessments { get; init; } = new();
        public byte[]? ReportDocument { get; init; }
        public string? ReportFormat { get; init; }
    }

    /// <summary>
    /// Assessment of a single compliance control.
    /// </summary>
    public class ControlAssessment
    {
        public string ControlId { get; init; } = string.Empty;
        public string ControlName { get; init; } = string.Empty;
        public bool IsPassing { get; init; }
        public string? Evidence { get; init; }
        public string? Notes { get; init; }
    }

    /// <summary>
    /// Context for compliance checks.
    /// </summary>
    public class ComplianceContext
    {
        public string? DataSubjectId { get; init; }
        public string? ProcessingPurpose { get; init; }
        public object? Data { get; init; }
        public bool PiiProtectionEnabled { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();

        // GDPR-specific properties
        public DateTime? DataCreatedAt { get; init; }
        public string? DestinationCountry { get; init; }
        public string? DataControllerId { get; init; }
        public string? LawfulBasis { get; init; }
        public string? DataCategory { get; init; }

        // HIPAA-specific properties
        public bool EncryptionEnabled { get; init; }
        public bool AccessControlEnabled { get; init; }
        public bool AuditLoggingEnabled { get; init; }
        public string? ThirdPartyId { get; init; }
        public List<string>? RequestedFields { get; init; }

        // PCI DSS-specific properties
        public string? EncryptionKeyId { get; init; }
        public DateTime? KeyCreatedAt { get; init; }
        public string? UserId { get; init; }
    }

    /// <summary>
    /// Result of compliance check.
    /// </summary>
    public class ComplianceCheckResult
    {
        public bool IsCompliant { get; init; }
        public List<ComplianceViolation> Violations { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public DateTime CheckedAt { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
        public string? Regulation { get; init; }
        public Dictionary<string, object>? Context { get; init; }
    }

    /// <summary>
    /// Data subject request (GDPR/CCPA).
    /// </summary>
    public class DataSubjectRequest
    {
        public string SubjectId { get; init; } = string.Empty;
        public string RequestType { get; init; } = string.Empty; // Access, Deletion, Portability, Rectification
        public string? Email { get; init; }
        public Dictionary<string, object> Details { get; init; } = new();
    }

    /// <summary>
    /// Data retention policy.
    /// </summary>
    public class RetentionPolicy
    {
        public string DataType { get; init; } = string.Empty;
        public TimeSpan RetentionPeriod { get; init; }
        public string? LegalBasis { get; init; }
        public bool RequiresSecureDeletion { get; init; }
        public List<string> ApplicableFrameworks { get; init; } = new();
    }

    #endregion

    #region Authentication and Authorization Types

    /// <summary>
    /// Authentication request.
    /// </summary>
    /// <remarks>
    /// <b>Security note:</b> <see cref="Password"/> is stored as a plain <c>string</c>
    /// and cannot be zeroed from memory. Prefer token-based authentication where possible,
    /// or clear the reference promptly after use to allow GC collection.
    /// </remarks>
    public class AuthenticationRequest
    {
        public string Method { get; init; } = "password";
        public string? Username { get; init; }
        /// <summary>
        /// Password for credential-based auth. Prefer token-based auth where possible;
        /// plain strings cannot be securely zeroed. Null out the reference promptly after use.
        /// </summary>
        public string? Password { get; init; }
        public string? Token { get; init; }
        public string? Provider { get; init; }
        public Dictionary<string, object> Claims { get; init; } = new();
    }

    /// <summary>
    /// Authentication result.
    /// </summary>
    public class AuthenticationResult
    {
        public bool Success { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? PrincipalId { get; init; }
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Token validation result.
    /// </summary>
    public class TokenValidationResult
    {
        public bool IsValid { get; init; }
        public ClaimsPrincipal? Principal { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Authorization result.
    /// </summary>
    public class AuthorizationResult
    {
        public bool IsAuthorized { get; init; }
        public string Resource { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string? DenialReason { get; init; }
        public IReadOnlyList<string> MatchedPolicies { get; init; } = Array.Empty<string>();
    }

    #endregion
}
