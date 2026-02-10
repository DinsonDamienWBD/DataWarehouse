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
    /// Compartmentalization strategy for need-to-know access control.
    /// Implements SCI (Sensitive Compartmented Information), SAP (Special Access Programs), and codeword compartments.
    /// </summary>
    public sealed class CompartmentalizationStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public CompartmentalizationStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "compartmentalization";

        /// <inheritdoc/>
        public override string StrategyName => "Compartmentalization";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 1000
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract user's compartments/codewords
            var userCompartments = context.SubjectAttributes.TryGetValue("compartments", out var userObj) &&
                                   userObj is IEnumerable<string> userComps
                ? userComps.Select(c => c.ToUpperInvariant()).ToHashSet()
                : new HashSet<string>();

            // Extract required compartments
            var requiredCompartments = context.ResourceAttributes.TryGetValue("required_compartments", out var reqObj) &&
                                       reqObj is IEnumerable<string> reqComps
                ? reqComps.Select(c => c.ToUpperInvariant()).ToList()
                : new List<string>();

            // Check if user has all required compartments
            var missingCompartments = requiredCompartments.Where(rc => !userCompartments.Contains(rc)).ToList();

            var isGranted = missingCompartments.Count == 0;

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = isGranted,
                Reason = isGranted
                    ? "All required compartments present"
                    : $"Missing compartments: {string.Join(", ", missingCompartments)}",
                Metadata = new Dictionary<string, object>
                {
                    ["user_compartments"] = userCompartments.ToList(),
                    ["required_compartments"] = requiredCompartments,
                    ["missing_compartments"] = missingCompartments,
                    ["clearance_system"] = "COMPARTMENTALIZATION"
                }
            });
        }
    }
}
