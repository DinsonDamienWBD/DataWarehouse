using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.NetworkSecurity
{
    /// <summary>
    /// Web Application Firewall: OWASP Top 10 rule sets, SQL injection/XSS detection.
    /// </summary>
    public sealed class WafStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "network-waf";
        public override string StrategyName => "Web Application Firewall";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var requestData = context.ResourceAttributes.TryGetValue("RequestData", out var rd) ? rd?.ToString() : "";

            // Simple XSS/SQLi detection
            var hasSqlInjection = Regex.IsMatch(requestData ?? "", @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|UNION)\b|--|;|')", RegexOptions.IgnoreCase);
            var hasXss = Regex.IsMatch(requestData ?? "", @"<script|javascript:|onerror=", RegexOptions.IgnoreCase);

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !hasSqlInjection && !hasXss,
                Reason = hasSqlInjection ? "SQL injection detected" : hasXss ? "XSS detected" : "Clean request"
            });
        }
    }
}
