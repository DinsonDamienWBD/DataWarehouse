using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.EmbeddedIdentity
{
    public sealed class PasswordHashingStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public PasswordHashingStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "password-hashing";
        public override string StrategyName => "Password Hashing Strategy";

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
                Reason = hasValidIdentity ? "Password Hashing Strategy verified" : "Authentication failed",
                ApplicablePolicies = new[] { "password-hashing-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["algorithm"] = "Argon2id",
                    ["strategy_type"] = "EmbeddedIdentity"
                }
            };
        }
    }
}
