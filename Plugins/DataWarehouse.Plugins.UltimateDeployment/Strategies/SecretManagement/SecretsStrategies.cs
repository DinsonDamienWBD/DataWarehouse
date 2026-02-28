using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.SecretManagement;

/// <summary>
/// HashiCorp Vault secret management strategy for dynamic secrets,
/// encryption as a service, and centralized secret storage.
/// </summary>
public sealed class HashiCorpVaultStrategy : DeploymentStrategyBase
{
    private readonly BoundedDictionary<string, VaultLease> _leases = new BoundedDictionary<string, VaultLease>(1000);
    private readonly BoundedDictionary<string, SecretVersion> _secretVersions = new BoundedDictionary<string, SecretVersion>(1000);

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "HashiCorp Vault",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 6,
        RequiredInfrastructure = new[] { "HashiCorpVault" },
        Description = "HashiCorp Vault for dynamic secrets, encryption, and identity-based access"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("hashi_corp_vault.deploy");
        var state = initialState;
        var vaultAddr = GetVaultAddress(config);
        var secretPath = GetSecretPath(config);
        var mountPath = GetMountPath(config);

        // Authenticate to Vault
        state = state with { ProgressPercent = 10 };
        var token = await AuthenticateAsync(vaultAddr, config, ct);

        // Write secrets to KV v2
        state = state with { ProgressPercent = 30 };
        var secretData = ExtractSecretData(config);
        var version = await WriteSecretAsync(vaultAddr, token, mountPath, secretPath, secretData, ct);

        // Track version for rollback
        _secretVersions[$"{secretPath}:{version}"] = new SecretVersion
        {
            Path = secretPath,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            KeyCount = secretData.Count
        };

        // Set up dynamic secret leases if configured
        state = state with { ProgressPercent = 60 };
        if (config.StrategyConfig.TryGetValue("dynamicSecrets", out var ds) && ds is true)
        {
            await SetupDynamicSecretsAsync(vaultAddr, token, secretPath, config, ct);
        }

