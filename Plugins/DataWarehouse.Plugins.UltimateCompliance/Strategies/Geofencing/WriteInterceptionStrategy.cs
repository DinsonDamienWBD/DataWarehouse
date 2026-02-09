using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.4: Write Interception Strategy
    /// Blocks writes to non-compliant storage nodes based on sovereignty requirements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Pre-write sovereignty validation
    /// - Node location verification before writes
    /// - Policy-based write routing
    /// - Write blocking with detailed rejection reasons
    /// - Automatic compliant node selection
    /// </para>
    /// </remarks>
    public sealed class WriteInterceptionStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, StorageNodeInfo> _storageNodes = new();
        private readonly ConcurrentDictionary<string, WritePolicy> _writePolicies = new();
        private readonly ConcurrentDictionary<string, WriteInterceptionStats> _stats = new();
        private readonly ConcurrentBag<WriteInterceptionEvent> _auditLog = new();

        private bool _enforceMode = true;
        private int _maxAuditLogSize = 10000;

        /// <inheritdoc/>
        public override string StrategyId => "write-interception";

        /// <inheritdoc/>
        public override string StrategyName => "Write Interception";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("EnforceMode", out var enforceObj) && enforceObj is bool enforce)
            {
                _enforceMode = enforce;
            }

            if (configuration.TryGetValue("MaxAuditLogSize", out var sizeObj) && sizeObj is int size)
            {
                _maxAuditLogSize = size;
            }

            // Load storage nodes
            if (configuration.TryGetValue("StorageNodes", out var nodesObj) &&
                nodesObj is IEnumerable<Dictionary<string, object>> nodes)
            {
                foreach (var nodeConfig in nodes)
                {
                    var node = ParseNodeFromConfig(nodeConfig);
                    if (node != null)
                    {
                        RegisterStorageNode(node);
                    }
                }
            }

            // Load write policies
            if (configuration.TryGetValue("WritePolicies", out var policiesObj) &&
                policiesObj is IEnumerable<Dictionary<string, object>> policies)
            {
                foreach (var policyConfig in policies)
                {
                    var policy = ParsePolicyFromConfig(policyConfig);
                    if (policy != null)
                    {
                        _writePolicies[policy.PolicyId] = policy;
                    }
                }
            }

            InitializeDefaultPolicies();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a storage node with its location information.
        /// </summary>
        public void RegisterStorageNode(StorageNodeInfo node)
        {
            _storageNodes[node.NodeId] = node;
            _stats.TryAdd(node.NodeId, new WriteInterceptionStats { NodeId = node.NodeId });
        }

        /// <summary>
        /// Unregisters a storage node.
        /// </summary>
        public bool UnregisterStorageNode(string nodeId)
        {
            return _storageNodes.TryRemove(nodeId, out _);
        }

        /// <summary>
        /// Validates a write operation before execution.
        /// </summary>
        public WriteInterceptionResult ValidateWrite(WriteRequest request)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            var startTime = DateTime.UtcNow;

            // Get target node info
            if (!_storageNodes.TryGetValue(request.TargetNodeId, out var targetNode))
            {
                return CreateRejection(request, "Unknown storage node", startTime);
            }

            // Get applicable policy
            var policy = GetApplicablePolicy(request.DataClassification);

            // Validate node location against policy
            var locationResult = ValidateNodeLocation(targetNode, policy, request);
            if (!locationResult.IsAllowed)
            {
                return CreateRejection(request, locationResult.Reason, startTime,
                    suggestedNodes: FindCompliantNodes(policy, request.RequiredCapacity));
            }

            // Validate node status
            if (!targetNode.IsActive)
            {
                return CreateRejection(request, "Node is not active", startTime,
                    suggestedNodes: FindCompliantNodes(policy, request.RequiredCapacity));
            }

            if (!targetNode.AcceptingWrites)
            {
                return CreateRejection(request, "Node is not accepting writes", startTime,
                    suggestedNodes: FindCompliantNodes(policy, request.RequiredCapacity));
            }

            // Validate capacity
            if (targetNode.AvailableCapacityBytes < request.RequiredCapacity)
            {
                return CreateRejection(request, "Insufficient capacity", startTime,
                    suggestedNodes: FindCompliantNodes(policy, request.RequiredCapacity));
            }

            // Validate additional policy requirements
            var policyResult = ValidatePolicyRequirements(targetNode, policy, request);
            if (!policyResult.IsAllowed)
            {
                return CreateRejection(request, policyResult.Reason, startTime);
            }

            // All validations passed
            var result = new WriteInterceptionResult
            {
                IsAllowed = true,
                RequestId = request.RequestId,
                TargetNodeId = request.TargetNodeId,
                ValidationTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
                PolicyApplied = policy.PolicyId,
                ApprovedAt = DateTime.UtcNow
            };

            // Update statistics
            UpdateStats(request.TargetNodeId, true);

            // Audit log
            LogEvent(request, result, "APPROVED");

            return result;
        }

        /// <summary>
        /// Intercepts and validates a batch of write requests.
        /// </summary>
        public async Task<IReadOnlyList<WriteInterceptionResult>> ValidateWriteBatchAsync(
            IEnumerable<WriteRequest> requests,
            CancellationToken cancellationToken = default)
        {
            var results = new List<WriteInterceptionResult>();

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(ValidateWrite(request));
                await Task.Yield(); // Allow other tasks to run
            }

            return results;
        }

        /// <summary>
        /// Finds compliant storage nodes for a given policy and capacity requirement.
        /// </summary>
        public IReadOnlyList<StorageNodeInfo> FindCompliantNodes(string dataClassification, long requiredCapacity)
        {
            var policy = GetApplicablePolicy(dataClassification);
            return FindCompliantNodes(policy, requiredCapacity);
        }

        /// <summary>
        /// Gets storage node information.
        /// </summary>
        public StorageNodeInfo? GetNodeInfo(string nodeId)
        {
            return _storageNodes.TryGetValue(nodeId, out var node) ? node : null;
        }

        /// <summary>
        /// Gets all registered storage nodes.
        /// </summary>
        public IReadOnlyCollection<StorageNodeInfo> GetAllNodes()
        {
            return _storageNodes.Values.ToList();
        }

        /// <summary>
        /// Gets write interception statistics.
        /// </summary>
        public WriteInterceptionStats? GetNodeStats(string nodeId)
        {
            return _stats.TryGetValue(nodeId, out var stats) ? stats : null;
        }

        /// <summary>
        /// Gets aggregated statistics across all nodes.
        /// </summary>
        public AggregatedWriteStats GetAggregatedStats()
        {
            var allStats = _stats.Values.ToList();
            return new AggregatedWriteStats
            {
                TotalWritesValidated = allStats.Sum(s => s.TotalWritesValidated),
                TotalWritesApproved = allStats.Sum(s => s.WritesApproved),
                TotalWritesRejected = allStats.Sum(s => s.WritesRejected),
                AverageValidationTimeMs = allStats.Count > 0 ? allStats.Average(s => s.AverageValidationTimeMs) : 0,
                NodeCount = _storageNodes.Count,
                ActiveNodeCount = _storageNodes.Values.Count(n => n.IsActive)
            };
        }

        /// <summary>
        /// Gets recent interception events.
        /// </summary>
        public IReadOnlyList<WriteInterceptionEvent> GetRecentEvents(int count = 100)
        {
            return _auditLog.Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if write interception is properly configured
            if (_storageNodes.Count == 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "WI-001",
                    Description = "No storage nodes registered for write interception",
                    Severity = ViolationSeverity.High,
                    Remediation = "Register storage nodes with their location information"
                });
            }

            // Check for nodes without location information
            var nodesWithoutLocation = _storageNodes.Values.Where(n => string.IsNullOrEmpty(n.Location)).ToList();
            if (nodesWithoutLocation.Count > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "WI-002",
                    Description = $"{nodesWithoutLocation.Count} storage nodes lack location information",
                    Severity = ViolationSeverity.High,
                    AffectedResource = string.Join(", ", nodesWithoutLocation.Select(n => n.NodeId)),
                    Remediation = "Update storage node registrations with accurate location data"
                });
            }

            // Check enforcement mode
            if (!_enforceMode)
            {
                recommendations.Add("Write interception is in audit-only mode. Enable enforcement for production.");
            }

            // Check for inactive nodes
            var inactiveNodes = _storageNodes.Values.Where(n => !n.IsActive).ToList();
            if (inactiveNodes.Count > 0)
            {
                recommendations.Add($"{inactiveNodes.Count} storage nodes are inactive: {string.Join(", ", inactiveNodes.Select(n => n.NodeId))}");
            }

            // Validate specific write request if provided
            if (context.Attributes.TryGetValue("WriteRequest", out var requestObj) &&
                requestObj is WriteRequest request)
            {
                var result = ValidateWrite(request);
                if (!result.IsAllowed)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WI-003",
                        Description = $"Write request rejected: {result.RejectionReason}",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = request.TargetNodeId,
                        Remediation = result.SuggestedNodes?.Count > 0
                            ? $"Use compliant node: {string.Join(", ", result.SuggestedNodes.Select(n => n.NodeId))}"
                            : "Contact administrator"
                    });
                }
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
                    ["EnforcementMode"] = _enforceMode,
                    ["RegisteredNodes"] = _storageNodes.Count,
                    ["ActiveNodes"] = _storageNodes.Values.Count(n => n.IsActive),
                    ["AggregatedStats"] = GetAggregatedStats()
                }
            });
        }

        private WritePolicy GetApplicablePolicy(string dataClassification)
        {
            // Try exact match
            if (_writePolicies.TryGetValue(dataClassification, out var exactPolicy))
            {
                return exactPolicy;
            }

            // Try prefix match
            foreach (var (key, policy) in _writePolicies)
            {
                if (dataClassification.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    return policy;
                }
            }

            // Return default policy
            return _writePolicies.GetValueOrDefault("default") ?? new WritePolicy
            {
                PolicyId = "default",
                PolicyName = "Default Policy",
                AllowedRegions = new List<string>(),
                ProhibitedRegions = new List<string>()
            };
        }

        private (bool IsAllowed, string Reason) ValidateNodeLocation(StorageNodeInfo node, WritePolicy policy, WriteRequest request)
        {
            // Check prohibited regions first
            if (policy.ProhibitedRegions.Contains(node.Location, StringComparer.OrdinalIgnoreCase) ||
                policy.ProhibitedRegions.Contains(node.Region, StringComparer.OrdinalIgnoreCase))
            {
                return (false, $"Node {node.NodeId} is in prohibited region {node.Location}");
            }

            // Check allowed regions if specified
            if (policy.AllowedRegions.Count > 0)
            {
                if (!policy.AllowedRegions.Contains(node.Location, StringComparer.OrdinalIgnoreCase) &&
                    !policy.AllowedRegions.Contains(node.Region, StringComparer.OrdinalIgnoreCase))
                {
                    return (false, $"Node {node.NodeId} location {node.Location} not in allowed regions: {string.Join(", ", policy.AllowedRegions)}");
                }
            }

            // Check sovereignty requirements from request
            if (request.SovereigntyRequirements != null)
            {
                if (request.SovereigntyRequirements.RequiredRegions.Count > 0 &&
                    !request.SovereigntyRequirements.RequiredRegions.Contains(node.Location, StringComparer.OrdinalIgnoreCase))
                {
                    return (false, $"Node location {node.Location} does not meet sovereignty requirements");
                }
            }

            return (true, "Location validated");
        }

        private (bool IsAllowed, string Reason) ValidatePolicyRequirements(StorageNodeInfo node, WritePolicy policy, WriteRequest request)
        {
            // Check encryption requirements
            if (policy.RequiresEncryption && !node.SupportsEncryptionAtRest)
            {
                return (false, "Policy requires encryption at rest but node does not support it");
            }

            // Check certification requirements
            if (policy.RequiredCertifications.Count > 0)
            {
                var missingCerts = policy.RequiredCertifications
                    .Except(node.Certifications, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (missingCerts.Count > 0)
                {
                    return (false, $"Node missing required certifications: {string.Join(", ", missingCerts)}");
                }
            }

            // Check node tier
            if (policy.MinimumNodeTier.HasValue && node.NodeTier < policy.MinimumNodeTier.Value)
            {
                return (false, $"Node tier {node.NodeTier} below minimum required {policy.MinimumNodeTier.Value}");
            }

            return (true, "Policy requirements met");
        }

        private IReadOnlyList<StorageNodeInfo> FindCompliantNodes(WritePolicy policy, long requiredCapacity)
        {
            return _storageNodes.Values
                .Where(n => n.IsActive &&
                           n.AcceptingWrites &&
                           n.AvailableCapacityBytes >= requiredCapacity &&
                           !policy.ProhibitedRegions.Contains(n.Location, StringComparer.OrdinalIgnoreCase) &&
                           (policy.AllowedRegions.Count == 0 ||
                            policy.AllowedRegions.Contains(n.Location, StringComparer.OrdinalIgnoreCase) ||
                            policy.AllowedRegions.Contains(n.Region, StringComparer.OrdinalIgnoreCase)) &&
                           (!policy.RequiresEncryption || n.SupportsEncryptionAtRest) &&
                           policy.RequiredCertifications.All(c => n.Certifications.Contains(c, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(n => n.AvailableCapacityBytes)
                .ThenBy(n => n.CurrentLoad)
                .ToList();
        }

        private WriteInterceptionResult CreateRejection(WriteRequest request, string reason, DateTime startTime,
            IReadOnlyList<StorageNodeInfo>? suggestedNodes = null)
        {
            var result = new WriteInterceptionResult
            {
                IsAllowed = _enforceMode ? false : true, // Allow in audit mode
                RequestId = request.RequestId,
                TargetNodeId = request.TargetNodeId,
                RejectionReason = reason,
                ValidationTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
                SuggestedNodes = suggestedNodes,
                RejectedAt = DateTime.UtcNow,
                IsAuditOnly = !_enforceMode
            };

            UpdateStats(request.TargetNodeId, false);
            LogEvent(request, result, _enforceMode ? "REJECTED" : "AUDIT_REJECT");

            return result;
        }

        private void UpdateStats(string nodeId, bool approved)
        {
            _stats.AddOrUpdate(
                nodeId,
                _ => new WriteInterceptionStats
                {
                    NodeId = nodeId,
                    TotalWritesValidated = 1,
                    WritesApproved = approved ? 1 : 0,
                    WritesRejected = approved ? 0 : 1
                },
                (_, stats) =>
                {
                    Interlocked.Increment(ref stats.TotalWritesValidated);
                    if (approved)
                        Interlocked.Increment(ref stats.WritesApproved);
                    else
                        Interlocked.Increment(ref stats.WritesRejected);
                    return stats;
                });
        }

        private void LogEvent(WriteRequest request, WriteInterceptionResult result, string action)
        {
            if (_auditLog.Count >= _maxAuditLogSize)
            {
                // Remove oldest entries (simplified cleanup)
                while (_auditLog.Count >= _maxAuditLogSize && _auditLog.TryTake(out _)) { }
            }

            _auditLog.Add(new WriteInterceptionEvent
            {
                EventId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                RequestId = request.RequestId,
                TargetNodeId = request.TargetNodeId,
                DataClassification = request.DataClassification,
                Action = action,
                Reason = result.RejectionReason,
                ValidationTimeMs = result.ValidationTimeMs
            });
        }

        private void InitializeDefaultPolicies()
        {
            if (!_writePolicies.ContainsKey("personal-eu"))
            {
                _writePolicies["personal-eu"] = new WritePolicy
                {
                    PolicyId = "personal-eu",
                    PolicyName = "EU Personal Data",
                    AllowedRegions = new List<string> { "EU", "EEA", "GDPR-ADEQUATE" },
                    RequiresEncryption = true
                };
            }

            if (!_writePolicies.ContainsKey("phi"))
            {
                _writePolicies["phi"] = new WritePolicy
                {
                    PolicyId = "phi",
                    PolicyName = "Protected Health Information",
                    AllowedRegions = new List<string> { "US" },
                    RequiresEncryption = true,
                    RequiredCertifications = new List<string> { "HIPAA", "SOC2" }
                };
            }

            if (!_writePolicies.ContainsKey("default"))
            {
                _writePolicies["default"] = new WritePolicy
                {
                    PolicyId = "default",
                    PolicyName = "Default Policy",
                    AllowedRegions = new List<string>(),
                    ProhibitedRegions = new List<string>()
                };
            }
        }

        private StorageNodeInfo? ParseNodeFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new StorageNodeInfo
                {
                    NodeId = config["NodeId"]?.ToString() ?? "",
                    Location = config["Location"]?.ToString() ?? "",
                    Region = config.GetValueOrDefault("Region")?.ToString(),
                    IsActive = !config.TryGetValue("IsActive", out var active) || active is true,
                    AcceptingWrites = !config.TryGetValue("AcceptingWrites", out var accepting) || accepting is true,
                    AvailableCapacityBytes = config.TryGetValue("AvailableCapacity", out var cap) && cap is long capVal ? capVal : 0,
                    SupportsEncryptionAtRest = config.TryGetValue("SupportsEncryption", out var enc) && enc is true,
                    Certifications = (config.TryGetValue("Certifications", out var certs) && certs is IEnumerable<string> certList)
                        ? certList.ToList() : new List<string>()
                };
            }
            catch
            {
                return null;
            }
        }

        private WritePolicy? ParsePolicyFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new WritePolicy
                {
                    PolicyId = config["PolicyId"]?.ToString() ?? "",
                    PolicyName = config["PolicyName"]?.ToString() ?? "",
                    AllowedRegions = (config.TryGetValue("AllowedRegions", out var ar) && ar is IEnumerable<string> allowed)
                        ? allowed.ToList() : new List<string>(),
                    ProhibitedRegions = (config.TryGetValue("ProhibitedRegions", out var pr) && pr is IEnumerable<string> prohibited)
                        ? prohibited.ToList() : new List<string>(),
                    RequiresEncryption = config.TryGetValue("RequiresEncryption", out var enc) && enc is true,
                    RequiredCertifications = (config.TryGetValue("RequiredCertifications", out var certs) && certs is IEnumerable<string> certList)
                        ? certList.ToList() : new List<string>()
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Information about a storage node.
    /// </summary>
    public sealed record StorageNodeInfo
    {
        public required string NodeId { get; init; }
        public required string Location { get; init; }
        public string? Region { get; init; }
        public bool IsActive { get; init; }
        public bool AcceptingWrites { get; init; }
        public long AvailableCapacityBytes { get; init; }
        public double CurrentLoad { get; init; }
        public bool SupportsEncryptionAtRest { get; init; }
        public int NodeTier { get; init; }
        public List<string> Certifications { get; init; } = new();
        public DateTime LastVerifiedAt { get; init; }
    }

    /// <summary>
    /// Write policy for data classification.
    /// </summary>
    public sealed record WritePolicy
    {
        public required string PolicyId { get; init; }
        public required string PolicyName { get; init; }
        public List<string> AllowedRegions { get; init; } = new();
        public List<string> ProhibitedRegions { get; init; } = new();
        public bool RequiresEncryption { get; init; }
        public List<string> RequiredCertifications { get; init; } = new();
        public int? MinimumNodeTier { get; init; }
    }

    /// <summary>
    /// Write request for validation.
    /// </summary>
    public sealed record WriteRequest
    {
        public required string RequestId { get; init; }
        public required string TargetNodeId { get; init; }
        public required string DataClassification { get; init; }
        public long RequiredCapacity { get; init; }
        public SovereigntyRequirements? SovereigntyRequirements { get; init; }
        public string? RequesterId { get; init; }
    }

    /// <summary>
    /// Sovereignty requirements for a write request.
    /// </summary>
    public sealed record SovereigntyRequirements
    {
        public List<string> RequiredRegions { get; init; } = new();
        public List<string> ProhibitedRegions { get; init; } = new();
        public bool RequiresEncryption { get; init; }
    }

    /// <summary>
    /// Result of write interception validation.
    /// </summary>
    public sealed record WriteInterceptionResult
    {
        public required bool IsAllowed { get; init; }
        public required string RequestId { get; init; }
        public required string TargetNodeId { get; init; }
        public string? RejectionReason { get; init; }
        public double ValidationTimeMs { get; init; }
        public string? PolicyApplied { get; init; }
        public IReadOnlyList<StorageNodeInfo>? SuggestedNodes { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public DateTime? RejectedAt { get; init; }
        public bool IsAuditOnly { get; init; }
    }

    /// <summary>
    /// Statistics for write interception on a node.
    /// </summary>
    public sealed class WriteInterceptionStats
    {
        public required string NodeId { get; init; }
        public long TotalWritesValidated;
        public long WritesApproved;
        public long WritesRejected;
        public double AverageValidationTimeMs { get; set; }
    }

    /// <summary>
    /// Aggregated write statistics.
    /// </summary>
    public sealed record AggregatedWriteStats
    {
        public long TotalWritesValidated { get; init; }
        public long TotalWritesApproved { get; init; }
        public long TotalWritesRejected { get; init; }
        public double AverageValidationTimeMs { get; init; }
        public int NodeCount { get; init; }
        public int ActiveNodeCount { get; init; }
    }

    /// <summary>
    /// Audit log event for write interception.
    /// </summary>
    public sealed record WriteInterceptionEvent
    {
        public required string EventId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string RequestId { get; init; }
        public required string TargetNodeId { get; init; }
        public required string DataClassification { get; init; }
        public required string Action { get; init; }
        public string? Reason { get; init; }
        public double ValidationTimeMs { get; init; }
    }
}
