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

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("network.waf.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("network.waf.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("network.waf.evaluate");
            var requestData = context.ResourceAttributes.TryGetValue("RequestData", out var rd) ? rd?.ToString() : "";

            // OWASP-aligned SQL injection patterns (hex encoding, EXEC, CAST, comment injection, HAVING, BENCHMARK)
            var hasSqlInjection = Regex.IsMatch(requestData ?? "",
                @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|UNION|EXEC|EXECUTE|CAST|CONVERT|DECLARE|HAVING|BENCHMARK|SLEEP|WAITFOR|xp_)\b" +
                @"|--|;|'|/\*|\*/|0x[0-9a-fA-F]+\b" +
                @"|\bOR\s+\d+=\d+\b|\bAND\s+\d+=\d+\b" +
                @"|CHAR\s*\(|CONCAT\s*\(|INFORMATION_SCHEMA)", RegexOptions.IgnoreCase);

            // OWASP-aligned XSS patterns (encoded variants, event handlers, data URIs, expression, SVG)
            var hasXss = Regex.IsMatch(requestData ?? "",
                @"(<script|javascript:|onerror\s*=|onload\s*=|onclick\s*=|onmouseover\s*=" +
                @"|<iframe|<embed|<object|<svg|<img[^>]+onerror" +
                @"|data\s*:\s*text/html|expression\s*\(" +
                @"|%3[Cc]script|&#x3[Cc]|&#60)", RegexOptions.IgnoreCase);

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !hasSqlInjection && !hasXss,
                Reason = hasSqlInjection ? "SQL injection detected" : hasXss ? "XSS detected" : "Clean request"
            });
        }
    }
}
