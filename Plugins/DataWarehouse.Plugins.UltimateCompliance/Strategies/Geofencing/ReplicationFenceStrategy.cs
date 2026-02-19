using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.5: Replication Fence Strategy
    /// Prevents replication across sovereignty boundaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Cross-border replication blocking
    /// - Sovereignty boundary enforcement for replicas
    /// - Replication topology validation
    /// - Quorum compliance for distributed data
    /// - Emergency replica isolation
    /// </para>
    /// </remarks>
    public sealed class ReplicationFenceStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, ReplicationTopology> _topologies = new();
        private readonly ConcurrentDictionary<string, SovereigntyBoundary> _boundaries = new();
        private readonly ConcurrentDictionary<string, ReplicationFenceRule> _rules = new();
        private readonly ConcurrentBag<ReplicationFenceEvent> _auditLog = new();

        private bool _enforceMode = true;
        private bool _allowEmergencyOverride;

        /// <inheritdoc/>
        public override string StrategyId => "replication-fence";

        /// <inheritdoc/>
        public override string StrategyName => "Replication Fence";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("EnforceMode", out var enforceObj) && enforceObj is bool enforce)
            {
                _enforceMode = enforce;
            }

            if (configuration.TryGetValue("AllowEmergencyOverride", out var overrideObj) && overrideObj is bool allowOverride)
            {
                _allowEmergencyOverride = allowOverride;
            }

            InitializeDefaultBoundaries();
            InitializeDefaultRules();

            // Load custom boundaries
            if (configuration.TryGetValue("Boundaries", out var boundariesObj) &&
                boundariesObj is IEnumerable<Dictionary<string, object>> boundaries)
            {
                foreach (var boundaryConfig in boundaries)
                {
                    var boundary = ParseBoundaryFromConfig(boundaryConfig);
                    if (boundary != null)
                    {
                        _boundaries[boundary.BoundaryId] = boundary;
                    }
                }
            }

            // Load custom rules
            if (configuration.TryGetValue("Rules", out var rulesObj) &&
                rulesObj is IEnumerable<Dictionary<string, object>> rules)
            {
                foreach (var ruleConfig in rules)
                {
                    var rule = ParseRuleFromConfig(ruleConfig);
                    if (rule != null)
                    {
                        _rules[rule.RuleId] = rule;
                    }
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a replication topology.
        /// </summary>
        public void RegisterTopology(ReplicationTopology topology)
        {
            _topologies[topology.TopologyId] = topology;
        }

        /// <summary>
        /// Validates a proposed replication operation.
        /// </summary>
        public ReplicationValidationResult ValidateReplication(ReplicationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            var violations = new List<ReplicationViolation>();
            var warnings = new List<string>();

            // Check if source and destination cross a sovereignty boundary
            var crossedBoundaries = FindCrossedBoundaries(request.SourceRegion, request.DestinationRegion);

            foreach (var boundary in crossedBoundaries)
            {
                if (boundary.IsHardBoundary)
                {
                    violations.Add(new ReplicationViolation
                    {
                        ViolationType = ReplicationViolationType.SovereigntyBoundary,
                        Description = $"Replication crosses sovereignty boundary: {boundary.BoundaryName}",
                        BoundaryId = boundary.BoundaryId,
                        Severity = ViolationSeverity.Critical
                    });
                }
                else
                {
                    warnings.Add($"Replication crosses soft boundary: {boundary.BoundaryName}. Additional safeguards may be required.");
                }
            }

            // Check data classification rules
            var applicableRules = GetApplicableRules(request.DataClassification);
            foreach (var rule in applicableRules)
            {
                var ruleResult = EvaluateRule(rule, request);
                if (!ruleResult.IsAllowed)
                {
                    violations.Add(new ReplicationViolation
                    {
                        ViolationType = ReplicationViolationType.PolicyViolation,
                        Description = ruleResult.Reason,
                        RuleId = rule.RuleId,
                        Severity = rule.Severity
                    });
                }
                else if (ruleResult.HasWarning)
                {
                    warnings.Add(ruleResult.Reason);
                }
            }

            // Validate topology compliance
            if (!string.IsNullOrEmpty(request.TopologyId) && _topologies.TryGetValue(request.TopologyId, out var topology))
            {
                var topologyResult = ValidateTopologyCompliance(topology, request);
                violations.AddRange(topologyResult.Violations);
                warnings.AddRange(topologyResult.Warnings);
            }

            // Check quorum requirements
            var quorumResult = ValidateQuorumCompliance(request);
            if (!quorumResult.IsCompliant)
            {
                violations.Add(new ReplicationViolation
                {
                    ViolationType = ReplicationViolationType.QuorumViolation,
                    Description = quorumResult.Reason,
                    Severity = ViolationSeverity.High
                });
            }

            var isAllowed = _enforceMode ? violations.Count == 0 : true;

            // Log the event
            LogEvent(request, isAllowed, violations);

            return new ReplicationValidationResult
            {
                IsAllowed = isAllowed,
                RequestId = request.RequestId,
                Violations = violations,
                Warnings = warnings,
                CrossedBoundaries = crossedBoundaries.Select(b => b.BoundaryId).ToList(),
                IsAuditOnly = !_enforceMode
            };
        }

        /// <summary>
        /// Validates an entire replication topology for compliance.
        /// </summary>
        public TopologyValidationResult ValidateTopology(string topologyId)
        {
            if (!_topologies.TryGetValue(topologyId, out var topology))
            {
                return new TopologyValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Topology not found: {topologyId}"
                };
            }

            var issues = new List<TopologyIssue>();

            // Check each replication path
            foreach (var path in topology.ReplicationPaths)
            {
                var crossedBoundaries = FindCrossedBoundaries(path.SourceRegion, path.DestinationRegion);

                foreach (var boundary in crossedBoundaries.Where(b => b.IsHardBoundary))
                {
                    issues.Add(new TopologyIssue
                    {
                        IssueType = TopologyIssueType.SovereigntyViolation,
                        Description = $"Path {path.SourceNode} -> {path.DestinationNode} crosses boundary {boundary.BoundaryName}",
                        SourceNode = path.SourceNode,
                        DestinationNode = path.DestinationNode,
                        Severity = ViolationSeverity.Critical
                    });
                }
            }

            // Check regional quorum requirements
            var regionCounts = topology.ReplicationPaths
                .SelectMany(p => new[] { p.SourceRegion, p.DestinationRegion })
                .GroupBy(r => r)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var (region, count) in regionCounts)
            {
                if (topology.MinimumRegionalQuorum.TryGetValue(region, out var minQuorum) && count < minQuorum)
                {
                    issues.Add(new TopologyIssue
                    {
                        IssueType = TopologyIssueType.InsufficientQuorum,
                        Description = $"Region {region} has {count} replicas, minimum required is {minQuorum}",
                        Severity = ViolationSeverity.High
                    });
                }
            }

            return new TopologyValidationResult
            {
                IsValid = !issues.Any(i => i.Severity >= ViolationSeverity.High),
                TopologyId = topologyId,
                Issues = issues
            };
        }

        /// <summary>
        /// Isolates replicas in a specific region (emergency action).
        /// </summary>
        public EmergencyIsolationResult IsolateRegion(string region, string reason, string authorizedBy)
        {
            if (!_allowEmergencyOverride)
            {
                return new EmergencyIsolationResult
                {
                    Success = false,
                    ErrorMessage = "Emergency override not enabled"
                };
            }

            // Create isolation boundary
            var isolationBoundary = new SovereigntyBoundary
            {
                BoundaryId = $"emergency-isolation-{region}-{DateTime.UtcNow.Ticks}",
                BoundaryName = $"Emergency Isolation: {region}",
                IncludedRegions = new List<string> { region },
                IsHardBoundary = true,
                IsTemporary = true,
                ExpiresAt = DateTime.UtcNow.AddHours(24), // 24-hour default
                CreatedAt = DateTime.UtcNow,
                Reason = reason,
                AuthorizedBy = authorizedBy
            };

            _boundaries[isolationBoundary.BoundaryId] = isolationBoundary;

            // Find affected topologies
            var affectedTopologies = _topologies.Values
                .Where(t => t.ReplicationPaths.Any(p =>
                    p.SourceRegion.Equals(region, StringComparison.OrdinalIgnoreCase) ||
                    p.DestinationRegion.Equals(region, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            LogEvent(new ReplicationRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                SourceRegion = region,
                DestinationRegion = "*",
                DataClassification = "emergency-isolation"
            }, false, new List<ReplicationViolation>
            {
                new()
                {
                    ViolationType = ReplicationViolationType.EmergencyIsolation,
                    Description = $"Emergency isolation: {reason}",
                    Severity = ViolationSeverity.Critical
                }
            });

            return new EmergencyIsolationResult
            {
                Success = true,
                IsolationBoundaryId = isolationBoundary.BoundaryId,
                AffectedTopologies = affectedTopologies.Select(t => t.TopologyId).ToList(),
                ExpiresAt = isolationBoundary.ExpiresAt
            };
        }

        /// <summary>
        /// Removes an emergency isolation.
        /// </summary>
        public bool RemoveEmergencyIsolation(string boundaryId, string authorizedBy)
        {
            if (_boundaries.TryGetValue(boundaryId, out var boundary) && boundary.IsTemporary)
            {
                return _boundaries.TryRemove(boundaryId, out _);
            }
            return false;
        }

        /// <summary>
        /// Gets all active sovereignty boundaries.
        /// </summary>
        public IReadOnlyList<SovereigntyBoundary> GetActiveBoundaries()
        {
            return _boundaries.Values
                .Where(b => !b.ExpiresAt.HasValue || b.ExpiresAt.Value > DateTime.UtcNow)
                .ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("replication_fence.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if replication request is provided
            if (context.Attributes.TryGetValue("ReplicationRequest", out var requestObj) &&
                requestObj is ReplicationRequest request)
            {
                var result = ValidateReplication(request);

                foreach (var violation in result.Violations)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = $"RF-{violation.ViolationType}",
                        Description = violation.Description,
                        Severity = violation.Severity,
                        AffectedResource = $"{request.SourceRegion} -> {request.DestinationRegion}",
                        Remediation = GetRemediationForViolationType(violation.ViolationType)
                    });
                }

                foreach (var warning in result.Warnings)
                {
                    recommendations.Add(warning);
                }
            }

            // Validate all topologies
            foreach (var topology in _topologies.Values)
            {
                var topologyResult = ValidateTopology(topology.TopologyId);
                foreach (var issue in topologyResult.Issues.Where(i => i.Severity >= ViolationSeverity.High))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = $"RF-TOPO-{issue.IssueType}",
                        Description = issue.Description,
                        Severity = issue.Severity,
                        AffectedResource = topology.TopologyId,
                        Remediation = "Review and reconfigure replication topology"
                    });
                }
            }

            // Check for expired temporary boundaries
            var expiredBoundaries = _boundaries.Values
                .Where(b => b.IsTemporary && b.ExpiresAt.HasValue && b.ExpiresAt.Value < DateTime.UtcNow)
                .ToList();

            if (expiredBoundaries.Count > 0)
            {
                recommendations.Add($"{expiredBoundaries.Count} temporary boundaries have expired and should be removed");
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
                    ["ActiveBoundaries"] = _boundaries.Count,
                    ["RegisteredTopologies"] = _topologies.Count,
                    ["ReplicationRules"] = _rules.Count
                }
            });
        }

        private IReadOnlyList<SovereigntyBoundary> FindCrossedBoundaries(string sourceRegion, string destRegion)
        {
            var crossed = new List<SovereigntyBoundary>();

            foreach (var boundary in _boundaries.Values)
            {
                if (boundary.ExpiresAt.HasValue && boundary.ExpiresAt.Value < DateTime.UtcNow)
                    continue;

                var sourceInBoundary = boundary.IncludedRegions.Contains(sourceRegion, StringComparer.OrdinalIgnoreCase);
                var destInBoundary = boundary.IncludedRegions.Contains(destRegion, StringComparer.OrdinalIgnoreCase);

                // Crossed if one is inside and one is outside
                if (sourceInBoundary != destInBoundary)
                {
                    crossed.Add(boundary);
                }
            }

            return crossed;
        }

        private IReadOnlyList<ReplicationFenceRule> GetApplicableRules(string dataClassification)
        {
            return _rules.Values
                .Where(r => r.AppliesToClassifications.Count == 0 ||
                           r.AppliesToClassifications.Contains(dataClassification, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        private (bool IsAllowed, string Reason, bool HasWarning) EvaluateRule(ReplicationFenceRule rule, ReplicationRequest request)
        {
            // Check prohibited paths
            if (rule.ProhibitedPaths.Any(p =>
                p.SourceRegion.Equals(request.SourceRegion, StringComparison.OrdinalIgnoreCase) &&
                p.DestinationRegion.Equals(request.DestinationRegion, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"Replication path {request.SourceRegion} -> {request.DestinationRegion} is prohibited by rule {rule.RuleName}", false);
            }

            // Check if path is in allowed list (if specified)
            if (rule.AllowedPaths.Count > 0)
            {
                var isAllowed = rule.AllowedPaths.Any(p =>
                    p.SourceRegion.Equals(request.SourceRegion, StringComparison.OrdinalIgnoreCase) &&
                    p.DestinationRegion.Equals(request.DestinationRegion, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    return (false, $"Replication path not in allowed list for rule {rule.RuleName}", false);
                }
            }

            // Check warnings
            if (rule.WarnPaths.Any(p =>
                p.SourceRegion.Equals(request.SourceRegion, StringComparison.OrdinalIgnoreCase) &&
                p.DestinationRegion.Equals(request.DestinationRegion, StringComparison.OrdinalIgnoreCase)))
            {
                return (true, $"Caution: Path {request.SourceRegion} -> {request.DestinationRegion} triggers warning for rule {rule.RuleName}", true);
            }

            return (true, "Rule passed", false);
        }

        private (List<ReplicationViolation> Violations, List<string> Warnings) ValidateTopologyCompliance(
            ReplicationTopology topology, ReplicationRequest request)
        {
            var violations = new List<ReplicationViolation>();
            var warnings = new List<string>();

            // Check if the request path exists in topology
            var pathExists = topology.ReplicationPaths.Any(p =>
                p.SourceRegion.Equals(request.SourceRegion, StringComparison.OrdinalIgnoreCase) &&
                p.DestinationRegion.Equals(request.DestinationRegion, StringComparison.OrdinalIgnoreCase));

            if (!pathExists && topology.StrictTopology)
            {
                violations.Add(new ReplicationViolation
                {
                    ViolationType = ReplicationViolationType.TopologyViolation,
                    Description = "Replication path not defined in strict topology",
                    Severity = ViolationSeverity.High
                });
            }

            return (violations, warnings);
        }

        private (bool IsCompliant, string Reason) ValidateQuorumCompliance(ReplicationRequest request)
        {
            // Check if regional quorum requirements are met
            if (request.RequiredQuorum.HasValue && request.CurrentReplicaCount < request.RequiredQuorum.Value)
            {
                return (false, $"Insufficient replicas: {request.CurrentReplicaCount} < required {request.RequiredQuorum.Value}");
            }

            return (true, "Quorum requirements met");
        }

        private void LogEvent(ReplicationRequest request, bool allowed, List<ReplicationViolation> violations)
        {
            _auditLog.Add(new ReplicationFenceEvent
            {
                EventId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                RequestId = request.RequestId,
                SourceRegion = request.SourceRegion,
                DestinationRegion = request.DestinationRegion,
                DataClassification = request.DataClassification,
                WasAllowed = allowed,
                ViolationCount = violations.Count,
                ViolationTypes = violations.Select(v => v.ViolationType.ToString()).ToList()
            });
        }

        private string GetRemediationForViolationType(ReplicationViolationType type)
        {
            return type switch
            {
                ReplicationViolationType.SovereigntyBoundary => "Choose a destination region within the sovereignty boundary",
                ReplicationViolationType.PolicyViolation => "Review replication policy and adjust data classification or destination",
                ReplicationViolationType.TopologyViolation => "Update replication topology to include the required path",
                ReplicationViolationType.QuorumViolation => "Add more replicas to meet quorum requirements",
                ReplicationViolationType.EmergencyIsolation => "Wait for emergency isolation to be lifted or contact administrator",
                _ => "Contact administrator for guidance"
            };
        }

        private void InitializeDefaultBoundaries()
        {
            // EU Data Sovereignty Boundary
            _boundaries["eu-sovereignty"] = new SovereigntyBoundary
            {
                BoundaryId = "eu-sovereignty",
                BoundaryName = "EU Data Sovereignty",
                IncludedRegions = new List<string>
                {
                    "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                    "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                    "PL", "PT", "RO", "SK", "SI", "ES", "SE", "EU", "EEA"
                },
                IsHardBoundary = false, // Soft boundary - requires safeguards
                Reason = "GDPR data protection requirements"
            };

            // China Data Localization
            _boundaries["china-localization"] = new SovereigntyBoundary
            {
                BoundaryId = "china-localization",
                BoundaryName = "China Data Localization",
                IncludedRegions = new List<string> { "CN" },
                IsHardBoundary = true,
                Reason = "PIPL/CSL data localization requirements"
            };

            // Russia Data Localization
            _boundaries["russia-localization"] = new SovereigntyBoundary
            {
                BoundaryId = "russia-localization",
                BoundaryName = "Russia Data Localization",
                IncludedRegions = new List<string> { "RU" },
                IsHardBoundary = true,
                Reason = "FZ-242 data localization requirements"
            };
        }

        private void InitializeDefaultRules()
        {
            // Personal data rule
            _rules["personal-data"] = new ReplicationFenceRule
            {
                RuleId = "personal-data",
                RuleName = "Personal Data Replication",
                AppliesToClassifications = new List<string> { "personal", "pii", "personal-eu" },
                ProhibitedPaths = new List<ReplicationPath>
                {
                    new() { SourceRegion = "EU", DestinationRegion = "CN" },
                    new() { SourceRegion = "EU", DestinationRegion = "RU" }
                },
                Severity = ViolationSeverity.Critical
            };

            // Health data rule
            _rules["health-data"] = new ReplicationFenceRule
            {
                RuleId = "health-data",
                RuleName = "Health Data Replication",
                AppliesToClassifications = new List<string> { "phi", "health" },
                AllowedPaths = new List<ReplicationPath>
                {
                    new() { SourceRegion = "US", DestinationRegion = "US" }
                },
                Severity = ViolationSeverity.Critical
            };
        }

        private SovereigntyBoundary? ParseBoundaryFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new SovereigntyBoundary
                {
                    BoundaryId = config["BoundaryId"]?.ToString() ?? "",
                    BoundaryName = config["BoundaryName"]?.ToString() ?? "",
                    IncludedRegions = (config.TryGetValue("IncludedRegions", out var regions) && regions is IEnumerable<string> r)
                        ? r.ToList() : new List<string>(),
                    IsHardBoundary = config.TryGetValue("IsHardBoundary", out var hard) && hard is true,
                    Reason = config.GetValueOrDefault("Reason")?.ToString()
                };
            }
            catch
            {
                return null;
            }
        }

        private ReplicationFenceRule? ParseRuleFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new ReplicationFenceRule
                {
                    RuleId = config["RuleId"]?.ToString() ?? "",
                    RuleName = config["RuleName"]?.ToString() ?? "",
                    AppliesToClassifications = (config.TryGetValue("Classifications", out var c) && c is IEnumerable<string> classifications)
                        ? classifications.ToList() : new List<string>(),
                    Severity = Enum.TryParse<ViolationSeverity>(config.GetValueOrDefault("Severity")?.ToString(), out var sev)
                        ? sev : ViolationSeverity.High
                };
            }
            catch
            {
                return null;
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("replication_fence.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("replication_fence.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Defines a sovereignty boundary for replication.
    /// </summary>
    public sealed record SovereigntyBoundary
    {
        public required string BoundaryId { get; init; }
        public required string BoundaryName { get; init; }
        public List<string> IncludedRegions { get; init; } = new();
        public bool IsHardBoundary { get; init; }
        public bool IsTemporary { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public string? Reason { get; init; }
        public string? AuthorizedBy { get; init; }
    }

    /// <summary>
    /// Replication fence rule.
    /// </summary>
    public sealed record ReplicationFenceRule
    {
        public required string RuleId { get; init; }
        public required string RuleName { get; init; }
        public List<string> AppliesToClassifications { get; init; } = new();
        public List<ReplicationPath> AllowedPaths { get; init; } = new();
        public List<ReplicationPath> ProhibitedPaths { get; init; } = new();
        public List<ReplicationPath> WarnPaths { get; init; } = new();
        public ViolationSeverity Severity { get; init; } = ViolationSeverity.High;
    }

    /// <summary>
    /// Replication path definition.
    /// </summary>
    public sealed record ReplicationPath
    {
        public required string SourceRegion { get; init; }
        public required string DestinationRegion { get; init; }
        public string? SourceNode { get; init; }
        public string? DestinationNode { get; init; }
    }

    /// <summary>
    /// Replication topology definition.
    /// </summary>
    public sealed record ReplicationTopology
    {
        public required string TopologyId { get; init; }
        public required string TopologyName { get; init; }
        public List<ReplicationPath> ReplicationPaths { get; init; } = new();
        public Dictionary<string, int> MinimumRegionalQuorum { get; init; } = new();
        public bool StrictTopology { get; init; }
    }

    /// <summary>
    /// Replication request for validation.
    /// </summary>
    public sealed record ReplicationRequest
    {
        public required string RequestId { get; init; }
        public required string SourceRegion { get; init; }
        public required string DestinationRegion { get; init; }
        public required string DataClassification { get; init; }
        public string? TopologyId { get; init; }
        public int? RequiredQuorum { get; init; }
        public int CurrentReplicaCount { get; init; }
    }

    /// <summary>
    /// Result of replication validation.
    /// </summary>
    public sealed record ReplicationValidationResult
    {
        public required bool IsAllowed { get; init; }
        public required string RequestId { get; init; }
        public List<ReplicationViolation> Violations { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public List<string> CrossedBoundaries { get; init; } = new();
        public bool IsAuditOnly { get; init; }
    }

    /// <summary>
    /// Replication violation details.
    /// </summary>
    public sealed record ReplicationViolation
    {
        public required ReplicationViolationType ViolationType { get; init; }
        public required string Description { get; init; }
        public string? BoundaryId { get; init; }
        public string? RuleId { get; init; }
        public required ViolationSeverity Severity { get; init; }
    }

    /// <summary>
    /// Type of replication violation.
    /// </summary>
    public enum ReplicationViolationType
    {
        SovereigntyBoundary,
        PolicyViolation,
        TopologyViolation,
        QuorumViolation,
        EmergencyIsolation
    }

    /// <summary>
    /// Result of topology validation.
    /// </summary>
    public sealed record TopologyValidationResult
    {
        public required bool IsValid { get; init; }
        public string? TopologyId { get; init; }
        public string? ErrorMessage { get; init; }
        public List<TopologyIssue> Issues { get; init; } = new();
    }

    /// <summary>
    /// Topology issue.
    /// </summary>
    public sealed record TopologyIssue
    {
        public required TopologyIssueType IssueType { get; init; }
        public required string Description { get; init; }
        public string? SourceNode { get; init; }
        public string? DestinationNode { get; init; }
        public required ViolationSeverity Severity { get; init; }
    }

    /// <summary>
    /// Type of topology issue.
    /// </summary>
    public enum TopologyIssueType
    {
        SovereigntyViolation,
        InsufficientQuorum,
        InvalidPath,
        MissingNode
    }

    /// <summary>
    /// Result of emergency isolation.
    /// </summary>
    public sealed record EmergencyIsolationResult
    {
        public required bool Success { get; init; }
        public string? IsolationBoundaryId { get; init; }
        public string? ErrorMessage { get; init; }
        public List<string> AffectedTopologies { get; init; } = new();
        public DateTime? ExpiresAt { get; init; }
    }

    /// <summary>
    /// Audit log event for replication fence.
    /// </summary>
    public sealed record ReplicationFenceEvent
    {
        public required string EventId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string RequestId { get; init; }
        public required string SourceRegion { get; init; }
        public required string DestinationRegion { get; init; }
        public required string DataClassification { get; init; }
        public required bool WasAllowed { get; init; }
        public int ViolationCount { get; init; }
        public List<string> ViolationTypes { get; init; } = new();
    }
}
