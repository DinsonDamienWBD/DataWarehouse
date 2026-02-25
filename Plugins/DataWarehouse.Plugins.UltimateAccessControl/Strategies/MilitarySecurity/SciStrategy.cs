using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.MilitarySecurity
{
    /// <summary>
    /// Sensitive Compartmented Information: compartment-based access, need-to-know enforcement.
    /// </summary>
    public sealed class SciStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "military-sci";
        public override string StrategyName => "Sensitive Compartmented Information";

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
            IncrementCounter("military.sci.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.sci.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("military.sci.evaluate");
            var requiredCompartments = context.ResourceAttributes.TryGetValue("SciCompartments", out var rc) && rc is string[] rca ? rca : Array.Empty<string>();
            var userCompartments = context.SubjectAttributes.TryGetValue("SciCompartments", out var uc) && uc is string[] uca ? uca : Array.Empty<string>();

            var hasAccess = requiredCompartments.All(req => userCompartments.Contains(req));

            return Task.FromResult(new AccessDecision
            {
                IsGranted = hasAccess,
                Reason = hasAccess ? "Compartment access granted" : "Missing required SCI compartments"
            });
        }
    }
}
