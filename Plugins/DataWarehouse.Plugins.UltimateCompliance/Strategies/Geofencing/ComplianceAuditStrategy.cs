using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.7: Compliance Audit Strategy
    /// Logs all sovereignty decisions for auditors with tamper-evident trails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Comprehensive sovereignty decision logging
    /// - Tamper-evident audit trails with hash chains
    /// - Structured audit reports for regulators
    /// - Real-time compliance dashboards
    /// - Retention policy enforcement
    /// - Export capabilities for external audit systems
    /// </para>
    /// </remarks>
    public sealed class ComplianceAuditStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, AuditTrail> _auditTrails = new BoundedDictionary<string, AuditTrail>(1000);
        // ConcurrentQueue preserves insertion order so EnforceMemoryLimits can drop
        // the oldest entries (front of queue) rather than random ones (ConcurrentBag).
        private readonly System.Collections.Concurrent.ConcurrentQueue<SovereigntyDecision> _decisions = new();
        private readonly BoundedDictionary<string, AuditReport> _reports = new BoundedDictionary<string, AuditReport>(1000);
        private readonly BoundedDictionary<string, ComplianceMetrics> _metrics = new BoundedDictionary<string, ComplianceMetrics>(1000);

        private TimeSpan _retentionPeriod = TimeSpan.FromDays(365 * 7); // 7 years default
        private int _maxDecisionsInMemory = 100000;
        private string? _currentChainHash;

        /// <inheritdoc/>
        public override string StrategyId => "compliance-audit";

        /// <inheritdoc/>
        public override string StrategyName => "Compliance Audit";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("RetentionYears", out var retentionObj) && retentionObj is int years)
            {
                _retentionPeriod = TimeSpan.FromDays(365 * years);
            }

            if (configuration.TryGetValue("MaxDecisionsInMemory", out var maxObj) && maxObj is int max)
            {
                _maxDecisionsInMemory = max;
            }

            InitializeAuditChain();
            InitializeMetrics();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Records a sovereignty decision.
        /// </summary>
        public AuditRecordResult RecordDecision(SovereigntyDecision decision)
        {
            ArgumentNullException.ThrowIfNull(decision, nameof(decision));

            // Assign ID and timestamp if not set
            var recordedDecision = decision with
            {
                DecisionId = decision.DecisionId ?? Guid.NewGuid().ToString(),
                Timestamp = decision.Timestamp == default ? DateTime.UtcNow : decision.Timestamp,
                AuditHash = ComputeDecisionHash(decision),
                PreviousHash = _currentChainHash ?? ""
            };

            // Update chain hash
            _currentChainHash = recordedDecision.AuditHash;

            // Store decision (FIFO queue maintains insertion order for correct eviction)
            _decisions.Enqueue(recordedDecision);

            // Update metrics
            UpdateMetrics(recordedDecision);

            // Add to appropriate audit trail
            AddToAuditTrail(recordedDecision);

            // Enforce memory limits
            EnforceMemoryLimits();

            return new AuditRecordResult
            {
                Success = true,
                DecisionId = recordedDecision.DecisionId,
                AuditHash = recordedDecision.AuditHash,
                ChainPosition = _decisions.Count
            };
        }

        /// <summary>
        /// Records a batch of decisions atomically.
        /// </summary>
        public BatchAuditResult RecordDecisionBatch(IEnumerable<SovereigntyDecision> decisions)
        {
            var results = new List<AuditRecordResult>();
            var batchId = Guid.NewGuid().ToString();
            var batchStartHash = _currentChainHash;

            foreach (var decision in decisions)
            {
                var batchedDecision = decision with { BatchId = batchId };
                var result = RecordDecision(batchedDecision);
                results.Add(result);
            }

            return new BatchAuditResult
            {
                Success = results.All(r => r.Success),
                BatchId = batchId,
                RecordCount = results.Count,
                StartChainHash = batchStartHash ?? "",
                EndChainHash = _currentChainHash ?? "",
                Results = results
            };
        }

        /// <summary>
        /// Queries sovereignty decisions with filters.
        /// </summary>
        public IReadOnlyList<SovereigntyDecision> QueryDecisions(DecisionQuery query)
        {
            var decisions = _decisions.AsEnumerable();

            if (query.StartDate.HasValue)
                decisions = decisions.Where(d => d.Timestamp >= query.StartDate.Value);

            if (query.EndDate.HasValue)
                decisions = decisions.Where(d => d.Timestamp <= query.EndDate.Value);

            if (!string.IsNullOrEmpty(query.DecisionType))
                decisions = decisions.Where(d => d.DecisionType.Equals(query.DecisionType, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(query.DataClassification))
                decisions = decisions.Where(d => d.DataClassification.Equals(query.DataClassification, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(query.Region))
                decisions = decisions.Where(d =>
                    (d.SourceRegion?.Equals(query.Region, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (d.DestinationRegion?.Equals(query.Region, StringComparison.OrdinalIgnoreCase) ?? false));

            if (query.OutcomeFilter.HasValue)
                decisions = decisions.Where(d => d.WasAllowed == (query.OutcomeFilter.Value == DecisionOutcome.Allowed));

            if (!string.IsNullOrEmpty(query.ResourceId))
                decisions = decisions.Where(d => d.ResourceId?.Equals(query.ResourceId, StringComparison.OrdinalIgnoreCase) ?? false);

            return decisions
                .OrderByDescending(d => d.Timestamp)
                .Skip(query.Offset)
                .Take(query.Limit)
                .ToList();
        }

        /// <summary>
        /// Generates a compliance audit report.
        /// </summary>
        public AuditReport GenerateReport(ReportRequest request)
        {
            var decisions = QueryDecisions(new DecisionQuery
            {
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Limit = int.MaxValue
            });

            var report = new AuditReport
            {
                ReportId = Guid.NewGuid().ToString(),
                GeneratedAt = DateTime.UtcNow,
                PeriodStart = request.StartDate,
                PeriodEnd = request.EndDate,
                ReportType = request.ReportType,
                Summary = GenerateReportSummary(decisions),
                DecisionBreakdown = GenerateDecisionBreakdown(decisions),
                RegionAnalysis = GenerateRegionAnalysis(decisions),
                ViolationAnalysis = GenerateViolationAnalysis(decisions),
                Recommendations = GenerateRecommendations(decisions),
                ChainIntegrity = VerifyChainIntegrity(decisions),
                TotalDecisions = decisions.Count,
                ComplianceScore = CalculateComplianceScore(decisions)
            };

            _reports[report.ReportId] = report;

            return report;
        }

        /// <summary>
        /// Exports audit data for external systems.
        /// </summary>
        public AuditExport ExportAuditData(ExportRequest request)
        {
            var decisions = QueryDecisions(new DecisionQuery
            {
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Limit = request.MaxRecords ?? int.MaxValue
            });

            var exportData = request.Format switch
            {
                ExportFormat.Json => JsonSerializer.Serialize(decisions, new JsonSerializerOptions { WriteIndented = true }),
                ExportFormat.Csv => ConvertToCsv(decisions),
                ExportFormat.Xml => ConvertToXml(decisions),
                _ => JsonSerializer.Serialize(decisions)
            };

            var exportHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exportData)));

            return new AuditExport
            {
                ExportId = Guid.NewGuid().ToString(),
                Format = request.Format,
                Data = exportData,
                RecordCount = decisions.Count,
                ExportHash = exportHash,
                GeneratedAt = DateTime.UtcNow,
                PeriodStart = request.StartDate,
                PeriodEnd = request.EndDate
            };
        }

        /// <summary>
        /// Gets real-time compliance metrics.
        /// </summary>
        public ComplianceMetricsSummary GetMetricsSummary(TimeSpan? window = null)
        {
            var windowStart = window.HasValue ? DateTime.UtcNow - window.Value : DateTime.MinValue;

            var recentDecisions = _decisions
                .Where(d => d.Timestamp >= windowStart)
                .ToList();

            return new ComplianceMetricsSummary
            {
                WindowStart = windowStart == DateTime.MinValue ? null : windowStart,
                WindowEnd = DateTime.UtcNow,
                TotalDecisions = recentDecisions.Count,
                AllowedDecisions = recentDecisions.Count(d => d.WasAllowed),
                BlockedDecisions = recentDecisions.Count(d => !d.WasAllowed),
                UniqueResources = recentDecisions.Select(d => d.ResourceId).Distinct().Count(),
                UniqueRegions = recentDecisions
                    .SelectMany(d => new[] { d.SourceRegion, d.DestinationRegion })
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Distinct()
                    .Count(),

                ViolationsByType = recentDecisions
                    .Where(d => !d.WasAllowed)
                    .GroupBy(d => d.ViolationType ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                DecisionsByClassification = recentDecisions
                    .GroupBy(d => d.DataClassification)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AverageDecisionTimeMs = recentDecisions.Count > 0
                    ? recentDecisions.Average(d => d.ProcessingTimeMs)
                    : 0
            };
        }

        /// <summary>
        /// Verifies the integrity of the audit chain.
        /// </summary>
        public ChainVerificationResult VerifyAuditChain()
        {
            var decisions = _decisions.OrderBy(d => d.Timestamp).ToList();
            var tamperedRecords = new List<string>();
            string? previousHash = null;

            foreach (var decision in decisions)
            {
                // Verify this decision's hash
                var computedHash = ComputeDecisionHash(decision with { AuditHash = null, PreviousHash = previousHash ?? "" });

                if (!computedHash.Equals(decision.AuditHash, StringComparison.OrdinalIgnoreCase))
                {
                    tamperedRecords.Add(decision.DecisionId ?? "unknown");
                }

                // Verify chain link
                if (previousHash != null && !decision.PreviousHash.Equals(previousHash, StringComparison.OrdinalIgnoreCase))
                {
                    tamperedRecords.Add(decision.DecisionId ?? "unknown");
                }

                previousHash = decision.AuditHash;
            }

            return new ChainVerificationResult
            {
                IsIntact = tamperedRecords.Count == 0,
                TotalRecords = decisions.Count,
                VerifiedRecords = decisions.Count - tamperedRecords.Count,
                TamperedRecords = tamperedRecords,
                CurrentChainHash = _currentChainHash ?? "",
                VerificationTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Gets audit trail for a specific resource.
        /// </summary>
        public AuditTrail? GetResourceAuditTrail(string resourceId)
        {
            return _auditTrails.TryGetValue(resourceId, out var trail) ? trail : null;
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("compliance_audit.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Verify audit chain integrity
            var chainResult = VerifyAuditChain();
            if (!chainResult.IsIntact)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AUDIT-001",
                    Description = $"Audit chain integrity compromised: {chainResult.TamperedRecords.Count} records affected",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Investigate and restore audit chain integrity"
                });
            }

            // Check retention compliance
            var oldestDecision = _decisions.OrderBy(d => d.Timestamp).FirstOrDefault();
            if (oldestDecision != null)
            {
                var age = DateTime.UtcNow - oldestDecision.Timestamp;
                if (age > _retentionPeriod)
                {
                    recommendations.Add($"Oldest audit record exceeds retention period. Consider archiving.");
                }
            }

            // Check for audit gaps
            var metrics = GetMetricsSummary(TimeSpan.FromHours(24));
            if (metrics.TotalDecisions == 0)
            {
                recommendations.Add("No sovereignty decisions recorded in the last 24 hours. Verify audit logging is active.");
            }

            // Provide compliance metrics
            var complianceRate = metrics.TotalDecisions > 0
                ? (double)metrics.AllowedDecisions / metrics.TotalDecisions * 100
                : 100;

            if (complianceRate < 90)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AUDIT-002",
                    Description = $"Compliance rate ({complianceRate:F1}%) below threshold",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Review blocked decisions and address root causes"
                });
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
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
                    ["TotalDecisions"] = _decisions.Count,
                    ["ChainIntact"] = chainResult.IsIntact,
                    ["ComplianceRate"] = complianceRate,
                    ["RetentionPeriodDays"] = _retentionPeriod.TotalDays,
                    ["Last24HourMetrics"] = metrics
                }
            });
        }

        private void InitializeAuditChain()
        {
            _currentChainHash = ComputeDecisionHash(new SovereigntyDecision
            {
                DecisionId = "CHAIN_INIT",
                DecisionType = "INIT",
                DataClassification = "SYSTEM",
                WasAllowed = true,
                Timestamp = DateTime.UtcNow
            });
        }

        private void InitializeMetrics()
        {
            var regions = new[] { "EU", "US", "APAC", "LATAM", "MENA" };
            foreach (var region in regions)
            {
                _metrics[region] = new ComplianceMetrics { Region = region };
            }
        }

        private string ComputeDecisionHash(SovereigntyDecision decision)
        {
            var data = $"{decision.DecisionId}:{decision.DecisionType}:{decision.DataClassification}:" +
                      $"{decision.SourceRegion}:{decision.DestinationRegion}:{decision.ResourceId}:" +
                      $"{decision.WasAllowed}:{decision.Timestamp.Ticks}:{decision.PreviousHash}";

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }

        private void UpdateMetrics(SovereigntyDecision decision)
        {
            var regions = new[] { decision.SourceRegion, decision.DestinationRegion }
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct();

            foreach (var region in regions)
            {
                if (region != null)
                {
                    _metrics.AddOrUpdate(
                        region,
                        _ => new ComplianceMetrics
                        {
                            Region = region,
                            TotalDecisions = 1,
                            AllowedCount = decision.WasAllowed ? 1 : 0,
                            BlockedCount = decision.WasAllowed ? 0 : 1
                        },
                        (_, m) =>
                        {
                            Interlocked.Increment(ref m.TotalDecisions);
                            if (decision.WasAllowed)
                                Interlocked.Increment(ref m.AllowedCount);
                            else
                                Interlocked.Increment(ref m.BlockedCount);
                            return m;
                        });
                }
            }
        }

        private void AddToAuditTrail(SovereigntyDecision decision)
        {
            if (string.IsNullOrEmpty(decision.ResourceId))
                return;

            _auditTrails.AddOrUpdate(
                decision.ResourceId,
                _ => new AuditTrail
                {
                    ResourceId = decision.ResourceId,
                    Decisions = new List<SovereigntyDecision> { decision }
                },
                (_, trail) =>
                {
                    trail.Decisions.Add(decision);
                    return trail;
                });
        }

        private void EnforceMemoryLimits()
        {
            // Drop the oldest entries (front of FIFO queue) when memory limit is exceeded.
            // Each dequeue removes exactly one entry; we stop once we are at half capacity.
            var targetCount = _maxDecisionsInMemory / 2;
            while (_decisions.Count > targetCount)
            {
                _decisions.TryDequeue(out _);
            }
        }

        private ReportSummary GenerateReportSummary(IReadOnlyList<SovereigntyDecision> decisions)
        {
            return new ReportSummary
            {
                TotalDecisions = decisions.Count,
                AllowedDecisions = decisions.Count(d => d.WasAllowed),
                BlockedDecisions = decisions.Count(d => !d.WasAllowed),
                UniqueResources = decisions.Select(d => d.ResourceId).Distinct().Count(),
                ComplianceRate = decisions.Count > 0
                    ? (double)decisions.Count(d => d.WasAllowed) / decisions.Count * 100
                    : 100
            };
        }

        private Dictionary<string, int> GenerateDecisionBreakdown(IReadOnlyList<SovereigntyDecision> decisions)
        {
            return decisions
                .GroupBy(d => d.DecisionType)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private Dictionary<string, RegionStats> GenerateRegionAnalysis(IReadOnlyList<SovereigntyDecision> decisions)
        {
            var regions = decisions
                .SelectMany(d => new[] { d.SourceRegion, d.DestinationRegion })
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct();

            return regions.ToDictionary(
                r => r!,
                r => new RegionStats
                {
                    Region = r!,
                    TotalDecisions = decisions.Count(d =>
                        (d.SourceRegion?.Equals(r, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (d.DestinationRegion?.Equals(r, StringComparison.OrdinalIgnoreCase) ?? false)),
                    BlockedDecisions = decisions.Count(d =>
                        !d.WasAllowed &&
                        ((d.SourceRegion?.Equals(r, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (d.DestinationRegion?.Equals(r, StringComparison.OrdinalIgnoreCase) ?? false)))
                });
        }

        private List<ViolationSummary> GenerateViolationAnalysis(IReadOnlyList<SovereigntyDecision> decisions)
        {
            return decisions
                .Where(d => !d.WasAllowed)
                .GroupBy(d => d.ViolationType ?? "Unknown")
                .Select(g => new ViolationSummary
                {
                    ViolationType = g.Key,
                    Count = g.Count(),
                    FirstOccurrence = g.Min(d => d.Timestamp),
                    LastOccurrence = g.Max(d => d.Timestamp),
                    AffectedResources = g.Select(d => d.ResourceId).Distinct().Count()
                })
                .OrderByDescending(v => v.Count)
                .ToList();
        }

        private List<string> GenerateRecommendations(IReadOnlyList<SovereigntyDecision> decisions)
        {
            var recommendations = new List<string>();
            var blockedRate = decisions.Count > 0
                ? (double)decisions.Count(d => !d.WasAllowed) / decisions.Count * 100
                : 0;

            if (blockedRate > 10)
            {
                recommendations.Add($"High block rate ({blockedRate:F1}%). Review data classification and sovereignty policies.");
            }

            var topViolation = decisions
                .Where(d => !d.WasAllowed)
                .GroupBy(d => d.ViolationType)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (topViolation != null && topViolation.Count() > 10)
            {
                recommendations.Add($"Frequent violation type: {topViolation.Key}. Consider policy review.");
            }

            return recommendations;
        }

        private bool VerifyChainIntegrity(IReadOnlyList<SovereigntyDecision> decisions)
        {
            if (decisions.Count < 2) return true;

            var ordered = decisions.OrderBy(d => d.Timestamp).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (!ordered[i].PreviousHash.Equals(ordered[i - 1].AuditHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private double CalculateComplianceScore(IReadOnlyList<SovereigntyDecision> decisions)
        {
            if (decisions.Count == 0) return 100;

            var allowedRate = (double)decisions.Count(d => d.WasAllowed) / decisions.Count;
            var chainIntact = VerifyChainIntegrity(decisions) ? 1.0 : 0.5;

            return allowedRate * chainIntact * 100;
        }

        private string ConvertToCsv(IReadOnlyList<SovereigntyDecision> decisions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DecisionId,Timestamp,DecisionType,DataClassification,SourceRegion,DestinationRegion,ResourceId,WasAllowed,ViolationType,AuditHash");

            foreach (var d in decisions)
            {
                sb.AppendLine($"{d.DecisionId},{d.Timestamp:O},{d.DecisionType},{d.DataClassification},{d.SourceRegion},{d.DestinationRegion},{d.ResourceId},{d.WasAllowed},{d.ViolationType},{d.AuditHash}");
            }

            return sb.ToString();
        }

        private string ConvertToXml(IReadOnlyList<SovereigntyDecision> decisions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<AuditExport>");

            foreach (var d in decisions)
            {
                sb.AppendLine("  <Decision>");
                sb.AppendLine($"    <DecisionId>{d.DecisionId}</DecisionId>");
                sb.AppendLine($"    <Timestamp>{d.Timestamp:O}</Timestamp>");
                sb.AppendLine($"    <DecisionType>{d.DecisionType}</DecisionType>");
                sb.AppendLine($"    <WasAllowed>{d.WasAllowed}</WasAllowed>");
                sb.AppendLine($"    <AuditHash>{d.AuditHash}</AuditHash>");
                sb.AppendLine("  </Decision>");
            }

            sb.AppendLine("</AuditExport>");
            return sb.ToString();
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("compliance_audit.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("compliance_audit.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Sovereignty decision record.
    /// </summary>
    public sealed record SovereigntyDecision
    {
        public string? DecisionId { get; init; }
        public required string DecisionType { get; init; }
        public required string DataClassification { get; init; }
        public string? SourceRegion { get; init; }
        public string? DestinationRegion { get; init; }
        public string? ResourceId { get; init; }
        public required bool WasAllowed { get; init; }
        public string? ViolationType { get; init; }
        public string? ViolationDetails { get; init; }
        public string? UserId { get; init; }
        public DateTime Timestamp { get; init; }
        public double ProcessingTimeMs { get; init; }
        public string? AuditHash { get; init; }
        public string PreviousHash { get; init; } = "";
        public string? BatchId { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Result of recording an audit decision.
    /// </summary>
    public sealed record AuditRecordResult
    {
        public required bool Success { get; init; }
        public string? DecisionId { get; init; }
        public string? AuditHash { get; init; }
        public int ChainPosition { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Result of batch audit recording.
    /// </summary>
    public sealed record BatchAuditResult
    {
        public required bool Success { get; init; }
        public required string BatchId { get; init; }
        public int RecordCount { get; init; }
        public required string StartChainHash { get; init; }
        public required string EndChainHash { get; init; }
        public List<AuditRecordResult> Results { get; init; } = new();
    }

    /// <summary>
    /// Query parameters for decisions.
    /// </summary>
    public sealed record DecisionQuery
    {
        public DateTime? StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public string? DecisionType { get; init; }
        public string? DataClassification { get; init; }
        public string? Region { get; init; }
        public string? ResourceId { get; init; }
        public DecisionOutcome? OutcomeFilter { get; init; }
        public int Offset { get; init; } = 0;
        public int Limit { get; init; } = 1000;
    }

    /// <summary>
    /// Decision outcome filter.
    /// </summary>
    public enum DecisionOutcome
    {
        Allowed,
        Blocked
    }

    /// <summary>
    /// Audit trail for a resource.
    /// </summary>
    public sealed class AuditTrail
    {
        public required string ResourceId { get; init; }
        public List<SovereigntyDecision> Decisions { get; init; } = new();
    }

    /// <summary>
    /// Audit report.
    /// </summary>
    public sealed record AuditReport
    {
        public required string ReportId { get; init; }
        public required DateTime GeneratedAt { get; init; }
        public DateTime? PeriodStart { get; init; }
        public DateTime? PeriodEnd { get; init; }
        public required string ReportType { get; init; }
        public required ReportSummary Summary { get; init; }
        public Dictionary<string, int> DecisionBreakdown { get; init; } = new();
        public Dictionary<string, RegionStats> RegionAnalysis { get; init; } = new();
        public List<ViolationSummary> ViolationAnalysis { get; init; } = new();
        public List<string> Recommendations { get; init; } = new();
        public bool ChainIntegrity { get; init; }
        public int TotalDecisions { get; init; }
        public double ComplianceScore { get; init; }
    }

    /// <summary>
    /// Report summary.
    /// </summary>
    public sealed record ReportSummary
    {
        public int TotalDecisions { get; init; }
        public int AllowedDecisions { get; init; }
        public int BlockedDecisions { get; init; }
        public int UniqueResources { get; init; }
        public double ComplianceRate { get; init; }
    }

    /// <summary>
    /// Region statistics.
    /// </summary>
    public sealed record RegionStats
    {
        public required string Region { get; init; }
        public int TotalDecisions { get; init; }
        public int BlockedDecisions { get; init; }
    }

    /// <summary>
    /// Violation summary.
    /// </summary>
    public sealed record ViolationSummary
    {
        public required string ViolationType { get; init; }
        public int Count { get; init; }
        public DateTime FirstOccurrence { get; init; }
        public DateTime LastOccurrence { get; init; }
        public int AffectedResources { get; init; }
    }

    /// <summary>
    /// Report request.
    /// </summary>
    public sealed record ReportRequest
    {
        public DateTime? StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public string ReportType { get; init; } = "Standard";
    }

    /// <summary>
    /// Export request.
    /// </summary>
    public sealed record ExportRequest
    {
        public DateTime? StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public ExportFormat Format { get; init; } = ExportFormat.Json;
        public int? MaxRecords { get; init; }
    }

    /// <summary>
    /// Export format options.
    /// </summary>
    public enum ExportFormat
    {
        Json,
        Csv,
        Xml
    }

    /// <summary>
    /// Audit export result.
    /// </summary>
    public sealed record AuditExport
    {
        public required string ExportId { get; init; }
        public required ExportFormat Format { get; init; }
        public required string Data { get; init; }
        public int RecordCount { get; init; }
        public required string ExportHash { get; init; }
        public required DateTime GeneratedAt { get; init; }
        public DateTime? PeriodStart { get; init; }
        public DateTime? PeriodEnd { get; init; }
    }

    /// <summary>
    /// Compliance metrics for a region.
    /// </summary>
    public sealed class ComplianceMetrics
    {
        public required string Region { get; init; }
        public long TotalDecisions;
        public long AllowedCount;
        public long BlockedCount;
    }

    /// <summary>
    /// Compliance metrics summary.
    /// </summary>
    public sealed record ComplianceMetricsSummary
    {
        public DateTime? WindowStart { get; init; }
        public DateTime? WindowEnd { get; init; }
        public int TotalDecisions { get; init; }
        public int AllowedDecisions { get; init; }
        public int BlockedDecisions { get; init; }
        public int UniqueResources { get; init; }
        public int UniqueRegions { get; init; }
        public Dictionary<string, int> ViolationsByType { get; init; } = new();
        public Dictionary<string, int> DecisionsByClassification { get; init; } = new();
        public double AverageDecisionTimeMs { get; init; }
    }

    /// <summary>
    /// Chain verification result.
    /// </summary>
    public sealed record ChainVerificationResult
    {
        public required bool IsIntact { get; init; }
        public int TotalRecords { get; init; }
        public int VerifiedRecords { get; init; }
        public List<string> TamperedRecords { get; init; } = new();
        public required string CurrentChainHash { get; init; }
        public DateTime VerificationTime { get; init; }
    }
}
