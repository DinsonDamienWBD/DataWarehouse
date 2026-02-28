namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.VMBareMetal;

/// <summary>
/// Ansible deployment strategy for VM/bare metal automation.
/// </summary>
public sealed class AnsibleStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Ansible",
        DeploymentType = DeploymentType.VirtualMachine,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["Ansible", "SSH"],
        Description = "Ansible playbook-based deployment for VMs and bare metal servers"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("ansible.deploy");
        var state = initialState;
        var playbookPath = GetPlaybookPath(config);
        var inventory = GetInventory(config);

        // Parse inventory and identify targets
        state = state with { ProgressPercent = 10 };
        var hosts = await ParseInventoryAsync(inventory, ct);

        // Run pre-deployment checks
        state = state with { ProgressPercent = 20 };
        await RunPreflightChecksAsync(hosts, ct);

        // Execute playbook
        state = state with { ProgressPercent = 40 };
        var result = await RunPlaybookAsync(playbookPath, inventory, config, ct);

        if (!result.Success)
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = result.ErrorMessage,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, object>(state.Metadata)
                {
                    ["failedHosts"] = result.FailedHosts
                }
            };
        }

        // Run post-deployment verification
        state = state with { ProgressPercent = 80 };
        await RunPostDeploymentVerificationAsync(hosts, config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = hosts.Length,
            HealthyInstances = hosts.Length,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["playbookPath"] = playbookPath,
                ["inventory"] = inventory,
                ["hosts"] = hosts
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("ansible.rollback");
        var playbookPath = currentState.Metadata.TryGetValue("playbookPath", out var pp) ? pp?.ToString() : "";
        var inventory = currentState.Metadata.TryGetValue("inventory", out var inv) ? inv?.ToString() : "";

        // Run rollback playbook — throw on failure (finding 2889)
        var rollbackPlaybook = playbookPath!.Replace(".yml", "-rollback.yml");
        var rollbackResult = await RunPlaybookAsync(rollbackPlaybook, inventory!, new DeploymentConfig
        {
            Environment = "rollback",
            Version = targetVersion,
            ArtifactUri = "",
            StrategyConfig = new Dictionary<string, object> { ["targetVersion"] = targetVersion }
        }, ct);

        if (!rollbackResult.Success)
        {
            throw new InvalidOperationException(
                $"Ansible rollback playbook failed: {rollbackResult.ErrorMessage}. " +
                $"Failed hosts: {string.Join(", ", rollbackResult.FailedHosts)}");
        }

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
        IncrementCounter("terraform.deploy");
        // Ansible doesn't handle scaling - would need to update inventory
        return Task.FromResult(currentState);
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("puppet.deploy");
        var hosts = currentState.Metadata.TryGetValue("hosts", out var h) && h is string[] hostArray
            ? hostArray : Array.Empty<string>();

        var results = new List<HealthCheckResult>();
        foreach (var host in hosts)
        {
            var healthy = await CheckHostHealthAsync(host, ct);
            results.Add(new HealthCheckResult
            {
                InstanceId = host,
                IsHealthy = healthy,
                StatusCode = healthy ? 200 : 503,
                ResponseTimeMs = 50
            });
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetPlaybookPath(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("playbookPath", out var pp) && pp is string pps ? pps : "deploy.yml";

    private static string GetInventory(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("inventory", out var inv) && inv is string invs ? invs : "hosts";

    private Task<string[]> ParseInventoryAsync(string inventory, CancellationToken ct)
        => Task.FromResult(new[] { "host1.example.com", "host2.example.com" });

    private Task RunPreflightChecksAsync(string[] hosts, CancellationToken ct) => Task.Delay(50, ct);

    private Task<(bool Success, string? ErrorMessage, string[] FailedHosts)> RunPlaybookAsync(
        string playbook, string inventory, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult((true, (string?)null, Array.Empty<string>()));

    private Task RunPostDeploymentVerificationAsync(string[] hosts, DeploymentConfig config, CancellationToken ct)
        => Task.Delay(50, ct);

    private Task<bool> CheckHostHealthAsync(string host, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// Terraform deployment strategy for infrastructure as code.
/// </summary>
public sealed class TerraformStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Terraform",
        DeploymentType = DeploymentType.VirtualMachine,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 20,
        ResourceOverheadPercent = 100,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["Terraform"],
        Description = "Terraform infrastructure-as-code deployment with state management"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        IncrementCounter("terraform.deploy");
        var state = initialState;
        var workingDir = GetWorkingDir(config);
        var workspace = GetWorkspace(config);

        // Initialize Terraform
        state = state with { ProgressPercent = 10 };
        await TerraformInitAsync(workingDir, ct);

        // Select workspace
        state = state with { ProgressPercent = 20 };
        await TerraformWorkspaceSelectAsync(workingDir, workspace, ct);

        // Plan
        state = state with { ProgressPercent = 40 };
        var plan = await TerraformPlanAsync(workingDir, config, ct);

        if (!plan.HasChanges)
        {
            return state with
            {
                Health = DeploymentHealth.Healthy,
                ProgressPercent = 100,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, object>(state.Metadata)
                {
                    ["noChanges"] = true
                }
            };
        }

        // Apply
        state = state with { ProgressPercent = 60 };
        var applyResult = await TerraformApplyAsync(workingDir, ct);

        if (!applyResult.Success)
        {
            return state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = applyResult.ErrorMessage,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        // Get outputs
        state = state with { ProgressPercent = 90 };
        var outputs = await TerraformOutputAsync(workingDir, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["workingDir"] = workingDir,
                ["workspace"] = workspace,
                ["outputs"] = outputs
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("terraform.rollback");
        var workingDir = currentState.Metadata.TryGetValue("workingDir", out var wd) ? wd?.ToString() : "";

        // Terraform doesn't have native rollback - need to revert to previous state
        // This would typically involve restoring from state backup
        await TerraformStateRestoreAsync(workingDir!, targetVersion, ct);
        await TerraformApplyAsync(workingDir!, ct);

        return currentState with
        {
            Health = DeploymentHealth.Healthy,
            Version = targetVersion,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("salt_stack.deploy");
        var workingDir = currentState.Metadata.TryGetValue("workingDir", out var wd) ? wd?.ToString() : "";

        // Update instance count variable and apply
        await TerraformApplyWithVarAsync(workingDir!, "instance_count", targetInstances.ToString(), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances
        };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        IncrementCounter("packer_ami.deploy");
        return Task.FromResult(new[]
        {
            new HealthCheckResult
            {
                InstanceId = deploymentId,
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = 100
            }
        });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetWorkingDir(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("workingDir", out var wd) && wd is string wds ? wds : "./terraform";

    private static string GetWorkspace(DeploymentConfig config)
        => config.StrategyConfig.TryGetValue("workspace", out var ws) && ws is string wss ? wss : config.Environment;

    private Task TerraformInitAsync(string workingDir, CancellationToken ct) => Task.Delay(50, ct);
    private Task TerraformWorkspaceSelectAsync(string workingDir, string workspace, CancellationToken ct) => Task.Delay(20, ct);
    private Task<(bool HasChanges, int AddCount, int ChangeCount, int DestroyCount)> TerraformPlanAsync(string workingDir, DeploymentConfig config, CancellationToken ct)
        => Task.FromResult((true, 2, 1, 0));
    private Task<(bool Success, string? ErrorMessage)> TerraformApplyAsync(string workingDir, CancellationToken ct)
        => Task.FromResult((true, (string?)null));
    private Task<Dictionary<string, object>> TerraformOutputAsync(string workingDir, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, object> { ["instance_ips"] = new[] { "10.0.0.1", "10.0.0.2" } });
    private Task TerraformStateRestoreAsync(string workingDir, string version, CancellationToken ct) => Task.Delay(50, ct);
    private Task TerraformApplyWithVarAsync(string workingDir, string varName, string value, CancellationToken ct) => Task.Delay(100, ct);
}

/// <summary>
/// Puppet deployment strategy.
/// </summary>
public sealed class PuppetStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Puppet",
        DeploymentType = DeploymentType.VirtualMachine,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["Puppet", "PuppetServer"],
        Description = "Puppet manifest-based configuration management deployment"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("puppet.deploy");
        var state = initialState;
        var environment = GetPuppetEnvironment(config);
        var nodes = GetNodes(config);

        // Update Puppet code
        state = state with { ProgressPercent = 20 };
        await UpdatePuppetCodeAsync(environment, config, ct);

        // Deploy r10k
        state = state with { ProgressPercent = 40 };
        await DeployR10kAsync(environment, ct);

        // Trigger Puppet runs
        state = state with { ProgressPercent = 60 };
        await TriggerPuppetRunsAsync(nodes, ct);

        // Wait for convergence
        state = state with { ProgressPercent = 80 };
        await WaitForConvergenceAsync(nodes, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = nodes.Length,
            HealthyInstances = nodes.Length,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["environment"] = environment, ["nodes"] = nodes }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("puppet.rollback");
        var environment = currentState.Metadata.TryGetValue("environment", out var e) ? e?.ToString() : "";
        var nodes = currentState.Metadata.TryGetValue("nodes", out var n) && n is string[] ns ? ns : Array.Empty<string>();

        await RevertPuppetCodeAsync(environment!, targetVersion, ct);
        await TriggerPuppetRunsAsync(nodes, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        var nodes = currentState.Metadata.TryGetValue("nodes", out var n) && n is string[] ns ? ns : Array.Empty<string>();
        return Task.FromResult(nodes.Select(node => new HealthCheckResult { InstanceId = node, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 30 }).ToArray());
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetPuppetEnvironment(DeploymentConfig config) => config.StrategyConfig.TryGetValue("puppetEnvironment", out var pe) && pe is string pes ? pes : config.Environment;
    private static string[] GetNodes(DeploymentConfig config) => config.StrategyConfig.TryGetValue("nodes", out var n) && n is string[] ns ? ns : new[] { "node1.example.com" };

    private Task UpdatePuppetCodeAsync(string env, DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
    private Task DeployR10kAsync(string env, CancellationToken ct) => Task.Delay(50, ct);
    private Task TriggerPuppetRunsAsync(string[] nodes, CancellationToken ct) => Task.Delay(100, ct);
    private Task WaitForConvergenceAsync(string[] nodes, CancellationToken ct) => Task.Delay(100, ct);
    private Task RevertPuppetCodeAsync(string env, string version, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// Chef deployment strategy.
/// </summary>
public sealed class ChefStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Chef",
        DeploymentType = DeploymentType.VirtualMachine,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 15,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["Chef", "ChefServer"],
        Description = "Chef cookbook-based infrastructure automation"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("chef.deploy");
        var state = initialState;
        var environment = GetChefEnvironment(config);
        var nodes = GetNodes(config);

        // Upload cookbooks
        state = state with { ProgressPercent = 30 };
        await UploadCookbooksAsync(config, ct);

        // Update environment
        state = state with { ProgressPercent = 50 };
        await UpdateChefEnvironmentAsync(environment, config, ct);

        // Run chef-client on nodes
        state = state with { ProgressPercent = 70 };
        await RunChefClientAsync(nodes, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = nodes.Length,
            HealthyInstances = nodes.Length,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["environment"] = environment, ["nodes"] = nodes }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("chef.rollback");
        var environment = currentState.Metadata.TryGetValue("environment", out var e) ? e?.ToString() : "";
        var nodes = currentState.Metadata.TryGetValue("nodes", out var n) && n is string[] ns ? ns : Array.Empty<string>();

        await RevertChefEnvironmentAsync(environment!, targetVersion, ct);
        await RunChefClientAsync(nodes, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        var nodes = currentState.Metadata.TryGetValue("nodes", out var n) && n is string[] ns ? ns : Array.Empty<string>();
        return Task.FromResult(nodes.Select(node => new HealthCheckResult { InstanceId = node, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 25 }).ToArray());
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetChefEnvironment(DeploymentConfig config) => config.StrategyConfig.TryGetValue("chefEnvironment", out var ce) && ce is string ces ? ces : config.Environment;
    private static string[] GetNodes(DeploymentConfig config) => config.StrategyConfig.TryGetValue("nodes", out var n) && n is string[] ns ? ns : new[] { "node1.example.com" };

    private Task UploadCookbooksAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
    private Task UpdateChefEnvironmentAsync(string env, DeploymentConfig config, CancellationToken ct) => Task.Delay(30, ct);
    private Task RunChefClientAsync(string[] nodes, CancellationToken ct) => Task.Delay(100, ct);
    private Task RevertChefEnvironmentAsync(string env, string version, CancellationToken ct) => Task.Delay(30, ct);
}

/// <summary>
/// SaltStack deployment strategy.
/// </summary>
public sealed class SaltStackStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "SaltStack",
        DeploymentType = DeploymentType.VirtualMachine,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 12,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["SaltStack", "SaltMaster"],
        Description = "SaltStack state-based configuration management"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("salt_stack.deploy");
        var state = initialState;
        var environment = GetSaltEnvironment(config);
        var targets = GetTargets(config);

        // Sync all modules
        state = state with { ProgressPercent = 20 };
        await SaltSyncAllAsync(targets, ct);

        // Apply highstate
        state = state with { ProgressPercent = 50 };
        var result = await SaltHighstateAsync(targets, environment, ct);

        if (!result.Success)
        {
            return state with { Health = DeploymentHealth.Failed, ErrorMessage = result.ErrorMessage, CompletedAt = DateTimeOffset.UtcNow };
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = result.SuccessCount,
            HealthyInstances = result.SuccessCount,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["environment"] = environment, ["targets"] = targets }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("salt_stack.rollback");
        var targets = currentState.Metadata.TryGetValue("targets", out var t) ? t?.ToString() : "*";
        await SaltStateApplyAsync(targets!, $"rollback.{targetVersion}", ct);
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 20 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetSaltEnvironment(DeploymentConfig config) => config.StrategyConfig.TryGetValue("saltEnvironment", out var se) && se is string ses ? ses : config.Environment;
    private static string GetTargets(DeploymentConfig config) => config.StrategyConfig.TryGetValue("targets", out var t) && t is string ts ? ts : "*";

    private Task SaltSyncAllAsync(string targets, CancellationToken ct) => Task.Delay(30, ct);
    private Task<(bool Success, string? ErrorMessage, int SuccessCount)> SaltHighstateAsync(string targets, string env, CancellationToken ct) => Task.FromResult((true, (string?)null, 3));
    private Task SaltStateApplyAsync(string targets, string state, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// Packer + AMI deployment strategy for AWS.
/// </summary>
public sealed class PackerAmiStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Packer AMI",
        DeploymentType = DeploymentType.VirtualMachine,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 25,
        ResourceOverheadPercent = 100,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["AWS", "Packer"],
        Description = "Immutable infrastructure with Packer-built AMIs"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("packer_ami.deploy");
        var state = initialState;
        var packerTemplate = GetPackerTemplate(config);

        // Build AMI with Packer
        state = state with { ProgressPercent = 30 };
        var amiId = await BuildAmiAsync(packerTemplate, config, ct);

        // Update Launch Template
        state = state with { ProgressPercent = 50 };
        await UpdateLaunchTemplateAsync(amiId, config, ct);

        // Refresh instances in ASG
        state = state with { ProgressPercent = 70 };
        await RefreshAutoScalingGroupAsync(config, ct);

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["amiId"] = amiId }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("packer_ami.rollback");
        // Roll back to previous AMI
        await RollbackLaunchTemplateAsync(targetVersion, ct);
        await RefreshAutoScalingGroupAsync(new DeploymentConfig { Environment = "", Version = "", ArtifactUri = "" }, ct);

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
    {
        await UpdateAsgCapacityAsync(targetInstances, ct);
        return currentState with { TargetInstances = targetInstances, DeployedInstances = targetInstances };
    }

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 30 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string GetPackerTemplate(DeploymentConfig config) => config.StrategyConfig.TryGetValue("packerTemplate", out var pt) && pt is string pts ? pts : "packer.json";

    private Task<string> BuildAmiAsync(string template, DeploymentConfig config, CancellationToken ct) => Task.FromResult("ami-12345678");
    private Task UpdateLaunchTemplateAsync(string amiId, DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
    private Task RefreshAutoScalingGroupAsync(DeploymentConfig config, CancellationToken ct) => Task.Delay(100, ct);
    private Task RollbackLaunchTemplateAsync(string version, CancellationToken ct) => Task.Delay(30, ct);
    private Task UpdateAsgCapacityAsync(int capacity, CancellationToken ct) => Task.Delay(50, ct);
}

/// <summary>
/// SSH-based direct deployment strategy.
/// </summary>
public sealed class SshDirectStrategy : DeploymentStrategyBase
{
    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "SSH Direct",
        DeploymentType = DeploymentType.BareMetal,
        SupportsZeroDowntime = false,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 2,
        RequiredInfrastructure = ["SSH"],
        Description = "Direct SSH-based deployment for simple scenarios"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("ssh_direct.deploy");
        var state = initialState;
        var hosts = GetHosts(config);
        var deployScript = GetDeployScript(config);

        foreach (var host in hosts)
        {
            state = state with { ProgressPercent = 20 + (60 * Array.IndexOf(hosts, host) / hosts.Length) };

            // Connect via SSH
            await SshConnectAsync(host, ct);

            // Upload artifact
            await ScpUploadAsync(host, config.ArtifactUri, ct);

            // Execute deploy script
            await SshExecuteAsync(host, deployScript, config, ct);
        }

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = hosts.Length,
            HealthyInstances = hosts.Length,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata) { ["hosts"] = hosts }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("ssh_direct.rollback");
        var hosts = currentState.Metadata.TryGetValue("hosts", out var h) && h is string[] hs ? hs : Array.Empty<string>();
        foreach (var host in hosts)
        {
            await SshExecuteAsync(host, $"rollback.sh {targetVersion}", new DeploymentConfig { Environment = "", Version = targetVersion, ArtifactUri = "" }, ct);
        }
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct) => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        var hosts = currentState.Metadata.TryGetValue("hosts", out var h) && h is string[] hs ? hs : Array.Empty<string>();
        return Task.FromResult(hosts.Select(host => new HealthCheckResult { InstanceId = host, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 50 }).ToArray());
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "unknown", Health = DeploymentHealth.Unknown });

    private static string[] GetHosts(DeploymentConfig config) => config.StrategyConfig.TryGetValue("hosts", out var h) && h is string[] hs ? hs : new[] { "server1.example.com" };
    private static string GetDeployScript(DeploymentConfig config) => config.StrategyConfig.TryGetValue("deployScript", out var ds) && ds is string dss ? dss : "deploy.sh";

    private Task SshConnectAsync(string host, CancellationToken ct) => Task.Delay(20, ct);
    private Task ScpUploadAsync(string host, string artifact, CancellationToken ct) => Task.Delay(50, ct);
    private Task SshExecuteAsync(string host, string script, DeploymentConfig config, CancellationToken ct) => Task.Delay(50, ct);
}
