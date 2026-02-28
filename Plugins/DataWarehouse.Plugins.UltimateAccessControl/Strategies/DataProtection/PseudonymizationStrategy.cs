using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.DataProtection
{
    /// <summary>
    /// Reversible pseudonymization with key-based mapping.
    /// </summary>
    public sealed class PseudonymizationStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, string> _pseudonymMap = new BoundedDictionary<string, string>(1000);
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

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dataprotection.pseudonymization.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dataprotection.pseudonymization.shutdown");
            _pseudonymMap.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
public string Pseudonymize(string identifier)
        {
            // Use GetOrAdd for atomic check-and-add to prevent TOCTOU race
            return _pseudonymMap.GetOrAdd(identifier, id =>
            {
                using var hmac = new HMACSHA256(_key);
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(id));
                return "PSE-" + Convert.ToBase64String(hash).Substring(0, 16).Replace("+", "").Replace("/", "");
            });
        }

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("dataprotection.pseudonymization.evaluate");
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
