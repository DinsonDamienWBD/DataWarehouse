using System.Reflection;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.DataMesh;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataMesh;

/// <summary>
/// Ultimate Data Mesh Plugin - Complete data mesh implementation for decentralized data architectures.
///
/// Implements T113 Data Mesh Plugin sub-tasks:
/// - 113.1: Domain-oriented data ownership (team autonomy, bounded contexts, lifecycle)
/// - 113.2: Data as a product (definition, SLA, versioning, quality, documentation)
/// - 113.3: Self-serve data infrastructure (platform, IaC, templates, pipelines)
/// - 113.4: Federated computational governance (policies, standards, compliance)
/// - 113.5: Domain data discovery (catalog, semantic search, marketplace, knowledge graph)
/// - 113.6: Cross-domain data sharing (agreements, federation, contracts, events)
/// - 113.7: Data mesh observability (metrics, tracing, quality monitoring, alerting)
/// - 113.8: Data mesh security (zero trust, RBAC/ABAC, encryption, masking, audit)
///
/// Features:
/// - 55+ production-ready strategies
/// - Intelligence-aware for AI-enhanced recommendations
/// - Multi-tenant support
/// - Federated governance
/// - Self-service capabilities
/// - Cross-domain sharing
/// - Zero trust security
/// </summary>
public sealed class UltimateDataMeshPlugin : DataManagementPluginBase, IDisposable
{
    private readonly DataMeshStrategyRegistry _registry;
    private readonly BoundedDictionary<string, long> _usageStats = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, DataDomain> _domains = new BoundedDictionary<string, DataDomain>(1000);
    private readonly BoundedDictionary<string, DataProduct> _products = new BoundedDictionary<string, DataProduct>(1000);
    private readonly BoundedDictionary<string, DataConsumer> _consumers = new BoundedDictionary<string, DataConsumer>(1000);
    private readonly BoundedDictionary<string, CrossDomainShare> _shares = new BoundedDictionary<string, CrossDomainShare>(1000);
    private readonly BoundedDictionary<string, GovernancePolicy> _policies = new BoundedDictionary<string, GovernancePolicy>(1000);
    private bool _disposed;

    private volatile bool _auditEnabled = true;
    private long _totalOperations;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.datamesh.ultimate";
    /// <inheritdoc/>
    public override string Name => "Ultimate Data Mesh";
    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string DataManagementDomain => "DataMesh";
    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>Semantic description for AI discovery.</summary>
    public string SemanticDescription =>
        "Ultimate data mesh plugin providing 55+ strategies for domain-oriented data ownership (team autonomy, bounded contexts), " +
        "data as a product (SLA, versioning, quality), self-serve data infrastructure (IaC, templates, pipelines), " +
        "federated computational governance (policy-as-code, compliance), domain data discovery (catalog, semantic search, marketplace), " +
        "cross-domain data sharing (federation, contracts, events), mesh observability (metrics, tracing, alerting), " +
        "and mesh security (zero trust, RBAC/ABAC, encryption, masking, audit).";

    /// <summary>Semantic tags for AI discovery.</summary>
    public string[] SemanticTags => [
        "data-mesh", "domain-ownership", "data-product", "self-serve", "federated-governance",
        "domain-discovery", "cross-domain", "observability", "security", "decentralized"
    ];

    /// <summary>Gets the data mesh strategy registry.</summary>
    public DataMeshStrategyRegistry Registry => _registry;

