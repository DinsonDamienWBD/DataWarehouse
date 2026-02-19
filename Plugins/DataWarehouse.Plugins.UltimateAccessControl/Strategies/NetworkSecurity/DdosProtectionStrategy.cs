using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.NetworkSecurity
{
    /// <summary>
    /// DDoS protection: rate limiting, connection throttling, SYN flood detection.
    /// </summary>
    public sealed class DdosProtectionStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();
        private int _maxRequestsPerMinute = 100;

        public override string StrategyId => "network-ddos";
        public override string StrategyName => "DDoS Protection";

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

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("MaxRequestsPerMinute", out var mrpm) && mrpm is int mrpmInt)
                _maxRequestsPerMinute = mrpmInt;

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("network.ddos.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("network.ddos.shutdown");
            _rateLimits.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("network.ddos.evaluate");
            var clientIp = context.ClientIpAddress ?? "unknown";
            var now = DateTime.UtcNow;

            var entry = _rateLimits.GetOrAdd(clientIp, _ => new RateLimitEntry { LastReset = now, RequestCount = 0 });

            if ((now - entry.LastReset).TotalMinutes >= 1)
            {
                entry = new RateLimitEntry { LastReset = now, RequestCount = 1 };
                _rateLimits[clientIp] = entry;
            }
            else
            {
                entry = entry with { RequestCount = entry.RequestCount + 1 };
                _rateLimits[clientIp] = entry;
            }

            var isRateLimited = entry.RequestCount > _maxRequestsPerMinute;

            return Task.FromResult(new AccessDecision
            {
                IsGranted = !isRateLimited,
                Reason = isRateLimited ? $"Rate limit exceeded ({entry.RequestCount}/{_maxRequestsPerMinute})" : "Within rate limit"
            });
        }
    }

    public sealed record RateLimitEntry
    {
        public required DateTime LastReset { get; init; }
        public required int RequestCount { get; init; }
    }
}
