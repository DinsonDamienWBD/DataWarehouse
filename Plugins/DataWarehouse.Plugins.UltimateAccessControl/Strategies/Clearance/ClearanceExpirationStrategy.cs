using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Clearance
{
    /// <summary>
    /// Clearance expiration strategy with time-limited access and auto-revocation.
    /// Enforces clearance expiration dates and periodic re-validation requirements.
    /// </summary>
    public sealed class ClearanceExpirationStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public ClearanceExpirationStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "clearance-expiration";

        /// <inheritdoc/>
        public override string StrategyName => "Clearance Expiration";

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
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Check clearance expiration date
            if (context.SubjectAttributes.TryGetValue("clearance_expiry", out var expiryObj) &&
                expiryObj is DateTime expiry)
            {
                if (expiry < DateTime.UtcNow)
                {
                    _logger.LogWarning("Clearance expired for {SubjectId} on {Expiry}", context.SubjectId, expiry);
                    return await Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Security clearance expired on {expiry:yyyy-MM-dd}",
                        Metadata = new Dictionary<string, object>
                        {
                            ["expiry_date"] = expiry,
                            ["days_expired"] = (DateTime.UtcNow - expiry).Days
                        }
                    });
                }

                // Check if expiring soon (warning)
                var daysUntilExpiry = (expiry - DateTime.UtcNow).Days;
                var warningThreshold = Configuration.TryGetValue("ExpiryWarningDays", out var thresholdObj) &&
                                       int.TryParse(thresholdObj?.ToString(), out var threshold)
                    ? threshold
                    : 30;

                if (daysUntilExpiry <= warningThreshold)
                {
                    _logger.LogWarning("Clearance expiring soon for {SubjectId}: {Days} days remaining",
                        context.SubjectId, daysUntilExpiry);
                }

                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Clearance valid",
                    Metadata = new Dictionary<string, object>
                    {
                        ["expiry_date"] = expiry,
                        ["days_until_expiry"] = daysUntilExpiry,
                        ["expiring_soon"] = daysUntilExpiry <= warningThreshold
                    }
                });
            }

            // No expiry date - check if required
            var requireExpiry = Configuration.TryGetValue("RequireExpiryDate", out var requireObj) &&
                                requireObj is bool require && require;

            if (requireExpiry)
            {
                return await Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Clearance expiry date required but not provided"
                });
            }

            return await Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "No expiry date configured"
            });
        }
    }
}