    /// <summary>Gets or sets whether audit logging is enabled.</summary>
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }

    /// <summary>Initializes a new instance of the Ultimate Data Mesh plugin.</summary>
    public UltimateDataMeshPlugin()
    {
        _registry = new DataMeshStrategyRegistry();
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);
        await RegisterAllKnowledgeAsync();

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["DomainOwnershipStrategies"] = GetStrategiesByCategory(DataMeshCategory.DomainOwnership).Count.ToString();
        response.Metadata["DataProductStrategies"] = GetStrategiesByCategory(DataMeshCategory.DataProduct).Count.ToString();
        response.Metadata["SelfServeStrategies"] = GetStrategiesByCategory(DataMeshCategory.SelfServe).Count.ToString();
        response.Metadata["FederatedGovernanceStrategies"] = GetStrategiesByCategory(DataMeshCategory.FederatedGovernance).Count.ToString();
        response.Metadata["DomainDiscoveryStrategies"] = GetStrategiesByCategory(DataMeshCategory.DomainDiscovery).Count.ToString();
        response.Metadata["CrossDomainSharingStrategies"] = GetStrategiesByCategory(DataMeshCategory.CrossDomainSharing).Count.ToString();
        response.Metadata["MeshObservabilityStrategies"] = GetStrategiesByCategory(DataMeshCategory.MeshObservability).Count.ToString();
        response.Metadata["MeshSecurityStrategies"] = GetStrategiesByCategory(DataMeshCategory.MeshSecurity).Count.ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities() =>
    [
        new() { Name = "datamesh.domain.register", DisplayName = "Register Domain", Description = "Register a data domain" },
        new() { Name = "datamesh.domain.list", DisplayName = "List Domains", Description = "List all data domains" },
        new() { Name = "datamesh.product.create", DisplayName = "Create Product", Description = "Create a data product" },
        new() { Name = "datamesh.product.list", DisplayName = "List Products", Description = "List data products" },
        new() { Name = "datamesh.consumer.register", DisplayName = "Register Consumer", Description = "Register a data consumer" },
        new() { Name = "datamesh.share.request", DisplayName = "Request Share", Description = "Request cross-domain share" },
        new() { Name = "datamesh.share.approve", DisplayName = "Approve Share", Description = "Approve cross-domain share" },
        new() { Name = "datamesh.policy.create", DisplayName = "Create Policy", Description = "Create governance policy" },
        new() { Name = "datamesh.discover", DisplayName = "Discover", Description = "Discover data products" },
        new() { Name = "datamesh.list-strategies", DisplayName = "List Strategies", Description = "List available strategies" },
        new() { Name = "datamesh.stats", DisplayName = "Statistics", Description = "Get data mesh statistics" }
    ];

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = "datamesh",
                    DisplayName = "Ultimate Data Mesh",
                    Description = SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = SemanticTags
                }
            };

            foreach (var strategy in _registry.GetAll())
            {
                var tags = new List<string> { "datamesh", strategy.Category.ToString().ToLowerInvariant() };
                tags.AddRange(strategy.Tags);

                capabilities.Add(new()
                {
                    CapabilityId = $"datamesh.{strategy.StrategyId}",
                    DisplayName = strategy.DisplayName,
                    Description = strategy.SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    SubCategory = strategy.Category.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["category"] = strategy.Category.ToString(),
                        ["supportsAsync"] = strategy.Capabilities.SupportsAsync,
                        ["supportsRealTime"] = strategy.Capabilities.SupportsRealTime,
                        ["supportsBatch"] = strategy.Capabilities.SupportsBatch,
                        ["supportsEventDriven"] = strategy.Capabilities.SupportsEventDriven,
                        ["supportsMultiTenancy"] = strategy.Capabilities.SupportsMultiTenancy,
                        ["supportsFederation"] = strategy.Capabilities.SupportsFederation
                    },
                    SemanticDescription = strategy.SemanticDescription
                });
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        var strategies = _registry.GetAll();
        var byCategory = strategies.GroupBy(s => s.Category).ToDictionary(g => g.Key.ToString(), g => g.Count());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.overview",
            Topic = "plugin.capabilities",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = SemanticDescription,
            Payload = new Dictionary<string, object>
            {
                ["totalStrategies"] = strategies.Count,
                ["categories"] = byCategory,
                ["supportsDomainOwnership"] = true,
                ["supportsDataProduct"] = true,
                ["supportsSelfServe"] = true,
                ["supportsFederatedGovernance"] = true,
                ["supportsDomainDiscovery"] = true,
                ["supportsCrossDomainSharing"] = true,
                ["supportsMeshObservability"] = true,
                ["supportsMeshSecurity"] = true
            },
            Tags = SemanticTags
        });

        return knowledge.AsReadOnly();
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.Count;
        metadata["TotalDomains"] = _domains.Count;
        metadata["TotalDataProducts"] = _products.Count;
        metadata["TotalConsumers"] = _consumers.Count;
        metadata["TotalShares"] = _shares.Count;
        metadata["TotalPolicies"] = _policies.Count;
        metadata["TotalOperations"] = Interlocked.Read(ref _totalOperations);
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message) => message.Type switch
    {
        "datamesh.domain.register" => HandleDomainRegisterAsync(message),
        "datamesh.domain.list" => HandleDomainListAsync(message),
        "datamesh.product.create" => HandleProductCreateAsync(message),
        "datamesh.product.list" => HandleProductListAsync(message),
        "datamesh.consumer.register" => HandleConsumerRegisterAsync(message),
        "datamesh.share.request" => HandleShareRequestAsync(message),
        "datamesh.share.approve" => HandleShareApproveAsync(message),
        "datamesh.policy.create" => HandlePolicyCreateAsync(message),
        "datamesh.discover" => HandleDiscoverAsync(message),
        "datamesh.list-strategies" => HandleListStrategiesAsync(message),
        "datamesh.stats" => HandleStatsAsync(message),
        _ => base.OnMessageAsync(message)
    };

    #region Message Handlers

    private Task HandleDomainRegisterAsync(PluginMessage message)
    {
        var domain = new DataDomain
        {
            DomainId = GetRequiredString(message.Payload, "domainId"),
            Name = GetRequiredString(message.Payload, "name"),
            Owner = GetRequiredString(message.Payload, "owner"),
            Description = message.Payload.TryGetValue("description", out var descObj) && descObj is string desc ? desc : null,
            Status = DomainStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        _domains[domain.DomainId] = domain;
        Interlocked.Increment(ref _totalOperations);
        message.Payload["domain"] = domain;
        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private Task HandleDomainListAsync(PluginMessage message)
    {
        message.Payload["domains"] = _domains.Values.ToList();
        message.Payload["count"] = _domains.Count;
        return Task.CompletedTask;
    }

    private Task HandleProductCreateAsync(PluginMessage message)
    {
        var product = new DataProduct
        {
            ProductId = GetRequiredString(message.Payload, "productId"),
            Name = GetRequiredString(message.Payload, "name"),
            DomainId = GetRequiredString(message.Payload, "domainId"),
            Owner = GetRequiredString(message.Payload, "owner"),
            Description = message.Payload.TryGetValue("description", out var descObj) && descObj is string desc ? desc : null,
            QualityTier = Enum.TryParse<DataProductQualityTier>(
                message.Payload.TryGetValue("qualityTier", out var tierObj) && tierObj is string tier ? tier : "Standard", true, out var qt)
                ? qt : DataProductQualityTier.Standard,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        _products[product.ProductId] = product;
        Interlocked.Increment(ref _totalOperations);
        message.Payload["product"] = product;
        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private Task HandleProductListAsync(PluginMessage message)
    {
        var domainFilter = message.Payload.TryGetValue("domainId", out var domObj) && domObj is string dom ? dom : null;
        var products = string.IsNullOrEmpty(domainFilter)
            ? _products.Values.ToList()
            : _products.Values.Where(p => p.DomainId == domainFilter).ToList();
        message.Payload["products"] = products;
        message.Payload["count"] = products.Count;
        return Task.CompletedTask;
    }

    private Task HandleConsumerRegisterAsync(PluginMessage message)
    {
        var consumer = new DataConsumer
        {
            ConsumerId = GetRequiredString(message.Payload, "consumerId"),
            Name = GetRequiredString(message.Payload, "name"),
            DomainId = GetRequiredString(message.Payload, "domainId"),
            Contact = message.Payload.TryGetValue("contact", out var contactObj) && contactObj is string contact ? contact : null,
            RegisteredAt = DateTimeOffset.UtcNow
        };
        _consumers[consumer.ConsumerId] = consumer;
        Interlocked.Increment(ref _totalOperations);
        message.Payload["consumer"] = consumer;
        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private Task HandleShareRequestAsync(PluginMessage message)
    {
        var share = new CrossDomainShare
        {
            ShareId = Guid.NewGuid().ToString("N"),
            SourceDomainId = GetRequiredString(message.Payload, "sourceDomainId"),
            TargetDomainId = GetRequiredString(message.Payload, "targetDomainId"),
            ProductId = GetRequiredString(message.Payload, "productId"),
            Status = ShareStatus.Pending
        };
        _shares[share.ShareId] = share;
        Interlocked.Increment(ref _totalOperations);
        message.Payload["share"] = share;
        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private Task HandleShareApproveAsync(PluginMessage message)
    {
        var shareId = GetRequiredString(message.Payload, "shareId");
        var approved = message.Payload.TryGetValue("approved", out var appObj) && appObj is bool app ? app : true;

        if (_shares.TryGetValue(shareId, out var share))
        {
            var updatedShare = share with
            {
                Status = approved ? ShareStatus.Approved : ShareStatus.Rejected,
                ApprovedAt = approved ? DateTimeOffset.UtcNow : null
            };
            _shares[shareId] = updatedShare;
            message.Payload["share"] = updatedShare;
            message.Payload["success"] = true;
        }
        else
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Share not found";
        }
        Interlocked.Increment(ref _totalOperations);
        return Task.CompletedTask;
    }

    private Task HandlePolicyCreateAsync(PluginMessage message)
    {
        var policy = new GovernancePolicy
        {
            PolicyId = GetRequiredString(message.Payload, "policyId"),
            Name = GetRequiredString(message.Payload, "name"),
            Type = Enum.TryParse<GovernancePolicyType>(
                message.Payload.TryGetValue("type", out var typeObj) && typeObj is string type ? type : "DataQuality", true, out var pt)
                ? pt : GovernancePolicyType.DataQuality,
            Scope = Enum.TryParse<GovernancePolicyScope>(
                message.Payload.TryGetValue("scope", out var scopeObj) && scopeObj is string scope ? scope : "Global", true, out var ps)
                ? ps : GovernancePolicyScope.Global,
            Description = message.Payload.TryGetValue("description", out var descObj) && descObj is string desc ? desc : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _policies[policy.PolicyId] = policy;
        Interlocked.Increment(ref _totalOperations);
        message.Payload["policy"] = policy;
        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private Task HandleDiscoverAsync(PluginMessage message)
    {
        var query = message.Payload.TryGetValue("query", out var queryObj) && queryObj is string q ? q : "";
        var domainFilter = message.Payload.TryGetValue("domainId", out var domObj) && domObj is string dom ? dom : null;

        var products = _products.Values.Where(p =>
            (string.IsNullOrEmpty(domainFilter) || p.DomainId == domainFilter) &&
            (string.IsNullOrEmpty(query) ||
             p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (p.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
        ).ToList();

        message.Payload["products"] = products;
        message.Payload["count"] = products.Count;
        Interlocked.Increment(ref _totalOperations);
        return Task.CompletedTask;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
            && Enum.TryParse<DataMeshCategory>(catStr, true, out var cat) ? cat : (DataMeshCategory?)null;

        var strategies = categoryFilter.HasValue ? _registry.GetByCategory(categoryFilter.Value) : _registry.GetAll();

        message.Payload["strategies"] = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.DisplayName,
            ["category"] = s.Category.ToString(),
            ["tags"] = s.Tags
        }).ToList();
        message.Payload["count"] = strategies.Count;

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalOperations"] = Interlocked.Read(ref _totalOperations);
        message.Payload["registeredStrategies"] = _registry.Count;
        message.Payload["totalDomains"] = _domains.Count;
        message.Payload["totalProducts"] = _products.Count;
        message.Payload["totalConsumers"] = _consumers.Count;
        message.Payload["totalShares"] = _shares.Count;
        message.Payload["totalPolicies"] = _policies.Count;
        message.Payload["usageByStrategy"] = new Dictionary<string, long>(_usageStats);
        message.Payload["strategiesByCategory"] = _registry.GetAll()
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private static string GetRequiredString(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var obj) || obj is not string str || string.IsNullOrWhiteSpace(str))
            throw new ArgumentException($"Missing required parameter: '{key}'");
        return str;
    }

    private IDataMeshStrategy GetStrategyOrThrow(string strategyId) =>
        _registry.Get(strategyId) ?? throw new ArgumentException($"Data mesh strategy '{strategyId}' not found");

    private List<IDataMeshStrategy> GetStrategiesByCategory(DataMeshCategory category) =>
        _registry.GetByCategory(category).ToList();

    private void IncrementUsageStats(string strategyId) =>
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);

    private void DiscoverAndRegisterStrategies()
    {
        var discovered = _registry.AutoDiscover(Assembly.GetExecutingAssembly());
        if (discovered == 0)
            System.Diagnostics.Debug.WriteLine($"[{Name}] Warning: No data mesh strategies discovered");
    }

    #endregion

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Disposes resources.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _usageStats.Clear();
            _domains.Clear();
            _products.Clear();
            _consumers.Clear();
            _shares.Clear();
            _policies.Clear();
        }
        base.Dispose(disposing);
    }
}
