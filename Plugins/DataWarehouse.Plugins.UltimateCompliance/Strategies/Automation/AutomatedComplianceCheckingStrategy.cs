using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Automation
{
    /// <summary>
    /// Automated compliance checking strategy that continuously validates operations
    /// against multiple compliance frameworks in real-time.
    /// Supports GDPR, HIPAA, SOX, PCI-DSS, and custom frameworks.
    /// </summary>
    public sealed class AutomatedComplianceCheckingStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, CheckResult> _checkCache = new BoundedDictionary<string, CheckResult>(1000);
        private readonly BoundedDictionary<string, List<ComplianceRule>> _rulesByFramework = new BoundedDictionary<string, List<ComplianceRule>>(1000);
        private Timer? _continuousCheckTimer;
        private bool _continuousCheckingEnabled;

        /// <inheritdoc/>
        public override string StrategyId => "automated-compliance-checking";

        /// <inheritdoc/>
        public override string StrategyName => "Automated Compliance Checking";

        /// <inheritdoc/>
        public override string Framework => "Multi-Framework";

        /// <inheritdoc/>
        public override async Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            await base.InitializeAsync(configuration, cancellationToken);

            // Load compliance rules from configuration
            if (configuration.TryGetValue("ComplianceRules", out var rulesObj) && rulesObj is List<ComplianceRule> rules)
            {
                foreach (var rule in rules)
                {
                    // AddOrUpdate is atomic; avoids TOCTOU between ContainsKey and indexer write
                    var ruleList = _rulesByFramework.GetOrAdd(rule.Framework, _ => new List<ComplianceRule>());
                    lock (ruleList) { ruleList.Add(rule); }
                }
            }

            // Enable continuous checking if configured
            if (configuration.TryGetValue("ContinuousChecking", out var continuousObj) && continuousObj is bool continuous && continuous)
            {
                _continuousCheckingEnabled = true;
                var intervalSeconds = configuration.TryGetValue("CheckIntervalSeconds", out var intervalObj) && intervalObj is int interval
                    ? interval : 300; // Default 5 minutes

                _continuousCheckTimer = new Timer(
                    async _ => { try { await RunContinuousChecksAsync(cancellationToken); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
                    null,
                    TimeSpan.FromSeconds(intervalSeconds),
                    TimeSpan.FromSeconds(intervalSeconds)
                );
            }

            // Load default rules if none provided
            if (_rulesByFramework.IsEmpty)
            {
                LoadDefaultRules();
            }

        }

        /// <inheritdoc/>
        protected override async Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("automated_compliance_checking.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();
            var checkedFrameworks = new HashSet<string>();

            // Check against all applicable frameworks based on context
            var applicableFrameworks = DetermineApplicableFrameworks(context);

            foreach (var framework in applicableFrameworks)
            {
                if (_rulesByFramework.TryGetValue(framework, out var rules))
                {
                    foreach (var rule in rules)
                    {
                        var ruleResult = await CheckRuleAsync(rule, context, cancellationToken);
                        if (!ruleResult.IsCompliant)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = rule.RuleId,
                                Description = $"{framework}: {ruleResult.ViolationMessage}",
                                Severity = MapSeverity(rule.Severity),
                                Remediation = ruleResult.Remediation,
                                RegulatoryReference = rule.RegulatoryReference,
                                AffectedResource = context.ResourceId
                            });
                        }

                        if (ruleResult.Recommendations != null)
                        {
                            recommendations.AddRange(ruleResult.Recommendations);
                        }
                    }
                    checkedFrameworks.Add(framework);
                }
            }

            // Cache result for continuous monitoring
            var checkId = $"{context.OperationType}:{context.ResourceId}:{DateTime.UtcNow.Ticks}";
            _checkCache[checkId] = new CheckResult
            {
                CheckId = checkId,
                Context = context,
                Violations = violations,
                Timestamp = DateTime.UtcNow
            };

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = string.Join(", ", checkedFrameworks),
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["CheckId"] = checkId,
                    ["FrameworksChecked"] = checkedFrameworks.ToList(),
                    ["TotalRulesEvaluated"] = checkedFrameworks.Sum(f => _rulesByFramework.TryGetValue(f, out var r) ? r.Count : 0),
                    ["ContinuousCheckingEnabled"] = _continuousCheckingEnabled
                }
            };
        }

        private List<string> DetermineApplicableFrameworks(ComplianceContext context)
        {
            var frameworks = new List<string>();

            // GDPR applies to personal data in EU or about EU residents
            if (context.DataClassification.Contains("personal", StringComparison.OrdinalIgnoreCase) ||
                context.DataSubjectCategories.Any() ||
                context.SourceLocation?.Contains("EU", StringComparison.OrdinalIgnoreCase) == true ||
                context.DestinationLocation?.Contains("EU", StringComparison.OrdinalIgnoreCase) == true)
            {
                frameworks.Add("GDPR");
            }

            // HIPAA applies to healthcare data
            if (context.DataClassification.Contains("health", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("medical", StringComparison.OrdinalIgnoreCase) ||
                context.DataSubjectCategories.Any(c => c.Contains("health", StringComparison.OrdinalIgnoreCase)))
            {
                frameworks.Add("HIPAA");
            }

            // SOX applies to financial data for public companies
            if (context.DataClassification.Contains("financial", StringComparison.OrdinalIgnoreCase) ||
                context.Attributes.TryGetValue("IsPublicCompany", out var isPub) && isPub is true)
            {
                frameworks.Add("SOX");
            }

            // PCI-DSS applies to payment card data
            if (context.DataClassification.Contains("payment", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Contains("card", StringComparison.OrdinalIgnoreCase))
            {
                frameworks.Add("PCI-DSS");
            }

            // If no specific framework identified, check all
            if (frameworks.Count == 0)
            {
                frameworks.AddRange(_rulesByFramework.Keys);
            }

            return frameworks;
        }

        private async Task<RuleCheckResult> CheckRuleAsync(ComplianceRule rule, ComplianceContext context, CancellationToken cancellationToken)
        {
            // Evaluate rule condition
            var isCompliant = rule.Condition?.Invoke(context) ?? true;

            if (!isCompliant)
            {
                return new RuleCheckResult
                {
                    IsCompliant = false,
                    ViolationMessage = rule.ViolationMessage,
                    Remediation = rule.RemediationAction,
                    Recommendations = rule.Recommendations
                };
            }

            return new RuleCheckResult { IsCompliant = true };
        }

        private async Task RunContinuousChecksAsync(CancellationToken cancellationToken)
        {
            // Re-check recent operations for compliance drift
            var recentChecks = _checkCache.Values
                .Where(c => c.Timestamp > DateTime.UtcNow.AddMinutes(-30))
                .ToList();

            foreach (var check in recentChecks)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Re-run compliance check to detect any drift
                await CheckComplianceAsync(check.Context, cancellationToken);
            }

            // Clean up old cache entries
            var cutoffTime = DateTime.UtcNow.AddHours(-24);
            var oldEntries = _checkCache.Where(kvp => kvp.Value.Timestamp < cutoffTime).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldEntries)
            {
                _checkCache.TryRemove(key, out _);
            }
        }

        private void LoadDefaultRules()
        {
            // GDPR default rules
            _rulesByFramework["GDPR"] = new List<ComplianceRule>
            {
                new ComplianceRule
                {
                    RuleId = "GDPR-AUTO-001",
                    Framework = "GDPR",
                    ViolationMessage = "Personal data processing without lawful basis",
                    Severity = "High",
                    RegulatoryReference = "GDPR Article 6",
                    RemediationAction = "Establish and document lawful basis for processing",
                    Condition = ctx => ctx.Attributes.ContainsKey("LawfulBasis")
                },
                new ComplianceRule
                {
                    RuleId = "GDPR-AUTO-002",
                    Framework = "GDPR",
                    ViolationMessage = "Cross-border transfer without safeguards",
                    Severity = "Critical",
                    RegulatoryReference = "GDPR Chapter V",
                    RemediationAction = "Implement Standard Contractual Clauses or other transfer mechanism",
                    Condition = ctx => string.IsNullOrEmpty(ctx.SourceLocation) ||
                                      string.IsNullOrEmpty(ctx.DestinationLocation) ||
                                      ctx.SourceLocation == ctx.DestinationLocation ||
                                      ctx.Attributes.ContainsKey("TransferMechanism")
                }
            };

            // HIPAA default rules
            _rulesByFramework["HIPAA"] = new List<ComplianceRule>
            {
                new ComplianceRule
                {
                    RuleId = "HIPAA-AUTO-001",
                    Framework = "HIPAA",
                    ViolationMessage = "PHI access without authorization",
                    Severity = "Critical",
                    RegulatoryReference = "HIPAA Privacy Rule 164.502",
                    RemediationAction = "Verify authorization before PHI access",
                    Condition = ctx => ctx.Attributes.TryGetValue("Authorization", out var auth) && auth is bool authorized && authorized
                },
                new ComplianceRule
                {
                    RuleId = "HIPAA-AUTO-002",
                    Framework = "HIPAA",
                    ViolationMessage = "PHI transmitted without encryption",
                    Severity = "High",
                    RegulatoryReference = "HIPAA Security Rule 164.312(e)(1)",
                    RemediationAction = "Enable encryption for PHI in transit",
                    Condition = ctx => !ctx.OperationType.Contains("transmit", StringComparison.OrdinalIgnoreCase) ||
                                      (ctx.Attributes.TryGetValue("Encrypted", out var enc) && enc is bool encrypted && encrypted)
                }
            };

            // SOX default rules
            _rulesByFramework["SOX"] = new List<ComplianceRule>
            {
                new ComplianceRule
                {
                    RuleId = "SOX-AUTO-001",
                    Framework = "SOX",
                    ViolationMessage = "Financial data modification without audit trail",
                    Severity = "Critical",
                    RegulatoryReference = "SOX Section 404",
                    RemediationAction = "Enable comprehensive audit logging",
                    Condition = ctx => !ctx.DataClassification.Contains("financial", StringComparison.OrdinalIgnoreCase) ||
                                      (ctx.Attributes.TryGetValue("AuditEnabled", out var audit) && audit is bool auditEnabled && auditEnabled)
                }
            };

            // PCI-DSS default rules
            _rulesByFramework["PCI-DSS"] = new List<ComplianceRule>
            {
                new ComplianceRule
                {
                    RuleId = "PCI-AUTO-001",
                    Framework = "PCI-DSS",
                    ViolationMessage = "Cardholder data stored without encryption",
                    Severity = "Critical",
                    RegulatoryReference = "PCI-DSS Requirement 3.4",
                    RemediationAction = "Encrypt cardholder data at rest",
                    Condition = ctx => !ctx.DataClassification.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                                      (ctx.Attributes.TryGetValue("EncryptedAtRest", out var enc) && enc is bool encrypted && encrypted)
                }
            };
        }

        private ViolationSeverity MapSeverity(string severity)
        {
            return severity?.ToLowerInvariant() switch
            {
                "critical" => ViolationSeverity.Critical,
                "high" => ViolationSeverity.High,
                "medium" => ViolationSeverity.Medium,
                "low" => ViolationSeverity.Low,
                _ => ViolationSeverity.Medium
            };
        }

        private sealed class CheckResult
        {
            public required string CheckId { get; init; }
            public required ComplianceContext Context { get; init; }
            public required List<ComplianceViolation> Violations { get; init; }
            public required DateTime Timestamp { get; init; }
        }

        private sealed class RuleCheckResult
        {
            public required bool IsCompliant { get; init; }
            public string? ViolationMessage { get; init; }
            public string? Remediation { get; init; }
            public List<string>? Recommendations { get; init; }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("automated_compliance_checking.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("automated_compliance_checking.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Represents a compliance rule for automated checking.
    /// </summary>
    public sealed class ComplianceRule
    {
        public required string RuleId { get; init; }
        public required string Framework { get; init; }
        public required string ViolationMessage { get; init; }
        public required string Severity { get; init; }
        public string? RegulatoryReference { get; init; }
        public string? RemediationAction { get; init; }
        public List<string>? Recommendations { get; init; }
        public Func<ComplianceContext, bool>? Condition { get; init; }
    }
}
