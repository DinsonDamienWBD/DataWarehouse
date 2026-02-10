using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.NetworkSecurity
{
    /// <summary>
    /// IP/port-based firewall rule management, allow/deny lists.
    /// </summary>
    public sealed class FirewallRulesStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentBag<FirewallRule> _rules = new();

        public override string StrategyId => "network-firewall";
        public override string StrategyName => "Firewall Rules";

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

        public void AddRule(string ipPattern, int? port, bool allow)
        {
            _rules.Add(new FirewallRule { IpPattern = ipPattern, Port = port, Allow = allow });
        }

        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var clientIp = context.ClientIpAddress;
            if (string.IsNullOrEmpty(clientIp))
            {
                return Task.FromResult(new AccessDecision { IsGranted = true, Reason = "No IP filtering" });
            }

            var matchingRule = _rules.FirstOrDefault(r => MatchesPattern(clientIp, r.IpPattern));
            var isGranted = matchingRule?.Allow ?? true;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = isGranted,
                Reason = matchingRule != null ? $"Firewall rule: {(isGranted ? "allow" : "deny")}" : "No matching rule"
            });
        }

        private bool MatchesPattern(string ip, string pattern)
        {
            if (pattern == "*") return true;
            if (pattern.EndsWith("*"))
            {
                var prefix = pattern.TrimEnd('*');
                return ip.StartsWith(prefix);
            }
            return ip == pattern;
        }
    }

    public sealed record FirewallRule
    {
        public required string IpPattern { get; init; }
        public int? Port { get; init; }
        public required bool Allow { get; init; }
    }
}
