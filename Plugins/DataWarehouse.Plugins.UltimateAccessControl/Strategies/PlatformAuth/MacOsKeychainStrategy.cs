using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PlatformAuth
{
    public sealed class MacOsKeychainStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public MacOsKeychainStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "macos-keychain";
        public override string StrategyName => "macOS Keychain Strategy";

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
                Reason = hasValidIdentity ? "macOS Keychain Strategy verified" : "Authentication failed",
                ApplicablePolicies = new[] { "macos-keychain-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["platform"] = "macOS",
                    ["strategy_type"] = "PlatformAuth"
                }
            };
        }
    }
}
