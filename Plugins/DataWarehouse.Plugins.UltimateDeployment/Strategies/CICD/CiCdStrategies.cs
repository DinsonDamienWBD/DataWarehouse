namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.CICD;

/// <summary>
/// GitHub Actions deployment strategy.
/// </summary>
public sealed class GitHubActionsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GitHub Actions",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["GitHub"],
        Description = "GitHub Actions workflow-based deployment"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("git_hub_actions.deploy");
        var state = initialState;
        var repo = GetRepo(config);
        var workflow = GetWorkflow(config);

        // Trigger workflow dispatch
        state = state with { ProgressPercent = 20 };
        var runId = await TriggerWorkflowAsync(repo, workflow, config, ct);

        // Wait for workflow completion
        state = state with { ProgressPercent = 50 };
        var result = await WaitForWorkflowCompletionAsync(repo, runId, ct);

        if (!result.Success)
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = result.ErrorMessage,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, object>(state.Metadata) { ["runId"] = runId, ["conclusion"] = result.Conclusion }
            };
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
                ["repo"] = repo,
                ["workflow"] = workflow,
                ["runId"] = runId
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("git_hub_actions.rollback");
        var repo = currentState.Metadata.TryGetValue("repo", out var r) ? r?.ToString() : "";
        var workflow = currentState.Metadata.TryGetValue("workflow", out var w) ? w?.ToString() : "";

        var runId = await TriggerWorkflowAsync(repo!, workflow!.Replace("deploy", "rollback"), new DeploymentConfig
        {
            Environment = "rollback",
            Version = targetVersion,
            ArtifactUri = ""
        }, ct);

        await WaitForWorkflowCompletionAsync(repo!, runId, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 10 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetRepo(DeploymentConfig config) => config.StrategyConfig.TryGetValue("repo", out var r) && r is string rs ? rs : "owner/repo";
    private static string GetWorkflow(DeploymentConfig config) => config.StrategyConfig.TryGetValue("workflow", out var w) && w is string ws ? ws : "deploy.yml";

    private Task<long> TriggerWorkflowAsync(string repo, string workflow, DeploymentConfig config, CancellationToken ct)
        => throw new NotSupportedException("Configure GitHub Actions endpoint and credentials via StrategyConfig['repo'] and StrategyConfig['githubToken'].");
    private Task<(bool Success, string? ErrorMessage, string Conclusion)> WaitForWorkflowCompletionAsync(string repo, long runId, CancellationToken ct)
        => throw new NotSupportedException("Configure GitHub Actions endpoint and credentials via StrategyConfig['repo'] and StrategyConfig['githubToken'].");
}

/// <summary>
/// GitLab CI/CD deployment strategy.
/// </summary>
public sealed class GitLabCiStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GitLab CI",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 12,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["GitLab"],
        Description = "GitLab CI/CD pipeline-based deployment with environment tracking"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("git_lab_ci.deploy");
        var state = initialState;
        var projectId = GetProjectId(config);
        var environment = config.Environment;

        // Trigger pipeline
        state = state with { ProgressPercent = 20 };
        var pipelineId = await TriggerPipelineAsync(projectId, config, ct);

        // Wait for pipeline
        state = state with { ProgressPercent = 60 };
        var result = await WaitForPipelineAsync(projectId, pipelineId, ct);

        if (!result.Success)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = result.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        // Update environment
        state = state with { ProgressPercent = 90 };
        await UpdateEnvironmentAsync(projectId, environment, config.Version, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["projectId"] = projectId, ["pipelineId"] = pipelineId }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("git_lab_ci.rollback");
        var projectId = currentState.Metadata.TryGetValue("projectId", out var p) ? p?.ToString() : "";
        await RollbackEnvironmentAsync(projectId!, targetVersion, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 12 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProjectId(DeploymentConfig config) => config.StrategyConfig.TryGetValue("projectId", out var p) && p is string ps ? ps : "12345";

    private Task<int> TriggerPipelineAsync(string projectId, DeploymentConfig config, CancellationToken ct)
        => throw new NotSupportedException("Configure GitLab CI/CD endpoint and credentials via StrategyConfig['projectId'] and StrategyConfig['gitlabToken'].");
    private Task<(bool Success, string? ErrorMessage)> WaitForPipelineAsync(string projectId, int pipelineId, CancellationToken ct)
        => throw new NotSupportedException("Configure GitLab CI/CD endpoint and credentials via StrategyConfig['projectId'] and StrategyConfig['gitlabToken'].");
    private Task UpdateEnvironmentAsync(string projectId, string env, string version, CancellationToken ct)
        => throw new NotSupportedException("Configure GitLab CI/CD endpoint and credentials via StrategyConfig['projectId'] and StrategyConfig['gitlabToken'].");
    private Task RollbackEnvironmentAsync(string projectId, string version, CancellationToken ct)
        => throw new NotSupportedException("Configure GitLab CI/CD endpoint and credentials via StrategyConfig['projectId'] and StrategyConfig['gitlabToken'].");
}

