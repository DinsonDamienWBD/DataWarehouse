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
    /// Escort requirement strategy for uncleared personnel in classified areas.
    /// Enforces escort rules for individuals without sufficient clearance.
    /// </summary>
    public sealed class EscortRequirementStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public EscortRequirementStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "escort-requirement";

        /// <inheritdoc/>
        public override string StrategyName => "Escort Requirement";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 500
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Check if resource requires escort for uncleared personnel
            var requiresEscort = context.ResourceAttributes.TryGetValue("requires_escort", out var escortObj) &&
                                 escortObj is bool requires && requires;

            if (!requiresEscort)
            {
                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No escort requirement for this resource"
                });
            }

            // Check user's clearance level
            var userClearanceLevel = context.SubjectAttributes.TryGetValue("clearance_level", out var userLevelObj) && userLevelObj != null
                ? userLevelObj.ToString()
                : "UNCLASSIFIED";

            var requiredClearanceLevel = context.ResourceAttributes.TryGetValue("clearance_level", out var reqLevelObj) && reqLevelObj != null
                ? reqLevelObj.ToString()
                : "SECRET";

            var hasSufficientClearance = IsClearanceSufficient(userClearanceLevel, requiredClearanceLevel ?? "SECRET");

            if (hasSufficientClearance)
            {
                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Sufficient clearance - no escort required",
                    Metadata = new Dictionary<string, object>
                    {
                        ["escort_required"] = false,
                        ["clearance_level"] = userClearanceLevel!
                    }
                });
            }

            // User needs escort - check if escort is present
            var escortId = context.SubjectAttributes.TryGetValue("escort_id", out var escortIdObj)
                ? escortIdObj?.ToString()
                : null;

            if (string.IsNullOrEmpty(escortId))
            {
                _logger.LogWarning("Escort required for {SubjectId} but no escort present", context.SubjectId);
                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Escort required - insufficient clearance ({userClearanceLevel} < {requiredClearanceLevel})",
                    Metadata = new Dictionary<string, object>
                    {
                        ["escort_required"] = true,
                        ["escort_present"] = false
                    }
                });
            }

            // Verify escort has sufficient clearance
            var escortClearance = context.SubjectAttributes.TryGetValue("escort_clearance", out var escortClearObj) && escortClearObj != null
                ? escortClearObj.ToString()
                : null;

            if (escortClearance == null || !IsClearanceSufficient(escortClearance, requiredClearanceLevel ?? "SECRET"))
            {
                _logger.LogWarning("Escort {EscortId} has insufficient clearance for {SubjectId}", escortId, context.SubjectId);
                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Escort clearance insufficient ({escortClearance ?? "unknown"} < {requiredClearanceLevel})"
                });
            }

            _logger.LogInformation("Access granted to {SubjectId} with escort {EscortId}", context.SubjectId, escortId);

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted with authorized escort",
                Metadata = new Dictionary<string, object>
                {
                    ["escort_required"] = true,
                    ["escort_present"] = true,
                    ["escort_id"] = escortId,
                    ["escort_clearance"] = escortClearance
                }
            });
        }

        private bool IsClearanceSufficient(string? userLevel, string requiredLevel)
        {
            var levels = new[] { "UNCLASSIFIED", "CONFIDENTIAL", "SECRET", "TOPSECRET", "TS-SCI" };
            var userIndex = Array.IndexOf(levels, userLevel?.ToUpperInvariant().Replace(" ", "").Replace("-", ""));
            var reqIndex = Array.IndexOf(levels, requiredLevel.ToUpperInvariant().Replace(" ", "").Replace("-", ""));

            return userIndex >= reqIndex;
        }
    }
}
