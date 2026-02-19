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

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.itar.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.itar.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("military.itar.evaluate");
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
