using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.DataProtection
{
    /// <summary>
    /// Replace sensitive data with tokens, maintain token vault.
    /// </summary>
    public sealed class TokenizationStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, string> _tokenVault = new();
        private readonly ConcurrentDictionary<string, string> _reverseVault = new();

        public override string StrategyId => "dataprotection-tokenization";
        public override string StrategyName => "Tokenization";

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

        public string Tokenize(string sensitiveData)
        {
            if (_reverseVault.TryGetValue(sensitiveData, out var existingToken))
                return existingToken;

            var token = "TOK-" + Guid.NewGuid().ToString("N").Substring(0, 16);
            _tokenVault[token] = sensitiveData;
            _reverseVault[sensitiveData] = token;
            return token;
        }

        public string? Detokenize(string token)
        {
            return _tokenVault.TryGetValue(token, out var sensitiveData) ? sensitiveData : null;
        }

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Tokenization available",
                Metadata = new Dictionary<string, object>
                {
                    ["VaultSize"] = _tokenVault.Count
                }
            });
        }
    }
}