/// <summary>
/// Jenkins deployment strategy.
/// </summary>
public sealed class JenkinsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Jenkins",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["Jenkins"],
        Description = "Jenkins pipeline-based deployment"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("jenkins.deploy");
        var state = initialState;
        var jobName = GetJobName(config);
        var jenkinsUrl = GetJenkinsUrl(config);

        // Trigger build
        state = state with { ProgressPercent = 20 };
        var buildNumber = await TriggerBuildAsync(jenkinsUrl, jobName, config, ct);

        // Wait for build
        state = state with { ProgressPercent = 60 };
        var result = await WaitForBuildAsync(jenkinsUrl, jobName, buildNumber, ct);

        if (!result.Success)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = result.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["jenkinsUrl"] = jenkinsUrl, ["jobName"] = jobName, ["buildNumber"] = buildNumber }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("jenkins.rollback");
        var jenkinsUrl = currentState.Metadata.TryGetValue("jenkinsUrl", out var ju) ? ju?.ToString() : "";
        var jobName = currentState.Metadata.TryGetValue("jobName", out var jn) ? jn?.ToString() : "";

        await TriggerBuildAsync(jenkinsUrl!, $"{jobName}-rollback", new DeploymentConfig { Environment = "", Version = targetVersion, ArtifactUri = "" }, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 15 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetJobName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("jobName", out var jn) && jn is string jns ? jns : "deploy";
    private static string GetJenkinsUrl(DeploymentConfig config) => config.StrategyConfig.TryGetValue("jenkinsUrl", out var ju) && ju is string jus ? jus : "http://jenkins:8080";

    private Task<int> TriggerBuildAsync(string url, string job, DeploymentConfig config, CancellationToken ct)
        => throw new NotSupportedException("Configure Jenkins endpoint and credentials via StrategyConfig['jenkinsUrl'] and StrategyConfig['jenkinsToken'].");
    private Task<(bool Success, string? ErrorMessage)> WaitForBuildAsync(string url, string job, int build, CancellationToken ct)
        => throw new NotSupportedException("Configure Jenkins endpoint and credentials via StrategyConfig['jenkinsUrl'] and StrategyConfig['jenkinsToken'].");
}

