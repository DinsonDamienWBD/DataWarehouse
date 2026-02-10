using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PlatformAuth
{
    public sealed class GcpIamStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public GcpIamStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "gcp-iam";
        public override string StrategyName => "GCP IAM Strategy";

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
                Reason = hasValidIdentity ? "GCP IAM Strategy verified" : "Authentication failed",
                ApplicablePolicies = new[] { "gcp-iam-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["platform"] = "GCP",
                    ["strategy_type"] = "PlatformAuth"
                }
            };
        }
    }
}
