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
    /// Custom clearance framework for user-defined hierarchical clearance levels.
    /// Supports arbitrary clearance hierarchies with configurable levels.
    /// </summary>
    public sealed class CustomClearanceFrameworkStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, int> _clearanceHierarchy = new(StringComparer.OrdinalIgnoreCase);

        public CustomClearanceFrameworkStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "custom-clearance-framework";

        /// <inheritdoc/>
        public override string StrategyName => "Custom Clearance Framework";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 2000
        };

        /// <inheritdoc/>
        public override async Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            await base.InitializeAsync(configuration, cancellationToken);

            // Load custom clearance hierarchy
            if (configuration.TryGetValue("ClearanceLevels", out var levelsObj) &&
                levelsObj is IEnumerable<Dictionary<string, object>> levels)
            {
                foreach (var level in levels)
                {
                    if (level.TryGetValue("Name", out var nameObj) && nameObj is string name &&
                        level.TryGetValue("Level", out var levelObj) && int.TryParse(levelObj?.ToString(), out var levelValue))
                    {
                        _clearanceHierarchy[name] = levelValue;
                    }
                }
            }
            else
            {
                // Default hierarchy
                _clearanceHierarchy["Public"] = 0;
                _clearanceHierarchy["Internal"] = 1;
                _clearanceHierarchy["Confidential"] = 2;
                _clearanceHierarchy["Restricted"] = 3;
                _clearanceHierarchy["HighlyRestricted"] = 4;
            }

            _logger.LogInformation("Custom clearance framework initialized with {Count} levels", _clearanceHierarchy.Count);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var userClearance = context.SubjectAttributes.TryGetValue("clearance", out var userObj) &&
                                userObj is string userLevel
                ? GetClearanceLevel(userLevel)
                : 0;

            var requiredClearance = context.ResourceAttributes.TryGetValue("required_clearance", out var reqObj) &&
                                    reqObj is string reqLevel
                ? GetClearanceLevel(reqLevel)
                : 0;

            var isGranted = userClearance >= requiredClearance;

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = isGranted,
                Reason = isGranted
                    ? $"Clearance sufficient: {userClearance} >= {requiredClearance}"
                    : $"Insufficient clearance: {userClearance} < {requiredClearance}",
                Metadata = new Dictionary<string, object>
                {
                    ["user_clearance_level"] = userClearance,
                    ["required_clearance_level"] = requiredClearance,
                    ["clearance_system"] = "CUSTOM"
                }
            });
        }

        private int GetClearanceLevel(string clearanceName)
        {
            return _clearanceHierarchy.TryGetValue(clearanceName, out var level) ? level : 0;
        }
    }
}
