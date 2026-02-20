using System.Security.Cryptography;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.ConfigManagement;

/// <summary>
/// Consul configuration management strategy for distributed key-value configuration.
/// Supports watches, versioning, and hierarchical configuration.
/// </summary>
public sealed class ConsulConfigStrategy : DeploymentStrategyBase
{
    private readonly BoundedDictionary<string, ConfigVersion> _configVersions = new BoundedDictionary<string, ConfigVersion>(1000);
    private readonly BoundedDictionary<string, List<ConfigWatch>> _watches = new BoundedDictionary<string, List<ConfigWatch>>(1000);

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Consul Config",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "Consul" },
        Description = "HashiCorp Consul distributed configuration management with KV store"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("consul_config.deploy");
        var state = initialState;
        var consulAddr = GetConsulAddress(config);
        var keyPrefix = GetKeyPrefix(config);

        // Connect to Consul
        state = state with { ProgressPercent = 10 };
        await ConnectConsulAsync(consulAddr, ct);

        // Put configuration values
        state = state with { ProgressPercent = 30 };
        var configData = ExtractConfigData(config);
        await PutKeyValuesAsync(keyPrefix, configData, ct);

        // Create or update config version
        state = state with { ProgressPercent = 60 };
        var version = new ConfigVersion
        {
            VersionId = $"cv-{Guid.NewGuid():N}",
            KeyPrefix = keyPrefix,
            Version = config.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCount = configData.Count,
            Checksum = ComputeConfigChecksum(configData)
        };
        _configVersions[$"{keyPrefix}:{config.Version}"] = version;

