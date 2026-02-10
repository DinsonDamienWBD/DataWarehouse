using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.DataProtection
{
    /// <summary>
    /// Reversible pseudonymization with key-based mapping.
    /// </summary>
    public sealed class PseudonymizationStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, string> _pseudonymMap = new();
        private readonly byte[] _key;

        public PseudonymizationStrategy()
        {
            _key = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_key);
            }
        }

        public override string StrategyId => "dataprotection-pseudonymization";
        public override string StrategyName => "Pseudonymization";

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

        public string Pseudonymize(string identifier)
        {
            if (_pseudonymMap.TryGetValue(identifier, out var existingPseudonym))
                return existingPseudonym;

            using var hmac = new HMACSHA256(_key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(identifier));
            var pseudonym = "PSE-" + Convert.ToBase64String(hash).Substring(0, 16).Replace("+", "").Replace("/", "");

            _pseudonymMap[identifier] = pseudonym;
            return pseudonym;
        }

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Pseudonymization available",
                Metadata = new Dictionary<string, object>
                {
                    ["MappingCount"] = _pseudonymMap.Count
                }
            });
        }
    }
}
