using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Services
{
    /// <summary>
    /// Multi-framework compliance report generation service with evidence collection.
    /// Supports SOC2, HIPAA, FedRAMP, GDPR frameworks. Iterates registered IComplianceStrategy
    /// instances to collect evidence, analyzes gaps, and maps controls to evidence items.
    /// Cross-framework evidence reuse: one evidence item maps to multiple controls across frameworks.
    /// Publishes reports via message bus topic "compliance.report.publish".
    /// </summary>
    public sealed class ComplianceReportService
    {
        private readonly BoundedDictionary<string, FrameworkComplianceReport> _reportCache = new BoundedDictionary<string, FrameworkComplianceReport>(1000);
        private readonly IReadOnlyCollection<IComplianceStrategy> _strategies;
        private readonly IMessageBus? _messageBus;
        private readonly string _pluginId;

        /// <summary>
        /// Framework-specific control definitions mapping framework to required control IDs.
        /// </summary>
        private static readonly Dictionary<string, List<ControlDefinition>> FrameworkControls = new()
        {
            ["SOC2"] = new List<ControlDefinition>
            {
                new("CC1.1", "Control Environment", "Security", "Integrity and ethical values"),
                new("CC2.1", "Communication", "Security", "Internal communication of objectives"),
                new("CC3.1", "Risk Assessment", "Security", "Identification and assessment of risks"),
                new("CC5.1", "Control Activities", "Security", "Selection and development of controls"),
                new("CC6.1", "Logical Access", "Security", "Logical and physical access controls"),
                new("CC6.2", "System Access", "Security", "Registration and authorization"),
                new("CC6.3", "Access Removal", "Security", "Timely removal of access"),
                new("CC7.1", "System Monitoring", "Availability", "Detection of unauthorized changes"),
                new("CC7.2", "Incident Response", "Availability", "Monitoring for security incidents"),
                new("CC8.1", "Change Management", "Processing Integrity", "Authorization of changes"),
                new("CC9.1", "Risk Mitigation", "Confidentiality", "Risk mitigation activities"),
                new("A1.1", "Availability Planning", "Availability", "Capacity planning and management"),
                new("C1.1", "Confidentiality Identification", "Confidentiality", "Identification of confidential information"),
                new("PI1.1", "Processing Accuracy", "Processing Integrity", "Completeness and accuracy"),
                new("P1.1", "Privacy Notice", "Privacy", "Privacy notice and consent")
            },
            ["HIPAA"] = new List<ControlDefinition>
            {
                new("164.308(a)(1)", "Security Management", "Administrative", "Security management process"),
                new("164.308(a)(3)", "Workforce Security", "Administrative", "Workforce authorization procedures"),
                new("164.308(a)(4)", "Information Access", "Administrative", "Access management controls"),
                new("164.308(a)(5)", "Security Awareness", "Administrative", "Security awareness and training"),
                new("164.308(a)(6)", "Security Incidents", "Administrative", "Security incident procedures"),
                new("164.308(a)(7)", "Contingency Plan", "Administrative", "Contingency planning"),
                new("164.310(a)(1)", "Facility Access", "Physical", "Physical access controls"),
                new("164.310(b)", "Workstation Use", "Physical", "Workstation use policies"),
                new("164.310(d)(1)", "Device Controls", "Physical", "Device and media controls"),
                new("164.312(a)(1)", "Access Control", "Technical", "Access control mechanisms"),
                new("164.312(b)", "Audit Controls", "Technical", "Audit logging and monitoring"),
                new("164.312(c)(1)", "Integrity Controls", "Technical", "Data integrity mechanisms"),
                new("164.312(d)", "Authentication", "Technical", "Person or entity authentication"),
                new("164.312(e)(1)", "Transmission Security", "Technical", "Encryption in transit")
            },
            ["FedRAMP"] = new List<ControlDefinition>
            {
                new("AC-1", "Access Control Policy", "Access Control", "Policy and procedures"),
                new("AC-2", "Account Management", "Access Control", "Account lifecycle management"),
                new("AC-3", "Access Enforcement", "Access Control", "Approved authorizations enforcement"),
                new("AU-1", "Audit Policy", "Audit", "Audit and accountability policy"),
                new("AU-2", "Audit Events", "Audit", "Auditable event definitions"),
                new("AU-6", "Audit Review", "Audit", "Audit log review and analysis"),
                new("CA-1", "Assessment Policy", "Assessment", "Security assessment policy"),
                new("CM-1", "Configuration Policy", "Configuration", "Configuration management policy"),
                new("CP-1", "Contingency Policy", "Contingency", "Contingency planning policy"),
                new("IA-1", "Identification Policy", "Identification", "Identification and authentication policy"),
                new("IR-1", "Incident Response Policy", "Incident Response", "Incident response policy"),
                new("RA-1", "Risk Assessment Policy", "Risk Assessment", "Risk assessment policy"),
                new("SC-1", "System Protection Policy", "System Protection", "System and communications protection"),
                new("SC-12", "Key Management", "System Protection", "Cryptographic key management"),
                new("SI-1", "System Integrity Policy", "System Integrity", "System and information integrity")
            },
            ["GDPR"] = new List<ControlDefinition>
            {
                new("Art.5", "Processing Principles", "Core", "Lawfulness, fairness, transparency"),
                new("Art.6", "Lawful Basis", "Core", "Legal basis for processing"),
                new("Art.7", "Consent", "Core", "Conditions for valid consent"),
                new("Art.13", "Transparency", "Data Subject Rights", "Information provided to data subjects"),
                new("Art.15", "Right of Access", "Data Subject Rights", "Data subject access requests"),
                new("Art.17", "Right to Erasure", "Data Subject Rights", "Right to be forgotten"),
                new("Art.20", "Data Portability", "Data Subject Rights", "Right to data portability"),
                new("Art.25", "Data Protection by Design", "Security", "Privacy by design and default"),
                new("Art.28", "Processor Agreements", "Third Party", "Data processor requirements"),
                new("Art.30", "Records of Processing", "Accountability", "Records of processing activities"),
                new("Art.32", "Security of Processing", "Security", "Appropriate technical measures"),
                new("Art.33", "Breach Notification", "Breach", "72-hour breach notification"),
                new("Art.35", "DPIA", "Risk", "Data protection impact assessment"),
                new("Art.44", "Transfer Principles", "International", "Cross-border transfer safeguards")
            }
        };

        public ComplianceReportService(
            IReadOnlyCollection<IComplianceStrategy> strategies,
            IMessageBus? messageBus,
            string pluginId)
        {
            _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
            _messageBus = messageBus;
            _pluginId = pluginId;
        }

        /// <summary>
        /// Generates a compliance report for the specified framework and time period.
        /// Collects evidence from all registered strategies, analyzes gaps, and maps controls.
        /// </summary>
        public async Task<FrameworkComplianceReport> GenerateReportAsync(
            string framework,
            ComplianceReportPeriod period,
            CancellationToken cancellationToken = default)
        {
            if (!FrameworkControls.ContainsKey(framework))
                throw new ArgumentException($"Unsupported framework: {framework}. Supported: {string.Join(", ", FrameworkControls.Keys)}");

            var evidence = await CollectEvidenceAsync(framework, period, cancellationToken);
            var controlMappings = await MapControlsAsync(framework, evidence, cancellationToken);
            var gaps = AnalyzeGaps(framework, controlMappings);

            var totalControls = FrameworkControls[framework].Count;
            var coveredControls = controlMappings.Count(m => m.EvidenceItems.Count > 0);
            var complianceScore = totalControls > 0 ? (double)coveredControls / totalControls * 100.0 : 0.0;

            var status = complianceScore >= 95.0 ? ComplianceStatus.Compliant
                       : complianceScore >= 70.0 ? ComplianceStatus.PartiallyCompliant
                       : ComplianceStatus.NonCompliant;

            var report = new FrameworkComplianceReport
            {
                ReportId = $"CR-{framework}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                Framework = framework,
                Period = period,
                GeneratedAtUtc = DateTime.UtcNow,
                Evidence = evidence,
                ControlMappings = controlMappings,
                Gaps = gaps,
                OverallStatus = status,
                ComplianceScore = complianceScore,
                TotalControls = totalControls,
                CoveredControls = coveredControls,
                Summary = BuildReportSummary(framework, complianceScore, gaps.Count, evidence.Count, period)
            };

            _reportCache[report.ReportId] = report;

            // Publish report via message bus
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("compliance.report.publish", new PluginMessage
                {
                    Type = "compliance.report.publish",
                    Source = _pluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["reportId"] = report.ReportId,
                        ["framework"] = report.Framework,
                        ["complianceScore"] = report.ComplianceScore,
                        ["status"] = report.OverallStatus.ToString(),
                        ["totalControls"] = report.TotalControls,
                        ["coveredControls"] = report.CoveredControls,
                        ["gapCount"] = report.Gaps.Count,
                        ["evidenceCount"] = report.Evidence.Count,
                        ["generatedAt"] = report.GeneratedAtUtc.ToString("O")
                    }
                }, cancellationToken);
            }

            return report;
        }

        /// <summary>
        /// Collects evidence from all registered IComplianceStrategy instances for the given framework.
        /// Each strategy's CheckComplianceAsync result is converted into evidence items.
        /// Cross-framework reuse: evidence from one strategy may apply to multiple frameworks.
        /// </summary>
        public async Task<List<EvidenceItem>> CollectEvidenceAsync(
            string framework,
            ComplianceReportPeriod period,
            CancellationToken cancellationToken = default)
        {
            var evidence = new List<EvidenceItem>();

            foreach (var strategy in _strategies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var context = new ComplianceContext
                    {
                        OperationType = "evidence-collection",
                        DataClassification = "compliance-audit",
                        Attributes = new Dictionary<string, object>
                        {
                            ["Framework"] = framework,
                            ["PeriodStart"] = period.Start.ToString("O"),
                            ["PeriodEnd"] = period.End.ToString("O"),
                            ["ComplianceReportingEnabled"] = true
                        }
                    };

                    var result = await strategy.CheckComplianceAsync(context, cancellationToken);
                    var stats = strategy.GetStatistics();

                    var item = new EvidenceItem
                    {
                        EvidenceId = $"EV-{strategy.StrategyId}-{Guid.NewGuid().ToString("N")[..8]}",
                        StrategyId = strategy.StrategyId,
                        StrategyName = strategy.StrategyName,
                        SourceFramework = strategy.Framework,
                        CollectedAtUtc = DateTime.UtcNow,
                        IsCompliant = result.IsCompliant,
                        Status = result.Status,
                        ViolationCount = result.Violations.Count,
                        Violations = result.Violations.ToList(),
                        Recommendations = result.Recommendations.ToList(),
                        TotalChecksPerformed = stats.TotalChecks,
                        ComplianceRate = stats.TotalChecks > 0
                            ? (double)stats.CompliantCount / stats.TotalChecks * 100.0
                            : 100.0,
                        ApplicableFrameworks = DetermineApplicableFrameworks(strategy.Framework)
                    };

                    evidence.Add(item);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    evidence.Add(new EvidenceItem
                    {
                        EvidenceId = $"EV-ERR-{strategy.StrategyId}-{Guid.NewGuid().ToString("N")[..8]}",
                        StrategyId = strategy.StrategyId,
                        StrategyName = strategy.StrategyName,
                        SourceFramework = strategy.Framework,
                        CollectedAtUtc = DateTime.UtcNow,
                        IsCompliant = false,
                        Status = ComplianceStatus.RequiresReview,
                        ViolationCount = 1,
                        Violations = new List<ComplianceViolation>
                        {
                            new()
                            {
                                Code = "EV-COLLECTION-ERROR",
                                Description = $"Evidence collection failed for {strategy.StrategyName}: {ex.Message}",
                                Severity = ViolationSeverity.Medium
                            }
                        },
                        Recommendations = new List<string> { $"Investigate evidence collection failure for {strategy.StrategyName}" },
                        TotalChecksPerformed = 0,
                        ComplianceRate = 0,
                        ApplicableFrameworks = new List<string> { strategy.Framework }
                    });
                }
            }

            return evidence;
        }

        /// <summary>
        /// Maps framework controls to collected evidence items. One evidence item can map to
        /// multiple controls across different frameworks (cross-framework reuse).
        /// </summary>
        public Task<List<ControlMapping>> MapControlsAsync(
            string framework,
            List<EvidenceItem> evidence,
            CancellationToken cancellationToken = default)
        {
            if (!FrameworkControls.TryGetValue(framework, out var controls))
                return Task.FromResult(new List<ControlMapping>());

            var mappings = new List<ControlMapping>();

            foreach (var control in controls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var matchingEvidence = evidence
                    .Where(e => IsEvidenceApplicableToControl(e, control, framework))
                    .ToList();

                var controlStatus = matchingEvidence.Count == 0
                    ? ControlStatus.NotAssessed
                    : matchingEvidence.All(e => e.IsCompliant)
                        ? ControlStatus.Satisfied
                        : matchingEvidence.Any(e => e.IsCompliant)
                            ? ControlStatus.PartiallySatisfied
                            : ControlStatus.NotSatisfied;

                mappings.Add(new ControlMapping
                {
                    ControlId = control.Id,
                    ControlName = control.Name,
                    Category = control.Category,
                    Description = control.Description,
                    Framework = framework,
                    Status = controlStatus,
                    EvidenceItems = matchingEvidence.Select(e => e.EvidenceId).ToList(),
                    EvidenceCount = matchingEvidence.Count,
                    ComplianceRate = matchingEvidence.Count > 0
                        ? matchingEvidence.Average(e => e.ComplianceRate)
                        : 0.0
                });
            }

            return Task.FromResult(mappings);
        }

        /// <summary>
        /// Analyzes gaps between required controls and available evidence.
        /// Identifies missing controls and provides remediation guidance.
        /// </summary>
        public List<ComplianceGap> AnalyzeGaps(string framework, List<ControlMapping> controlMappings)
        {
            var gaps = new List<ComplianceGap>();

            foreach (var mapping in controlMappings)
            {
                if (mapping.Status == ControlStatus.NotAssessed || mapping.Status == ControlStatus.NotSatisfied)
                {
                    var severity = mapping.Status == ControlStatus.NotAssessed
                        ? GapSeverity.High
                        : GapSeverity.Critical;

                    gaps.Add(new ComplianceGap
                    {
                        ControlId = mapping.ControlId,
                        ControlName = mapping.ControlName,
                        Framework = framework,
                        Category = mapping.Category,
                        Severity = severity,
                        Description = mapping.Status == ControlStatus.NotAssessed
                            ? $"No evidence collected for control {mapping.ControlId}: {mapping.ControlName}"
                            : $"Control {mapping.ControlId} ({mapping.ControlName}) failed compliance checks",
                        Remediation = GenerateRemediationGuidance(framework, mapping),
                        DetectedAtUtc = DateTime.UtcNow
                    });
                }
                else if (mapping.Status == ControlStatus.PartiallySatisfied)
                {
                    gaps.Add(new ComplianceGap
                    {
                        ControlId = mapping.ControlId,
                        ControlName = mapping.ControlName,
                        Framework = framework,
                        Category = mapping.Category,
                        Severity = GapSeverity.Medium,
                        Description = $"Control {mapping.ControlId} ({mapping.ControlName}) partially satisfied - {mapping.ComplianceRate:F1}% compliance rate",
                        Remediation = GenerateRemediationGuidance(framework, mapping),
                        DetectedAtUtc = DateTime.UtcNow
                    });
                }
            }

            return gaps.OrderByDescending(g => g.Severity).ToList();
        }

        /// <summary>
        /// Returns a cached report by ID if available.
        /// </summary>
        public FrameworkComplianceReport? GetCachedReport(string reportId)
        {
            return _reportCache.TryGetValue(reportId, out var report) ? report : null;
        }

        private bool IsEvidenceApplicableToControl(EvidenceItem evidence, ControlDefinition control, string framework)
        {
            // Evidence is applicable if its source framework matches or if the evidence
            // explicitly lists this framework in its applicable frameworks (cross-framework reuse)
            if (!evidence.ApplicableFrameworks.Contains(framework) &&
                !evidence.SourceFramework.Equals(framework, StringComparison.OrdinalIgnoreCase))
                return false;

            // Category-based matching: security strategies map to security controls, etc.
            var categoryMapping = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Security"] = new(StringComparer.OrdinalIgnoreCase) { "security", "access", "encryption", "authentication", "authorization", "tamper", "integrity", "firewall", "ids", "ips", "waf" },
                ["Administrative"] = new(StringComparer.OrdinalIgnoreCase) { "policy", "management", "workforce", "awareness", "training", "incident", "contingency", "compliance" },
                ["Physical"] = new(StringComparer.OrdinalIgnoreCase) { "facility", "workstation", "device", "media" },
                ["Technical"] = new(StringComparer.OrdinalIgnoreCase) { "access-control", "audit", "integrity", "authentication", "transmission", "encryption" },
                ["Access Control"] = new(StringComparer.OrdinalIgnoreCase) { "access", "rbac", "abac", "mac", "dac", "acl", "identity", "authentication" },
                ["Audit"] = new(StringComparer.OrdinalIgnoreCase) { "audit", "logging", "monitoring", "trail" },
                ["Core"] = new(StringComparer.OrdinalIgnoreCase) { "gdpr", "privacy", "consent", "data-protection", "sovereignty" },
                ["Data Subject Rights"] = new(StringComparer.OrdinalIgnoreCase) { "access", "erasure", "portability", "transparency", "consent" },
                ["Availability"] = new(StringComparer.OrdinalIgnoreCase) { "availability", "monitoring", "health", "incident", "recovery" },
                ["Confidentiality"] = new(StringComparer.OrdinalIgnoreCase) { "encryption", "confidentiality", "dlp", "classification", "masking" },
                ["Processing Integrity"] = new(StringComparer.OrdinalIgnoreCase) { "integrity", "validation", "accuracy", "change-management" },
                ["Privacy"] = new(StringComparer.OrdinalIgnoreCase) { "privacy", "consent", "gdpr", "pii", "personal-data" },
                ["System Protection"] = new(StringComparer.OrdinalIgnoreCase) { "encryption", "key-management", "tls", "communication", "protection" },
                ["System Integrity"] = new(StringComparer.OrdinalIgnoreCase) { "integrity", "monitoring", "patch", "vulnerability" },
                ["Risk Assessment"] = new(StringComparer.OrdinalIgnoreCase) { "risk", "assessment", "vulnerability", "threat" },
                ["Incident Response"] = new(StringComparer.OrdinalIgnoreCase) { "incident", "response", "forensic", "alert" },
                ["Configuration"] = new(StringComparer.OrdinalIgnoreCase) { "configuration", "baseline", "change-management" },
                ["Contingency"] = new(StringComparer.OrdinalIgnoreCase) { "contingency", "backup", "disaster-recovery", "continuity" },
                ["Identification"] = new(StringComparer.OrdinalIgnoreCase) { "identification", "authentication", "identity", "mfa" },
                ["Assessment"] = new(StringComparer.OrdinalIgnoreCase) { "assessment", "testing", "scanning", "audit" },
                ["Breach"] = new(StringComparer.OrdinalIgnoreCase) { "breach", "notification", "incident", "detection" },
                ["Risk"] = new(StringComparer.OrdinalIgnoreCase) { "risk", "impact", "assessment", "dpia" },
                ["Third Party"] = new(StringComparer.OrdinalIgnoreCase) { "processor", "vendor", "third-party", "contract" },
                ["Accountability"] = new(StringComparer.OrdinalIgnoreCase) { "records", "processing", "documentation", "compliance" },
                ["International"] = new(StringComparer.OrdinalIgnoreCase) { "transfer", "cross-border", "sovereignty", "geofencing" }
            };

            if (categoryMapping.TryGetValue(control.Category, out var keywords))
            {
                var strategyLower = evidence.StrategyId.ToLowerInvariant();
                var frameworkLower = evidence.SourceFramework.ToLowerInvariant();
                return keywords.Any(k => strategyLower.Contains(k) || frameworkLower.Contains(k));
            }

            return false;
        }

        private List<string> DetermineApplicableFrameworks(string sourceFramework)
        {
            // Cross-framework evidence reuse mapping
            var frameworks = new List<string> { sourceFramework };

            var crossFrameworkMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["GDPR"] = new() { "SOC2", "HIPAA" },
                ["HIPAA"] = new() { "SOC2", "FedRAMP" },
                ["SOX"] = new() { "SOC2" },
                ["PCI-DSS"] = new() { "SOC2", "FedRAMP" },
                ["FedRAMP"] = new() { "SOC2", "HIPAA" },
                ["SOC2"] = new() { "HIPAA", "FedRAMP", "GDPR" },
                ["Audit-Based"] = new() { "SOC2", "HIPAA", "FedRAMP", "GDPR" },
                ["Multi-Framework Reporting"] = new() { "SOC2", "HIPAA", "FedRAMP", "GDPR" },
                ["Access Control"] = new() { "SOC2", "HIPAA", "FedRAMP", "GDPR" },
                ["Encryption"] = new() { "SOC2", "HIPAA", "FedRAMP", "GDPR" },
                ["Data Protection"] = new() { "GDPR", "HIPAA", "SOC2" }
            };

            if (crossFrameworkMap.TryGetValue(sourceFramework, out var additional))
            {
                frameworks.AddRange(additional);
            }

            return frameworks.Distinct().ToList();
        }

        private string GenerateRemediationGuidance(string framework, ControlMapping mapping)
        {
            var sb = new StringBuilder();
            sb.Append($"[{framework}] Control {mapping.ControlId} ({mapping.ControlName}): ");

            switch (mapping.Status)
            {
                case ControlStatus.NotAssessed:
                    sb.Append("Implement evidence collection procedures. Deploy monitoring and audit logging to capture compliance data for this control.");
                    break;
                case ControlStatus.NotSatisfied:
                    sb.Append("Review and remediate failing controls. Investigate root causes of non-compliance and implement corrective actions.");
                    break;
                case ControlStatus.PartiallySatisfied:
                    sb.Append($"Improve compliance rate from {mapping.ComplianceRate:F1}% to 95%+. Address specific violations identified in evidence.");
                    break;
            }

            return sb.ToString();
        }

        private string BuildReportSummary(string framework, double score, int gapCount, int evidenceCount, ComplianceReportPeriod period)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Compliance Report: {framework}");
            sb.AppendLine($"Period: {period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd}");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Compliance Score: {score:F1}%");
            sb.AppendLine($"Evidence Items Collected: {evidenceCount}");
            sb.AppendLine($"Compliance Gaps Identified: {gapCount}");

            if (score >= 95.0)
                sb.AppendLine("Status: COMPLIANT - All required controls are satisfied.");
            else if (score >= 70.0)
                sb.AppendLine("Status: PARTIALLY COMPLIANT - Some controls require remediation.");
            else
                sb.AppendLine("Status: NON-COMPLIANT - Significant remediation required.");

            return sb.ToString();
        }
    }

    #region Report Types

    /// <summary>
    /// Date range for compliance report periods.
    /// </summary>
    public record ComplianceReportPeriod(DateTime Start, DateTime End);

    /// <summary>
    /// Complete compliance report for a single framework.
    /// </summary>
    public sealed class FrameworkComplianceReport
    {
        public required string ReportId { get; init; }
        public required string Framework { get; init; }
        public required ComplianceReportPeriod Period { get; init; }
        public required DateTime GeneratedAtUtc { get; init; }
        public required List<EvidenceItem> Evidence { get; init; }
        public required List<ControlMapping> ControlMappings { get; init; }
        public required List<ComplianceGap> Gaps { get; init; }
        public required ComplianceStatus OverallStatus { get; init; }
        public required double ComplianceScore { get; init; }
        public required int TotalControls { get; init; }
        public required int CoveredControls { get; init; }
        public required string Summary { get; init; }
    }

    /// <summary>
    /// Evidence collected from a compliance strategy.
    /// </summary>
    public sealed class EvidenceItem
    {
        public required string EvidenceId { get; init; }
        public required string StrategyId { get; init; }
        public required string StrategyName { get; init; }
        public required string SourceFramework { get; init; }
        public required DateTime CollectedAtUtc { get; init; }
        public required bool IsCompliant { get; init; }
        public required ComplianceStatus Status { get; init; }
        public required int ViolationCount { get; init; }
        public required List<ComplianceViolation> Violations { get; init; }
        public required List<string> Recommendations { get; init; }
        public required long TotalChecksPerformed { get; init; }
        public required double ComplianceRate { get; init; }
        public required List<string> ApplicableFrameworks { get; init; }
    }

    /// <summary>
    /// Mapping of a framework control to evidence items.
    /// </summary>
    public sealed class ControlMapping
    {
        public required string ControlId { get; init; }
        public required string ControlName { get; init; }
        public required string Category { get; init; }
        public required string Description { get; init; }
        public required string Framework { get; init; }
        public required ControlStatus Status { get; init; }
        public required List<string> EvidenceItems { get; init; }
        public required int EvidenceCount { get; init; }
        public required double ComplianceRate { get; init; }
    }

    /// <summary>
    /// Compliance gap identified during analysis.
    /// </summary>
    public sealed class ComplianceGap
    {
        public required string ControlId { get; init; }
        public required string ControlName { get; init; }
        public required string Framework { get; init; }
        public required string Category { get; init; }
        public required GapSeverity Severity { get; init; }
        public required string Description { get; init; }
        public required string Remediation { get; init; }
        public required DateTime DetectedAtUtc { get; init; }
    }

    /// <summary>
    /// Framework control definition.
    /// </summary>
    public sealed record ControlDefinition(string Id, string Name, string Category, string Description);

    /// <summary>
    /// Status of a control mapping.
    /// </summary>
    public enum ControlStatus
    {
        NotAssessed,
        Satisfied,
        PartiallySatisfied,
        NotSatisfied
    }

    /// <summary>
    /// Severity of a compliance gap.
    /// </summary>
    public enum GapSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    #endregion
}
