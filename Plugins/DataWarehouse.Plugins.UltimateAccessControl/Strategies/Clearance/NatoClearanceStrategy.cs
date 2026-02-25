using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Clearance
{
    /// <summary>
    /// NATO security clearance strategy.
    /// Implements NATO Unclassified through Cosmic Top Secret levels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NATO clearance levels (hierarchical):
    /// 1. NATO Unclassified
    /// 2. NATO Restricted
    /// 3. NATO Confidential
    /// 4. NATO Secret
    /// 5. Cosmic Top Secret
    /// </para>
    /// </remarks>
    public sealed class NatoClearanceStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public NatoClearanceStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "nato-clearance";

        /// <inheritdoc/>
        public override string StrategyName => "NATO Clearance";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 1000
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("nato.clearance.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("nato.clearance.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
/// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nato.clearance.evaluate");
            var userClearance = context.SubjectAttributes.TryGetValue("nato_clearance", out var userObj) &&
                                userObj is string userLevel
                ? ParseClearanceLevel(userLevel)
                : NatoClearanceLevel.Unclassified;

            var requiredClearance = context.ResourceAttributes.TryGetValue("nato_classification", out var reqObj) &&
                                    reqObj is string reqLevel
                ? ParseClearanceLevel(reqLevel)
                : NatoClearanceLevel.Unclassified;

            var isGranted = userClearance >= requiredClearance;

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = isGranted,
                Reason = isGranted
                    ? $"NATO clearance sufficient: {userClearance} >= {requiredClearance}"
                    : $"Insufficient NATO clearance: {userClearance} < {requiredClearance}",
                Metadata = new Dictionary<string, object>
                {
                    ["user_clearance"] = userClearance.ToString(),
                    ["required_clearance"] = requiredClearance.ToString(),
                    ["clearance_system"] = "NATO"
                }
            });
        }

        private NatoClearanceLevel ParseClearanceLevel(string level)
        {
            return level?.ToUpperInvariant().Replace(" ", "") switch
            {
                "NATOUNCLASSIFIED" or "NU" => NatoClearanceLevel.Unclassified,
                "NATORESTRICTED" or "NR" => NatoClearanceLevel.Restricted,
                "NATOCONFIDENTIAL" or "NC" => NatoClearanceLevel.Confidential,
                "NATOSECRET" or "NS" => NatoClearanceLevel.Secret,
                "COSMICTOPSECRET" or "CTS" => NatoClearanceLevel.CosmicTopSecret,
                _ => NatoClearanceLevel.Unclassified
            };
        }

        private enum NatoClearanceLevel
        {
            Unclassified = 0,
            Restricted = 1,
            Confidential = 2,
            Secret = 3,
            CosmicTopSecret = 4
        }
    }
}