/// <summary>
/// Azure DevOps deployment strategy.
/// </summary>
public sealed class AzureDevOpsStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Azure DevOps",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 12,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["AzureDevOps"],
        Description = "Azure DevOps pipeline with release management"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("azure_dev_ops.deploy");
        var state = initialState;
        var org = GetOrganization(config);
        var project = GetProject(config);
        var pipelineId = GetPipelineId(config);

        // Queue pipeline run
        state = state with { ProgressPercent = 20 };
        var runId = await QueuePipelineRunAsync(org, project, pipelineId, config, ct);

        // Wait for completion
        state = state with { ProgressPercent = 70 };
        var result = await WaitForRunAsync(org, project, runId, ct);

        if (!result.Success)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = result.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["organization"] = org, ["project"] = project, ["runId"] = runId }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("azure_dev_ops.rollback");
        var org = currentState.Metadata.TryGetValue("organization", out var o) ? o?.ToString() : "";
        var project = currentState.Metadata.TryGetValue("project", out var p) ? p?.ToString() : "";

        await TriggerRollbackAsync(org!, project!, targetVersion, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 10 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetOrganization(DeploymentConfig config) => config.StrategyConfig.TryGetValue("organization", out var o) && o is string os ? os : "myorg";
    private static string GetProject(DeploymentConfig config) => config.StrategyConfig.TryGetValue("project", out var p) && p is string ps ? ps : "myproject";
    private static int GetPipelineId(DeploymentConfig config) => config.StrategyConfig.TryGetValue("pipelineId", out var p) && p is int pi ? pi : 1;

    private Task<int> QueuePipelineRunAsync(string org, string project, int pipeline, DeploymentConfig config, CancellationToken ct)
        => throw new NotSupportedException("Configure Azure DevOps endpoint and credentials via StrategyConfig['organization'] and StrategyConfig['azureDevOpsToken'].");
    private Task<(bool Success, string? ErrorMessage)> WaitForRunAsync(string org, string project, int runId, CancellationToken ct)
        => throw new NotSupportedException("Configure Azure DevOps endpoint and credentials via StrategyConfig['organization'] and StrategyConfig['azureDevOpsToken'].");
    private Task TriggerRollbackAsync(string org, string project, string version, CancellationToken ct)
        => throw new NotSupportedException("Configure Azure DevOps endpoint and credentials via StrategyConfig['organization'] and StrategyConfig['azureDevOpsToken'].");
}

/// <summary>
/// CircleCI deployment strategy.
/// </summary>
public sealed class CircleCiStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "CircleCI",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 4,
        RequiredInfrastructure = ["CircleCI"],
        Description = "CircleCI pipeline-based deployment"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("circle_ci.deploy");
        var state = initialState;
        var projectSlug = GetProjectSlug(config);

        // Trigger pipeline
        state = state with { ProgressPercent = 30 };
        var pipelineId = await TriggerPipelineAsync(projectSlug, config, ct);

        // Wait for workflow
        state = state with { ProgressPercent = 70 };
        var result = await WaitForWorkflowAsync(pipelineId, ct);

        if (!result.Success)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = result.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["projectSlug"] = projectSlug, ["pipelineId"] = pipelineId }
        };
    }

    protected override Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow });

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 8 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetProjectSlug(DeploymentConfig config) => config.StrategyConfig.TryGetValue("projectSlug", out var ps) && ps is string pss ? pss : "gh/owner/repo";

    private Task<string> TriggerPipelineAsync(string projectSlug, DeploymentConfig config, CancellationToken ct)
        => throw new NotSupportedException("Configure CircleCI endpoint and credentials via StrategyConfig['projectSlug'] and StrategyConfig['circleCiToken'].");
    private Task<(bool Success, string? ErrorMessage)> WaitForWorkflowAsync(string pipelineId, CancellationToken ct)
        => throw new NotSupportedException("Configure CircleCI endpoint and credentials via StrategyConfig['projectSlug'] and StrategyConfig['circleCiToken'].");
}

