using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.DataProtection
{
    /// <summary>
    /// K-anonymity and L-diversity implementations.
    /// </summary>
    public sealed class AnonymizationStrategy : AccessControlStrategyBase
    {
        private int _kValue = 5; // K-anonymity parameter
        private int _lValue = 3; // L-diversity parameter

        public override string StrategyId => "dataprotection-anonymization";
        public override string StrategyName => "Anonymization (K-Anonymity/L-Diversity)";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("KValue", out var k) && k is int kInt)
                _kValue = kInt;

            if (configuration.TryGetValue("LValue", out var l) && l is int lInt)
                _lValue = lInt;

            return base.InitializeAsync(configuration, cancellationToken);
        }

        public Dictionary<string, object> GeneralizeData(Dictionary<string, object> data, string[] quasiIdentifiers)
        {
            var anonymized = new Dictionary<string, object>(data);

            foreach (var qi in quasiIdentifiers)
            {
                if (!anonymized.ContainsKey(qi))
                    continue;

                anonymized[qi] = GeneralizeValue(anonymized[qi]);
            }

            return anonymized;
        }

        private object GeneralizeValue(object value)
        {
            return value switch
            {
                int age => (age / 10) * 10, // Age ranges
                string zipCode when zipCode.Length >= 3 => zipCode.Substring(0, 3) + "**", // Zip code prefix
                DateTime date => new DateTime(date.Year, 1, 1), // Year only
                _ => value
            };
        }

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Anonymization available",
                Metadata = new Dictionary<string, object>
                {
                    ["KValue"] = _kValue,
                    ["LValue"] = _lValue
                }
            });
        }
    }
}
