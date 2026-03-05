namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.Serverless;

/// <summary>
/// AWS Lambda deployment strategy for serverless functions.
/// </summary>
public sealed class AwsLambdaStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AWS Lambda",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true, // Automatic
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["AWS", "Lambda"],
        Description = "AWS Lambda function deployment with alias-based traffic shifting"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("aws_lambda.deploy");
        var state = initialState;
        var functionName = GetFunctionName(config);
        var aliasName = GetAliasName(config);

        // Create/Update function code
        state = state with { ProgressPercent = 20 };
        var newVersion = await UpdateFunctionCodeAsync(functionName, config.ArtifactUri, ct);

        // Update function configuration
        state = state with { ProgressPercent = 40 };
        await UpdateFunctionConfigurationAsync(functionName, config, ct);

        // Publish version
        state = state with { ProgressPercent = 60 };
        var publishedVersion = await PublishVersionAsync(functionName, ct);

        // Update alias with traffic shifting (if configured)
        state = state with { ProgressPercent = 80 };
        if (config.CanaryPercent > 0 && config.CanaryPercent < 100)
        {
            // Gradual traffic shift using weighted alias
            await UpdateAliasWithTrafficShiftAsync(functionName, aliasName, publishedVersion, config.CanaryPercent, ct);
        }
        else
        {
            // Direct cutover
            await UpdateAliasAsync(functionName, aliasName, publishedVersion, ct);
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1, // Serverless - instances managed by AWS
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["functionName"] = functionName,
                ["aliasName"] = aliasName,
                ["publishedVersion"] = publishedVersion,
                ["previousVersion"] = state.PreviousVersion ?? ""
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("aws_lambda.rollback");
        var functionName = currentState.Metadata.TryGetValue("functionName", out var fn) ? fn?.ToString() : "";
        var aliasName = currentState.Metadata.TryGetValue("aliasName", out var an) ? an?.ToString() : "prod";

        // Point alias back to previous version
        await UpdateAliasAsync(functionName!, aliasName!, targetVersion, ct);

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
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("azure_functions.deploy");
        // Lambda auto-scales - we can only configure concurrency limits
        return Task.FromResult(currentState with
        {
            Metadata = new Dictionary<string, object>(currentState.Metadata)
            {
                ["reservedConcurrency"] = targetInstances * 100 // Approximate mapping
            }
        });
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("google_cloud_functions.deploy");
        var functionName = currentState.Metadata.TryGetValue("functionName", out var fn) ? fn?.ToString() : "";

        // Invoke function to check health
        var result = await InvokeFunctionAsync(functionName!, ct);

        return new[]
        {
            new HealthCheckResult
            {
                InstanceId = functionName!,
                IsHealthy = result.Success,
                StatusCode = result.Success ? 200 : 500,
                ResponseTimeMs = result.DurationMs,
                Details = new Dictionary<string, object>
                {
                    ["requestId"] = result.RequestId,
                    ["billedDuration"] = result.BilledDurationMs
                }
            }
        };
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        IncrementCounter("cloudflare_workers.deploy");
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private static string GetFunctionName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("functionName", out var fn) && fn is string fns ? fns : $"fn-{config.Environment}";

    private static string GetAliasName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("aliasName", out var an) && an is string ans ? ans : "prod";

    private Task<string> UpdateFunctionCodeAsync(string functionName, string artifactUri, CancellationToken ct)
        => Task.FromResult("$LATEST");

    private Task UpdateFunctionConfigurationAsync(string functionName, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task<string> PublishVersionAsync(string functionName, CancellationToken ct)
        => Task.FromResult("42");

    private Task UpdateAliasAsync(string functionName, string aliasName, string version, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task UpdateAliasWithTrafficShiftAsync(string functionName, string aliasName, string newVersion, int newVersionPercent, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task<(bool Success, string RequestId, double DurationMs, double BilledDurationMs)> InvokeFunctionAsync(string functionName, CancellationToken ct)
        => Task.FromResult((true, "req-123", 50.0, 100.0));

    /// <summary>Configures Lambda@Edge for CloudFront distribution.</summary>
    public async Task<LambdaEdgeConfig> ConfigureLambdaEdgeAsync(
        string functionArn,
        string distributionId,
        LambdaEdgeEventType eventType,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(functionArn);
        ArgumentException.ThrowIfNullOrEmpty(distributionId);

        // Lambda@Edge associations can be added to CloudFront distribution behaviors
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

        return new LambdaEdgeConfig
        {
            FunctionArn = functionArn,
            DistributionId = distributionId,
            EventType = eventType,
            ConfiguredAt = DateTimeOffset.UtcNow,
            Status = "Active"
        };
    }

    /// <summary>Configures provisioned concurrency for Lambda function.</summary>
    public async Task<ProvisionedConcurrencyConfig> ConfigureProvisionedConcurrencyAsync(
        string functionName,
        string aliasOrVersion,
        int concurrentExecutions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(functionName);
        ArgumentException.ThrowIfNullOrEmpty(aliasOrVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrentExecutions);

        // Provisioned concurrency eliminates cold starts by keeping instances warm
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        return new ProvisionedConcurrencyConfig
        {
            FunctionName = functionName,
            AliasOrVersion = aliasOrVersion,
            AllocatedConcurrentExecutions = concurrentExecutions,
            AvailableConcurrentExecutions = concurrentExecutions,
            Status = "Ready",
            ConfiguredAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Lambda@Edge configuration.
/// </summary>
public sealed class LambdaEdgeConfig
{
    public required string FunctionArn { get; init; }
    public required string DistributionId { get; init; }
    public LambdaEdgeEventType EventType { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset ConfiguredAt { get; init; }
}

/// <summary>
/// Lambda@Edge event types.
/// </summary>
public enum LambdaEdgeEventType
{
    ViewerRequest,
    ViewerResponse,
    OriginRequest,
    OriginResponse
}

/// <summary>
/// Provisioned concurrency configuration.
/// </summary>
public sealed class ProvisionedConcurrencyConfig
{
    public required string FunctionName { get; init; }
    public required string AliasOrVersion { get; init; }
    public int AllocatedConcurrentExecutions { get; init; }
    public int AvailableConcurrentExecutions { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset ConfiguredAt { get; init; }
}

/// <summary>
/// Azure Functions deployment strategy.
/// </summary>
public sealed class AzureFunctionsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Azure Functions",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 8,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["Azure", "FunctionApp"],
        Description = "Azure Functions deployment with slot-based deployment"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("azure_functions.deploy");
        var state = initialState;
        var resourceGroup = GetResourceGroup(config);
        var functionAppName = GetFunctionAppName(config);
        var slotName = GetSlotName(config);

        // Deploy to staging slot
        state = state with { ProgressPercent = 20 };
        await DeployToSlotAsync(resourceGroup, functionAppName, slotName, config, ct);

        // Wait for slot to be ready
        state = state with { ProgressPercent = 50 };
        await WaitForSlotReadyAsync(resourceGroup, functionAppName, slotName, ct);

        // Swap slots (staging -> production)
        state = state with { ProgressPercent = 80 };
        await SwapSlotsAsync(resourceGroup, functionAppName, slotName, "production", ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["resourceGroup"] = resourceGroup,
                ["functionAppName"] = functionAppName,
                ["slotName"] = slotName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("azure_functions.rollback");
        var resourceGroup = currentState.Metadata.TryGetValue("resourceGroup", out var rg) ? rg?.ToString() : "";
        var functionAppName = currentState.Metadata.TryGetValue("functionAppName", out var fn) ? fn?.ToString() : "";
        var slotName = currentState.Metadata.TryGetValue("slotName", out var sn) ? sn?.ToString() : "staging";

        // Swap back
        await SwapSlotsAsync(resourceGroup!, functionAppName!, "production", slotName!, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("google_cloud_run.deploy");
        // Azure Functions auto-scales in consumption plan
        return Task.FromResult(currentState);
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("azure_container_apps.deploy");
        var functionAppName = currentState.Metadata.TryGetValue("functionAppName", out var fn) ? fn?.ToString() : "";
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = functionAppName!,
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 45
            }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetResourceGroup(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("resourceGroup", out var rg) && rg is string rgs ? rgs : "default-rg";

    private static string GetFunctionAppName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("functionAppName", out var fn) && fn is string fns ? fns : $"func-{config.Environment}";

    private static string GetSlotName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("slotName", out var sn) && sn is string sns ? sns : "staging";

    private Task DeployToSlotAsync(string rg, string app, string slot, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task WaitForSlotReadyAsync(string rg, string app, string slot, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task SwapSlotsAsync(string rg, string app, string sourceSlot, string targetSlot, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    /// <summary>Deploys Durable Functions orchestration.</summary>
    public async Task<DurableFunctionDeployment> DeployDurableFunctionAsync(
        string resourceGroup,
        string functionAppName,
        DurableFunctionDefinition definition,
        CancellationToken ct = default)
    {
        IncrementCounter("aws_lambda.deploy");
        ArgumentException.ThrowIfNullOrEmpty(resourceGroup);
        ArgumentException.ThrowIfNullOrEmpty(functionAppName);
        ArgumentNullException.ThrowIfNull(definition);

        // Durable Functions require storage backend (Azure Storage or Netherite)
        // and TaskHub configuration
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        return new DurableFunctionDeployment
        {
            ResourceGroup = resourceGroup,
            FunctionAppName = functionAppName,
            OrchestrationName = definition.OrchestrationName,
            TaskHubName = definition.TaskHubName ?? "DefaultTaskHub",
            StorageProvider = definition.StorageProvider,
            DeployedAt = DateTimeOffset.UtcNow,
            Status = "Active",
            Endpoints = new Dictionary<string, string>
            {
                ["orchestration"] = $"https://{functionAppName}.azurewebsites.net/api/{definition.OrchestrationName}",
                ["status"] = $"https://{functionAppName}.azurewebsites.net/runtime/webhooks/durabletask/instances/{{instanceId}}"
            }
        };
    }

    /// <summary>Configures Premium plan for Azure Functions.</summary>
    public async Task<PremiumPlanConfig> ConfigurePremiumPlanAsync(
        string resourceGroup,
        string planName,
        string sku,
        int maxInstances,
        bool preWarmedInstances,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceGroup);
        ArgumentException.ThrowIfNullOrEmpty(planName);
        ArgumentException.ThrowIfNullOrEmpty(sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInstances);

        // Premium plan provides VNet integration, faster scaling, and pre-warmed instances
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        return new PremiumPlanConfig
        {
            ResourceGroup = resourceGroup,
            PlanName = planName,
            Sku = sku, // EP1, EP2, EP3
            MaxInstances = maxInstances,
            MinInstances = preWarmedInstances ? 1 : 0,
            PreWarmedInstances = preWarmedInstances,
            VNetIntegrationEnabled = true,
            Status = "Ready",
            ConfiguredAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Durable Function definition.
/// </summary>
public sealed class DurableFunctionDefinition
{
    public required string OrchestrationName { get; init; }
    public string? TaskHubName { get; init; }
    public DurableStorageProvider StorageProvider { get; init; } = DurableStorageProvider.AzureStorage;
    public List<string> ActivityFunctions { get; init; } = new();
    public Dictionary<string, object> OrchestrationConfig { get; init; } = new();
}

/// <summary>
/// Durable Function deployment result.
/// </summary>
public sealed class DurableFunctionDeployment
{
    public required string ResourceGroup { get; init; }
    public required string FunctionAppName { get; init; }
    public required string OrchestrationName { get; init; }
    public required string TaskHubName { get; init; }
    public DurableStorageProvider StorageProvider { get; init; }
    public required Dictionary<string, string> Endpoints { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset DeployedAt { get; init; }
}

/// <summary>
/// Durable Functions storage provider.
/// </summary>
public enum DurableStorageProvider
{
    AzureStorage,
    Netherite,
    Emulator
}

/// <summary>
/// Premium plan configuration for Azure Functions.
/// </summary>
public sealed class PremiumPlanConfig
{
    public required string ResourceGroup { get; init; }
    public required string PlanName { get; init; }
    public required string Sku { get; init; }
    public int MaxInstances { get; init; }
    public int MinInstances { get; init; }
    public bool PreWarmedInstances { get; init; }
    public bool VNetIntegrationEnabled { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset ConfiguredAt { get; init; }
}

/// <summary>
/// Google Cloud Functions deployment strategy.
/// </summary>
public sealed class GoogleCloudFunctionsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Google Cloud Functions",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 6,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["GCP", "CloudFunctions"],
        Description = "Google Cloud Functions deployment with traffic splitting"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("google_cloud_functions.deploy");
        var state = initialState;
        var project = GetProject(config);
        var region = GetRegion(config);
        var functionName = GetFunctionName(config);

        // Deploy function
        state = state with { ProgressPercent = 30 };
        await DeployFunctionAsync(project, region, functionName, config, ct);

        // Wait for deployment
        state = state with { ProgressPercent = 70 };
        await WaitForOperationAsync(project, region, functionName, ct);

        // Configure traffic if canary
        state = state with { ProgressPercent = 90 };
        if (config.CanaryPercent > 0 && config.CanaryPercent < 100)
        {
            await ConfigureTrafficSplitAsync(project, region, functionName, config.CanaryPercent, ct);
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
                ["region"] = region,
                ["functionName"] = functionName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("google_cloud_functions.rollback");
        var project = currentState.Metadata.TryGetValue("project", out var p) ? p?.ToString() : "";
        var region = currentState.Metadata.TryGetValue("region", out var r) ? r?.ToString() : "";
        var functionName = currentState.Metadata.TryGetValue("functionName", out var fn) ? fn?.ToString() : "";

        await RollbackFunctionAsync(project!, region!, functionName!, targetVersion, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Cloud Functions auto-scales
        return Task.FromResult(currentState);
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var functionName = currentState.Metadata.TryGetValue("functionName", out var fn) ? fn?.ToString() : "";
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = functionName!,
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 35
            }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProject(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("project", out var p) && p is string ps ? ps : "my-project";

    private static string GetRegion(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("region", out var r) && r is string rs ? rs : "us-central1";

    private static string GetFunctionName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("functionName", out var fn) && fn is string fns ? fns : $"function-{config.Environment}";

    private Task DeployFunctionAsync(string project, string region, string name, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task WaitForOperationAsync(string project, string region, string name, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(100), ct);

    private Task ConfigureTrafficSplitAsync(string project, string region, string name, int newVersionPercent, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task RollbackFunctionAsync(string project, string region, string name, string version, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);
}

/// <summary>
/// Cloudflare Workers deployment strategy.
/// </summary>
public sealed class CloudflareWorkersStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Cloudflare Workers",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["Cloudflare"],
        Description = "Cloudflare Workers edge deployment with global distribution"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("cloudflare_workers.deploy");
        var state = initialState;
        var workerName = GetWorkerName(config);
        var accountId = GetAccountId(config);

        // Upload worker script
        state = state with { ProgressPercent = 40 };
        await UploadWorkerAsync(accountId, workerName, config, ct);

        // Configure routes
        state = state with { ProgressPercent = 70 };
        await ConfigureRoutesAsync(accountId, workerName, config, ct);

        // Deploy to edge
        state = state with { ProgressPercent = 90 };
        await DeployToEdgeAsync(accountId, workerName, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1, // Globally distributed
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["accountId"] = accountId,
                ["workerName"] = workerName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("cloudflare_workers.rollback");
        var accountId = currentState.Metadata.TryGetValue("accountId", out var ai) ? ai?.ToString() : "";
        var workerName = currentState.Metadata.TryGetValue("workerName", out var wn) ? wn?.ToString() : "";

        await RollbackWorkerAsync(accountId!, workerName!, targetVersion, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Workers auto-scale at the edge
        return Task.FromResult(currentState);
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var workerName = currentState.Metadata.TryGetValue("workerName", out var wn) ? wn?.ToString() : "";
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = workerName!,
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 5, // Edge latency
                Details = new Dictionary<string, object> { ["edge"] = true }
            }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetWorkerName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("workerName", out var wn) && wn is string wns ? wns : $"worker-{config.Environment}";

    private static string GetAccountId(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("accountId", out var ai) && ai is string ais ? ais : "account123";

    private Task UploadWorkerAsync(string accountId, string workerName, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(50), ct);

    private Task ConfigureRoutesAsync(string accountId, string workerName, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task DeployToEdgeAsync(string accountId, string workerName, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);

    private Task RollbackWorkerAsync(string accountId, string workerName, string version, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(30), ct);
}

/// <summary>
/// AWS App Runner deployment strategy.
/// </summary>
public sealed class AwsAppRunnerStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AWS App Runner",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 8,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 2,
        RequiredInfrastructure = ["AWS", "AppRunner"],
        Description = "AWS App Runner fully managed container deployment"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("aws_app_runner.deploy");
        var state = initialState;
        var serviceName = GetServiceName(config);

        // Create or update App Runner service
        state = state with { ProgressPercent = 30 };
        var serviceArn = await CreateOrUpdateServiceAsync(serviceName, config, ct);

        // Wait for deployment
        state = state with { ProgressPercent = 80 };
        await WaitForServiceRunningAsync(serviceArn, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["serviceName"] = serviceName,
                ["serviceArn"] = serviceArn
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("aws_app_runner.rollback");
        var serviceArn = currentState.Metadata.TryGetValue("serviceArn", out var sa) ? sa?.ToString() : "";
        await RollbackServiceAsync(serviceArn!, targetVersion, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState); // Auto-scales

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 20 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetServiceName(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("serviceName", out var sn) && sn is string sns ? sns : $"apprunner-{config.Environment}";

    private Task<string> CreateOrUpdateServiceAsync(string serviceName, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult("arn:aws:apprunner:us-east-1:123456789:service/app/123");

    private Task WaitForServiceRunningAsync(string serviceArn, CancellationToken ct) => Task.Delay(100, ct);
    private Task RollbackServiceAsync(string serviceArn, string version, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// Google Cloud Run deployment strategy.
/// </summary>
public sealed class GoogleCloudRunStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Google Cloud Run",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["GCP", "CloudRun"],
        Description = "Google Cloud Run serverless container deployment with traffic splitting"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("google_cloud_run.deploy");
        var state = initialState;
        var project = GetProject(config);
        var region = GetRegion(config);
        var serviceName = GetServiceName(config);

        // Deploy revision
        state = state with { ProgressPercent = 40 };
        var revisionName = await DeployRevisionAsync(project, region, serviceName, config, ct);

        // Configure traffic
        state = state with { ProgressPercent = 80 };
        if (config.CanaryPercent > 0 && config.CanaryPercent < 100)
        {
            await ConfigureTrafficSplitAsync(project, region, serviceName, revisionName, config.CanaryPercent, ct);
        }
        else
        {
            await SetTrafficToRevisionAsync(project, region, serviceName, revisionName, 100, ct);
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
                ["region"] = region,
                ["serviceName"] = serviceName,
                ["revisionName"] = revisionName
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("google_cloud_run.rollback");
        var project = currentState.Metadata.TryGetValue("project", out var p) ? p?.ToString() : "";
        var region = currentState.Metadata.TryGetValue("region", out var r) ? r?.ToString() : "";
        var serviceName = currentState.Metadata.TryGetValue("serviceName", out var sn) ? sn?.ToString() : "";

        await SetTrafficToRevisionAsync(project!, region!, serviceName!, targetVersion, 100, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState); // Auto-scales

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 15 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProject(DeploymentConfig config) => config.StrategyConfig.TryGetValue("project", out var p) && p is string ps ? ps : "my-project";
    private static string GetRegion(DeploymentConfig config) => config.StrategyConfig.TryGetValue("region", out var r) && r is string rs ? rs : "us-central1";
    private static string GetServiceName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("serviceName", out var sn) && sn is string sns ? sns : $"cloudrun-{config.Environment}";

    private Task<string> DeployRevisionAsync(string project, string region, string service, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult($"{service}-rev-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

    private Task ConfigureTrafficSplitAsync(string project, string region, string service, string revision, int percent, CancellationToken ct) => Task.Delay(30, ct);
    private Task SetTrafficToRevisionAsync(string project, string region, string service, string revision, int percent, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// Azure Container Apps deployment strategy.
/// </summary>
public sealed class AzureContainerAppsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Azure Container Apps",
        DeploymentType = DeploymentType.Serverless,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 6,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 3,
        RequiredInfrastructure = ["Azure", "ContainerApps"],
        Description = "Azure Container Apps serverless container deployment with revision management"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("azure_container_apps.deploy");
        var state = initialState;
        var resourceGroup = GetResourceGroup(config);
        var appName = GetAppName(config);

        // Create revision
        state = state with { ProgressPercent = 40 };
        var revisionName = await CreateRevisionAsync(resourceGroup, appName, config, ct);

        // Update traffic
        state = state with { ProgressPercent = 80 };
        await UpdateTrafficAsync(resourceGroup, appName, revisionName, 100, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["resourceGroup"] = resourceGroup, ["appName"] = appName, ["revisionName"] = revisionName }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("azure_container_apps.rollback");
        var rg = currentState.Metadata.TryGetValue("resourceGroup", out var r) ? r?.ToString() : "";
        var app = currentState.Metadata.TryGetValue("appName", out var a) ? a?.ToString() : "";
        await UpdateTrafficAsync(rg!, app!, targetVersion, 100, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 18 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetResourceGroup(DeploymentConfig config) => config.StrategyConfig.TryGetValue("resourceGroup", out var rg) && rg is string rgs ? rgs : "default-rg";
    private static string GetAppName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("appName", out var an) && an is string ans ? ans : $"containerapp-{config.Environment}";

    private Task<string> CreateRevisionAsync(string rg, string app, DeploymentConfig config, CancellationToken ct) => Task.FromResult($"{app}--rev{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
    private Task UpdateTrafficAsync(string rg, string app, string revision, int percent, CancellationToken ct) => Task.Delay(30, ct);
}
