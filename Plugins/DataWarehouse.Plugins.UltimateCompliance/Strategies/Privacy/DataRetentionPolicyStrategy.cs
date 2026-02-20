using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Privacy
{
    /// <summary>
    /// T124.6: Data Retention Policy Strategy
    /// Enforces data retention and deletion policies across the organization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retention Policy Features:
    /// - Policy definition with flexible retention periods
    /// - Legal hold override mechanism
    /// - Automatic expiration scheduling
    /// - Retention period extension/reduction
    /// - Multi-level retention hierarchies
    /// - Policy inheritance and override
    /// </para>
    /// <para>
    /// Regulatory Compliance:
    /// - GDPR: Storage limitation principle (Article 5(1)(e))
    /// - HIPAA: 6-year retention for PHI
    /// - SOX: 7-year retention for financial records
    /// - PCI-DSS: 1-year for cardholder data logs
    /// </para>
    /// </remarks>
    public sealed class DataRetentionPolicyStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, RetentionPolicy> _policies = new BoundedDictionary<string, RetentionPolicy>(1000);
        private readonly BoundedDictionary<string, DataRetentionRecord> _records = new BoundedDictionary<string, DataRetentionRecord>(1000);
        private readonly BoundedDictionary<string, RetentionHold> _holds = new BoundedDictionary<string, RetentionHold>(1000);
        private readonly BoundedDictionary<string, RetentionSchedule> _schedules = new BoundedDictionary<string, RetentionSchedule>(1000);
        private readonly ConcurrentBag<RetentionAuditEntry> _auditLog = new();

        private TimeSpan _defaultRetention = TimeSpan.FromDays(365 * 3); // 3 years default
        private bool _autoScheduleDeletion = true;
        private int _warningDaysBeforeExpiry = 30;

        /// <inheritdoc/>
        public override string StrategyId => "data-retention-policy";

        /// <inheritdoc/>
        public override string StrategyName => "Data Retention Policy";

        /// <inheritdoc/>
        public override string Framework => "DataPrivacy";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("DefaultRetentionDays", out var retObj) && retObj is int days)
                _defaultRetention = TimeSpan.FromDays(days);

            if (configuration.TryGetValue("AutoScheduleDeletion", out var autoObj) && autoObj is bool auto)
                _autoScheduleDeletion = auto;

            if (configuration.TryGetValue("WarningDaysBeforeExpiry", out var warnObj) && warnObj is int warn)
                _warningDaysBeforeExpiry = warn;

            InitializeDefaultPolicies();
            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a retention policy.
        /// </summary>
        public void RegisterPolicy(RetentionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            _policies[policy.PolicyId] = policy;
        }

        /// <summary>
        /// Applies a retention policy to data.
        /// </summary>
        public ApplyPolicyResult ApplyPolicy(string dataId, string policyId, ApplyPolicyOptions? options = null)
        {
            if (!_policies.TryGetValue(policyId, out var policy))
            {
                return new ApplyPolicyResult
                {
                    Success = false,
                    ErrorMessage = $"Policy not found: {policyId}"
                };
            }

            options ??= new ApplyPolicyOptions();

            // Check for existing record
            if (_records.TryGetValue(dataId, out var existing))
            {
                // Handle policy conflict
                if (!options.OverrideExisting)
                {
                    return new ApplyPolicyResult
                    {
                        Success = false,
                        ErrorMessage = "Data already has retention policy applied. Use OverrideExisting to replace.",
                        ExistingPolicyId = existing.PolicyId
                    };
                }

                // Keep stricter of two policies unless explicitly overriding
                if (!options.ForceOverride && existing.RetentionEndsAt > DateTime.UtcNow.Add(policy.RetentionPeriod))
                {
                    return new ApplyPolicyResult
                    {
                        Success = false,
                        ErrorMessage = "Existing policy has longer retention. Use ForceOverride to shorten.",
                        ExistingPolicyId = existing.PolicyId
                    };
                }
            }

            var retentionEndDate = options.CustomRetentionEnd ?? DateTime.UtcNow.Add(policy.RetentionPeriod);

            var record = new DataRetentionRecord
            {
                RecordId = Guid.NewGuid().ToString(),
                DataId = dataId,
                PolicyId = policyId,
                PolicyName = policy.Name,
                DataCategory = options.DataCategory ?? policy.DefaultCategory,
                RetentionStartedAt = DateTime.UtcNow,
                RetentionEndsAt = retentionEndDate,
                CreatedBy = options.CreatedBy ?? "system",
                IsOnHold = false
            };

            _records[dataId] = record;

            // Schedule deletion if enabled
            if (_autoScheduleDeletion && policy.AutoDelete)
            {
                ScheduleDeletion(dataId, retentionEndDate);
            }

            _auditLog.Add(new RetentionAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                DataId = dataId,
                PolicyId = policyId,
                Action = RetentionAction.PolicyApplied,
                Timestamp = DateTime.UtcNow,
                Details = $"Retention until {retentionEndDate:d}"
            });

            return new ApplyPolicyResult
            {
                Success = true,
                RecordId = record.RecordId,
                DataId = dataId,
                PolicyId = policyId,
                RetentionEndsAt = retentionEndDate
            };
        }

        /// <summary>
        /// Places a legal hold on data, preventing deletion.
        /// </summary>
        public HoldResult PlaceHold(PlaceHoldRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var holdId = Guid.NewGuid().ToString();
            var hold = new RetentionHold
            {
                HoldId = holdId,
                Name = request.Name,
                Description = request.Description,
                HoldType = request.HoldType,
                CreatedBy = request.CreatedBy,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = request.ExpiresAt,
                AffectedDataIds = new HashSet<string>(request.DataIds ?? Array.Empty<string>()),
                Scope = request.Scope,
                IsActive = true
            };

            _holds[holdId] = hold;

            // Mark affected records as on hold
            foreach (var dataId in hold.AffectedDataIds)
            {
                if (_records.TryGetValue(dataId, out var record))
                {
                    _records[dataId] = record with { IsOnHold = true, HoldId = holdId };
                }

                // Remove from deletion schedule
                if (_schedules.TryGetValue(dataId, out var schedule))
                {
                    _schedules[dataId] = schedule with { IsSuspended = true, SuspendedBy = holdId };
                }
            }

            _auditLog.Add(new RetentionAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                Action = RetentionAction.HoldPlaced,
                HoldId = holdId,
                Timestamp = DateTime.UtcNow,
                Details = $"Hold '{request.Name}' placed on {hold.AffectedDataIds.Count} items"
            });

            return new HoldResult
            {
                Success = true,
                HoldId = holdId,
                AffectedCount = hold.AffectedDataIds.Count
            };
        }

        /// <summary>
        /// Releases a legal hold.
        /// </summary>
        public HoldResult ReleaseHold(string holdId, string releasedBy, string? reason = null)
        {
            if (!_holds.TryGetValue(holdId, out var hold))
            {
                return new HoldResult
                {
                    Success = false,
                    ErrorMessage = "Hold not found"
                };
            }

            // Deactivate hold
            _holds[holdId] = hold with
            {
                IsActive = false,
                ReleasedAt = DateTime.UtcNow,
                ReleasedBy = releasedBy,
                ReleaseReason = reason
            };

            // Update affected records
            foreach (var dataId in hold.AffectedDataIds)
            {
                if (_records.TryGetValue(dataId, out var record) && record.HoldId == holdId)
                {
                    // Check if any other holds apply
                    var otherHold = _holds.Values
                        .FirstOrDefault(h => h.IsActive && h.HoldId != holdId && h.AffectedDataIds.Contains(dataId));

                    _records[dataId] = record with
                    {
                        IsOnHold = otherHold != null,
                        HoldId = otherHold?.HoldId
                    };
                }

                // Resume deletion schedule if no other holds
                if (_schedules.TryGetValue(dataId, out var schedule) && schedule.SuspendedBy == holdId)
                {
                    var hasOtherHold = _holds.Values.Any(h => h.IsActive && h.HoldId != holdId && h.AffectedDataIds.Contains(dataId));
                    if (!hasOtherHold)
                    {
                        _schedules[dataId] = schedule with { IsSuspended = false, SuspendedBy = null };
                    }
                }
            }

            _auditLog.Add(new RetentionAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                Action = RetentionAction.HoldReleased,
                HoldId = holdId,
                Timestamp = DateTime.UtcNow,
                Details = $"Released by {releasedBy}: {reason}"
            });

            return new HoldResult
            {
                Success = true,
                HoldId = holdId,
                AffectedCount = hold.AffectedDataIds.Count
            };
        }

        /// <summary>
        /// Extends retention for data.
        /// </summary>
        public ExtendRetentionResult ExtendRetention(string dataId, TimeSpan extension, string reason, string extendedBy)
        {
            if (!_records.TryGetValue(dataId, out var record))
            {
                return new ExtendRetentionResult
                {
                    Success = false,
                    ErrorMessage = "Retention record not found"
                };
            }

            var newEndDate = record.RetentionEndsAt.Add(extension);

            _records[dataId] = record with
            {
                RetentionEndsAt = newEndDate,
                ExtendedAt = DateTime.UtcNow,
                ExtendedBy = extendedBy,
                ExtensionReason = reason
            };

            // Update deletion schedule
            if (_schedules.TryGetValue(dataId, out var schedule))
            {
                _schedules[dataId] = schedule with { ScheduledDeletionAt = newEndDate };
            }

            _auditLog.Add(new RetentionAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                DataId = dataId,
                PolicyId = record.PolicyId,
                Action = RetentionAction.RetentionExtended,
                Timestamp = DateTime.UtcNow,
                Details = $"Extended by {extension.TotalDays} days to {newEndDate:d}: {reason}"
            });

            return new ExtendRetentionResult
            {
                Success = true,
                DataId = dataId,
                NewRetentionEndDate = newEndDate,
                ExtendedBy = extension
            };
        }

        /// <summary>
        /// Gets data approaching retention expiry.
        /// </summary>
        public IReadOnlyList<DataRetentionRecord> GetExpiringData(int daysAhead)
        {
            var threshold = DateTime.UtcNow.AddDays(daysAhead);
            return _records.Values
                .Where(r => !r.IsOnHold && r.RetentionEndsAt < threshold && r.RetentionEndsAt > DateTime.UtcNow)
                .OrderBy(r => r.RetentionEndsAt)
                .ToList();
        }

        /// <summary>
        /// Gets data past retention period ready for deletion.
        /// </summary>
        public IReadOnlyList<DataRetentionRecord> GetExpiredData()
        {
            return _records.Values
                .Where(r => !r.IsOnHold && r.RetentionEndsAt < DateTime.UtcNow)
                .OrderBy(r => r.RetentionEndsAt)
                .ToList();
        }

        /// <summary>
        /// Processes scheduled deletions.
        /// </summary>
        public async Task<ProcessDeletionsResult> ProcessScheduledDeletionsAsync(CancellationToken ct = default)
        {
            var dueDeletions = _schedules.Values
                .Where(s => !s.IsSuspended &&
                           !s.IsCompleted &&
                           s.ScheduledDeletionAt <= DateTime.UtcNow)
                .ToList();

            var results = new List<DeletionResult>();

            foreach (var schedule in dueDeletions)
            {
                ct.ThrowIfCancellationRequested();

                // Verify still eligible for deletion
                if (_records.TryGetValue(schedule.DataId, out var record) && record.IsOnHold)
                {
                    results.Add(new DeletionResult
                    {
                        DataId = schedule.DataId,
                        Success = false,
                        Reason = "On legal hold"
                    });
                    continue;
                }

                // Perform deletion (simulated - would call actual deletion API)
                var deleted = await DeleteDataAsync(schedule.DataId, ct);

                if (deleted)
                {
                    _schedules[schedule.DataId] = schedule with
                    {
                        IsCompleted = true,
                        CompletedAt = DateTime.UtcNow
                    };

                    _records.TryRemove(schedule.DataId, out _);

                    results.Add(new DeletionResult
                    {
                        DataId = schedule.DataId,
                        Success = true,
                        DeletedAt = DateTime.UtcNow
                    });

                    _auditLog.Add(new RetentionAuditEntry
                    {
                        EntryId = Guid.NewGuid().ToString(),
                        DataId = schedule.DataId,
                        PolicyId = record?.PolicyId,
                        Action = RetentionAction.DataDeleted,
                        Timestamp = DateTime.UtcNow,
                        Details = "Scheduled deletion completed"
                    });
                }
                else
                {
                    results.Add(new DeletionResult
                    {
                        DataId = schedule.DataId,
                        Success = false,
                        Reason = "Deletion failed"
                    });
                }
            }

            return new ProcessDeletionsResult
            {
                TotalProcessed = results.Count,
                SuccessfulDeletions = results.Count(r => r.Success),
                FailedDeletions = results.Count(r => !r.Success),
                Results = results
            };
        }

        /// <summary>
        /// Gets all registered policies.
        /// </summary>
        public IReadOnlyList<RetentionPolicy> GetPolicies()
        {
            return _policies.Values.ToList();
        }

        /// <summary>
        /// Gets all active holds.
        /// </summary>
        public IReadOnlyList<RetentionHold> GetActiveHolds()
        {
            return _holds.Values.Where(h => h.IsActive).ToList();
        }

        /// <summary>
        /// Gets retention statistics.
        /// </summary>
        public new RetentionStatistics GetStatistics()
        {
            var records = _records.Values.ToList();
            var now = DateTime.UtcNow;

            return new RetentionStatistics
            {
                TotalRecords = records.Count,
                RecordsOnHold = records.Count(r => r.IsOnHold),
                RecordsExpired = records.Count(r => r.RetentionEndsAt < now),
                RecordsExpiringWithin30Days = records.Count(r => !r.IsOnHold && r.RetentionEndsAt > now && r.RetentionEndsAt < now.AddDays(30)),
                ActiveHolds = _holds.Values.Count(h => h.IsActive),
                ScheduledDeletions = _schedules.Values.Count(s => !s.IsCompleted && !s.IsSuspended),
                PolicyDistribution = records.GroupBy(r => r.PolicyId).ToDictionary(g => g.Key, g => g.Count())
            };
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<RetentionAuditEntry> GetAuditLog(string? dataId = null, int count = 100)
        {
            var query = _auditLog.AsEnumerable();
            if (!string.IsNullOrEmpty(dataId))
                query = query.Where(e => e.DataId == dataId);

            return query.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("data_retention_policy.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check for data without retention policy
            if (context.Attributes.TryGetValue("DataId", out var dataIdObj) && dataIdObj is string dataId)
            {
                if (!_records.ContainsKey(dataId))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "RET-001",
                        Description = "Data has no retention policy applied",
                        Severity = ViolationSeverity.High,
                        AffectedResource = dataId,
                        Remediation = "Apply appropriate retention policy",
                        RegulatoryReference = "GDPR Article 5(1)(e)"
                    });
                }
            }

            // Check for expired data not deleted
            var expiredCount = GetExpiredData().Count;
            if (expiredCount > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "RET-002",
                    Description = $"{expiredCount} data item(s) past retention period not deleted",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Process scheduled deletions or apply legal holds if retention is required"
                });
            }

            // Check for expiring data
            var expiringCount = GetExpiringData(_warningDaysBeforeExpiry).Count;
            if (expiringCount > 0)
            {
                recommendations.Add($"{expiringCount} data item(s) will expire within {_warningDaysBeforeExpiry} days");
            }

            // Check for expired holds
            var expiredHolds = _holds.Values.Where(h => h.IsActive && h.ExpiresAt.HasValue && h.ExpiresAt.Value < DateTime.UtcNow).ToList();
            if (expiredHolds.Count > 0)
            {
                recommendations.Add($"{expiredHolds.Count} legal hold(s) have expired. Review and release if appropriate.");
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
                    ["TotalRecords"] = _records.Count,
                    ["ActiveHolds"] = _holds.Values.Count(h => h.IsActive),
                    ["PoliciesRegistered"] = _policies.Count,
                    ["ExpiredData"] = expiredCount
                }
            });
        }

        private void ScheduleDeletion(string dataId, DateTime deletionDate)
        {
            _schedules[dataId] = new RetentionSchedule
            {
                ScheduleId = Guid.NewGuid().ToString(),
                DataId = dataId,
                ScheduledDeletionAt = deletionDate,
                CreatedAt = DateTime.UtcNow
            };
        }

        private Task<bool> DeleteDataAsync(string dataId, CancellationToken ct)
        {
            // In production, would call actual data deletion API
            return Task.FromResult(true);
        }

        private void InitializeDefaultPolicies()
        {
            RegisterPolicy(new RetentionPolicy
            {
                PolicyId = "gdpr-personal-data",
                Name = "GDPR Personal Data",
                Description = "Standard retention for personal data under GDPR",
                RetentionPeriod = TimeSpan.FromDays(365 * 3),
                DefaultCategory = "personal",
                LegalBasis = "GDPR Article 5(1)(e)",
                AutoDelete = true
            });

            RegisterPolicy(new RetentionPolicy
            {
                PolicyId = "hipaa-phi",
                Name = "HIPAA PHI Retention",
                Description = "Protected Health Information retention under HIPAA",
                RetentionPeriod = TimeSpan.FromDays(365 * 6),
                DefaultCategory = "phi",
                LegalBasis = "HIPAA 45 CFR 164.530(j)",
                AutoDelete = false // Requires manual review
            });

            RegisterPolicy(new RetentionPolicy
            {
                PolicyId = "sox-financial",
                Name = "SOX Financial Records",
                Description = "Financial records retention under Sarbanes-Oxley",
                RetentionPeriod = TimeSpan.FromDays(365 * 7),
                DefaultCategory = "financial",
                LegalBasis = "SOX Section 802",
                AutoDelete = false
            });

            RegisterPolicy(new RetentionPolicy
            {
                PolicyId = "pci-cardholder",
                Name = "PCI-DSS Cardholder Data",
                Description = "Cardholder data and audit logs retention",
                RetentionPeriod = TimeSpan.FromDays(365),
                DefaultCategory = "pci",
                LegalBasis = "PCI-DSS Requirement 10.7",
                AutoDelete = true
            });

            RegisterPolicy(new RetentionPolicy
            {
                PolicyId = "transient",
                Name = "Transient Data",
                Description = "Short-term retention for temporary data",
                RetentionPeriod = TimeSpan.FromDays(30),
                DefaultCategory = "temp",
                AutoDelete = true
            });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("data_retention_policy.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("data_retention_policy.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    #region Types

    /// <summary>
    /// Retention action types for audit.
    /// </summary>
    public enum RetentionAction
    {
        PolicyApplied,
        PolicyUpdated,
        HoldPlaced,
        HoldReleased,
        RetentionExtended,
        DataDeleted,
        DeletionScheduled,
        DeletionSuspended
    }

    /// <summary>
    /// Hold type classification.
    /// </summary>
    public enum HoldType
    {
        Legal,
        Litigation,
        Regulatory,
        Investigation,
        Audit,
        Custom
    }

    /// <summary>
    /// Retention policy definition.
    /// </summary>
    public sealed record RetentionPolicy
    {
        public required string PolicyId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required TimeSpan RetentionPeriod { get; init; }
        public string? DefaultCategory { get; init; }
        public string? LegalBasis { get; init; }
        public bool AutoDelete { get; init; }
        public string[]? ApplicableCategories { get; init; }
    }

    /// <summary>
    /// Data retention record.
    /// </summary>
    public sealed record DataRetentionRecord
    {
        public required string RecordId { get; init; }
        public required string DataId { get; init; }
        public required string PolicyId { get; init; }
        public required string PolicyName { get; init; }
        public string? DataCategory { get; init; }
        public required DateTime RetentionStartedAt { get; init; }
        public required DateTime RetentionEndsAt { get; init; }
        public required string CreatedBy { get; init; }
        public bool IsOnHold { get; init; }
        public string? HoldId { get; init; }
        public DateTime? ExtendedAt { get; init; }
        public string? ExtendedBy { get; init; }
        public string? ExtensionReason { get; init; }
    }

    /// <summary>
    /// Retention hold.
    /// </summary>
    public sealed record RetentionHold
    {
        public required string HoldId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required HoldType HoldType { get; init; }
        public required string CreatedBy { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public required HashSet<string> AffectedDataIds { get; init; }
        public HoldScope? Scope { get; init; }
        public required bool IsActive { get; init; }
        public DateTime? ReleasedAt { get; init; }
        public string? ReleasedBy { get; init; }
        public string? ReleaseReason { get; init; }
    }

    /// <summary>
    /// Hold scope for pattern-based holds.
    /// </summary>
    public sealed record HoldScope
    {
        public string? CategoryPattern { get; init; }
        public string? DateRange { get; init; }
        public string? OwnerPattern { get; init; }
    }

    /// <summary>
    /// Retention schedule.
    /// </summary>
    public sealed record RetentionSchedule
    {
        public required string ScheduleId { get; init; }
        public required string DataId { get; init; }
        public required DateTime ScheduledDeletionAt { get; init; }
        public required DateTime CreatedAt { get; init; }
        public bool IsSuspended { get; init; }
        public string? SuspendedBy { get; init; }
        public bool IsCompleted { get; init; }
        public DateTime? CompletedAt { get; init; }
    }

    /// <summary>
    /// Apply policy options.
    /// </summary>
    public sealed record ApplyPolicyOptions
    {
        public string? DataCategory { get; init; }
        public string? CreatedBy { get; init; }
        public DateTime? CustomRetentionEnd { get; init; }
        public bool OverrideExisting { get; init; }
        public bool ForceOverride { get; init; }
    }

    /// <summary>
    /// Apply policy result.
    /// </summary>
    public sealed record ApplyPolicyResult
    {
        public required bool Success { get; init; }
        public string? RecordId { get; init; }
        public string? DataId { get; init; }
        public string? PolicyId { get; init; }
        public DateTime RetentionEndsAt { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ExistingPolicyId { get; init; }
    }

    /// <summary>
    /// Place hold request.
    /// </summary>
    public sealed record PlaceHoldRequest
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required HoldType HoldType { get; init; }
        public required string CreatedBy { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string[]? DataIds { get; init; }
        public HoldScope? Scope { get; init; }
    }

    /// <summary>
    /// Hold result.
    /// </summary>
    public sealed record HoldResult
    {
        public required bool Success { get; init; }
        public string? HoldId { get; init; }
        public int AffectedCount { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Extend retention result.
    /// </summary>
    public sealed record ExtendRetentionResult
    {
        public required bool Success { get; init; }
        public string? DataId { get; init; }
        public DateTime NewRetentionEndDate { get; init; }
        public TimeSpan ExtendedBy { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Deletion result.
    /// </summary>
    public sealed record DeletionResult
    {
        public required string DataId { get; init; }
        public required bool Success { get; init; }
        public DateTime DeletedAt { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Process deletions result.
    /// </summary>
    public sealed record ProcessDeletionsResult
    {
        public int TotalProcessed { get; init; }
        public int SuccessfulDeletions { get; init; }
        public int FailedDeletions { get; init; }
        public required IReadOnlyList<DeletionResult> Results { get; init; }
    }

    /// <summary>
    /// Retention statistics.
    /// </summary>
    public sealed record RetentionStatistics
    {
        public int TotalRecords { get; init; }
        public int RecordsOnHold { get; init; }
        public int RecordsExpired { get; init; }
        public int RecordsExpiringWithin30Days { get; init; }
        public int ActiveHolds { get; init; }
        public int ScheduledDeletions { get; init; }
        public required Dictionary<string, int> PolicyDistribution { get; init; }
    }

    /// <summary>
    /// Retention audit entry.
    /// </summary>
    public sealed record RetentionAuditEntry
    {
        public required string EntryId { get; init; }
        public string? DataId { get; init; }
        public string? PolicyId { get; init; }
        public string? HoldId { get; init; }
        public required RetentionAction Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public string? Details { get; init; }
    }

    #endregion
}
