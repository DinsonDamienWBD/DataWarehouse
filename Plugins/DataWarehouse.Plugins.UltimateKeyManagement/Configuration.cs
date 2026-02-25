using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateKeyManagement
{
    public record UltimateKeyManagementConfig
    {
        public bool AutoDiscoverStrategies { get; init; } = true;
        public List<string> DiscoveryAssemblyPatterns { get; init; } = new();
        public List<string> DiscoveryExcludePatterns { get; init; } = new();
        public bool EnableKeyRotation { get; init; } = true;
        public KeyRotationPolicy DefaultRotationPolicy { get; init; } = new();
        public Dictionary<string, KeyRotationPolicy> StrategyRotationPolicies { get; init; } = new();
        public Dictionary<string, Dictionary<string, object>> StrategyConfigurations { get; init; } = new();
        public bool PublishKeyEvents { get; init; } = true;
        public TimeSpan RotationCheckInterval { get; init; } = TimeSpan.FromHours(1);
    }

    public record KeyRotationPolicy
    {
        public bool Enabled { get; init; }
        public TimeSpan RotationInterval { get; init; } = TimeSpan.FromDays(90);
        public TimeSpan GracePeriod { get; init; } = TimeSpan.FromDays(7);
        public TimeSpan? MaxKeyAge { get; init; }
        public bool DeleteOldKeysAfterGrace { get; init; }
        public List<string> TargetKeyIds { get; init; } = new();
        public bool NotifyOnRotation { get; init; } = true;
        public RotationRetryPolicy RetryPolicy { get; init; } = new();
    }

    public record RotationRetryPolicy
    {
        public int MaxRetries { get; init; } = 3;
        public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);
        public double BackoffMultiplier { get; init; } = 2.0;
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(1);
    }

    public record StrategyConfiguration
    {
        public string StrategyId { get; init; } = "";
        public Dictionary<string, object> Configuration { get; init; } = new();
        public KeyRotationPolicy? RotationPolicy { get; init; }
        public bool Enabled { get; init; } = true;
        public int Priority { get; init; } = 100;
    }
}
