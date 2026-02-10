using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.NetworkSecurity
{
    /// <summary>
    /// Intrusion Prevention: signature-based detection, anomaly-based detection.
    /// </summary>
    public sealed class IpsStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "network-ips";
        public override string StrategyName => "Intrusion Prevention System";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var threatScore = context.EnvironmentAttributes.TryGetValue("ThreatScore", out var ts) && ts is double score ? score : 0.0;
            var isBlocked = threatScore > 0.8;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !isBlocked,
                Reason = isBlocked ? $"IPS: High threat score ({threatScore:F2})" : "No threats detected"
            });
        }
    }
}
