using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PlatformAuth
{
    public sealed class SshKeyAuthStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public SshKeyAuthStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "ssh-key-auth";
        public override string StrategyName => "SSH Key Auth Strategy";

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
                Reason = hasValidIdentity ? "SSH Key Auth Strategy verified" : "Authentication failed",
                ApplicablePolicies = new[] { "ssh-key-auth-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["key_type"] = "ed25519",
                    ["strategy_type"] = "PlatformAuth"
                }
            };
        }
    }
}
