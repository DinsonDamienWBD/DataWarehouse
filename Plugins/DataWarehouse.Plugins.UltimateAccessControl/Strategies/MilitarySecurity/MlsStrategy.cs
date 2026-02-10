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

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var subjectLevel = GetLevel(context.SubjectAttributes.TryGetValue("SecurityLevel", out var sl) ? sl?.ToString() : "U");
            var objectLevel = GetLevel(context.ResourceAttributes.TryGetValue("SecurityLevel", out var ol) ? ol?.ToString() : "U");

            // Bell-LaPadula: no read up, no write down
            var canRead = context.Action == "read" && subjectLevel >= objectLevel;
            var canWrite = context.Action == "write" && subjectLevel <= objectLevel;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = canRead || canWrite,
                Reason = (canRead || canWrite) ? "MLS policy satisfied" : "MLS policy violation"
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
