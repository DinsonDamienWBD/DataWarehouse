using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.MilitarySecurity
{
    /// <summary>
    /// ITAR compliance: export controls, foreign person restrictions.
    /// </summary>
    public sealed class ItarStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "military-itar";
        public override string StrategyName => "ITAR Export Control";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var isItarControlled = context.ResourceAttributes.TryGetValue("ItarControlled", out var itar) && itar is bool i && i;
            var isUsPerson = context.SubjectAttributes.TryGetValue("UsPersonStatus", out var us) && us is bool u && u;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !isItarControlled || isUsPerson,
                Reason = isItarControlled && !isUsPerson ? "ITAR: US Person required" : "Access granted"
            });
        }
    }
}
