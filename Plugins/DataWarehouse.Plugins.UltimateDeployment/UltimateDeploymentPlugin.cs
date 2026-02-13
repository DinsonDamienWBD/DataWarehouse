using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDeployment;

/// <summary>
/// Ultimate Deployment Plugin - Comprehensive deployment orchestration with 65+ deployment strategies.
///
/// Implements deployment strategies across categories:
/// - Deployment Patterns: Blue/green, canary, rolling update, recreate, A/B testing, shadow deployment
/// - Container Orchestration: Kubernetes, Docker Swarm, Nomad, ECS, AKS, GKE, EKS
/// - Serverless: AWS Lambda, Azure Functions, Google Cloud Functions, Cloudflare Workers
/// - VM/Bare Metal: Ansible, Terraform, Puppet, Chef, SaltStack
/// - CI/CD Integration: GitHub Actions, GitLab CI, Jenkins, Azure DevOps, CircleCI, ArgoCD, FluxCD
/// - Feature Flags: LaunchDarkly, Split.io, Unleash, Flagsmith, custom feature flags
/// - Hot Reload: Assembly reload, configuration reload, plugin hot swap
/// - Rollback: Automatic rollback, manual rollback, version pinning, snapshot restore
///
/// Features:
/// - Strategy pattern for extensibility
/// - Auto-discovery of strategies
/// - Health check orchestration
/// - Automatic rollback on failure
/// - Traffic shifting support
/// - Multi-cloud deployment
/// - Intelligence-aware deployment recommendations
/// </summary>
public sealed class UltimateDeploymentPlugin : InfrastructurePluginBase, IDisposable
{
    private readonly ConcurrentDictionary<string, IDeploymentStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DeploymentState> _activeDeployments = new();
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly object _statsLock = new();
    private bool _disposed;

    // Statistics
    private long _totalDeployments;
    private long _successfulDeployments;
    private long _failedDeployments;
    private long _rollbackCount;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.deployment.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Deployment";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string InfrastructureDomain => "Deployment";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Gets the strategy registry.
    /// </summary>
    public IReadOnlyDictionary<string, IDeploymentStrategy> Strategies => _strategies;

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate deployment plugin providing 65+ deployment strategies including blue/green, canary, " +
        "rolling update, container orchestration (Kubernetes, Docker Swarm, ECS, AKS, GKE), serverless " +
        "(Lambda, Azure Functions, Cloud Functions), VM/bare metal (Ansible, Terraform), CI/CD integration, " +
        "feature flags, hot reload, and automatic rollback.";

    /// <summary>
    /// Semantic tags for AI discovery.
    /// </summary>
    public string[] SemanticTags =>
    [
        "deployment", "orchestration", "kubernetes", "docker", "serverless",
        "blue-green", "canary", "rollback", "ci-cd", "devops", "infrastructure"
    ];

    /// <summary>
    /// Initializes the Ultimate Deployment plugin.
    /// </summary>
    public UltimateDeploymentPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    /// <summary>
    /// Registers a deployment strategy.
    /// </summary>
    public void RegisterStrategy(IDeploymentStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var key = strategy.Characteristics.StrategyName;
        _strategies[key] = strategy;

        if (strategy is DeploymentStrategyBase baseStrategy && MessageBus != null)
        {
            baseStrategy.ConfigureIntelligence(MessageBus);
        }
    }

