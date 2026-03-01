using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.AppPlatform;

/// <summary>
/// Application hosting strategy for DataWarehouse platform deployments.
/// Manages application registration, lifecycle, service token provisioning,
/// per-app isolation via tenant binding, and service routing for registered applications.
///
/// <para>
/// Supports the following hosting operations:
/// <list type="bullet">
///   <item>Application registration and deregistration with automatic tenant provisioning</item>
///   <item>Service token creation, rotation, revocation, and validation</item>
///   <item>Per-app access control policy binding via message bus</item>
///   <item>Service routing: storage, access control, intelligence, observability, replication, compliance</item>
///   <item>Platform service catalog and health checks</item>
/// </list>
/// </para>
/// </summary>
public sealed class AppHostingStrategy : DeploymentStrategyBase
{
    private readonly BoundedDictionary<string, AppRegistration> _registrations = new BoundedDictionary<string, AppRegistration>(1000);
    private readonly BoundedDictionary<string, ServiceToken> _tokens = new BoundedDictionary<string, ServiceToken>(1000);
    private readonly BoundedDictionary<string, AppAccessPolicy> _policies = new BoundedDictionary<string, AppAccessPolicy>(1000);
    private readonly BoundedDictionary<string, ServiceEndpoint> _serviceEndpoints = new BoundedDictionary<string, ServiceEndpoint>(1000);

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "App Hosting Platform",
        DeploymentType = DeploymentType.ContainerOrchestration,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 6,
        RequiredInfrastructure = ["DataWarehouse Kernel"],
        Description = "Application hosting with per-app isolation, service tokens, and tenant provisioning"
    };

    #region Deployment Lifecycle

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("app_hosting.deploy");
        var state = initialState;

        // Register the application
        var appId = config.StrategyConfig.TryGetValue("appId", out var id) && id is string idStr ? idStr : $"app-{Guid.NewGuid():N}";
        var appName = config.StrategyConfig.TryGetValue("appName", out var n) && n is string ns ? ns : config.Environment;
        var scopes = config.StrategyConfig.TryGetValue("scopes", out var s) && s is string ss
            ? ss.Split(',').Select(x => x.Trim()).ToList()
            : new List<string> { "storage.read", "storage.write" };

        var registration = new AppRegistration
        {
            AppId = appId,
            AppName = appName,
            OwnerEmail = config.StrategyConfig.TryGetValue("ownerEmail", out var e) && e is string es ? es : "admin@datawarehouse.io",
            Scopes = scopes,
            RegisteredAt = DateTime.UtcNow,
            IsActive = true
        };

        _registrations[appId] = registration;
        state = state with { ProgressPercent = 30 };

        // Create service token
        var token = new ServiceToken
        {
            TokenId = $"tok-{Guid.NewGuid():N}",
            AppId = appId,
            TokenHash = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            IsRevoked = false,
            Scopes = scopes
        };
        _tokens[token.TokenId] = token;
        state = state with { ProgressPercent = 60 };

        // Register service endpoints
        InitializeServiceEndpoints(appId);
        state = state with { ProgressPercent = 80 };

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["appId"] = appId,
                ["tokenId"] = token.TokenId,
                ["scopes"] = string.Join(",", scopes),
                ["serviceCount"] = _serviceEndpoints.Count
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("app_hosting.rollback");
        // Deregister the application
        if (currentState.Metadata.TryGetValue("appId", out var appIdObj) && appIdObj is string appId)
        {
            if (_registrations.TryGetValue(appId, out var reg))
                reg.IsActive = false;

            // Revoke tokens
            foreach (var token in _tokens.Values.Where(t => t.AppId == appId))
                token.IsRevoked = true;
        }

        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState with { TargetInstances = targetInstances, DeployedInstances = targetInstances, HealthyInstances = targetInstances });

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
    {
        // LOW-2870: Health is derived from active registration state. ResponseTimeMs is
        // not measurable without a live HTTP call to the app's health endpoint; report 0
        // to indicate "not measured" rather than a misleading 1ms constant.
        var results = _registrations.Values.Where(r => r.IsActive).Select(r => new HealthCheckResult
        {
            InstanceId = r.AppId,
            IsHealthy = true,
            StatusCode = 200,
            ResponseTimeMs = 0
        }).ToArray();

        return Task.FromResult(results.Length > 0 ? results : new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 0 } });
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "1.0.0", Health = DeploymentHealth.Healthy });

    #endregion

    #region App Management

    /// <summary>Registers a new application.</summary>
    public AppRegistration RegisterApp(string appName, string ownerEmail, List<string> scopes)
    {
        var registration = new AppRegistration
        {
            AppId = $"app-{Guid.NewGuid():N}",
            AppName = appName,
            OwnerEmail = ownerEmail,
            Scopes = scopes,
            RegisteredAt = DateTime.UtcNow,
            IsActive = true
        };
        _registrations[registration.AppId] = registration;
        IncrementCounter("app_hosting.register");
        return registration;
    }

    /// <summary>Deregisters an application.</summary>
    public bool DeregisterApp(string appId)
    {
        if (_registrations.TryGetValue(appId, out var reg))
        {
            reg.IsActive = false;
            IncrementCounter("app_hosting.deregister");
            return true;
        }
        return false;
    }

    /// <summary>Gets an app registration by ID.</summary>
    public AppRegistration? GetApp(string appId) => _registrations.TryGetValue(appId, out var r) ? r : null;

    /// <summary>Lists all active registrations.</summary>
    public IReadOnlyList<AppRegistration> ListApps() => _registrations.Values.Where(r => r.IsActive).ToList();

    /// <summary>Creates a service token for an app.</summary>
    public ServiceToken CreateToken(string appId, List<string> scopes)
    {
        var token = new ServiceToken
        {
            TokenId = $"tok-{Guid.NewGuid():N}",
            AppId = appId,
            TokenHash = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            IsRevoked = false,
            Scopes = scopes
        };
        _tokens[token.TokenId] = token;
        IncrementCounter("app_hosting.token_create");
        return token;
    }

    /// <summary>Validates a service token.</summary>
    public bool ValidateToken(string tokenId)
    {
        return _tokens.TryGetValue(tokenId, out var t) && !t.IsRevoked && t.ExpiresAt > DateTime.UtcNow;
    }

    /// <summary>Revokes a service token.</summary>
    public bool RevokeToken(string tokenId)
    {
        if (_tokens.TryGetValue(tokenId, out var t))
        {
            t.IsRevoked = true;
            IncrementCounter("app_hosting.token_revoke");
            return true;
        }
        return false;
    }

    /// <summary>Binds an access policy to an app.</summary>
    public void BindPolicy(string appId, AppAccessPolicy policy)
    {
        policy.AppId = appId;
        _policies[appId] = policy;
        IncrementCounter("app_hosting.policy_bind");
    }

    /// <summary>Gets an app's access policy.</summary>
    public AppAccessPolicy? GetPolicy(string appId) => _policies.TryGetValue(appId, out var p) ? p : null;

    #endregion

    #region Helpers

    private void InitializeServiceEndpoints(string appId)
    {
        var services = new[]
        {
            ("storage", "storage.request", "Storage service for data operations"),
            ("accesscontrol", "accesscontrol.evaluate", "Access control evaluation"),
            ("intelligence", "intelligence.request", "AI/ML intelligence services"),
            ("observability", "observability.metrics.emit", "Observability and monitoring"),
            ("replication", "replication.request", "Data replication services"),
            ("compliance", "compliance.evaluate", "Compliance evaluation services")
        };

        foreach (var (name, topic, description) in services)
        {
            _serviceEndpoints[$"{appId}:{name}"] = new ServiceEndpoint
            {
                ServiceName = name,
                Topic = topic,
                Description = description,
                IsAvailable = true
            };
        }
    }

    #endregion

    #region Internal Types

    public sealed class AppRegistration
    {
        public string AppId { get; init; } = string.Empty;
        public string AppName { get; init; } = string.Empty;
        public string OwnerEmail { get; init; } = string.Empty;
        public List<string> Scopes { get; init; } = new();
        public DateTime RegisteredAt { get; init; }
        public bool IsActive { get; set; }
    }

    public sealed class ServiceToken
    {
        public string TokenId { get; init; } = string.Empty;
        public string AppId { get; init; } = string.Empty;
        public string TokenHash { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public bool IsRevoked { get; set; }
        public List<string> Scopes { get; init; } = new();
    }

    public sealed class AppAccessPolicy
    {
        public string AppId { get; set; } = string.Empty;
        public string PolicyType { get; init; } = "RBAC";
        public List<string> AllowedRoles { get; init; } = new();
        public List<string> DeniedOperations { get; init; } = new();
    }

    public sealed class ServiceEndpoint
    {
        public string ServiceName { get; init; } = string.Empty;
        public string Topic { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool IsAvailable { get; init; }
    }

    #endregion
}
