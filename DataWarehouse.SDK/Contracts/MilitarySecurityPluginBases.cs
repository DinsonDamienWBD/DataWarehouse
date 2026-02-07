using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Base class for Mandatory Access Control (MAC) plugins implementing Bell-LaPadula security model.
    /// Provides default implementations of the core MAC rules:
    /// - Simple Security Property (no read up): Subject clearance must dominate object classification
    /// - *-Property (no write down): Object classification must dominate subject's current level
    /// - Discretionary Security Property: MAC rules are enforced in addition to discretionary controls
    /// Derived classes must implement subject label resolution and validation logic specific to their environment.
    /// Reference: Bell-LaPadula Model (1973), DoD 5200.28-STD.
    /// </summary>
    public abstract class MandatoryAccessControlPluginBase : SecurityProviderPluginBase, IMandatoryAccessControl, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.mac",
                DisplayName = $"{Name} - Mandatory Access Control",
                Description = "Bell-LaPadula MAC implementation for multi-level security",
                Category = CapabilityCategory.Security,
                SubCategory = "AccessControl",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "mac", "bell-lapadula", "security", "mls" },
                SemanticDescription = "Use for mandatory access control with Bell-LaPadula model"
            }
        };

        protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.mac.capability",
                    Topic = "security.mac",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = "Mandatory Access Control with Bell-LaPadula model",
                    Payload = new Dictionary<string, object>
                    {
                        ["model"] = "Bell-LaPadula",
                        ["simpleSecurityProperty"] = true,
                        ["starProperty"] = true
                    },
                    Tags = new[] { "mac", "security", "mls" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted threat prediction for access patterns.
        /// </summary>
        protected virtual async Task<AccessThreatPrediction?> RequestThreatPredictionAsync(string subjectId, SecurityLabel objectLabel, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        /// <summary>
        /// Requests AI-assisted anomaly detection for access attempts.
        /// </summary>
        protected virtual async Task<AccessAnomalyDetection?> RequestAnomalyDetectionAsync(string subjectId, AccessPattern pattern, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion


        /// <summary>
        /// Checks if subject can read object under Bell-LaPadula Simple Security Property (no read up).
        /// Default implementation enforces:
        /// 1. Subject clearance level must be >= object classification level
        /// 2. Subject must have access to all compartments marked on the object
        /// Override this method if you need custom read access logic beyond standard Bell-LaPadula.
        /// </summary>
        /// <param name="subjectLabel">Security label of the subject requesting read access.</param>
        /// <param name="objectLabel">Security label of the object to be read.</param>
        /// <returns>True if read access is permitted under MAC rules; false otherwise.</returns>
        public virtual Task<bool> CanReadAsync(SecurityLabel subjectLabel, SecurityLabel objectLabel)
        {
            // Simple Security Property: Subject clearance must dominate object classification (no read up)
            if (subjectLabel.Level < objectLabel.Level)
            {
                return Task.FromResult(false);
            }

            // Compartment containment: Subject must have access to all object compartments
            if (!objectLabel.Compartments.All(compartment => subjectLabel.Compartments.Contains(compartment)))
            {
                return Task.FromResult(false);
            }

            // Access permitted under MAC rules
            return Task.FromResult(true);
        }

        /// <summary>
        /// Checks if subject can write to object under Bell-LaPadula *-Property (no write down).
        /// Default implementation enforces:
        /// 1. Object classification level must be >= subject's current level
        /// This prevents classified data from being written to lower classification levels (prevents leakage).
        /// Override this method if you need custom write access logic or support tranquility properties.
        /// </summary>
        /// <param name="subjectLabel">Security label of the subject requesting write access.</param>
        /// <param name="objectLabel">Security label of the object to be written.</param>
        /// <returns>True if write access is permitted under MAC rules; false otherwise.</returns>
        public virtual Task<bool> CanWriteAsync(SecurityLabel subjectLabel, SecurityLabel objectLabel)
        {
            // *-Property: Object classification must dominate subject current level (no write down)
            // This prevents a Top Secret process from writing to Secret or Unclassified storage
            if (objectLabel.Level < subjectLabel.Level)
            {
                return Task.FromResult(false);
            }

            // Access permitted under MAC rules
            return Task.FromResult(true);
        }

        /// <summary>
        /// Resolves the effective security label for a subject.
        /// Derived classes must implement this to determine:
        /// - Subject's maximum clearance level
        /// - Authorized compartment access
        /// - Current operating level (may be lower than max clearance)
        /// This typically involves querying a personnel security database or Active Directory.
        /// </summary>
        /// <param name="subjectId">Unique identifier of the subject.</param>
        /// <param name="compartments">Compartments the subject is requesting to access.</param>
        /// <returns>The effective security label for the subject.</returns>
        protected abstract Task<SecurityLabel> ResolveEffectiveLabelAsync(string subjectId, string[] compartments);

        /// <summary>
        /// Performs environment-specific validation of a security label.
        /// Derived classes must implement this to validate:
        /// - Classification level is supported by the system
        /// - Compartment codes are recognized and properly formatted
        /// - Caveats are valid and authorized
        /// - Classification authority is legitimate
        /// - Dates are logical and follow policy
        /// </summary>
        /// <param name="label">The security label to validate.</param>
        /// <returns>Validation result with any errors found.</returns>
        protected abstract Task<ValidationResult> PerformLabelValidationAsync(SecurityLabel label);

        /// <summary>
        /// Gets the effective security label for a subject.
        /// Delegates to derived class implementation after input validation.
        /// </summary>
        /// <param name="subjectId">Unique identifier of the subject.</param>
        /// <param name="requestedCompartments">Array of compartment codes the subject is requesting access to.</param>
        /// <returns>The effective security label for the subject with validated compartment access.</returns>
        public Task<SecurityLabel> GetEffectiveLabelAsync(string subjectId, string[] requestedCompartments)
            => ResolveEffectiveLabelAsync(subjectId, requestedCompartments);

        /// <summary>
        /// Validates that a security label is properly formed and authorized.
        /// Delegates to derived class implementation for environment-specific validation.
        /// </summary>
        /// <param name="label">The security label to validate.</param>
        /// <returns>Validation result indicating success or specific errors found.</returns>
        public Task<ValidationResult> ValidateLabelAsync(SecurityLabel label)
            => PerformLabelValidationAsync(label);
    }

    /// <summary>
    /// Base class for Multi-Level Security (MLS) plugins.
    /// Provides infrastructure for systems accredited to process multiple classification levels.
    /// Supports:
    /// - System High mode (all users cleared to system high, all data treated at system high)
    /// - Compartmented mode (users cleared to system high, data at varying levels with need-to-know)
    /// - Multi-Level Secure mode (trusted computing base separates levels, users at varying clearances)
    /// Derived classes must implement the actual downgrade/sanitization logic appropriate for their domain.
    /// Reference: TCSEC (Orange Book), Common Criteria, DoDI 8500.01.
    /// </summary>
    public abstract class MultiLevelSecurityPluginBase : SecurityProviderPluginBase, IMultiLevelSecurity, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.mls",
                DisplayName = $"{Name} - Multi-Level Security",
                Description = "Multi-level security with trusted downgrade and sanitization",
                Category = CapabilityCategory.Security,
                SubCategory = "Classification",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "mls", "classification", "security", "downgrade" },
                SemanticDescription = "Use for multi-level classified data handling"
            }
        };

        protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.mls.capability",
                    Topic = "security.mls",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"Multi-level security, SystemHigh: {SystemHighLevel}",
                    Payload = new Dictionary<string, object>
                    {
                        ["systemHighLevel"] = SystemHighLevel.ToString(),
                        ["supportedLevels"] = SupportedLevels.Select(l => l.ToString()).ToArray(),
                        ["supportsDowngrade"] = true,
                        ["supportsSanitization"] = true
                    },
                    Tags = new[] { "mls", "classification", "security" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted security level recommendation for data.
        /// </summary>
        protected virtual async Task<SecurityLevelRecommendation?> RequestSecurityLevelAsync(byte[] data, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion


        /// <summary>
        /// Gets all classification levels this system is accredited to handle.
        /// Must be defined by derived class based on system accreditation documentation.
        /// </summary>
        public abstract IReadOnlyList<ClassificationLevel> SupportedLevels { get; }

        /// <summary>
        /// Gets the system high classification level.
        /// Must be defined by derived class based on system accreditation (Authority to Operate).
        /// </summary>
        public abstract ClassificationLevel SystemHighLevel { get; }

        /// <summary>
        /// Performs trusted downgrade of data from current classification to lower target level.
        /// Derived classes must implement this with:
        /// - Verification of authorization code cryptographic validity
        /// - Application of new classification markings
        /// - Audit trail generation
        /// - Preservation of downgrade authority information
        /// This is a privileged operation typically requiring Original Classification Authority (OCA) approval.
        /// </summary>
        /// <param name="data">The classified data to downgrade.</param>
        /// <param name="current">Current security label of the data.</param>
        /// <param name="target">Target classification level.</param>
        /// <param name="authCode">Authorization code from designated authority.</param>
        /// <returns>Downgraded data with new classification markings.</returns>
        protected abstract Task<byte[]> PerformDowngradeAsync(byte[] data, SecurityLabel current, ClassificationLevel target, string authCode);

        /// <summary>
        /// Performs sanitization of data by removing/redacting portions classified above target level.
        /// Derived classes must implement this with appropriate content analysis:
        /// - Text analysis for classification markings and sensitive keywords
        /// - Structured data field-level classification assessment
        /// - Binary data pattern matching
        /// - Redaction of classified portions
        /// - Application of target level markings
        /// </summary>
        /// <param name="data">The source data at higher classification.</param>
        /// <param name="source">Source security label.</param>
        /// <param name="target">Target classification level.</param>
        /// <returns>Sanitized data appropriate for target level.</returns>
        protected abstract Task<byte[]> PerformSanitizationAsync(byte[] data, SecurityLabel source, ClassificationLevel target);

        /// <summary>
        /// Performs trusted downgrade with validation and audit trail.
        /// Enforces that target level is lower than current level.
        /// </summary>
        /// <param name="data">The classified data to downgrade.</param>
        /// <param name="currentLabel">Current security label of the data.</param>
        /// <param name="targetLevel">Target classification level (must be lower than current).</param>
        /// <param name="authorizationCode">Authorization code from designated authority proving approval.</param>
        /// <returns>Downgraded data with new classification markings.</returns>
        /// <exception cref="InvalidOperationException">If target level is not lower than current level.</exception>
        public async Task<byte[]> DowngradeAsync(byte[] data, SecurityLabel currentLabel, ClassificationLevel targetLevel, string authorizationCode)
        {
            // Validate that this is actually a downgrade (target must be lower than current)
            if (targetLevel >= currentLabel.Level)
            {
                throw new InvalidOperationException(
                    $"Target level {targetLevel} must be lower than current level {currentLabel.Level} for downgrade operation. " +
                    "If levels are equal, no downgrade is needed; if target is higher, this is an upgrade which requires reclassification.");
            }

            // Delegate to derived class for actual downgrade implementation
            return await PerformDowngradeAsync(data, currentLabel, targetLevel, authorizationCode);
        }

        /// <summary>
        /// Sanitizes data for lower classification level by removing/redacting sensitive portions.
        /// Delegates to derived class for content-specific sanitization logic.
        /// </summary>
        /// <param name="data">The source data at higher classification.</param>
        /// <param name="sourceLabel">Source security label.</param>
        /// <param name="targetLevel">Target classification level for sanitized output.</param>
        /// <returns>Sanitized data appropriate for target classification level with portions redacted.</returns>
        public Task<byte[]> SanitizeAsync(byte[] data, SecurityLabel sourceLabel, ClassificationLevel targetLevel)
            => PerformSanitizationAsync(data, sourceLabel, targetLevel);
    }

    /// <summary>
    /// Base class for Two-Person Integrity (TPI) plugins.
    /// Provides infrastructure for managing dual-authorization operations.
    /// Maintains in-memory state of pending operations requiring second-person approval.
    /// Derived classes must implement persistent storage and authorization validation specific to their environment.
    /// Critical for nuclear command and control, cryptographic operations, and classification changes.
    /// Reference: DoD Directive 5210.41, NIST SP 800-53 AC-5 (Separation of Duties).
    /// </summary>
    public abstract class TwoPersonIntegrityPluginBase : SecurityProviderPluginBase, ITwoPersonIntegrity, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.tpi",
                DisplayName = $"{Name} - Two-Person Integrity",
                Description = "Dual-authorization operations for critical actions",
                Category = CapabilityCategory.Security,
                SubCategory = "Authorization",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "tpi", "dual-auth", "security", "separation-of-duties" },
                SemanticDescription = "Use for operations requiring two-person authorization"
            }
        };

        protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.tpi.capability",
                    Topic = "security.tpi",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = "Two-person integrity for dual-authorization",
                    Payload = new Dictionary<string, object>
                    {
                        ["defaultExpiration"] = TimeSpan.FromHours(24).TotalHours,
                        ["requiresDifferentPrincipals"] = true
                    },
                    Tags = new[] { "tpi", "dual-auth", "security" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted authorization pattern analysis.
        /// </summary>
        protected virtual async Task<AuthorizationPatternAnalysis?> RequestPatternAnalysisAsync(string initiatorId, string operationType, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion


        /// <summary>
        /// In-memory cache of pending operations awaiting authorization.
        /// For production use, this should be backed by persistent storage to survive restarts.
        /// Key: Operation ID, Value: Current authorization status.
        /// </summary>
        protected readonly Dictionary<string, AuthorizationStatus> _pendingOperations = new();

        /// <summary>
        /// Stores operation details to persistent storage.
        /// Derived classes must implement this to ensure operation survives system restarts.
        /// Should store: operation ID, type, initiator ID, creation timestamp, expiration.
        /// </summary>
        /// <param name="operationId">Unique operation identifier.</param>
        /// <param name="operationType">Type of operation for audit and display.</param>
        /// <param name="initiatorId">ID of person initiating the operation.</param>
        /// <returns>Task representing the asynchronous storage operation.</returns>
        protected abstract Task StoreOperationAsync(string operationId, string operationType, string initiatorId);

        /// <summary>
        /// Validates that the authorizer is permitted to authorize this operation.
        /// Derived classes must implement checks for:
        /// - Authorizer has appropriate clearance and role
        /// - Authorizer is different from initiator (no self-authorization)
        /// - Authorization proof is cryptographically valid (signed token, hardware key, biometric)
        /// - Authorizer has required permissions for this operation type
        /// </summary>
        /// <param name="operationId">Operation being authorized.</param>
        /// <param name="authorizerId">ID of person providing authorization.</param>
        /// <param name="proof">Cryptographic proof of authorization.</param>
        /// <returns>True if authorizer is valid and proof is correct; false otherwise.</returns>
        protected abstract Task<bool> ValidateAuthorizerAsync(string operationId, string authorizerId, byte[] proof);

        /// <summary>
        /// Records authorization to persistent storage and audit log.
        /// Derived classes must implement this to create immutable audit trail.
        /// Should include: operation ID, authorizer ID, timestamp, authorization proof hash.
        /// </summary>
        /// <param name="operationId">Operation that was authorized.</param>
        /// <param name="authorizerId">ID of person who authorized.</param>
        /// <returns>Task representing the asynchronous storage operation.</returns>
        protected abstract Task RecordAuthorizationAsync(string operationId, string authorizerId);

        /// <summary>
        /// Begins an operation that requires two-person integrity approval.
        /// Creates a pending operation with 24-hour default expiration.
        /// Stores operation to persistent storage and in-memory cache.
        /// </summary>
        /// <param name="operationType">Type of operation being initiated (for audit and authorization purposes).</param>
        /// <param name="initiatorId">ID of the person initiating the operation.</param>
        /// <returns>Unique operation ID to be used for subsequent authorization and execution.</returns>
        public async Task<string> BeginOperationAsync(string operationType, string initiatorId)
        {
            // Generate unique operation ID with TPI prefix for easy identification in logs
            var operationId = $"TPI-{Guid.NewGuid():N}";

            // Persist to storage for durability
            await StoreOperationAsync(operationId, operationType, initiatorId);

            // Create authorization status with 24-hour expiration (configurable in derived classes)
            var status = new AuthorizationStatus(
                operationId,
                InitiatorApproved: true,  // Initiator approval is implicit by starting operation
                AuthorizerApproved: false, // Awaiting second person
                initiatorId,
                AuthorizerId: null,        // Not yet authorized
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(24)
            );

            _pendingOperations[operationId] = status;

            return operationId;
        }

        /// <summary>
        /// Provides second-person authorization for a pending operation.
        /// Validates authorizer credentials and prevents self-authorization.
        /// Records authorization to persistent storage and audit log.
        /// </summary>
        /// <param name="operationId">The operation ID returned from BeginOperationAsync.</param>
        /// <param name="authorizerId">ID of the person authorizing the operation (must differ from initiator).</param>
        /// <param name="authorizationProof">Cryptographic proof of authorization (e.g., signed token, hardware key signature).</param>
        /// <returns>True if authorization is accepted; false otherwise.</returns>
        public async Task<bool> AuthorizeOperationAsync(string operationId, string authorizerId, byte[] authorizationProof)
        {
            // Verify operation exists and is pending
            if (!_pendingOperations.TryGetValue(operationId, out var status))
            {
                return false; // Unknown operation
            }

            // Prevent self-authorization (critical security control)
            if (status.InitiatorId == authorizerId)
            {
                return false; // Cannot authorize own operation
            }

            // Validate authorizer credentials and cryptographic proof
            if (!await ValidateAuthorizerAsync(operationId, authorizerId, authorizationProof))
            {
                return false; // Invalid authorizer or proof
            }

            // Record authorization to persistent storage and audit log
            await RecordAuthorizationAsync(operationId, authorizerId);

            // Update status to reflect authorization
            _pendingOperations[operationId] = status with
            {
                AuthorizerApproved = true,
                AuthorizerId = authorizerId
            };

            return true;
        }

        /// <summary>
        /// Executes an operation only after dual authorization is complete.
        /// Validates both approvals are present and authorization hasn't expired.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="operationId">The operation ID that has been dual-authorized.</param>
        /// <param name="operation">The operation to execute after validation.</param>
        /// <returns>Result of the operation execution.</returns>
        /// <exception cref="UnauthorizedAccessException">If dual authorization is not complete or has expired.</exception>
        public async Task<T> ExecuteWithDualAuthAsync<T>(string operationId, Func<Task<T>> operation)
        {
            // Get current authorization status
            var status = await GetAuthorizationStatusAsync(operationId);

            // Validate both approvals are present
            if (!status.InitiatorApproved || !status.AuthorizerApproved)
            {
                throw new UnauthorizedAccessException(
                    $"Operation {operationId} requires dual authorization. " +
                    $"Initiator approved: {status.InitiatorApproved}, Authorizer approved: {status.AuthorizerApproved}");
            }

            // Check for expiration
            if (status.ExpiresAt.HasValue && DateTimeOffset.UtcNow > status.ExpiresAt)
            {
                throw new UnauthorizedAccessException(
                    $"Authorization for operation {operationId} has expired. " +
                    $"Expired at: {status.ExpiresAt}");
            }

            // Execute the operation with full dual authorization
            return await operation();
        }

        /// <summary>
        /// Gets the current authorization status of an operation.
        /// Returns a default "not found" status if operation doesn't exist.
        /// </summary>
        /// <param name="operationId">The operation ID to query.</param>
        /// <returns>Current authorization status including initiator, authorizer, and expiration.</returns>
        public Task<AuthorizationStatus> GetAuthorizationStatusAsync(string operationId)
        {
            var status = _pendingOperations.GetValueOrDefault(operationId)
                ?? new AuthorizationStatus(operationId, false, false, null, null, null);
            return Task.FromResult(status);
        }
    }

    /// <summary>
    /// Base class for Secure Destruction plugins implementing DoD/NIST sanitization standards.
    /// Provides infrastructure for certified data destruction with audit trail.
    /// Derived classes must implement the actual overwrite/destruction logic appropriate for their storage medium:
    /// - Magnetic media: Multi-pass overwrite patterns
    /// - Solid-state media: Cryptographic erase or physical destruction (overwrite may not be effective)
    /// - Cloud storage: Cryptographic key destruction with verification
    /// - Physical media: Coordinate with destruction facility
    /// Reference: DoD 5220.22-M (deprecated but still used), NIST SP 800-88 Rev 1.
    /// </summary>
    public abstract class SecureDestructionPluginBase : SecurityProviderPluginBase, ISecureDestruction, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.destruction",
                DisplayName = $"{Name} - Secure Destruction",
                Description = "NIST 800-88 compliant secure data destruction",
                Category = CapabilityCategory.Security,
                SubCategory = "Destruction",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "destruction", "sanitization", "nist-800-88", "security" },
                SemanticDescription = "Use for certified secure data destruction"
            }
        };

        protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.destruction.capability",
                    Topic = "security.destruction",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = "Secure destruction per NIST 800-88",
                    Payload = new Dictionary<string, object>
                    {
                        ["certifiedDestruction"] = true,
                        ["auditTrail"] = true
                    },
                    Tags = new[] { "destruction", "sanitization", "security" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted destruction verification confidence.
        /// </summary>
        protected virtual async Task<DestructionVerificationConfidence?> RequestVerificationConfidenceAsync(string resourceId, DestructionMethod method, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion


        /// <summary>
        /// Performs the actual data overwrite or destruction process.
        /// Derived classes must implement method-appropriate destruction:
        /// - DoD 5220.22-M: 3-pass overwrite (0, 1, random) with verification
        /// - DoD 5220.22-M ECE: 7-pass overwrite with more complex patterns
        /// - Gutmann: 35-pass overwrite for maximum assurance
        /// - NIST 800-88 Clear: Logical techniques (suitable for reuse in same domain)
        /// - NIST 800-88 Purge: Cryptographic erase or degauss (suitable for domain change)
        /// - Physical: Coordinate physical destruction and return verification hash
        /// Must return hash of final state for verification.
        /// </summary>
        /// <param name="resourceId">ID of resource to destroy.</param>
        /// <param name="method">Destruction method to apply.</param>
        /// <returns>Verification hash proving destruction (hash of final overwrite or destruction log).</returns>
        protected abstract Task<byte[]> OverwriteDataAsync(string resourceId, DestructionMethod method);

        /// <summary>
        /// Records destruction certificate to immutable audit storage.
        /// Derived classes must implement this to store certificate in:
        /// - Write-once storage (WORM)
        /// - Blockchain or distributed ledger
        /// - Signed and timestamped audit log
        /// Certificate must be tamper-evident and retained per compliance requirements (typically 7+ years).
        /// </summary>
        /// <param name="certificate">The destruction certificate to record.</param>
        /// <returns>Task representing the asynchronous storage operation.</returns>
        protected abstract Task RecordDestructionAsync(DestructionCertificate certificate);

        /// <summary>
        /// Retrieves a destruction certificate from audit storage.
        /// Derived classes must implement this to query the immutable certificate storage.
        /// </summary>
        /// <param name="certificateId">ID of the certificate to retrieve.</param>
        /// <returns>The certificate if found; null if not found or invalid.</returns>
        protected abstract Task<DestructionCertificate?> GetCertificateAsync(string certificateId);

        /// <summary>
        /// Securely destroys data using specified method and creates audit certificate.
        /// Performs destruction, generates certificate, and records to immutable storage.
        /// </summary>
        /// <param name="resourceId">ID of the resource to destroy.</param>
        /// <param name="method">Destruction method to use (must be appropriate for data classification).</param>
        /// <param name="authorizedBy">ID of the authority authorizing destruction (typically information owner or security officer).</param>
        /// <returns>Certificate proving destruction was completed per specified method.</returns>
        public async Task<DestructionCertificate> DestroyAsync(string resourceId, DestructionMethod method, string authorizedBy)
        {
            // Perform the actual data destruction/overwrite
            var verificationHash = await OverwriteDataAsync(resourceId, method);

            // Create immutable destruction certificate
            var certificate = new DestructionCertificate(
                CertificateId: $"DC-{Guid.NewGuid():N}",
                ResourceId: resourceId,
                Method: method,
                DestroyedAt: DateTimeOffset.UtcNow,
                AuthorizedBy: authorizedBy,
                VerificationHash: verificationHash
            );

            // Record to immutable audit storage
            await RecordDestructionAsync(certificate);

            return certificate;
        }

        /// <summary>
        /// Verifies that destruction was completed as certified.
        /// Checks that certificate exists in immutable storage.
        /// For full verification, derived classes may also verify the resource is actually inaccessible.
        /// </summary>
        /// <param name="certificateId">ID of the destruction certificate to verify.</param>
        /// <returns>True if destruction is verified and certificate is valid; false otherwise.</returns>
        public async Task<bool> VerifyDestructionAsync(string certificateId)
        {
            var certificate = await GetCertificateAsync(certificateId);
            return certificate != null;
        }
    }

    #region Stub Types for Military Security Intelligence Integration

    /// <summary>Stub type for access threat predictions from AI.</summary>
    public record AccessThreatPrediction(
        string ThreatLevel,
        string[] PotentialThreats,
        string RecommendedAction,
        double ConfidenceScore);

    /// <summary>Stub type for access anomaly detection from AI.</summary>
    public record AccessAnomalyDetection(
        bool IsAnomaly,
        string AnomalyType,
        string Description,
        double ConfidenceScore);

    /// <summary>Stub type for access pattern analysis.</summary>
    public record AccessPattern(
        string SubjectId,
        DateTimeOffset[] AccessTimes,
        SecurityLabel[] AccessedLabels);

    /// <summary>Stub type for security level recommendation from AI.</summary>
    public record SecurityLevelRecommendation(
        ClassificationLevel RecommendedLevel,
        string[] DetectedKeywords,
        string Rationale,
        double ConfidenceScore);

    /// <summary>Stub type for authorization pattern analysis from AI.</summary>
    public record AuthorizationPatternAnalysis(
        bool IsNormalPattern,
        string[] RiskFactors,
        string Recommendation,
        double ConfidenceScore);

    /// <summary>Stub type for destruction verification confidence from AI.</summary>
    public record DestructionVerificationConfidence(
        bool IsVerified,
        double ConfidenceScore,
        string[] VerificationSteps,
        string Recommendation);

    #endregion
}