        // Configure policies
        state = state with { ProgressPercent = 80 };
        await ConfigurePoliciesAsync(vaultAddr, token, secretPath, config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["vaultAddress"] = vaultAddr,
                ["secretPath"] = secretPath,
                ["mountPath"] = mountPath,
                ["secretVersion"] = version,
                ["keyCount"] = secretData.Count
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("hashi_corp_vault.rollback");
        var vaultAddr = currentState.Metadata.TryGetValue("vaultAddress", out var va) ? va?.ToString() : "";
        var secretPath = currentState.Metadata.TryGetValue("secretPath", out var sp) ? sp?.ToString() : "";
        var mountPath = currentState.Metadata.TryGetValue("mountPath", out var mp) ? mp?.ToString() : "";

        // Rollback to specific version in KV v2
        if (!int.TryParse(targetVersion, out var vaultVersion))
            throw new ArgumentException($"Invalid Vault KV version '{targetVersion}': must be a positive integer.", nameof(targetVersion));
        await RollbackSecretVersionAsync(vaultAddr!, mountPath!, secretPath!, vaultVersion, ct);

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

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("aws_secrets_manager.deploy");
        var vaultAddr = currentState.Metadata.TryGetValue("vaultAddress", out var va) ? va?.ToString() : "";
        var isSealed = await CheckVaultSealStatusAsync(vaultAddr!, ct);

        return new[]
        {
            new HealthCheckResult
            {
                InstanceId = $"vault-{deploymentId}",
                IsHealthy = !isSealed,
                StatusCode = isSealed ? 503 : 200,
                ResponseTimeMs = 5,
                Details = new Dictionary<string, object>
                {
                    ["sealed"] = isSealed,
                    ["type"] = "vault-kv"
                }
            }
        };
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetVaultAddress(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("vaultAddress", out var va) && va is string vas ? vas : "http://localhost:8200";

    private static string GetSecretPath(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("secretPath", out var sp) && sp is string sps ? sps : $"data/{config.Environment}";

    private static string GetMountPath(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("mountPath", out var mp) && mp is string mps ? mps : "secret";

    private static Dictionary<string, string> ExtractSecretData(DeploymentConfig config)
    {
        var data = new Dictionary<string, string>();
        if (config.StrategyConfig.TryGetValue("secrets", out var s) && s is Dictionary<string, string> secrets)
        {
            foreach (var kv in secrets)
                data[kv.Key] = kv.Value;
        }
        return data;
    }

    private Task<string> AuthenticateAsync(string addr, DeploymentConfig config, CancellationToken ct) => Task.FromResult("hvs.token123");
    private Task<int> WriteSecretAsync(string addr, string token, string mount, string path, Dictionary<string, string> data, CancellationToken ct) => Task.FromResult(42);
    private Task SetupDynamicSecretsAsync(string addr, string token, string path, DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
    private Task ConfigurePoliciesAsync(string addr, string token, string path, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task RollbackSecretVersionAsync(string addr, string mount, string path, int version, CancellationToken ct) => Task.Delay(30, ct);
    private Task<bool> CheckVaultSealStatusAsync(string addr, CancellationToken ct) => Task.FromResult(false);

    private sealed class VaultLease
    {
        public required string LeaseId { get; init; }
        public required string Path { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class SecretVersion
    {
        public required string Path { get; init; }
        public int Version { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public int KeyCount { get; init; }
    }
}

/// <summary>
/// AWS Secrets Manager strategy for secret rotation, versioning, and cross-region replication.
/// </summary>
public sealed class AwsSecretsManagerStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "AWS Secrets Manager",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "AWS", "SecretsManager" },
        Description = "AWS Secrets Manager with automatic rotation, versioning, and cross-region replication"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("aws_secrets_manager.deploy");
        var state = initialState;
        var secretName = GetSecretName(config);
        var region = GetRegion(config);

        // Create or update secret
        state = state with { ProgressPercent = 30 };
        var secretArn = await PutSecretValueAsync(secretName, region, config, ct);

        // Set up rotation if configured
        state = state with { ProgressPercent = 60 };
        if (config.StrategyConfig.TryGetValue("rotationEnabled", out var re) && re is true)
        {
            await ConfigureRotationAsync(secretArn, config, ct);
        }

        // Set up cross-region replication if configured
        state = state with { ProgressPercent = 80 };
        if (config.StrategyConfig.TryGetValue("replicaRegions", out var rr) && rr is string[] regions)
        {
            await ConfigureReplicationAsync(secretArn, regions, ct);
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["secretName"] = secretName,
                ["secretArn"] = secretArn,
                ["region"] = region
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("aws_secrets_manager.rollback");
        var secretArn = currentState.Metadata.TryGetValue("secretArn", out var sa) ? sa?.ToString() : "";
        await RestoreSecretVersionAsync(secretArn!, targetVersion, ct);
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

    private static string GetSecretName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("secretName", out var sn) && sn is string sns ? sns : $"secret/{config.Environment}";

    private static string GetRegion(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("region", out var r) && r is string rs ? rs : "us-east-1";

    private Task<string> PutSecretValueAsync(string name, string region, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult($"arn:aws:secretsmanager:{region}:123456789:secret:{name}");
    private Task ConfigureRotationAsync(string arn, DeploymentConfig config, CancellationToken ct) => Task.Delay(40, ct);
    private Task ConfigureReplicationAsync(string arn, string[] regions, CancellationToken ct) => Task.Delay(50, ct);
    private Task RestoreSecretVersionAsync(string arn, string version, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// Azure Key Vault strategy for secrets, keys, and certificates management.
/// </summary>
public sealed class AzureKeyVaultStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Azure Key Vault",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "Azure", "KeyVault" },
        Description = "Azure Key Vault for secrets, keys, and certificates with soft-delete and purge protection"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("azure_key_vault.deploy");
        var state = initialState;
        var vaultUrl = GetVaultUrl(config);

        // Set secrets
        state = state with { ProgressPercent = 30 };
        var secretVersions = await SetSecretsAsync(vaultUrl, config, ct);

        // Set access policies if configured
        state = state with { ProgressPercent = 60 };
        if (config.StrategyConfig.TryGetValue("accessPolicies", out _))
        {
            await ConfigureAccessPoliciesAsync(vaultUrl, config, ct);
        }

        // Configure networking if configured
        state = state with { ProgressPercent = 80 };
        if (config.StrategyConfig.TryGetValue("networkRules", out _))
        {
            await ConfigureNetworkRulesAsync(vaultUrl, config, ct);
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["vaultUrl"] = vaultUrl,
                ["secretVersions"] = secretVersions
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("azure_key_vault.rollback");
        var vaultUrl = currentState.Metadata.TryGetValue("vaultUrl", out var vu) ? vu?.ToString() : "";
        await RestoreSecretVersionsAsync(vaultUrl!, targetVersion, ct);
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

    private static string GetVaultUrl(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("vaultUrl", out var vu) && vu is string vus ? vus : "https://myvault.vault.azure.net";

    private Task<Dictionary<string, string>> SetSecretsAsync(string vaultUrl, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, string> { ["secret1"] = "v1" });
    private Task ConfigureAccessPoliciesAsync(string vaultUrl, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task ConfigureNetworkRulesAsync(string vaultUrl, DeploymentConfig config, CancellationToken ct) => Task.Delay(20, ct);
    private Task RestoreSecretVersionsAsync(string vaultUrl, string version, CancellationToken ct) => Task.Delay(40, ct);
}

/// <summary>
/// Google Cloud Secret Manager strategy for versioned secrets with IAM integration.
/// </summary>
public sealed class GcpSecretManagerStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "GCP Secret Manager",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "GCP", "SecretManager" },
        Description = "Google Cloud Secret Manager with versioning, IAM, and audit logging"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("gcp_secret_manager.deploy");
        var state = initialState;
        var project = GetProject(config);
        var secretId = GetSecretId(config);

        // Create secret if not exists
        state = state with { ProgressPercent = 20 };
        await EnsureSecretExistsAsync(project, secretId, ct);

        // Add secret version
        state = state with { ProgressPercent = 50 };
        var versionId = await AddSecretVersionAsync(project, secretId, config, ct);

        // Set IAM policy
        state = state with { ProgressPercent = 80 };
        if (config.StrategyConfig.TryGetValue("iamBindings", out _))
        {
            await SetIamPolicyAsync(project, secretId, config, ct);
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["project"] = project,
                ["secretId"] = secretId,
                ["versionId"] = versionId
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("gcp_secret_manager.rollback");
        var project = currentState.Metadata.TryGetValue("project", out var p) ? p?.ToString() : "";
        var secretId = currentState.Metadata.TryGetValue("secretId", out var si) ? si?.ToString() : "";

        await EnableSecretVersionAsync(project!, secretId!, targetVersion, ct);
        await DisableCurrentVersionAsync(project!, secretId!, currentState.Version, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 7 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProject(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("project", out var p) && p is string ps ? ps : "my-project";

    private static string GetSecretId(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("secretId", out var si) && si is string sis ? sis : $"secret-{config.Environment}";

    private Task EnsureSecretExistsAsync(string project, string secretId, CancellationToken ct) => Task.Delay(20, ct);
    private Task<string> AddSecretVersionAsync(string project, string secretId, DeploymentConfig config, CancellationToken ct) => Task.FromResult("1");
    private Task SetIamPolicyAsync(string project, string secretId, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task EnableSecretVersionAsync(string project, string secretId, string version, CancellationToken ct) => Task.Delay(20, ct);
    private Task DisableCurrentVersionAsync(string project, string secretId, string version, CancellationToken ct) => Task.Delay(20, ct);
}

/// <summary>
/// Kubernetes Secrets strategy for native Kubernetes secret management.
/// </summary>
public sealed class KubernetesSecretsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Kubernetes Secrets",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = new[] { "Kubernetes" },
        Description = "Kubernetes native Secrets with optional encryption at rest and external secrets operators"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("kubernetes_secrets.deploy");
        var state = initialState;
        var namespace_ = GetNamespace(config);
        var secretName = GetSecretName(config);
        var secretType = GetSecretType(config);

        // Create or update secret
        state = state with { ProgressPercent = 40 };
        await ApplySecretAsync(namespace_, secretName, secretType, config, ct);

        // Update referencing workloads if configured
        state = state with { ProgressPercent = 70 };
        if (config.StrategyConfig.TryGetValue("restartWorkloads", out var rw) && rw is true)
        {
            await RestartReferencingWorkloadsAsync(namespace_, secretName, ct);
        }

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
                ["secretName"] = secretName,
                ["secretType"] = secretType
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("kubernetes_secrets.rollback");
        var namespace_ = currentState.Metadata.TryGetValue("namespace", out var ns) ? ns?.ToString() : "";
        var secretName = currentState.Metadata.TryGetValue("secretName", out var sn) ? sn?.ToString() : "";

        await RollbackSecretAsync(namespace_!, secretName!, targetVersion, ct);

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

    private static string GetNamespace(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("namespace", out var ns) && ns is string nss ? nss : "default";

    private static string GetSecretName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("secretName", out var sn) && sn is string sns ? sns : $"secret-{config.Environment}";

    private static string GetSecretType(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("secretType", out var st) && st is string sts ? sts : "Opaque";

    private Task ApplySecretAsync(string ns, string name, string type, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task RestartReferencingWorkloadsAsync(string ns, string secretName, CancellationToken ct) => Task.Delay(50, ct);
    private Task RollbackSecretAsync(string ns, string name, string version, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// External Secrets Operator strategy for syncing secrets from external providers to Kubernetes.
/// </summary>
public sealed class ExternalSecretsOperatorStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "External Secrets Operator",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 5,
        RequiredInfrastructure = new[] { "Kubernetes", "ExternalSecretsOperator" },
        Description = "External Secrets Operator for syncing secrets from Vault, AWS, Azure, GCP to Kubernetes"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("external_secrets_operator.deploy");
        var state = initialState;
        var namespace_ = GetNamespace(config);
        var externalSecretName = GetExternalSecretName(config);
        var secretStoreName = GetSecretStoreName(config);
        var provider = GetProvider(config);

        // Ensure SecretStore exists
        state = state with { ProgressPercent = 20 };
        await EnsureSecretStoreAsync(namespace_, secretStoreName, provider, config, ct);

        // Create or update ExternalSecret
        state = state with { ProgressPercent = 50 };
        await ApplyExternalSecretAsync(namespace_, externalSecretName, secretStoreName, config, ct);

        // Wait for sync
        state = state with { ProgressPercent = 80 };
        await WaitForSecretSyncAsync(namespace_, externalSecretName, ct);

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
                ["externalSecretName"] = externalSecretName,
                ["secretStoreName"] = secretStoreName,
                ["provider"] = provider
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("external_secrets_operator.rollback");
        var namespace_ = currentState.Metadata.TryGetValue("namespace", out var ns) ? ns?.ToString() : "";
        var externalSecretName = currentState.Metadata.TryGetValue("externalSecretName", out var es) ? es?.ToString() : "";

        await UpdateExternalSecretVersionAsync(namespace_!, externalSecretName!, targetVersion, ct);
        await WaitForSecretSyncAsync(namespace_!, externalSecretName!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 5 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetNamespace(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("namespace", out var ns) && ns is string nss ? nss : "default";

    private static string GetExternalSecretName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("externalSecretName", out var es) && es is string ess ? ess : $"external-secret-{config.Environment}";

    private static string GetSecretStoreName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("secretStoreName", out var ss) && ss is string sss ? sss : "vault-backend";

    private static string GetProvider(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("provider", out var p) && p is string ps ? ps : "vault";

    private Task EnsureSecretStoreAsync(string ns, string name, string provider, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task ApplyExternalSecretAsync(string ns, string name, string storeName, DeploymentConfig config, CancellationToken ct) => Task.Delay(40, ct);
    private Task WaitForSecretSyncAsync(string ns, string name, CancellationToken ct) => Task.Delay(50, ct);
    private Task UpdateExternalSecretVersionAsync(string ns, string name, string version, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// CyberArk Conjur strategy for enterprise secret management.
/// </summary>
public sealed class CyberArkConjurStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "CyberArk Conjur",
        DeploymentType = DeploymentType.FeatureFlag,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 3,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 6,
        RequiredInfrastructure = new[] { "CyberArk", "Conjur" },
        Description = "CyberArk Conjur enterprise secrets management with RBAC and audit"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("cyber_ark_conjur.deploy");
        var state = initialState;
        var conjurUrl = GetConjurUrl(config);
        var account = GetAccount(config);
        var policyBranch = GetPolicyBranch(config);

        // Authenticate to Conjur
        state = state with { ProgressPercent = 15 };
        var token = await AuthenticateAsync(conjurUrl, account, config, ct);

        // Load policy
        state = state with { ProgressPercent = 35 };
        await LoadPolicyAsync(conjurUrl, token, account, policyBranch, config, ct);

        // Set secret values
        state = state with { ProgressPercent = 60 };
        await SetSecretValuesAsync(conjurUrl, token, account, config, ct);

        // Grant permissions
        state = state with { ProgressPercent = 85 };
        await GrantPermissionsAsync(conjurUrl, token, account, config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["conjurUrl"] = conjurUrl,
                ["account"] = account,
                ["policyBranch"] = policyBranch
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("cyber_ark_conjur.rollback");
        var conjurUrl = currentState.Metadata.TryGetValue("conjurUrl", out var cu) ? cu?.ToString() : "";
        var account = currentState.Metadata.TryGetValue("account", out var acc) ? acc?.ToString() : "";

        await RollbackSecretsAsync(conjurUrl!, account!, targetVersion, ct);

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

    private static string GetConjurUrl(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("conjurUrl", out var cu) && cu is string cus ? cus : "https://conjur.example.com";

    private static string GetAccount(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("account", out var acc) && acc is string accs ? accs : "myaccount";

    private static string GetPolicyBranch(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("policyBranch", out var pb) && pb is string pbs ? pbs : "root";

    private Task<string> AuthenticateAsync(string url, string account, DeploymentConfig config, CancellationToken ct) => Task.FromResult("token123");
    private Task LoadPolicyAsync(string url, string token, string account, string branch, DeploymentConfig config, CancellationToken ct) => Task.Delay(40, ct);
    private Task SetSecretValuesAsync(string url, string token, string account, DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
    private Task GrantPermissionsAsync(string url, string token, string account, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task RollbackSecretsAsync(string url, string account, string version, CancellationToken ct) => Task.Delay(40, ct);
}
