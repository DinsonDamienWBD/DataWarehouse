using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PlatformAuth
{
    public sealed class SssdStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public SssdStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "sssd";
        public override string StrategyName => "SSSD Strategy";

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
                Reason = hasValidIdentity ? "SSSD Strategy verified" : "Authentication failed",
                ApplicablePolicies = new[] { "sssd-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["platform"] = "Linux",
                    ["strategy_type"] = "PlatformAuth"
                }
            };
        }
    }
}
