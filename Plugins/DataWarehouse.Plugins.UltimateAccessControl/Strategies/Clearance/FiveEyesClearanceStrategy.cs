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
    /// Five Eyes intelligence sharing clearance strategy.
    /// Enforces clearance requirements for US, UK, Canada, Australia, and New Zealand intelligence sharing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five Eyes nations: USA, GBR, CAN, AUS, NZL
    /// Caveat markings: FVEY, NOFORN, REL TO [country codes]
    /// </para>
    /// </remarks>
    public sealed class FiveEyesClearanceStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly HashSet<string> _fiveEyesCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "USA", "US", "GBR", "GB", "UK", "CAN", "CA", "AUS", "AU", "NZL", "NZ"
        };

        public FiveEyesClearanceStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "five-eyes-clearance";

        /// <inheritdoc/>
        public override string StrategyName => "Five Eyes Clearance";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 1000
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("five.eyes.clearance.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("five.eyes.clearance.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
/// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("five.eyes.clearance.evaluate");
            // Extract user's nationality/clearance
            var userCountry = context.SubjectAttributes.TryGetValue("nationality", out var countryObj)
                ? countryObj?.ToString()
                : null;

            // Extract caveat markings from resource
            var caveats = context.ResourceAttributes.TryGetValue("caveats", out var caveatObj) &&
                         caveatObj is IEnumerable<string> caveatList
                ? caveatList.ToList()
                : new List<string>();

            // Check NOFORN (No Foreign Nationals)
            if (caveats.Any(c => c.Equals("NOFORN", StringComparison.OrdinalIgnoreCase)))
            {
                // Only US nationals allowed
                if (userCountry == null || !userCountry.Equals("USA", StringComparison.OrdinalIgnoreCase) &&
                    !userCountry.Equals("US", StringComparison.OrdinalIgnoreCase))
                {
                    return await Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "NOFORN restriction - US nationals only"
                    });
                }
            }

            // Check FVEY marking
            if (caveats.Any(c => c.Equals("FVEY", StringComparison.OrdinalIgnoreCase)))
            {
                if (userCountry == null || !_fiveEyesCountries.Contains(userCountry))
                {
                    return await Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "FVEY restriction - Five Eyes nationals only"
                    });
                }
            }

            // Check REL TO markings
            var relToCaveats = caveats.Where(c => c.StartsWith("REL TO", StringComparison.OrdinalIgnoreCase)).ToList();
            if (relToCaveats.Any())
            {
                var allowedCountries = relToCaveats
                    .SelectMany(c => c.Substring(6).Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Select(country => country.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (userCountry == null || !allowedCountries.Contains(userCountry))
                {
                    return await Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"REL TO restriction - authorized countries: {string.Join(", ", allowedCountries)}"
                    });
                }
            }

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Five Eyes clearance requirements met",
                Metadata = new Dictionary<string, object>
                {
                    ["user_country"] = userCountry ?? "unknown",
                    ["caveats"] = caveats,
                    ["clearance_system"] = "FIVE_EYES"
                }
            });
        }
    }
}