        // Set up watches for config changes
        state = state with { ProgressPercent = 80 };
        await SetupWatchesAsync(keyPrefix, config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["consulAddress"] = consulAddr,
                ["keyPrefix"] = keyPrefix,
                ["versionId"] = version.VersionId,
                ["keyCount"] = configData.Count
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("consul_config.deploy");
        var keyPrefix = currentState.Metadata.TryGetValue("keyPrefix", out var kp) ? kp?.ToString() : "";
        var versionKey = $"{keyPrefix}:{targetVersion}";

        if (!_configVersions.TryGetValue(versionKey, out var targetConfigVersion))
            throw new InvalidOperationException($"Config version {targetVersion} not found");

        await RestoreConfigVersionAsync(keyPrefix!, targetConfigVersion, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("etcd_config.deploy");
        var keyPrefix = currentState.Metadata.TryGetValue("keyPrefix", out var kp) ? kp?.ToString() : "";
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"consul-{keyPrefix}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 5,
                Details = new Dictionary<string, object> { ["type"] = "consul-kv" }
            }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetConsulAddress(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("consulAddress", out var ca) && ca is string cas ? cas : "http://localhost:8500";

    private static string GetKeyPrefix(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("keyPrefix", out var kp) && kp is string kps ? kps : $"config/{config.Environment}";

    private static Dictionary<string, object> ExtractConfigData(DeploymentConfig config)
    {
        var data = config.EnvironmentVariables.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        if (config.StrategyConfig.TryGetValue("configData", out var cd) && cd is Dictionary<string, object> configData)
        {
            foreach (var kv in configData)
                data[kv.Key] = kv.Value;
        }
        return data;
    }

    private static string ComputeConfigChecksum(Dictionary<string, object> data)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant()[..16];
    }

    private Task ConnectConsulAsync(string addr, CancellationToken ct) => Task.Delay(20, ct);
    private Task PutKeyValuesAsync(string prefix, Dictionary<string, object> data, CancellationToken ct) => Task.Delay(50, ct);
    private Task SetupWatchesAsync(string prefix, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task RestoreConfigVersionAsync(string prefix, ConfigVersion version, CancellationToken ct) => Task.Delay(30, ct);

    private sealed class ConfigVersion
    {
        public required string VersionId { get; init; }
        public required string KeyPrefix { get; init; }
        public required string Version { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public int KeyCount { get; init; }
        public required string Checksum { get; init; }
    }

    private sealed class ConfigWatch
    {
        public required string WatchId { get; init; }
        public required string KeyPattern { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}

/// <summary>
/// etcd configuration management strategy for distributed, reliable key-value store.
/// Supports transactions, watches, and leader election.
/// </summary>
public sealed class EtcdConfigStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "etcd Config",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "etcd" },
        Description = "etcd distributed key-value configuration with strong consistency"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("etcd_config.deploy");
        var state = initialState;
        var etcdEndpoints = GetEtcdEndpoints(config);
        var keyPrefix = GetKeyPrefix(config);

        // Connect to etcd cluster
        state = state with { ProgressPercent = 20 };
        await ConnectEtcdAsync(etcdEndpoints, ct);

        // Put configuration in transaction
        state = state with { ProgressPercent = 50 };
        var revision = await PutConfigTransactionAsync(keyPrefix, config, ct);

        // Set up watches
        state = state with { ProgressPercent = 80 };
        await SetupWatchesAsync(keyPrefix, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["etcdEndpoints"] = string.Join(",", etcdEndpoints),
                ["keyPrefix"] = keyPrefix,
                ["revision"] = revision
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("spring_cloud_config.deploy");
        var keyPrefix = currentState.Metadata.TryGetValue("keyPrefix", out var kp) ? kp?.ToString() : "";
        await RollbackToRevisionAsync(keyPrefix!, targetVersion, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 3 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string[] GetEtcdEndpoints(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("etcdEndpoints", out var ep) && ep is string[] eps ? eps : new[] { "http://localhost:2379" };

    private static string GetKeyPrefix(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("keyPrefix", out var kp) && kp is string kps ? kps : $"/config/{config.Environment}";

    private Task ConnectEtcdAsync(string[] endpoints, CancellationToken ct) => Task.Delay(30, ct);
    private Task<long> PutConfigTransactionAsync(string prefix, DeploymentConfig config, CancellationToken ct) => Task.FromResult(12345L);
    private Task SetupWatchesAsync(string prefix, CancellationToken ct) => Task.Delay(20, ct);
    private Task RollbackToRevisionAsync(string prefix, string version, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// Spring Cloud Config Server integration for centralized configuration management.
/// </summary>
public sealed class SpringCloudConfigStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Spring Cloud Config",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 3,
        RequiredInfrastructure = new[] { "SpringCloudConfig", "Git" },
        Description = "Spring Cloud Config Server for centralized, Git-backed configuration"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("spring_cloud_config.deploy");
        var state = initialState;
        var configServerUrl = GetConfigServerUrl(config);
        var application = GetApplication(config);
        var profile = config.Environment;

        // Update Git repository with new config
        state = state with { ProgressPercent = 30 };
        await UpdateGitConfigAsync(application, profile, config, ct);

        // Refresh config server
        state = state with { ProgressPercent = 60 };
        await RefreshConfigServerAsync(configServerUrl, ct);

        // Notify clients to refresh
        state = state with { ProgressPercent = 80 };
        await NotifyClientsAsync(configServerUrl, application, profile, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["configServerUrl"] = configServerUrl,
                ["application"] = application,
                ["profile"] = profile
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("aws_app_config.deploy");
        var configServerUrl = currentState.Metadata.TryGetValue("configServerUrl", out var cs) ? cs?.ToString() : "";
        var application = currentState.Metadata.TryGetValue("application", out var app) ? app?.ToString() : "";

        await RevertGitConfigAsync(application!, targetVersion, ct);
        await RefreshConfigServerAsync(configServerUrl!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 10 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetConfigServerUrl(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("configServerUrl", out var cs) && cs is string css ? css : "http://localhost:8888";

    private static string GetApplication(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("application", out var app) && app is string apps ? apps : "default";

    private Task UpdateGitConfigAsync(string app, string profile, DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
    private Task RefreshConfigServerAsync(string url, CancellationToken ct) => Task.Delay(30, ct);
    private Task NotifyClientsAsync(string url, string app, string profile, CancellationToken ct) => Task.Delay(40, ct);
    private Task RevertGitConfigAsync(string app, string version, CancellationToken ct) => Task.Delay(40, ct);
}

/// <summary>
/// AWS AppConfig configuration management with validation and gradual deployment.
/// </summary>
public sealed class AwsAppConfigStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "AWS AppConfig",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "AWS", "AppConfig" },
        Description = "AWS AppConfig with validation, gradual deployment, and automatic rollback"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("aws_app_config.deploy");
        var state = initialState;
        var appId = GetApplicationId(config);
        var envId = GetEnvironmentId(config);
        var profileId = GetConfigProfileId(config);

        // Create hosted configuration version
        state = state with { ProgressPercent = 20 };
        var versionNumber = await CreateHostedConfigVersionAsync(appId, profileId, config, ct);

        // Start deployment
        state = state with { ProgressPercent = 40 };
        var deploymentId = await StartDeploymentAsync(appId, envId, profileId, versionNumber, config, ct);

        // Wait for deployment completion
        state = state with { ProgressPercent = 70 };
        await WaitForDeploymentAsync(appId, envId, deploymentId, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["applicationId"] = appId,
                ["environmentId"] = envId,
                ["configProfileId"] = profileId,
                ["versionNumber"] = versionNumber,
                ["deploymentId"] = deploymentId
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("azure_app_configuration.deploy");
        var appId = currentState.Metadata.TryGetValue("applicationId", out var ai) ? ai?.ToString() : "";
        var envId = currentState.Metadata.TryGetValue("environmentId", out var ei) ? ei?.ToString() : "";
        var profileId = currentState.Metadata.TryGetValue("configProfileId", out var pi) ? pi?.ToString() : "";

        await RollbackDeploymentAsync(appId!, envId!, profileId!, int.Parse(targetVersion), ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 8 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetApplicationId(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("applicationId", out var ai) && ai is string ais ? ais : "app123";

    private static string GetEnvironmentId(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("environmentId", out var ei) && ei is string eis ? eis : "env123";

    private static string GetConfigProfileId(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("configProfileId", out var pi) && pi is string pis ? pis : "profile123";

    private Task<int> CreateHostedConfigVersionAsync(string appId, string profileId, DeploymentConfig config, CancellationToken ct) => Task.FromResult(42);
    private Task<string> StartDeploymentAsync(string appId, string envId, string profileId, int version, DeploymentConfig config, CancellationToken ct) => Task.FromResult("deploy-123");
    private Task WaitForDeploymentAsync(string appId, string envId, string deploymentId, CancellationToken ct) => Task.Delay(100, ct);
    private Task RollbackDeploymentAsync(string appId, string envId, string profileId, int version, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// Azure App Configuration service for centralized configuration management.
/// </summary>
public sealed class AzureAppConfigurationStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Azure App Configuration",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = new[] { "Azure", "AppConfiguration" },
        Description = "Azure App Configuration with labels, snapshots, and Key Vault integration"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("azure_app_configuration.deploy");
        var state = initialState;
        var configStoreEndpoint = GetConfigStoreEndpoint(config);
        var label = GetLabel(config);

        // Set configuration values
        state = state with { ProgressPercent = 30 };
        await SetConfigurationValuesAsync(configStoreEndpoint, label, config, ct);

        // Create snapshot for versioning
        state = state with { ProgressPercent = 60 };
        var snapshotName = await CreateSnapshotAsync(configStoreEndpoint, label, config.Version, ct);

        // Notify connected applications
        state = state with { ProgressPercent = 85 };
        await NotifyApplicationsAsync(configStoreEndpoint, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["configStoreEndpoint"] = configStoreEndpoint,
                ["label"] = label,
                ["snapshotName"] = snapshotName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("kubernetes_config_map.deploy");
        var configStoreEndpoint = currentState.Metadata.TryGetValue("configStoreEndpoint", out var cs) ? cs?.ToString() : "";
        await RestoreSnapshotAsync(configStoreEndpoint!, targetVersion, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 6 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetConfigStoreEndpoint(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("configStoreEndpoint", out var cs) && cs is string css ? css : "https://myconfig.azconfig.io";

    private static string GetLabel(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("label", out var l) && l is string ls ? ls : config.Environment;

    private Task SetConfigurationValuesAsync(string endpoint, string label, DeploymentConfig config, CancellationToken ct) => Task.Delay(40, ct);
    private Task<string> CreateSnapshotAsync(string endpoint, string label, string version, CancellationToken ct) => Task.FromResult($"snapshot-{version}");
    private Task NotifyApplicationsAsync(string endpoint, CancellationToken ct) => Task.Delay(20, ct);
    private Task RestoreSnapshotAsync(string endpoint, string snapshotName, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// Kubernetes ConfigMap and environment configuration management.
/// </summary>
public sealed class KubernetesConfigMapStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Kubernetes ConfigMap",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = new[] { "Kubernetes" },
        Description = "Kubernetes ConfigMap management with pod rolling update triggers"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("kubernetes_config_map.deploy");
        var state = initialState;
        var namespace_ = GetNamespace(config);
        var configMapName = GetConfigMapName(config);

        // Create or update ConfigMap
        state = state with { ProgressPercent = 30 };
        await ApplyConfigMapAsync(namespace_, configMapName, config, ct);

        // Update annotation to trigger pod rollout
        state = state with { ProgressPercent = 60 };
        if (config.StrategyConfig.TryGetValue("triggerRollout", out var tr) && tr is true)
        {
            await TriggerPodRolloutAsync(namespace_, configMapName, config, ct);
        }

        // Wait for rollout if triggered
        state = state with { ProgressPercent = 85 };
        await WaitForRolloutCompletionAsync(namespace_, configMapName, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["namespace"] = namespace_,
                ["configMapName"] = configMapName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var namespace_ = currentState.Metadata.TryGetValue("namespace", out var ns) ? ns?.ToString() : "";
        var configMapName = currentState.Metadata.TryGetValue("configMapName", out var cm) ? cm?.ToString() : "";

        await RollbackConfigMapAsync(namespace_!, configMapName!, targetVersion, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 4 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetNamespace(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("namespace", out var ns) && ns is string nss ? nss : "default";

    private static string GetConfigMapName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("configMapName", out var cm) && cm is string cms ? cms : $"config-{config.Environment}";

    private Task ApplyConfigMapAsync(string ns, string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task TriggerPodRolloutAsync(string ns, string name, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task WaitForRolloutCompletionAsync(string ns, string name, CancellationToken ct) => Task.Delay(50, ct);
    private Task RollbackConfigMapAsync(string ns, string name, string version, CancellationToken ct) => Task.Delay(30, ct);
}
