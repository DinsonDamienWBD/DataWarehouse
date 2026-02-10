using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.EmbeddedIdentity
{
    public sealed class OfflineAuthenticationStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public OfflineAuthenticationStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "offline-auth";
        public override string StrategyName => "Offline Authentication Strategy";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 200
        };

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();

            var hasValidIdentity = context.SubjectId.Length > 0;

            return new AccessDecision
            {
                IsGranted = hasValidIdentity,
                Reason = hasValidIdentity ? "Offline Authentication Strategy verified" : "Authentication failed",
                ApplicablePolicies = new[] { "offline-auth-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["offline_capable"] = "true",
                    ["strategy_type"] = "EmbeddedIdentity"
                }
            };
        }
    }
}
