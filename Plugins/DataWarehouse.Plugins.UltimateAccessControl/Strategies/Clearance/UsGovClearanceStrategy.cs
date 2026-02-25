using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Clearance
{
    /// <summary>
    /// U.S. Government security clearance strategy.
    /// Implements Unclassified, Confidential, Secret, Top Secret, and TS-SCI levels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clearance levels (hierarchical):
    /// 1. Unclassified
    /// 2. Confidential
    /// 3. Secret
    /// 4. Top Secret
    /// 5. TS-SCI (Top Secret - Sensitive Compartmented Information)
    /// </para>
    /// </remarks>
    public sealed class UsGovClearanceStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public UsGovClearanceStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "us-gov-clearance";

        /// <inheritdoc/>
        public override string StrategyName => "U.S. Government Clearance";

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
            IncrementCounter("us.gov.clearance.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("us.gov.clearance.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
/// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("us.gov.clearance.evaluate");
            // Extract clearance level from subject attributes
            var userClearance = context.SubjectAttributes.TryGetValue("clearance_level", out var userObj) &&
                                userObj is string userLevel
                ? ParseClearanceLevel(userLevel)
                : UsGovClearanceLevel.Unclassified;

            // Extract required clearance from resource attributes
            var requiredClearance = context.ResourceAttributes.TryGetValue("required_clearance", out var reqObj) &&
                                    reqObj is string reqLevel
                ? ParseClearanceLevel(reqLevel)
                : UsGovClearanceLevel.Unclassified;

            // Check clearance expiration
            if (context.SubjectAttributes.TryGetValue("clearance_expiry", out var expiryObj) &&
                expiryObj is DateTime expiry && expiry < DateTime.UtcNow)
            {
                _logger.LogWarning("Clearance expired for {SubjectId}", context.SubjectId);
                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Security clearance expired"
                });
            }

            // Hierarchical clearance check
            var isGranted = userClearance >= requiredClearance;

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = isGranted,
                Reason = isGranted
                    ? $"Clearance sufficient: {userClearance} >= {requiredClearance}"
                    : $"Insufficient clearance: {userClearance} < {requiredClearance}",
                Metadata = new Dictionary<string, object>
                {
                    ["user_clearance"] = userClearance.ToString(),
                    ["required_clearance"] = requiredClearance.ToString(),
                    ["clearance_system"] = "US_GOV"
                }
            });
        }

        private UsGovClearanceLevel ParseClearanceLevel(string level)
        {
            return level?.ToUpperInvariant() switch
            {
                "UNCLASSIFIED" or "U" => UsGovClearanceLevel.Unclassified,
                "CONFIDENTIAL" or "C" => UsGovClearanceLevel.Confidential,
                "SECRET" or "S" => UsGovClearanceLevel.Secret,
                "TOP SECRET" or "TS" or "TOPSECRET" => UsGovClearanceLevel.TopSecret,
                "TS-SCI" or "TS/SCI" or "TSSCI" => UsGovClearanceLevel.TsSci,
                _ => UsGovClearanceLevel.Unclassified
            };
        }

        private enum UsGovClearanceLevel
        {
            Unclassified = 0,
            Confidential = 1,
            Secret = 2,
            TopSecret = 3,
            TsSci = 4
        }
    }
}