    /// <summary>
    /// Gets a strategy by name.
    /// </summary>
    public IDeploymentStrategy? GetStrategy(string strategyName)
    {
        _strategies.TryGetValue(strategyName, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets all registered strategy names.
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredStrategies() =>
        _strategies.Keys.ToArray();

    /// <summary>
    /// Deploys using the specified strategy.
    /// </summary>
    public async Task<DeploymentState> DeployAsync(
        string strategyName,
        DeploymentConfig config,
        CancellationToken ct = default)
    {
        var strategy = GetStrategy(strategyName)
            ?? throw new ArgumentException($"Strategy '{strategyName}' not found");

        Interlocked.Increment(ref _totalDeployments);
        IncrementUsageStats(strategyName);

        try
        {
            var state = await strategy.DeployAsync(config, ct);

            if (state.Health == DeploymentHealth.Healthy)
            {
                Interlocked.Increment(ref _successfulDeployments);
            }
            else if (state.Health == DeploymentHealth.Failed)
            {
                Interlocked.Increment(ref _failedDeployments);
            }

            _activeDeployments[state.DeploymentId] = state;
            return state;
        }
        catch
        {
            Interlocked.Increment(ref _failedDeployments);
            throw;
        }
    }

    /// <summary>
    /// Gets the state of a deployment.
    /// </summary>
    public async Task<DeploymentState?> GetDeploymentStateAsync(
        string deploymentId,
        CancellationToken ct = default)
    {
        if (_activeDeployments.TryGetValue(deploymentId, out var cachedState))
        {
            return cachedState;
        }

        // Try to find the strategy that owns this deployment
        foreach (var strategy in _strategies.Values)
        {
            try
            {
                var state = await strategy.GetStateAsync(deploymentId, ct);
                if (state.Health != DeploymentHealth.Unknown)
                {
                    _activeDeployments[deploymentId] = state;
                    return state;
                }
            }
            catch
            {
                // Strategy doesn't know this deployment
            }
        }

        return null;
    }

    /// <summary>
    /// Performs health checks on a deployment.
    /// </summary>
    public async Task<HealthCheckResult[]> HealthCheckAsync(
        string deploymentId,
        CancellationToken ct = default)
    {
        // Find the strategy that owns this deployment
        if (_activeDeployments.TryGetValue(deploymentId, out var state))
        {
            var strategyName = state.Metadata.TryGetValue("strategy", out var sn)
                ? sn?.ToString()
                : null;

            if (strategyName != null && _strategies.TryGetValue(strategyName, out var strategy))
            {
                return await strategy.HealthCheckAsync(deploymentId, ct);
            }
        }

        // Try all strategies
        foreach (var strategy in _strategies.Values)
        {
            try
            {
                var results = await strategy.HealthCheckAsync(deploymentId, ct);
                if (results.Length > 0)
                    return results;
            }
            catch
            {
                // Strategy doesn't know this deployment
            }
        }

        return Array.Empty<HealthCheckResult>();
    }

    /// <summary>
    /// Rolls back a deployment.
    /// </summary>
    public async Task<DeploymentState> RollbackAsync(
        string deploymentId,
        string? targetVersion = null,
        CancellationToken ct = default)
    {
        var state = await GetDeploymentStateAsync(deploymentId, ct)
            ?? throw new ArgumentException($"Deployment '{deploymentId}' not found");

        var strategyName = state.Metadata.TryGetValue("strategy", out var sn)
            ? sn?.ToString()
            : null;

        IDeploymentStrategy? strategy = strategyName != null ? GetStrategy(strategyName) : null;
        if (strategy == null)
            throw new InvalidOperationException("Cannot determine strategy for rollback");

        Interlocked.Increment(ref _rollbackCount);

        var newState = await strategy.RollbackAsync(deploymentId, targetVersion, ct);
        _activeDeployments[deploymentId] = newState;

        return newState;
    }

    /// <summary>
    /// Scales a deployment.
    /// </summary>
    public async Task<DeploymentState> ScaleAsync(
        string deploymentId,
        int targetInstances,
        CancellationToken ct = default)
    {
        var state = await GetDeploymentStateAsync(deploymentId, ct)
            ?? throw new ArgumentException($"Deployment '{deploymentId}' not found");

        var strategyName = state.Metadata.TryGetValue("strategy", out var sn)
            ? sn?.ToString()
            : null;

        IDeploymentStrategy? strategy = strategyName != null ? GetStrategy(strategyName) : null;
        if (strategy == null)
            throw new InvalidOperationException("Cannot determine strategy for scaling");

        var newState = await strategy.ScaleAsync(deploymentId, targetInstances, ct);
        _activeDeployments[deploymentId] = newState;

        return newState;
    }

    /// <summary>
    /// Recommends the best deployment strategy based on requirements.
    /// </summary>
    public IDeploymentStrategy RecommendStrategy(
        bool requireZeroDowntime = true,
        bool requireInstantRollback = false,
        bool requireTrafficShifting = false,
        string? preferredInfrastructure = null,
        int maxComplexity = 10)
    {
        var candidates = _strategies.Values
            .Where(s => !requireZeroDowntime || s.Characteristics.SupportsZeroDowntime)
            .Where(s => !requireInstantRollback || s.Characteristics.SupportsInstantRollback)
            .Where(s => !requireTrafficShifting || s.Characteristics.SupportsTrafficShifting)
            .Where(s => s.Characteristics.ComplexityLevel <= maxComplexity)
            .Where(s => preferredInfrastructure == null ||
                        s.Characteristics.RequiredInfrastructure.Length == 0 ||
                        s.Characteristics.RequiredInfrastructure.Contains(preferredInfrastructure, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s.Characteristics.ComplexityLevel)
            .ThenBy(s => s.Characteristics.TypicalDeploymentTimeMinutes)
            .ThenBy(s => s.Characteristics.ResourceOverheadPercent)
            .ToList();

        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("No strategy matches the specified requirements");
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        response.Metadata["RegisteredStrategies"] = _strategies.Count.ToString();
        response.Metadata["DeploymentPatterns"] = _strategies.Values
            .Count(s => s.Characteristics.DeploymentType <= DeploymentType.Shadow).ToString();
        response.Metadata["ContainerStrategies"] = _strategies.Values
            .Count(s => s.Characteristics.DeploymentType == DeploymentType.ContainerOrchestration).ToString();
        response.Metadata["ServerlessStrategies"] = _strategies.Values
            .Count(s => s.Characteristics.DeploymentType == DeploymentType.Serverless).ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "deployment.deploy", DisplayName = "Deploy", Description = "Deploy using specified strategy" },
            new() { Name = "deployment.rollback", DisplayName = "Rollback", Description = "Rollback to previous version" },
            new() { Name = "deployment.scale", DisplayName = "Scale", Description = "Scale deployment instances" },
            new() { Name = "deployment.health-check", DisplayName = "Health Check", Description = "Check deployment health" },
            new() { Name = "deployment.list-strategies", DisplayName = "List Strategies", Description = "List available deployment strategies" },
            new() { Name = "deployment.recommend", DisplayName = "Recommend Strategy", Description = "Get strategy recommendation based on requirements" },
            new() { Name = "deployment.status", DisplayName = "Get Status", Description = "Get deployment status" }
        ];
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = $"{Id}.deploy",
                    DisplayName = $"{Name} - Deploy",
                    Description = "Deploy applications using various strategies",
                    Category = SDK.Contracts.CapabilityCategory.Deployment,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = ["deployment", "orchestration", "devops"]
                },
                new()
                {
                    CapabilityId = $"{Id}.rollback",
                    DisplayName = $"{Name} - Rollback",
                    Description = "Rollback deployments to previous versions",
                    Category = SDK.Contracts.CapabilityCategory.Deployment,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = ["deployment", "rollback", "recovery"]
                }
            };

