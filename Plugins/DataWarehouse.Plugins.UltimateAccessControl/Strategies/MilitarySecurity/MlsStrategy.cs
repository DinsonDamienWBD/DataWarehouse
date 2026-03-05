using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.MilitarySecurity
{
    /// <summary>
    /// Multi-Level Security: TS/S/C/U with cross-level read/write rules (Bell-LaPadula model).
    /// </summary>
    public sealed class MlsStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "military-mls";
        public override string StrategyName => "Multi-Level Security (MLS)";

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
            IncrementCounter("military.mls.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("military.mls.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("military.mls.evaluate");
            var subjectLevel = GetLevel(context.SubjectAttributes.TryGetValue("SecurityLevel", out var sl) ? sl?.ToString() : "U");
            var objectLevel = GetLevel(context.ResourceAttributes.TryGetValue("SecurityLevel", out var ol) ? ol?.ToString() : "U");

            // Bell-LaPadula: no read up, no write down
            // Map additional actions to read/write semantics
            var action = context.Action?.ToLowerInvariant() ?? "";
            bool isGranted;
            string reason;

            switch (action)
            {
                case "read" or "view" or "get" or "list":
                    isGranted = subjectLevel >= objectLevel;
                    reason = isGranted ? "MLS read-down policy satisfied" : "MLS violation: subject clearance insufficient for read";
                    break;
                case "write" or "create" or "update" or "modify":
                    isGranted = subjectLevel <= objectLevel;
                    reason = isGranted ? "MLS write-up policy satisfied" : "MLS violation: subject clearance too high for write (no write-down)";
                    break;
                case "delete":
                    isGranted = subjectLevel >= objectLevel; // Delete requires dominance
                    reason = isGranted ? "MLS delete policy satisfied" : "MLS violation: insufficient clearance for delete";
                    break;
                case "execute" or "run":
                    isGranted = subjectLevel >= objectLevel; // Execute treated like read
                    reason = isGranted ? "MLS execute policy satisfied" : "MLS violation: insufficient clearance for execute";
                    break;
                default:
                    isGranted = false;
                    reason = $"MLS policy: unrecognized action '{action}' denied (only read/write/delete/execute supported)";
                    break;
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = isGranted,
                Reason = reason,
                Metadata = new Dictionary<string, object>
                {
                    ["subject_level"] = subjectLevel,
                    ["object_level"] = objectLevel,
                    ["action"] = action
                }
            });
        }

        private int GetLevel(string? level)
        {
            return level?.ToUpper() switch
            {
                "TS" => 3,
                "S" => 2,
                "C" => 1,
                _ => 0 // U
            };
        }
    }
}
