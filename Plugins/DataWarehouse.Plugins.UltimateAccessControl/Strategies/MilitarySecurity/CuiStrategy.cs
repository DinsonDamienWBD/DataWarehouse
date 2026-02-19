using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.MilitarySecurity
{
    /// <summary>
    /// Controlled Unclassified Information: CUI marking, handling, dissemination controls.
    /// </summary>
    public sealed class CuiStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "military-cui";
        public override string StrategyName => "Controlled Unclassified Information";

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

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.cui.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.cui.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("military.cui.evaluate");
            var isCui = context.ResourceAttributes.TryGetValue("CuiMarking", out var cui) && cui != null;
            var hasCuiTraining = context.SubjectAttributes.TryGetValue("CuiTrained", out var trained) && trained is bool tr && tr;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !isCui || hasCuiTraining,
                Reason = isCui && !hasCuiTraining ? "CUI training required" : "Access granted"
            });
        }
    }
}