            // Add strategy-specific capabilities
            foreach (var strategy in _strategies.Values)
            {
                if (strategy is DeploymentStrategyBase baseStrategy)
                {
                    capabilities.Add(baseStrategy.GetStrategyCapability());
                }
                else
                {
                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"{Id}.strategy.{strategy.Characteristics.StrategyName.ToLowerInvariant().Replace(" ", "-")}",
                        DisplayName = strategy.Characteristics.StrategyName,
                        Description = strategy.Characteristics.Description ?? $"{strategy.Characteristics.StrategyName} deployment strategy",
                        Category = SDK.Contracts.CapabilityCategory.Deployment,
                        SubCategory = strategy.Characteristics.DeploymentType.ToString(),
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = ["deployment", "strategy", strategy.Characteristics.DeploymentType.ToString().ToLowerInvariant()]
                    });
                }
            }

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        var strategies = _strategies.Values.ToList();
        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.strategies",
            Topic = "deployment.strategies",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"{strategies.Count} deployment strategies available",
            Payload = new Dictionary<string, object>
            {
                ["count"] = strategies.Count,
                ["types"] = strategies.Select(s => s.Characteristics.DeploymentType.ToString()).Distinct().ToArray(),
                ["zeroDowntimeCount"] = strategies.Count(s => s.Characteristics.SupportsZeroDowntime),
                ["instantRollbackCount"] = strategies.Count(s => s.Characteristics.SupportsInstantRollback),
                ["trafficShiftingCount"] = strategies.Count(s => s.Characteristics.SupportsTrafficShifting)
            },
            Tags = ["deployment", "strategies", "summary"]
        });

        // Add individual strategy knowledge
        foreach (var strategy in strategies)
        {
            if (strategy is DeploymentStrategyBase baseStrategy)
            {
                knowledge.Add(baseStrategy.GetStrategyKnowledge());
            }
        }

        return knowledge;
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _strategies.Count;
        metadata["TotalDeployments"] = Interlocked.Read(ref _totalDeployments);
        metadata["SuccessfulDeployments"] = Interlocked.Read(ref _successfulDeployments);
        metadata["FailedDeployments"] = Interlocked.Read(ref _failedDeployments);
        metadata["RollbackCount"] = Interlocked.Read(ref _rollbackCount);
        metadata["ActiveDeployments"] = _activeDeployments.Count;
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "deployment.deploy" => HandleDeployAsync(message),
            "deployment.rollback" => HandleRollbackAsync(message),
            "deployment.scale" => HandleScaleAsync(message),
            "deployment.health-check" => HandleHealthCheckAsync(message),
            "deployment.list-strategies" => HandleListStrategiesAsync(message),
            "deployment.recommend" => HandleRecommendAsync(message),
            "deployment.status" => HandleStatusAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Message Handlers

    private async Task HandleDeployAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategy", out var strategyObj) || strategyObj is not string strategyName)
            throw new ArgumentException("Missing 'strategy' parameter");

        var config = ExtractDeploymentConfig(message.Payload);
        var state = await DeployAsync(strategyName, config);

        message.Payload["result"] = state;
        message.Payload["deploymentId"] = state.DeploymentId;
    }

    private async Task HandleRollbackAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("deploymentId", out var idObj) || idObj is not string deploymentId)
            throw new ArgumentException("Missing 'deploymentId' parameter");

        var targetVersion = message.Payload.TryGetValue("targetVersion", out var tvObj) && tvObj is string tv
            ? tv : null;

        var state = await RollbackAsync(deploymentId, targetVersion);
        message.Payload["result"] = state;
    }

    private async Task HandleScaleAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("deploymentId", out var idObj) || idObj is not string deploymentId)
            throw new ArgumentException("Missing 'deploymentId' parameter");

        if (!message.Payload.TryGetValue("targetInstances", out var tiObj) ||
            !int.TryParse(tiObj?.ToString(), out var targetInstances))
            throw new ArgumentException("Missing or invalid 'targetInstances' parameter");

        var state = await ScaleAsync(deploymentId, targetInstances);
        message.Payload["result"] = state;
    }

    private async Task HandleHealthCheckAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("deploymentId", out var idObj) || idObj is not string deploymentId)
            throw new ArgumentException("Missing 'deploymentId' parameter");

        var results = await HealthCheckAsync(deploymentId);
        message.Payload["results"] = results;
        message.Payload["healthyCount"] = results.Count(r => r.IsHealthy);
        message.Payload["totalCount"] = results.Length;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _strategies.Values.Select(s => new Dictionary<string, object>
        {
            ["name"] = s.Characteristics.StrategyName,
            ["type"] = s.Characteristics.DeploymentType.ToString(),
            ["supportsZeroDowntime"] = s.Characteristics.SupportsZeroDowntime,
            ["supportsInstantRollback"] = s.Characteristics.SupportsInstantRollback,
            ["supportsTrafficShifting"] = s.Characteristics.SupportsTrafficShifting,
            ["complexity"] = s.Characteristics.ComplexityLevel,
            ["typicalDeploymentTime"] = s.Characteristics.TypicalDeploymentTimeMinutes,
            ["description"] = s.Characteristics.Description ?? ""
        }).ToList();

        message.Payload["strategies"] = strategies;
        message.Payload["count"] = strategies.Count;

        return Task.CompletedTask;
    }

    private Task HandleRecommendAsync(PluginMessage message)
    {
        var requireZeroDowntime = message.Payload.TryGetValue("requireZeroDowntime", out var zd) && zd is true;
        var requireInstantRollback = message.Payload.TryGetValue("requireInstantRollback", out var ir) && ir is true;
        var requireTrafficShifting = message.Payload.TryGetValue("requireTrafficShifting", out var ts) && ts is true;
        var preferredInfrastructure = message.Payload.TryGetValue("infrastructure", out var infra) && infra is string i ? i : null;
        var maxComplexity = message.Payload.TryGetValue("maxComplexity", out var mc) && int.TryParse(mc?.ToString(), out var mcVal) ? mcVal : 10;

        var strategy = RecommendStrategy(requireZeroDowntime, requireInstantRollback, requireTrafficShifting, preferredInfrastructure, maxComplexity);

        message.Payload["recommendation"] = new Dictionary<string, object>
        {
            ["name"] = strategy.Characteristics.StrategyName,
            ["type"] = strategy.Characteristics.DeploymentType.ToString(),
            ["complexity"] = strategy.Characteristics.ComplexityLevel,
            ["reasoning"] = GenerateRecommendationReasoning(strategy, requireZeroDowntime, requireInstantRollback, requireTrafficShifting)
        };

        return Task.CompletedTask;
    }

    private async Task HandleStatusAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("deploymentId", out var idObj) || idObj is not string deploymentId)
            throw new ArgumentException("Missing 'deploymentId' parameter");

        var state = await GetDeploymentStateAsync(deploymentId);
        message.Payload["state"] = state!;
        message.Payload["found"] = state != null;
    }

    #endregion

    #region Intelligence Integration

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            // Configure intelligence for all strategies
            foreach (var strategy in _strategies.Values)
            {
                if (strategy is DeploymentStrategyBase baseStrategy)
                {
                    baseStrategy.ConfigureIntelligence(MessageBus);
                }
            }

            // Register deployment capabilities with Intelligence
            var strategies = _strategies.Values.ToList();
            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "deployment",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["deploymentTypes"] = strategies.Select(s => s.Characteristics.DeploymentType.ToString()).Distinct().ToArray(),
                        ["zeroDowntimeCount"] = strategies.Count(s => s.Characteristics.SupportsZeroDowntime),
                        ["supportsDeploymentRecommendation"] = true,
                        ["supportsAutoRollback"] = true
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            SubscribeToDeploymentRecommendationRequests();
        }
    }

    private void SubscribeToDeploymentRecommendationRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe("intelligence.request.deployment-recommendation", async msg =>
        {
            var requireZeroDowntime = msg.Payload.TryGetValue("requireZeroDowntime", out var zd) && zd is true;
            var requireInstantRollback = msg.Payload.TryGetValue("requireInstantRollback", out var ir) && ir is true;
            var requireTrafficShifting = msg.Payload.TryGetValue("requireTrafficShifting", out var ts) && ts is true;
            var preferredInfrastructure = msg.Payload.TryGetValue("infrastructure", out var infra) && infra is string i ? i : null;

            try
            {
                var strategy = RecommendStrategy(requireZeroDowntime, requireInstantRollback, requireTrafficShifting, preferredInfrastructure);

                await MessageBus.PublishAsync("intelligence.request.deployment-recommendation.response", new PluginMessage
                {
                    Type = "deployment-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["strategy"] = strategy.Characteristics.StrategyName,
                        ["deploymentType"] = strategy.Characteristics.DeploymentType.ToString(),
                        ["complexity"] = strategy.Characteristics.ComplexityLevel,
                        ["typicalTime"] = strategy.Characteristics.TypicalDeploymentTimeMinutes,
                        ["reasoning"] = GenerateRecommendationReasoning(strategy, requireZeroDowntime, requireInstantRollback, requireTrafficShifting)
                    }
                });
            }
            catch (Exception ex)
            {
                await MessageBus.PublishAsync("intelligence.request.deployment-recommendation.response", new PluginMessage
                {
                    Type = "deployment-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = ex.Message
                    }
                });
            }
        });
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private void DiscoverAndRegisterStrategies()
    {
        var strategyTypes = GetType().Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(DeploymentStrategyBase).IsAssignableFrom(t));

        foreach (var strategyType in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(strategyType) is DeploymentStrategyBase strategy)
                {
                    _strategies[strategy.Characteristics.StrategyName] = strategy;
                }
            }
            catch
            {
                // Strategy failed to instantiate, skip
            }
        }
    }

    private DeploymentConfig ExtractDeploymentConfig(Dictionary<string, object> payload)
    {
        return new DeploymentConfig
        {
            Environment = payload.TryGetValue("environment", out var env) && env is string e ? e : "production",
            Version = payload.TryGetValue("version", out var ver) && ver is string v ? v : throw new ArgumentException("Missing 'version' parameter"),
            ArtifactUri = payload.TryGetValue("artifactUri", out var uri) && uri is string u ? u : throw new ArgumentException("Missing 'artifactUri' parameter"),
            TargetInstances = payload.TryGetValue("targetInstances", out var ti) && int.TryParse(ti?.ToString(), out var tiVal) ? tiVal : 1,
            HealthCheckPath = payload.TryGetValue("healthCheckPath", out var hcp) && hcp is string h ? h : "/health",
            AutoRollbackOnFailure = !payload.TryGetValue("autoRollback", out var ar) || ar is not false,
            CanaryPercent = payload.TryGetValue("canaryPercent", out var cp) && int.TryParse(cp?.ToString(), out var cpVal) ? cpVal : 10,
            EnvironmentVariables = payload.TryGetValue("environmentVariables", out var evObj) && evObj is Dictionary<string, string> ev ? ev : new(),
            Labels = payload.TryGetValue("labels", out var labelsObj) && labelsObj is Dictionary<string, string> labels ? labels : new()
        };
    }

    private static string GenerateRecommendationReasoning(
        IDeploymentStrategy strategy,
        bool requireZeroDowntime,
        bool requireInstantRollback,
        bool requireTrafficShifting)
    {
        var reasons = new List<string>();

        reasons.Add($"{strategy.Characteristics.StrategyName} selected for deployment");

        if (requireZeroDowntime && strategy.Characteristics.SupportsZeroDowntime)
            reasons.Add("supports zero-downtime deployments as required");

        if (requireInstantRollback && strategy.Characteristics.SupportsInstantRollback)
            reasons.Add("provides instant rollback capability");

        if (requireTrafficShifting && strategy.Characteristics.SupportsTrafficShifting)
            reasons.Add("enables gradual traffic shifting");

        reasons.Add($"complexity level {strategy.Characteristics.ComplexityLevel}/10");
        reasons.Add($"typical deployment time {strategy.Characteristics.TypicalDeploymentTimeMinutes} minutes");

        if (strategy.Characteristics.RequiredInfrastructure.Length > 0)
            reasons.Add($"requires {string.Join(", ", strategy.Characteristics.RequiredInfrastructure)}");

        return string.Join("; ", reasons);
    }

    private void IncrementUsageStats(string strategyName)
    {
        _usageStats.AddOrUpdate(strategyName, 1, (_, count) => count + 1);
    }

    #endregion

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _activeDeployments.Clear();
            _usageStats.Clear();
        }
        base.Dispose(disposing);
    }
}
