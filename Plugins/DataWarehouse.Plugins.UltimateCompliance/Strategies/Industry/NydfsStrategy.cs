using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// NY DFS 23 NYCRR Part 500 Cybersecurity Requirements compliance strategy.
    /// </summary>
    public sealed class NydfsStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "nydfs-500";
        public override string StrategyName => "NY DFS 23NYCRR500";
        public override string Framework => "NYDFS-500";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nydfs.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("CisoDesignated", out var cisoObj) || cisoObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NYDFS-500.04",
                    Description = "Chief Information Security Officer (CISO) not designated",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Designate qualified CISO to oversee cybersecurity program (500.04)",
                    RegulatoryReference = "23 NYCRR 500.04"
                });
            }

            if (!context.Attributes.TryGetValue("PenetrationTestingAnnual", out var penTestObj) || penTestObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NYDFS-500.05",
                    Description = "Annual penetration testing not conducted",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct annual penetration testing and biannual vulnerability assessments (500.05)",
                    RegulatoryReference = "23 NYCRR 500.05"
                });
            }

            // Key uses camelCase without space to avoid false-positive violations when callers use "MultiFactorAuthentication"
            if (!context.Attributes.TryGetValue("MultiFactorAuthentication", out var mfaObj) || mfaObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NYDFS-500.12",
                    Description = "Multi-factor authentication not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement MFA for accessing internal networks from external networks (500.12)",
                    RegulatoryReference = "23 NYCRR 500.12"
                });
            }

            if (!context.Attributes.TryGetValue("EncryptionInTransit", out var encryptObj) || encryptObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NYDFS-500.15",
                    Description = "Encryption not used for nonpublic information in transit",
                    Severity = ViolationSeverity.High,
                    Remediation = "Encrypt nonpublic information in transit over external networks (500.15)",
                    RegulatoryReference = "23 NYCRR 500.15"
                });
            }

            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var irpObj) || irpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NYDFS-500.16",
                    Description = "Incident response plan not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Develop and maintain incident response plan (500.16)",
                    RegulatoryReference = "23 NYCRR 500.16"
                });
            }

            recommendations.Add("Submit annual certification of compliance to NY DFS");

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
                Recommendations = recommendations
            });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nydfs.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nydfs.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