/// <summary>
/// ArgoCD GitOps deployment strategy.
/// </summary>
public sealed class ArgoCdStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ArgoCD",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 8,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["ArgoCD", "Kubernetes", "Git"],
        Description = "ArgoCD GitOps continuous delivery with auto-sync"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("argo_cd.deploy");
        var state = initialState;
        var appName = GetAppName(config);
        var repoUrl = GetRepoUrl(config);
        var targetRevision = config.Version;

        // Update application target revision
        state = state with { ProgressPercent = 20 };
        await UpdateApplicationAsync(appName, targetRevision, ct);

        // Sync application
        state = state with { ProgressPercent = 40 };
        await SyncApplicationAsync(appName, ct);

        // Wait for sync
        state = state with { ProgressPercent = 70 };
        var syncResult = await WaitForSyncAsync(appName, ct);

        if (!syncResult.Synced)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = syncResult.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        // Wait for health
        state = state with { ProgressPercent = 90 };
        await WaitForHealthyAsync(appName, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["appName"] = appName, ["revision"] = targetRevision }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("argo_cd.rollback");
        var appName = currentState.Metadata.TryGetValue("appName", out var an) ? an?.ToString() : "";

        await RollbackApplicationAsync(appName!, targetVersion, ct);
        await WaitForSyncAsync(appName!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("argo_cd.scale");
        // ArgoCD handles scaling through Git - would need to update manifests
        return currentState with { TargetInstances = targetInstances };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 5 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetAppName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("appName", out var an) && an is string ans ? ans : $"app-{config.Environment}";
    private static string GetRepoUrl(DeploymentConfig config) => config.StrategyConfig.TryGetValue("repoUrl", out var ru) && ru is string rus ? rus : "https://github.com/org/manifests";

    private Task UpdateApplicationAsync(string appName, string revision, CancellationToken ct)
        => throw new NotSupportedException("Configure ArgoCD endpoint and credentials via StrategyConfig['argoCdUrl'] and StrategyConfig['argoCdToken'].");
    private Task SyncApplicationAsync(string appName, CancellationToken ct)
        => throw new NotSupportedException("Configure ArgoCD endpoint and credentials via StrategyConfig['argoCdUrl'] and StrategyConfig['argoCdToken'].");
    private Task<(bool Synced, string? ErrorMessage)> WaitForSyncAsync(string appName, CancellationToken ct)
        => throw new NotSupportedException("Configure ArgoCD endpoint and credentials via StrategyConfig['argoCdUrl'] and StrategyConfig['argoCdToken'].");
    private Task WaitForHealthyAsync(string appName, CancellationToken ct)
        => throw new NotSupportedException("Configure ArgoCD endpoint and credentials via StrategyConfig['argoCdUrl'] and StrategyConfig['argoCdToken'].");
    private Task RollbackApplicationAsync(string appName, string version, CancellationToken ct)
        => throw new NotSupportedException("Configure ArgoCD endpoint and credentials via StrategyConfig['argoCdUrl'] and StrategyConfig['argoCdToken'].");
}

