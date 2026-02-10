using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.MilitarySecurity
{
    /// <summary>
    /// Cross-Domain Solutions: controlled data transfer between classification levels.
    /// </summary>
    public sealed class CdsStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "military-cds";
        public override string StrategyName => "Cross-Domain Solutions";

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
            var isTransfer = context.Action.Equals("transfer", StringComparison.OrdinalIgnoreCase);
            var hasApproval = context.SubjectAttributes.ContainsKey("CdsApproval");

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !isTransfer || hasApproval,
                Reason = isTransfer && !hasApproval ? "CDS approval required" : "Access granted"
            });
        }
    }
}
