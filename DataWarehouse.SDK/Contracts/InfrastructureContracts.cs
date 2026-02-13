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
        RAID_0,     // Striping (performance)
        RAID_1,     // Mirroring (redundancy)
        RAID_2,     // Bit-level striping with Hamming code
        RAID_3,     // Byte-level striping with dedicated parity
        RAID_4,     // Block-level striping with dedicated parity
        RAID_5,     // Block-level striping with distributed parity
        RAID_6,     // Block-level striping with dual distributed parity

        // Nested RAID Levels
        RAID_10,    // RAID 1+0 (mirrored stripes)
        RAID_01,    // RAID 0+1 (striped mirrors)
        RAID_03,    // RAID 0+3 (striped dedicated parity)
        RAID_50,    // RAID 5+0 (striped RAID 5 sets)
        RAID_60,    // RAID 6+0 (striped RAID 6 sets)
        RAID_100,   // RAID 10+0 (striped mirrors of mirrors)

        // Enhanced RAID Levels
        RAID_1E,    // RAID 1 Enhanced (mirrored striping)
        RAID_5E,    // RAID 5 with hot spare
        RAID_5EE,   // RAID 5 Enhanced with distributed spare
        RAID_6E,    // RAID 6 Enhanced with extra parity

        // Vendor-Specific RAID
        RAID_DP,    // NetApp Double Parity (RAID 6 variant)
        RAID_S,     // Dell/EMC Parity RAID (RAID 5 variant)
        RAID_7,     // Cached striping with parity
        RAID_FR,    // Fast Rebuild (optimized RAID 6)

        // ZFS RAID Levels
        RAID_Z1,    // ZFS single parity (RAID 5 equivalent)
        RAID_Z2,    // ZFS double parity (RAID 6 equivalent)
        RAID_Z3,    // ZFS triple parity

        // Advanced/Proprietary RAID
        RAID_MD10,      // Linux MD RAID 10 (near/far/offset layouts)
        RAID_Adaptive,  // IBM Adaptive RAID (auto-tuning)
        RAID_Beyond,    // Drobo BeyondRAID (single/dual parity)
        RAID_Unraid,    // Unraid parity system (1-2 parity disks)
        RAID_Declustered, // Declustered/Distributed RAID

        // Extended RAID Levels
        RAID_71,        // RAID 7.1 - Enhanced RAID 7 with read cache
        RAID_72,        // RAID 7.2 - Enhanced RAID 7 with write-back cache
        RAID_NM,        // RAID N+M - Flexible N data + M parity
        RAID_Matrix,    // Intel Matrix RAID - Multiple RAID types on same disks
        RAID_JBOD,      // Just a Bunch of Disks - Simple concatenation
        RAID_Crypto,    // Crypto SoftRAID - Encrypted software RAID
        RAID_DUP,       // Btrfs DUP Profile - Duplicate on same device
        RAID_DDP,       // NetApp Dynamic Disk Pool
        RAID_SPAN,      // Simple disk spanning/concatenation
        RAID_BIG,       // Concatenated volumes (Linux md BIG)
        RAID_MAID,      // Massive Array of Idle Disks - Power managed RAID
        RAID_Linear     // Linear mode - Sequential concatenation
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
        public object? ViolationSeverity { get; init; }
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
    public class AuthenticationRequest
    {
        public string Method { get; init; } = "password";
        public string? Username { get; init; }
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
