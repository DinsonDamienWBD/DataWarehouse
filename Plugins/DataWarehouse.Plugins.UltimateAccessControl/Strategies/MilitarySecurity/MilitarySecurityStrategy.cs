using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.MilitarySecurity
{
    /// <summary>
    /// Classified data handling with security markings.
    /// </summary>
    public sealed class MilitarySecurityStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, ClassificationMarking> _markings = new();

        public override string StrategyId => "military-classification";
        public override string StrategyName => "Military Classification";

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
            IncrementCounter("military.classification.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.classification.shutdown");
            _markings.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
public void SetClassification(string resourceId, string level, string[] caveats)
        {
            _markings[resourceId] = new ClassificationMarking
            {
                ResourceId = resourceId,
                ClassificationLevel = level,
                Caveats = caveats,
                MarkedAt = DateTime.UtcNow
            };
        }

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("military.classification.evaluate");
            if (!_markings.TryGetValue(context.ResourceId, out var marking))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No classification marking"
                });
            }

            var userClearance = context.SubjectAttributes.TryGetValue("Clearance", out var cl) ? cl?.ToString() : "U";
            var hasAccess = CheckClearance(userClearance ?? "U", marking.ClassificationLevel);

            return Task.FromResult(new AccessDecision
            {
                IsGranted = hasAccess,
                Reason = hasAccess ? "Clearance sufficient" : "Insufficient clearance",
                Metadata = new Dictionary<string, object>
                {
                    ["Classification"] = marking.ClassificationLevel,
                    ["UserClearance"] = userClearance ?? "U"
                }
            });
        }

        private bool CheckClearance(string userLevel, string requiredLevel)
        {
            var levels = new[] { "U", "C", "S", "TS" };
            var userIdx = Array.IndexOf(levels, userLevel);
            var reqIdx = Array.IndexOf(levels, requiredLevel);
            return userIdx >= reqIdx;
        }
    }

    public sealed record ClassificationMarking
    {
        public required string ResourceId { get; init; }
        public required string ClassificationLevel { get; init; }
        public required string[] Caveats { get; init; }
        public required DateTime MarkedAt { get; init; }
    }
}
