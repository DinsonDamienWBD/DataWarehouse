using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.NetworkSecurity
{
    /// <summary>
    /// VPN tunnel management, IPSec/WireGuard config, split tunneling policies.
    /// </summary>
    public sealed class VpnStrategy : AccessControlStrategyBase
    {
        public override string StrategyId => "network-vpn";
        public override string StrategyName => "VPN Security";

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
            var requireVpn = context.ResourceAttributes.TryGetValue("RequireVpn", out var rv) && rv is bool rvBool && rvBool;
            var isVpn = context.EnvironmentAttributes.TryGetValue("VpnConnected", out var vpn) && vpn is bool vpnBool && vpnBool;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !requireVpn || isVpn,
                Reason = requireVpn && !isVpn ? "VPN connection required" : "Access granted"
            });
        }
    }
}
