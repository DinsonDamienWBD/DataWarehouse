using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.8: Dynamic Reconfiguration Strategy
    /// Handles node location changes and dynamic sovereignty boundary updates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Real-time node location change detection
    /// - Automatic data migration triggers
    /// - Grace period management for transitions
    /// - Rollback capabilities
    /// - Compliance impact assessment
    /// - Notification and alerting
    /// </para>
    /// </remarks>
    public sealed class DynamicReconfigurationStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, NodeLocationRecord> _nodeLocations = new();
        private readonly ConcurrentDictionary<string, LocationChangeRequest> _pendingChanges = new();
        private readonly ConcurrentDictionary<string, LocationChangeHistory> _changeHistory = new();
        private readonly ConcurrentDictionary<string, MigrationPlan> _migrationPlans = new();
        private readonly ConcurrentBag<ReconfigurationEvent> _events = new();

        private TimeSpan _defaultGracePeriod = TimeSpan.FromHours(24);
        private bool _autoMigrationEnabled = true;
        private readonly List<IReconfigurationListener> _listeners = new();

        /// <inheritdoc/>
        public override string StrategyId => "dynamic-reconfiguration";

        /// <inheritdoc/>
        public override string StrategyName => "Dynamic Reconfiguration";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("DefaultGracePeriodHours", out var graceObj) && graceObj is int hours)
            {
                _defaultGracePeriod = TimeSpan.FromHours(hours);
            }

            if (configuration.TryGetValue("AutoMigrationEnabled", out var autoObj) && autoObj is bool auto)
            {
                _autoMigrationEnabled = auto;
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a node with its current location.
        /// </summary>
        public void RegisterNode(string nodeId, string location, string region, NodeType nodeType = NodeType.Storage)
        {
            var record = new NodeLocationRecord
            {
                NodeId = nodeId,
                CurrentLocation = location,
                CurrentRegion = region,
                NodeType = nodeType,
                RegisteredAt = DateTime.UtcNow,
                LastVerifiedAt = DateTime.UtcNow,
                IsActive = true
            };

            _nodeLocations[nodeId] = record;

            LogEvent(new ReconfigurationEvent
            {
                EventType = ReconfigurationEventType.NodeRegistered,
                NodeId = nodeId,
                NewLocation = location,
                NewRegion = region,
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Initiates a node location change request.
        /// </summary>
        public LocationChangeResult RequestLocationChange(LocationChangeRequest request)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            // Validate node exists
            if (!_nodeLocations.TryGetValue(request.NodeId, out var currentRecord))
            {
                return new LocationChangeResult
                {
                    Success = false,
                    ErrorMessage = "Node not registered"
                };
            }

            // Assess compliance impact
            var impactAssessment = AssessComplianceImpact(request, currentRecord);

            if (impactAssessment.HasBlockingIssues)
            {
                return new LocationChangeResult
                {
                    Success = false,
                    ErrorMessage = "Location change blocked due to compliance issues",
                    ImpactAssessment = impactAssessment
                };
            }

            // Create change request
            var changeRequest = request with
            {
                RequestId = Guid.NewGuid().ToString(),
                RequestedAt = DateTime.UtcNow,
                Status = ChangeStatus.Pending,
                PreviousLocation = currentRecord.CurrentLocation,
                PreviousRegion = currentRecord.CurrentRegion,
                GracePeriodEnd = DateTime.UtcNow.Add(request.GracePeriod ?? _defaultGracePeriod),
                ImpactAssessment = impactAssessment
            };

            _pendingChanges[changeRequest.RequestId] = changeRequest;

            // Notify listeners
            NotifyListeners(new LocationChangeNotification
            {
                NotificationType = NotificationType.ChangeRequested,
                ChangeRequest = changeRequest
            });

            // Create migration plan if auto-migration enabled
            if (_autoMigrationEnabled && impactAssessment.RequiresMigration)
            {
                var migrationPlan = CreateMigrationPlan(changeRequest, impactAssessment);
                _migrationPlans[changeRequest.RequestId] = migrationPlan;
            }

            LogEvent(new ReconfigurationEvent
            {
                EventType = ReconfigurationEventType.ChangeRequested,
                NodeId = request.NodeId,
                PreviousLocation = currentRecord.CurrentLocation,
                NewLocation = request.NewLocation,
                PreviousRegion = currentRecord.CurrentRegion,
                NewRegion = request.NewRegion,
                Timestamp = DateTime.UtcNow,
                RequestId = changeRequest.RequestId
            });

            return new LocationChangeResult
            {
                Success = true,
                RequestId = changeRequest.RequestId,
                GracePeriodEnd = changeRequest.GracePeriodEnd,
                ImpactAssessment = impactAssessment,
                MigrationPlanId = _migrationPlans.ContainsKey(changeRequest.RequestId)
                    ? changeRequest.RequestId : null
            };
        }

        /// <summary>
        /// Applies a pending location change.
        /// </summary>
        public ApplyChangeResult ApplyLocationChange(string requestId)
        {
            if (!_pendingChanges.TryGetValue(requestId, out var changeRequest))
            {
                return new ApplyChangeResult
                {
                    Success = false,
                    ErrorMessage = "Change request not found"
                };
            }

            if (changeRequest.Status != ChangeStatus.Pending && changeRequest.Status != ChangeStatus.Approved)
            {
                return new ApplyChangeResult
                {
                    Success = false,
                    ErrorMessage = $"Change request in invalid state: {changeRequest.Status}"
                };
            }

            // Check if migration is complete (if required)
            if (changeRequest.ImpactAssessment?.RequiresMigration == true)
            {
                if (_migrationPlans.TryGetValue(requestId, out var plan) && !plan.IsComplete)
                {
                    return new ApplyChangeResult
                    {
                        Success = false,
                        ErrorMessage = "Migration not complete. Complete migration before applying change.",
                        MigrationStatus = plan.Status
                    };
                }
            }

            // Apply the change
            if (!_nodeLocations.TryGetValue(changeRequest.NodeId, out var nodeRecord))
            {
                return new ApplyChangeResult
                {
                    Success = false,
                    ErrorMessage = "Node no longer exists"
                };
            }

            // Update node location
            var updatedRecord = nodeRecord with
            {
                CurrentLocation = changeRequest.NewLocation,
                CurrentRegion = changeRequest.NewRegion,
                LastVerifiedAt = DateTime.UtcNow
            };

            _nodeLocations[changeRequest.NodeId] = updatedRecord;

            // Update change request status
            var completedRequest = changeRequest with
            {
                Status = ChangeStatus.Applied,
                AppliedAt = DateTime.UtcNow
            };

            _pendingChanges[requestId] = completedRequest;

            // Record in history
            RecordChangeHistory(completedRequest);

            // Notify listeners
            NotifyListeners(new LocationChangeNotification
            {
                NotificationType = NotificationType.ChangeApplied,
                ChangeRequest = completedRequest
            });

            LogEvent(new ReconfigurationEvent
            {
                EventType = ReconfigurationEventType.ChangeApplied,
                NodeId = changeRequest.NodeId,
                PreviousLocation = changeRequest.PreviousLocation,
                NewLocation = changeRequest.NewLocation,
                PreviousRegion = changeRequest.PreviousRegion,
                NewRegion = changeRequest.NewRegion,
                Timestamp = DateTime.UtcNow,
                RequestId = requestId
            });

            return new ApplyChangeResult
            {
                Success = true,
                RequestId = requestId,
                AppliedAt = completedRequest.AppliedAt
            };
        }

        /// <summary>
        /// Rolls back a location change.
        /// </summary>
        public RollbackResult RollbackLocationChange(string requestId, string reason)
        {
            if (!_pendingChanges.TryGetValue(requestId, out var changeRequest))
            {
                // Check history
                if (!_changeHistory.TryGetValue(requestId, out var history))
                {
                    return new RollbackResult
                    {
                        Success = false,
                        ErrorMessage = "Change request not found"
                    };
                }

                changeRequest = history.ChangeRequest;
            }

            // Create rollback request
            var rollbackRequest = new LocationChangeRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                NodeId = changeRequest.NodeId,
                NewLocation = changeRequest.PreviousLocation ?? "",
                NewRegion = changeRequest.PreviousRegion ?? "",
                Reason = $"Rollback: {reason}",
                IsRollback = true,
                RollbackOfRequestId = requestId,
                RequestedAt = DateTime.UtcNow,
                Status = ChangeStatus.Approved // Auto-approve rollbacks
            };

            // Apply rollback
            if (_nodeLocations.TryGetValue(changeRequest.NodeId, out var nodeRecord))
            {
                var rolledBackRecord = nodeRecord with
                {
                    CurrentLocation = rollbackRequest.NewLocation,
                    CurrentRegion = rollbackRequest.NewRegion,
                    LastVerifiedAt = DateTime.UtcNow
                };

                _nodeLocations[changeRequest.NodeId] = rolledBackRecord;
            }

            // Update original request status
            _pendingChanges[requestId] = changeRequest with { Status = ChangeStatus.RolledBack };

            LogEvent(new ReconfigurationEvent
            {
                EventType = ReconfigurationEventType.ChangeRolledBack,
                NodeId = changeRequest.NodeId,
                PreviousLocation = changeRequest.NewLocation,
                NewLocation = changeRequest.PreviousLocation,
                PreviousRegion = changeRequest.NewRegion,
                NewRegion = changeRequest.PreviousRegion,
                Timestamp = DateTime.UtcNow,
                RequestId = requestId,
                Reason = reason
            });

            return new RollbackResult
            {
                Success = true,
                OriginalRequestId = requestId,
                RollbackRequestId = rollbackRequest.RequestId,
                RolledBackAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Gets the current location of a node.
        /// </summary>
        public NodeLocationRecord? GetNodeLocation(string nodeId)
        {
            return _nodeLocations.TryGetValue(nodeId, out var record) ? record : null;
        }

        /// <summary>
        /// Gets all nodes in a region.
        /// </summary>
        public IReadOnlyList<NodeLocationRecord> GetNodesInRegion(string region)
        {
            return _nodeLocations.Values
                .Where(n => n.CurrentRegion.Equals(region, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Gets pending change requests.
        /// </summary>
        public IReadOnlyList<LocationChangeRequest> GetPendingChanges()
        {
            return _pendingChanges.Values
                .Where(c => c.Status == ChangeStatus.Pending || c.Status == ChangeStatus.Approved)
                .ToList();
        }

        /// <summary>
        /// Gets migration plan for a change request.
        /// </summary>
        public MigrationPlan? GetMigrationPlan(string requestId)
        {
            return _migrationPlans.TryGetValue(requestId, out var plan) ? plan : null;
        }

        /// <summary>
        /// Updates migration progress.
        /// </summary>
        public void UpdateMigrationProgress(string requestId, MigrationProgress progress)
        {
            if (_migrationPlans.TryGetValue(requestId, out var plan))
            {
                var updatedPlan = plan with
                {
                    CurrentProgress = progress,
                    LastUpdated = DateTime.UtcNow,
                    IsComplete = progress.PercentComplete >= 100
                };

                _migrationPlans[requestId] = updatedPlan;

                if (updatedPlan.IsComplete)
                {
                    NotifyListeners(new LocationChangeNotification
                    {
                        NotificationType = NotificationType.MigrationComplete,
                        MigrationPlan = updatedPlan
                    });
                }
            }
        }

        /// <summary>
        /// Registers a reconfiguration listener.
        /// </summary>
        public void RegisterListener(IReconfigurationListener listener)
        {
            _listeners.Add(listener);
        }

        /// <summary>
        /// Gets reconfiguration events.
        /// </summary>
        public IReadOnlyList<ReconfigurationEvent> GetEvents(int count = 100)
        {
            return _events.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("dynamic_reconfiguration.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check for pending changes past grace period
            var expiredChanges = _pendingChanges.Values
                .Where(c => c.Status == ChangeStatus.Pending && c.GracePeriodEnd < DateTime.UtcNow)
                .ToList();

            if (expiredChanges.Count > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "RECONFIG-001",
                    Description = $"{expiredChanges.Count} location changes have exceeded grace period",
                    Severity = ViolationSeverity.High,
                    Remediation = "Apply or reject pending location changes"
                });
            }

            // Check for nodes with stale location verification
            var staleNodes = _nodeLocations.Values
                .Where(n => n.IsActive && (DateTime.UtcNow - n.LastVerifiedAt) > TimeSpan.FromDays(7))
                .ToList();

            if (staleNodes.Count > 0)
            {
                recommendations.Add($"{staleNodes.Count} nodes have not been location-verified in over 7 days");
            }

            // Check for incomplete migrations
            var incompleteMigrations = _migrationPlans.Values
                .Where(p => !p.IsComplete && p.StartedAt < DateTime.UtcNow.AddDays(-1))
                .ToList();

            if (incompleteMigrations.Count > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "RECONFIG-002",
                    Description = $"{incompleteMigrations.Count} migrations are incomplete for over 24 hours",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Complete or abort stalled migrations"
                });
            }

            // Check for changes with compliance impact
            var impactfulChanges = _pendingChanges.Values
                .Where(c => c.Status == ChangeStatus.Pending && c.ImpactAssessment?.ComplianceImpact == ComplianceImpact.High)
                .ToList();

            if (impactfulChanges.Count > 0)
            {
                recommendations.Add($"{impactfulChanges.Count} pending changes have high compliance impact. Review carefully before applying.");
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
                    ["RegisteredNodes"] = _nodeLocations.Count,
                    ["PendingChanges"] = _pendingChanges.Values.Count(c => c.Status == ChangeStatus.Pending),
                    ["ActiveMigrations"] = _migrationPlans.Values.Count(p => !p.IsComplete),
                    ["AutoMigrationEnabled"] = _autoMigrationEnabled
                }
            });
        }

        private ComplianceImpactAssessment AssessComplianceImpact(LocationChangeRequest request, NodeLocationRecord currentRecord)
        {
            var issues = new List<string>();
            var warnings = new List<string>();
            var requiresMigration = false;
            var impact = ComplianceImpact.Low;

            // Check if crossing sovereignty boundaries
            var crossesBoundary = !currentRecord.CurrentRegion.Equals(request.NewRegion, StringComparison.OrdinalIgnoreCase);

            if (crossesBoundary)
            {
                impact = ComplianceImpact.High;
                warnings.Add($"Node will move from {currentRecord.CurrentRegion} to {request.NewRegion}");

                // Check for data that cannot cross boundaries
                var affectedDataCount = GetAffectedDataCount(request.NodeId, currentRecord.CurrentRegion);
                if (affectedDataCount > 0)
                {
                    requiresMigration = true;
                    warnings.Add($"{affectedDataCount} data objects may need migration");
                }
            }

            // Check regulatory implications
            var regulatoryIssues = CheckRegulatoryImplications(currentRecord.CurrentRegion, request.NewRegion);
            issues.AddRange(regulatoryIssues.Where(r => r.IsBlocking).Select(r => r.Description));
            warnings.AddRange(regulatoryIssues.Where(r => !r.IsBlocking).Select(r => r.Description));

            return new ComplianceImpactAssessment
            {
                ComplianceImpact = impact,
                HasBlockingIssues = issues.Count > 0,
                BlockingIssues = issues,
                Warnings = warnings,
                RequiresMigration = requiresMigration,
                AffectedDataCount = requiresMigration ? GetAffectedDataCount(request.NodeId, currentRecord.CurrentRegion) : 0
            };
        }

        private int GetAffectedDataCount(string nodeId, string region)
        {
            // In production, query actual data inventory
            // Returns count of data objects that may be affected by location change
            return 0;
        }

        private List<(string Description, bool IsBlocking)> CheckRegulatoryImplications(string fromRegion, string toRegion)
        {
            var implications = new List<(string Description, bool IsBlocking)>();

            // EU to non-EU
            if (IsEuRegion(fromRegion) && !IsEuRegion(toRegion))
            {
                implications.Add(("Moving from EU requires GDPR transfer mechanisms", true));
            }

            // China localization
            if (fromRegion.Equals("CN", StringComparison.OrdinalIgnoreCase) &&
                !toRegion.Equals("CN", StringComparison.OrdinalIgnoreCase))
            {
                implications.Add(("China PIPL may restrict data movement out of China", true));
            }

            // Russia localization
            if (fromRegion.Equals("RU", StringComparison.OrdinalIgnoreCase) &&
                !toRegion.Equals("RU", StringComparison.OrdinalIgnoreCase))
            {
                implications.Add(("Russian personal data must remain in Russia", true));
            }

            return implications;
        }

        private bool IsEuRegion(string region)
        {
            var euRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "EU", "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                "PL", "PT", "RO", "SK", "SI", "ES", "SE"
            };

            return euRegions.Contains(region);
        }

        private MigrationPlan CreateMigrationPlan(LocationChangeRequest changeRequest, ComplianceImpactAssessment impact)
        {
            return new MigrationPlan
            {
                PlanId = changeRequest.RequestId ?? Guid.NewGuid().ToString(),
                NodeId = changeRequest.NodeId,
                FromLocation = changeRequest.PreviousLocation ?? "",
                FromRegion = changeRequest.PreviousRegion ?? "",
                ToLocation = changeRequest.NewLocation,
                ToRegion = changeRequest.NewRegion,
                EstimatedDataObjects = impact.AffectedDataCount,
                Status = MigrationStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                StartedAt = null,
                IsComplete = false
            };
        }

        private void RecordChangeHistory(LocationChangeRequest request)
        {
            if (request.RequestId == null) return;

            _changeHistory[request.RequestId] = new LocationChangeHistory
            {
                RequestId = request.RequestId,
                ChangeRequest = request,
                RecordedAt = DateTime.UtcNow
            };
        }

        private void NotifyListeners(LocationChangeNotification notification)
        {
            foreach (var listener in _listeners)
            {
                try
                {
                    listener.OnLocationChange(notification);
                }
                catch
                {
                    // Log but don't fail on listener errors
                }
            }
        }

        private void LogEvent(ReconfigurationEvent evt)
        {
            _events.Add(evt);
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("dynamic_reconfiguration.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("dynamic_reconfiguration.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Node location record.
    /// </summary>
    public sealed record NodeLocationRecord
    {
        public required string NodeId { get; init; }
        public required string CurrentLocation { get; init; }
        public required string CurrentRegion { get; init; }
        public NodeType NodeType { get; init; }
        public DateTime RegisteredAt { get; init; }
        public DateTime LastVerifiedAt { get; init; }
        public bool IsActive { get; init; }
    }

    /// <summary>
    /// Node type enumeration.
    /// </summary>
    public enum NodeType
    {
        Storage,
        Compute,
        Network,
        Gateway
    }

    /// <summary>
    /// Location change request.
    /// </summary>
    public sealed record LocationChangeRequest
    {
        public string? RequestId { get; init; }
        public required string NodeId { get; init; }
        public required string NewLocation { get; init; }
        public required string NewRegion { get; init; }
        public string? PreviousLocation { get; init; }
        public string? PreviousRegion { get; init; }
        public string? Reason { get; init; }
        public TimeSpan? GracePeriod { get; init; }
        public DateTime RequestedAt { get; init; }
        public DateTime? AppliedAt { get; init; }
        public DateTime? GracePeriodEnd { get; init; }
        public ChangeStatus Status { get; init; }
        public ComplianceImpactAssessment? ImpactAssessment { get; init; }
        public bool IsRollback { get; init; }
        public string? RollbackOfRequestId { get; init; }
    }

    /// <summary>
    /// Change request status.
    /// </summary>
    public enum ChangeStatus
    {
        Pending,
        Approved,
        Applied,
        Rejected,
        RolledBack,
        Expired
    }

    /// <summary>
    /// Compliance impact assessment.
    /// </summary>
    public sealed record ComplianceImpactAssessment
    {
        public ComplianceImpact ComplianceImpact { get; init; }
        public bool HasBlockingIssues { get; init; }
        public List<string> BlockingIssues { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public bool RequiresMigration { get; init; }
        public int AffectedDataCount { get; init; }
    }

    /// <summary>
    /// Compliance impact level.
    /// </summary>
    public enum ComplianceImpact
    {
        None,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Location change result.
    /// </summary>
    public sealed record LocationChangeResult
    {
        public required bool Success { get; init; }
        public string? RequestId { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime? GracePeriodEnd { get; init; }
        public ComplianceImpactAssessment? ImpactAssessment { get; init; }
        public string? MigrationPlanId { get; init; }
    }

    /// <summary>
    /// Apply change result.
    /// </summary>
    public sealed record ApplyChangeResult
    {
        public required bool Success { get; init; }
        public string? RequestId { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime? AppliedAt { get; init; }
        public MigrationStatus? MigrationStatus { get; init; }
    }

    /// <summary>
    /// Rollback result.
    /// </summary>
    public sealed record RollbackResult
    {
        public required bool Success { get; init; }
        public string? OriginalRequestId { get; init; }
        public string? RollbackRequestId { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime? RolledBackAt { get; init; }
    }

    /// <summary>
    /// Migration plan.
    /// </summary>
    public sealed record MigrationPlan
    {
        public required string PlanId { get; init; }
        public required string NodeId { get; init; }
        public required string FromLocation { get; init; }
        public required string FromRegion { get; init; }
        public required string ToLocation { get; init; }
        public required string ToRegion { get; init; }
        public int EstimatedDataObjects { get; init; }
        public MigrationStatus Status { get; init; }
        public MigrationProgress? CurrentProgress { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? LastUpdated { get; init; }
        public bool IsComplete { get; init; }
    }

    /// <summary>
    /// Migration status.
    /// </summary>
    public enum MigrationStatus
    {
        Pending,
        InProgress,
        Paused,
        Complete,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Migration progress.
    /// </summary>
    public sealed record MigrationProgress
    {
        public int TotalObjects { get; init; }
        public int MigratedObjects { get; init; }
        public int FailedObjects { get; init; }
        public double PercentComplete => TotalObjects > 0 ? (double)MigratedObjects / TotalObjects * 100 : 0;
        public TimeSpan? EstimatedTimeRemaining { get; init; }
    }

    /// <summary>
    /// Location change history record.
    /// </summary>
    public sealed record LocationChangeHistory
    {
        public required string RequestId { get; init; }
        public required LocationChangeRequest ChangeRequest { get; init; }
        public DateTime RecordedAt { get; init; }
    }

    /// <summary>
    /// Reconfiguration event.
    /// </summary>
    public sealed record ReconfigurationEvent
    {
        public required ReconfigurationEventType EventType { get; init; }
        public required string NodeId { get; init; }
        public string? PreviousLocation { get; init; }
        public string? NewLocation { get; init; }
        public string? PreviousRegion { get; init; }
        public string? NewRegion { get; init; }
        public DateTime Timestamp { get; init; }
        public string? RequestId { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Reconfiguration event type.
    /// </summary>
    public enum ReconfigurationEventType
    {
        NodeRegistered,
        NodeUnregistered,
        ChangeRequested,
        ChangeApproved,
        ChangeRejected,
        ChangeApplied,
        ChangeRolledBack,
        MigrationStarted,
        MigrationComplete,
        MigrationFailed
    }

    /// <summary>
    /// Location change notification.
    /// </summary>
    public sealed record LocationChangeNotification
    {
        public required NotificationType NotificationType { get; init; }
        public LocationChangeRequest? ChangeRequest { get; init; }
        public MigrationPlan? MigrationPlan { get; init; }
    }

    /// <summary>
    /// Notification type.
    /// </summary>
    public enum NotificationType
    {
        ChangeRequested,
        ChangeApproved,
        ChangeRejected,
        ChangeApplied,
        ChangeRolledBack,
        MigrationStarted,
        MigrationProgress,
        MigrationComplete,
        MigrationFailed,
        GracePeriodWarning,
        GracePeriodExpired
    }

    /// <summary>
    /// Interface for reconfiguration listeners.
    /// </summary>
    public interface IReconfigurationListener
    {
        void OnLocationChange(LocationChangeNotification notification);
    }
}