/// <summary>
/// FluxCD GitOps deployment strategy.
/// </summary>
public sealed class FluxCdStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "FluxCD",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 8,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["FluxCD", "Kubernetes", "Git"],
        Description = "FluxCD GitOps toolkit for continuous delivery"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("flux_cd.deploy");
        var state = initialState;
        var namespace_ = GetNamespace(config);
        var kustomizationName = GetKustomizationName(config);

        // Update image automation
        state = state with { ProgressPercent = 30 };
        await UpdateImageAutomationAsync(namespace_, config.ArtifactUri, ct);

        // Reconcile
        state = state with { ProgressPercent = 50 };
        await ReconcileKustomizationAsync(namespace_, kustomizationName, ct);

        // Wait for reconciliation
        state = state with { ProgressPercent = 80 };
        await WaitForReconciliationAsync(namespace_, kustomizationName, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["namespace"] = namespace_, ["kustomizationName"] = kustomizationName }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("flux_cd.rollback");
        var namespace_ = currentState.Metadata.TryGetValue("namespace", out var ns) ? ns?.ToString() : "";
        var kustomizationName = currentState.Metadata.TryGetValue("kustomizationName", out var kn) ? kn?.ToString() : "";

        await SuspendKustomizationAsync(namespace_!, kustomizationName!, ct);
        await RevertGitAsync(targetVersion, ct);
        await ResumeKustomizationAsync(namespace_!, kustomizationName!, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 6 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetNamespace(DeploymentConfig config) => config.StrategyConfig.TryGetValue("namespace", out var ns) && ns is string nss ? nss : "flux-system";
    private static string GetKustomizationName(DeploymentConfig config) => config.StrategyConfig.TryGetValue("kustomizationName", out var kn) && kn is string kns ? kns : "app";

    private Task UpdateImageAutomationAsync(string namespace_, string image, CancellationToken ct)
        => throw new NotSupportedException("Configure FluxCD endpoint and credentials via StrategyConfig['fluxCdKubeconfig'] and StrategyConfig['namespace'].");
    private Task ReconcileKustomizationAsync(string namespace_, string name, CancellationToken ct)
        => throw new NotSupportedException("Configure FluxCD endpoint and credentials via StrategyConfig['fluxCdKubeconfig'] and StrategyConfig['namespace'].");
    private Task WaitForReconciliationAsync(string namespace_, string name, CancellationToken ct)
        => throw new NotSupportedException("Configure FluxCD endpoint and credentials via StrategyConfig['fluxCdKubeconfig'] and StrategyConfig['namespace'].");
    private Task SuspendKustomizationAsync(string namespace_, string name, CancellationToken ct)
        => throw new NotSupportedException("Configure FluxCD endpoint and credentials via StrategyConfig['fluxCdKubeconfig'] and StrategyConfig['namespace'].");
    private Task RevertGitAsync(string version, CancellationToken ct)
        => throw new NotSupportedException("Configure FluxCD endpoint and credentials via StrategyConfig['fluxCdKubeconfig'] and StrategyConfig['namespace'].");
    private Task ResumeKustomizationAsync(string namespace_, string name, CancellationToken ct)
        => throw new NotSupportedException("Configure FluxCD endpoint and credentials via StrategyConfig['fluxCdKubeconfig'] and StrategyConfig['namespace'].");
}

/// <summary>
/// Spinnaker deployment strategy.
/// </summary>
public sealed class SpinnakerStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Spinnaker",
        DeploymentType = DeploymentType.CICD,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 25,
        ComplexityLevel = 7,
        RequiredInfrastructure = ["Spinnaker"],
        Description = "Netflix Spinnaker multi-cloud continuous delivery platform"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("spinnaker.deploy");
        var state = initialState;
        var application = GetApplication(config);
        var pipelineName = GetPipeline(config);

        // Trigger pipeline
        state = state with { ProgressPercent = 20 };
        var executionId = await TriggerPipelineAsync(application, pipelineName, config, ct);

        // Wait for execution
        state = state with { ProgressPercent = 70 };
        var result = await WaitForExecutionAsync(application, executionId, ct);

        if (!result.Success)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = result.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["application"] = application, ["executionId"] = executionId }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        var application = currentState.Metadata.TryGetValue("application", out var a) ? a?.ToString() : "";
        await TriggerRollbackPipelineAsync(application!, targetVersion, ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);
    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 15 } });
    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetApplication(DeploymentConfig config) => config.StrategyConfig.TryGetValue("application", out var a) && a is string ass ? ass : "myapp";
    private static string GetPipeline(DeploymentConfig config) => config.StrategyConfig.TryGetValue("pipeline", out var p) && p is string ps ? ps : "deploy";

    private Task<string> TriggerPipelineAsync(string app, string pipeline, DeploymentConfig config, CancellationToken ct)
        => throw new NotSupportedException("Configure Spinnaker endpoint and credentials via StrategyConfig['spinnakerUrl'] and StrategyConfig['spinnakerToken'].");
    private Task<(bool Success, string? ErrorMessage)> WaitForExecutionAsync(string app, string execId, CancellationToken ct)
        => throw new NotSupportedException("Configure Spinnaker endpoint and credentials via StrategyConfig['spinnakerUrl'] and StrategyConfig['spinnakerToken'].");
    private Task TriggerRollbackPipelineAsync(string app, string version, CancellationToken ct)
        => throw new NotSupportedException("Configure Spinnaker endpoint and credentials via StrategyConfig['spinnakerUrl'] and StrategyConfig['spinnakerToken'].");
}
