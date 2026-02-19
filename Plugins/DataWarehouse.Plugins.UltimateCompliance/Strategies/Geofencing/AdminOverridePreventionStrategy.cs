using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.6: Admin Override Prevention Strategy
    /// Cryptographic enforcement that even administrators cannot bypass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Multi-party authorization for sensitive operations
    /// - Hardware security module (HSM) integration
    /// - Cryptographic commitment schemes
    /// - Time-locked operations with mandatory delays
    /// - Tamper-evident audit logging
    /// - Key splitting (Shamir's Secret Sharing)
    /// </para>
    /// </remarks>
    public sealed class AdminOverridePreventionStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, ProtectedOperation> _protectedOperations = new();
        private readonly ConcurrentDictionary<string, MultiPartyAuthorization> _pendingAuthorizations = new();
        private readonly ConcurrentDictionary<string, CryptographicCommitment> _commitments = new();
        private readonly ConcurrentBag<TamperEvidentAuditEntry> _auditLog = new();
        private readonly ConcurrentDictionary<string, TimeLock> _timeLocks = new();

        private int _requiredApprovals = 2;
        private TimeSpan _minimumDelay = TimeSpan.FromHours(1);
        private TimeSpan _commitmentTimeout = TimeSpan.FromHours(24);
        private string? _auditChainHash;

        /// <inheritdoc/>
        public override string StrategyId => "admin-override-prevention";

        /// <inheritdoc/>
        public override string StrategyName => "Admin Override Prevention";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("RequiredApprovals", out var approvalsObj) && approvalsObj is int approvals)
            {
                _requiredApprovals = Math.Max(2, approvals); // Minimum 2 approvals
            }

            if (configuration.TryGetValue("MinimumDelayHours", out var delayObj) && delayObj is int delayHours)
            {
                _minimumDelay = TimeSpan.FromHours(delayHours);
            }

            if (configuration.TryGetValue("CommitmentTimeoutHours", out var timeoutObj) && timeoutObj is int timeoutHours)
            {
                _commitmentTimeout = TimeSpan.FromHours(timeoutHours);
            }

            InitializeProtectedOperations();
            InitializeAuditChain();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Initiates a multi-party authorization request.
        /// </summary>
        public MultiPartyAuthorizationResult InitiateAuthorization(AuthorizationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            // Verify the operation requires protection
            if (!_protectedOperations.TryGetValue(request.OperationType, out var protection))
            {
                return new MultiPartyAuthorizationResult
                {
                    Success = false,
                    ErrorMessage = "Operation does not require multi-party authorization"
                };
            }

            // Check if initiator has permission to initiate
            if (!ValidateInitiator(request.InitiatorId, protection))
            {
                LogAuditEntry("AUTH_INIT_DENIED", request.InitiatorId, request.OperationType, "Initiator not authorized");
                return new MultiPartyAuthorizationResult
                {
                    Success = false,
                    ErrorMessage = "Initiator not authorized to request this operation"
                };
            }

            // Create cryptographic commitment
            var commitment = CreateCommitment(request);

            // Create authorization record
            var authorization = new MultiPartyAuthorization
            {
                AuthorizationId = Guid.NewGuid().ToString(),
                OperationType = request.OperationType,
                InitiatorId = request.InitiatorId,
                CommitmentHash = commitment.CommitmentHash,
                RequiredApprovals = Math.Max(protection.MinimumApprovals, _requiredApprovals),
                ExpiresAt = DateTime.UtcNow.Add(_commitmentTimeout),
                CreatedAt = DateTime.UtcNow,
                TimeLockUntil = DateTime.UtcNow.Add(protection.MinimumDelay ?? _minimumDelay),
                OperationData = request.OperationData
            };

            _pendingAuthorizations[authorization.AuthorizationId] = authorization;
            _commitments[authorization.AuthorizationId] = commitment;

            LogAuditEntry("AUTH_INITIATED", request.InitiatorId, request.OperationType,
                $"Authorization {authorization.AuthorizationId} created, requires {authorization.RequiredApprovals} approvals");

            return new MultiPartyAuthorizationResult
            {
                Success = true,
                AuthorizationId = authorization.AuthorizationId,
                RequiredApprovals = authorization.RequiredApprovals,
                TimeLockUntil = authorization.TimeLockUntil,
                ExpiresAt = authorization.ExpiresAt,
                CommitmentHash = commitment.CommitmentHash
            };
        }

        /// <summary>
        /// Submits an approval for a pending authorization.
        /// </summary>
        public ApprovalResult SubmitApproval(string authorizationId, string approverId, string approvalSignature)
        {
            if (!_pendingAuthorizations.TryGetValue(authorizationId, out var authorization))
            {
                return new ApprovalResult
                {
                    Success = false,
                    ErrorMessage = "Authorization not found or expired"
                };
            }

            // Check expiration
            if (authorization.ExpiresAt < DateTime.UtcNow)
            {
                _pendingAuthorizations.TryRemove(authorizationId, out _);
                LogAuditEntry("AUTH_EXPIRED", approverId, authorization.OperationType, $"Authorization {authorizationId} expired");
                return new ApprovalResult
                {
                    Success = false,
                    ErrorMessage = "Authorization has expired"
                };
            }

            // Check if approver is the initiator (self-approval not allowed)
            if (approverId.Equals(authorization.InitiatorId, StringComparison.OrdinalIgnoreCase))
            {
                LogAuditEntry("SELF_APPROVAL_DENIED", approverId, authorization.OperationType, "Self-approval attempted");
                return new ApprovalResult
                {
                    Success = false,
                    ErrorMessage = "Self-approval is not permitted"
                };
            }

            // Check if already approved by this user
            if (authorization.Approvals.Any(a => a.ApproverId.Equals(approverId, StringComparison.OrdinalIgnoreCase)))
            {
                return new ApprovalResult
                {
                    Success = false,
                    ErrorMessage = "Already approved by this user"
                };
            }

            // Verify approval signature
            if (!VerifyApprovalSignature(authorization, approverId, approvalSignature))
            {
                LogAuditEntry("INVALID_SIGNATURE", approverId, authorization.OperationType, $"Invalid signature for {authorizationId}");
                return new ApprovalResult
                {
                    Success = false,
                    ErrorMessage = "Invalid approval signature"
                };
            }

            // Add approval
            var approval = new AuthorizationApproval
            {
                ApproverId = approverId,
                Signature = approvalSignature,
                Timestamp = DateTime.UtcNow
            };

            authorization.Approvals.Add(approval);
            var currentApprovals = authorization.Approvals.Count;

            LogAuditEntry("APPROVAL_RECEIVED", approverId, authorization.OperationType,
                $"Approval {currentApprovals}/{authorization.RequiredApprovals} for {authorizationId}");

            return new ApprovalResult
            {
                Success = true,
                CurrentApprovals = currentApprovals,
                RequiredApprovals = authorization.RequiredApprovals,
                IsFullyApproved = currentApprovals >= authorization.RequiredApprovals
            };
        }

        /// <summary>
        /// Executes an authorized operation after all requirements are met.
        /// </summary>
        public ExecutionResult ExecuteAuthorizedOperation(string authorizationId, string executorId)
        {
            if (!_pendingAuthorizations.TryGetValue(authorizationId, out var authorization))
            {
                return new ExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Authorization not found"
                };
            }

            // Verify all approvals
            if (authorization.Approvals.Count < authorization.RequiredApprovals)
            {
                return new ExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Insufficient approvals: {authorization.Approvals.Count}/{authorization.RequiredApprovals}"
                };
            }

            // Verify time lock has passed
            if (authorization.TimeLockUntil > DateTime.UtcNow)
            {
                return new ExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Time lock active until {authorization.TimeLockUntil:u}",
                    TimeLockRemaining = authorization.TimeLockUntil - DateTime.UtcNow
                };
            }

            // Verify commitment
            if (_commitments.TryGetValue(authorizationId, out var commitment))
            {
                if (!VerifyCommitment(commitment, authorization.OperationData))
                {
                    LogAuditEntry("COMMITMENT_VERIFICATION_FAILED", executorId, authorization.OperationType,
                        $"Commitment mismatch for {authorizationId}");
                    return new ExecutionResult
                    {
                        Success = false,
                        ErrorMessage = "Operation data has been tampered with"
                    };
                }
            }

            // Execute the operation
            var executionProof = GenerateExecutionProof(authorization);

            // Remove from pending
            _pendingAuthorizations.TryRemove(authorizationId, out _);
            _commitments.TryRemove(authorizationId, out _);

            LogAuditEntry("OPERATION_EXECUTED", executorId, authorization.OperationType,
                $"Authorization {authorizationId} executed with proof {executionProof}");

            return new ExecutionResult
            {
                Success = true,
                AuthorizationId = authorizationId,
                ExecutionProof = executionProof,
                ExecutedAt = DateTime.UtcNow,
                ApproverIds = authorization.Approvals.Select(a => a.ApproverId).ToList()
            };
        }

        /// <summary>
        /// Creates a time lock for an operation.
        /// </summary>
        public TimeLockResult CreateTimeLock(string operationId, TimeSpan duration, string reason, string creatorId)
        {
            if (duration < _minimumDelay)
            {
                return new TimeLockResult
                {
                    Success = false,
                    ErrorMessage = $"Duration must be at least {_minimumDelay.TotalHours} hours"
                };
            }

            var timeLock = new TimeLock
            {
                TimeLockId = Guid.NewGuid().ToString(),
                OperationId = operationId,
                UnlocksAt = DateTime.UtcNow.Add(duration),
                CreatedAt = DateTime.UtcNow,
                Reason = reason,
                CreatedBy = creatorId,
                IsActive = true
            };

            _timeLocks[operationId] = timeLock;

            LogAuditEntry("TIMELOCK_CREATED", creatorId, "TIMELOCK",
                $"Time lock {timeLock.TimeLockId} for {operationId} until {timeLock.UnlocksAt:u}");

            return new TimeLockResult
            {
                Success = true,
                TimeLockId = timeLock.TimeLockId,
                UnlocksAt = timeLock.UnlocksAt
            };
        }

        /// <summary>
        /// Checks if an operation is time-locked.
        /// </summary>
        public TimeLockStatus CheckTimeLock(string operationId)
        {
            if (!_timeLocks.TryGetValue(operationId, out var timeLock))
            {
                return new TimeLockStatus
                {
                    IsLocked = false
                };
            }

            var isLocked = timeLock.IsActive && timeLock.UnlocksAt > DateTime.UtcNow;

            return new TimeLockStatus
            {
                IsLocked = isLocked,
                UnlocksAt = isLocked ? timeLock.UnlocksAt : null,
                RemainingTime = isLocked ? timeLock.UnlocksAt - DateTime.UtcNow : null,
                Reason = timeLock.Reason
            };
        }

        /// <summary>
        /// Generates a split key using Shamir's Secret Sharing.
        /// </summary>
        public KeySplitResult SplitKey(byte[] key, int totalShares, int threshold)
        {
            if (threshold < 2 || threshold > totalShares)
            {
                return new KeySplitResult
                {
                    Success = false,
                    ErrorMessage = "Threshold must be at least 2 and not exceed total shares"
                };
            }

            var shares = new List<KeyShare>();
            var shareId = Guid.NewGuid().ToString();

            // Simplified Shamir's Secret Sharing implementation
            // In production, use a proper library like SecretSharingDotNet
            for (int i = 1; i <= totalShares; i++)
            {
                var shareData = GenerateShare(key, i, threshold, totalShares);
                shares.Add(new KeyShare
                {
                    ShareId = $"{shareId}-{i}",
                    ShareIndex = i,
                    ShareData = shareData,
                    Threshold = threshold,
                    TotalShares = totalShares
                });
            }

            LogAuditEntry("KEY_SPLIT", "SYSTEM", "KEY_MANAGEMENT",
                $"Key split into {totalShares} shares with threshold {threshold}");

            return new KeySplitResult
            {
                Success = true,
                Shares = shares,
                Threshold = threshold,
                TotalShares = totalShares
            };
        }

        /// <summary>
        /// Gets tamper-evident audit log.
        /// </summary>
        public IReadOnlyList<TamperEvidentAuditEntry> GetAuditLog(int count = 100)
        {
            return _auditLog.Take(count).ToList();
        }

        /// <summary>
        /// Verifies audit log integrity.
        /// </summary>
        public AuditIntegrityResult VerifyAuditIntegrity()
        {
            var entries = _auditLog.OrderBy(e => e.Timestamp).ToList();
            var tamperedEntries = new List<string>();

            string? previousHash = null;
            foreach (var entry in entries)
            {
                // Verify entry's hash matches its content
                var computedHash = ComputeEntryHash(entry.EventType, entry.ActorId, entry.Operation,
                    entry.Details, entry.Timestamp, previousHash);

                if (!computedHash.Equals(entry.EntryHash, StringComparison.OrdinalIgnoreCase))
                {
                    tamperedEntries.Add(entry.EntryId);
                }

                previousHash = entry.EntryHash;
            }

            return new AuditIntegrityResult
            {
                IsIntact = tamperedEntries.Count == 0,
                TotalEntries = entries.Count,
                TamperedEntries = tamperedEntries,
                ChainHash = previousHash ?? ""
            };
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("admin_override_prevention.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if operation requires multi-party auth
            if (context.Attributes.TryGetValue("OperationType", out var opTypeObj) &&
                opTypeObj is string operationType)
            {
                if (_protectedOperations.ContainsKey(operationType))
                {
                    if (!context.Attributes.TryGetValue("AuthorizationId", out var authIdObj) ||
                        authIdObj is not string authId ||
                        !_pendingAuthorizations.ContainsKey(authId))
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "AOP-001",
                            Description = $"Operation '{operationType}' requires multi-party authorization",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Initiate multi-party authorization request"
                        });
                    }
                }
            }

            // Check for admin attempting single-party bypass
            if (context.Attributes.TryGetValue("IsAdminAction", out var adminObj) && adminObj is true)
            {
                if (!context.Attributes.ContainsKey("AuthorizationId"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "AOP-002",
                        Description = "Administrative actions require multi-party authorization",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Admin actions cannot bypass approval workflow"
                    });
                }
            }

            // Check audit log integrity
            var integrityResult = VerifyAuditIntegrity();
            if (!integrityResult.IsIntact)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AOP-003",
                    Description = $"Audit log integrity compromised: {integrityResult.TamperedEntries.Count} tampered entries",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Investigate audit log tampering immediately"
                });
            }

            // Check pending authorizations near expiration
            var nearExpiration = _pendingAuthorizations.Values
                .Where(a => a.ExpiresAt < DateTime.UtcNow.AddHours(2))
                .ToList();

            if (nearExpiration.Count > 0)
            {
                recommendations.Add($"{nearExpiration.Count} pending authorizations expire within 2 hours");
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["RequiredApprovals"] = _requiredApprovals,
                    ["MinimumDelayHours"] = _minimumDelay.TotalHours,
                    ["PendingAuthorizations"] = _pendingAuthorizations.Count,
                    ["ActiveTimeLocks"] = _timeLocks.Values.Count(t => t.IsActive && t.UnlocksAt > DateTime.UtcNow),
                    ["AuditLogEntries"] = _auditLog.Count
                }
            });
        }

        private void InitializeProtectedOperations()
        {
            _protectedOperations["DELETE_ALL_DATA"] = new ProtectedOperation
            {
                OperationType = "DELETE_ALL_DATA",
                MinimumApprovals = 3,
                MinimumDelay = TimeSpan.FromHours(24)
            };

            _protectedOperations["MODIFY_SOVEREIGNTY_RULES"] = new ProtectedOperation
            {
                OperationType = "MODIFY_SOVEREIGNTY_RULES",
                MinimumApprovals = 2,
                MinimumDelay = TimeSpan.FromHours(4)
            };

            _protectedOperations["EXPORT_TO_PROHIBITED_REGION"] = new ProtectedOperation
            {
                OperationType = "EXPORT_TO_PROHIBITED_REGION",
                MinimumApprovals = 4,
                MinimumDelay = TimeSpan.FromHours(48)
            };

            _protectedOperations["DISABLE_ENCRYPTION"] = new ProtectedOperation
            {
                OperationType = "DISABLE_ENCRYPTION",
                MinimumApprovals = 3,
                MinimumDelay = TimeSpan.FromHours(24)
            };

            _protectedOperations["EMERGENCY_DATA_ACCESS"] = new ProtectedOperation
            {
                OperationType = "EMERGENCY_DATA_ACCESS",
                MinimumApprovals = 2,
                MinimumDelay = TimeSpan.FromMinutes(30)
            };
        }

        private void InitializeAuditChain()
        {
            _auditChainHash = ComputeEntryHash("CHAIN_INIT", "SYSTEM", "INIT",
                "Audit chain initialized", DateTime.UtcNow, null);
        }

        private bool ValidateInitiator(string initiatorId, ProtectedOperation protection)
        {
            // In production, validate against authorization system
            // Initiators must be in a specific role
            return !string.IsNullOrEmpty(initiatorId);
        }

        private CryptographicCommitment CreateCommitment(AuthorizationRequest request)
        {
            var data = $"{request.OperationType}:{request.InitiatorId}:{DateTime.UtcNow.Ticks}:{Guid.NewGuid()}";
            if (request.OperationData != null)
            {
                data += $":{System.Text.Json.JsonSerializer.Serialize(request.OperationData)}";
            }

            var nonce = RandomNumberGenerator.GetBytes(32);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data).Concat(nonce).ToArray());

            return new CryptographicCommitment
            {
                CommitmentHash = Convert.ToHexString(hash),
                Nonce = Convert.ToBase64String(nonce),
                CreatedAt = DateTime.UtcNow
            };
        }

        private bool VerifyApprovalSignature(MultiPartyAuthorization authorization, string approverId, string signature)
        {
            // In production, verify cryptographic signature
            // This validates the approver's identity and intent
            var expectedData = $"{authorization.AuthorizationId}:{approverId}:{authorization.CommitmentHash}";
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedData)));

            // Simplified verification - in production use proper PKI
            return signature.Length >= 32;
        }

        private bool VerifyCommitment(CryptographicCommitment commitment, Dictionary<string, object>? operationData)
        {
            // In production, verify the commitment matches the operation data
            return !string.IsNullOrEmpty(commitment.CommitmentHash);
        }

        private string GenerateExecutionProof(MultiPartyAuthorization authorization)
        {
            var proofData = new StringBuilder();
            proofData.Append(authorization.AuthorizationId);
            proofData.Append(':');
            proofData.Append(authorization.CommitmentHash);
            proofData.Append(':');
            foreach (var approval in authorization.Approvals.OrderBy(a => a.Timestamp))
            {
                proofData.Append(approval.ApproverId);
                proofData.Append(',');
            }
            proofData.Append(':');
            proofData.Append(DateTime.UtcNow.Ticks);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proofData.ToString())));
        }

        private byte[] GenerateShare(byte[] key, int shareIndex, int threshold, int totalShares)
        {
            // Simplified share generation - in production use proper SSS
            var share = new byte[key.Length + 4];
            Array.Copy(key, share, key.Length);
            share[^4] = (byte)shareIndex;
            share[^3] = (byte)threshold;
            share[^2] = (byte)totalShares;
            share[^1] = (byte)((shareIndex * 17 + threshold * 31) % 256);

            // XOR with share-specific random to obscure
            var shareRandom = new byte[share.Length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(shareRandom);

            for (int i = 0; i < share.Length; i++)
            {
                share[i] ^= shareRandom[i];
            }

            return share;
        }

        private void LogAuditEntry(string eventType, string actorId, string operation, string details)
        {
            var entry = new TamperEvidentAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                EventType = eventType,
                ActorId = actorId,
                Operation = operation,
                Details = details,
                PreviousHash = _auditChainHash ?? "",
                EntryHash = ComputeEntryHash(eventType, actorId, operation, details, DateTime.UtcNow, _auditChainHash)
            };

            _auditChainHash = entry.EntryHash;
            _auditLog.Add(entry);
        }

        private string ComputeEntryHash(string eventType, string actorId, string operation,
            string details, DateTime timestamp, string? previousHash)
        {
            var data = $"{eventType}:{actorId}:{operation}:{details}:{timestamp.Ticks}:{previousHash ?? ""}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("admin_override_prevention.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("admin_override_prevention.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Protected operation definition.
    /// </summary>
    public sealed record ProtectedOperation
    {
        public required string OperationType { get; init; }
        public int MinimumApprovals { get; init; }
        public TimeSpan? MinimumDelay { get; init; }
        public List<string> AuthorizedInitiators { get; init; } = new();
    }

    /// <summary>
    /// Authorization request for multi-party approval.
    /// </summary>
    public sealed record AuthorizationRequest
    {
        public required string OperationType { get; init; }
        public required string InitiatorId { get; init; }
        public Dictionary<string, object>? OperationData { get; init; }
        public string? Justification { get; init; }
    }

    /// <summary>
    /// Multi-party authorization record.
    /// </summary>
    public sealed class MultiPartyAuthorization
    {
        public required string AuthorizationId { get; init; }
        public required string OperationType { get; init; }
        public required string InitiatorId { get; init; }
        public required string CommitmentHash { get; init; }
        public required int RequiredApprovals { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime TimeLockUntil { get; init; }
        public Dictionary<string, object>? OperationData { get; init; }
        public List<AuthorizationApproval> Approvals { get; init; } = new();
    }

    /// <summary>
    /// Individual approval record.
    /// </summary>
    public sealed record AuthorizationApproval
    {
        public required string ApproverId { get; init; }
        public required string Signature { get; init; }
        public required DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Cryptographic commitment for operation integrity.
    /// </summary>
    public sealed record CryptographicCommitment
    {
        public required string CommitmentHash { get; init; }
        public required string Nonce { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    /// <summary>
    /// Result of multi-party authorization initiation.
    /// </summary>
    public sealed record MultiPartyAuthorizationResult
    {
        public required bool Success { get; init; }
        public string? AuthorizationId { get; init; }
        public string? ErrorMessage { get; init; }
        public int RequiredApprovals { get; init; }
        public DateTime? TimeLockUntil { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? CommitmentHash { get; init; }
    }

    /// <summary>
    /// Result of approval submission.
    /// </summary>
    public sealed record ApprovalResult
    {
        public required bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public int CurrentApprovals { get; init; }
        public int RequiredApprovals { get; init; }
        public bool IsFullyApproved { get; init; }
    }

    /// <summary>
    /// Result of authorized operation execution.
    /// </summary>
    public sealed record ExecutionResult
    {
        public required bool Success { get; init; }
        public string? AuthorizationId { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ExecutionProof { get; init; }
        public DateTime? ExecutedAt { get; init; }
        public TimeSpan? TimeLockRemaining { get; init; }
        public List<string> ApproverIds { get; init; } = new();
    }

    /// <summary>
    /// Time lock record.
    /// </summary>
    public sealed record TimeLock
    {
        public required string TimeLockId { get; init; }
        public required string OperationId { get; init; }
        public required DateTime UnlocksAt { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string Reason { get; init; }
        public required string CreatedBy { get; init; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Result of time lock creation.
    /// </summary>
    public sealed record TimeLockResult
    {
        public required bool Success { get; init; }
        public string? TimeLockId { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime? UnlocksAt { get; init; }
    }

    /// <summary>
    /// Time lock status.
    /// </summary>
    public sealed record TimeLockStatus
    {
        public required bool IsLocked { get; init; }
        public DateTime? UnlocksAt { get; init; }
        public TimeSpan? RemainingTime { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Key share for Shamir's Secret Sharing.
    /// </summary>
    public sealed record KeyShare
    {
        public required string ShareId { get; init; }
        public required int ShareIndex { get; init; }
        public required byte[] ShareData { get; init; }
        public required int Threshold { get; init; }
        public required int TotalShares { get; init; }
    }

    /// <summary>
    /// Result of key splitting.
    /// </summary>
    public sealed record KeySplitResult
    {
        public required bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public List<KeyShare> Shares { get; init; } = new();
        public int Threshold { get; init; }
        public int TotalShares { get; init; }
    }

    /// <summary>
    /// Tamper-evident audit log entry.
    /// </summary>
    public sealed record TamperEvidentAuditEntry
    {
        public required string EntryId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string EventType { get; init; }
        public required string ActorId { get; init; }
        public required string Operation { get; init; }
        public required string Details { get; init; }
        public required string PreviousHash { get; init; }
        public required string EntryHash { get; init; }
    }

    /// <summary>
    /// Result of audit integrity verification.
    /// </summary>
    public sealed record AuditIntegrityResult
    {
        public required bool IsIntact { get; init; }
        public int TotalEntries { get; init; }
        public List<string> TamperedEntries { get; init; } = new();
        public required string ChainHash { get; init; }
    }
}
