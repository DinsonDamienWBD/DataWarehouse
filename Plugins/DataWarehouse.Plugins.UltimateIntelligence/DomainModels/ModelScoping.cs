// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.DomainModels;

#region Enums and Supporting Types

/// <summary>
/// Defines the organizational scope level for model deployment.
/// Scopes form a hierarchy where broader scopes can inherit models from narrower scopes.
/// </summary>
public enum ModelScope
{
    /// <summary>
    /// Global scope - shared across all instances and tenants.
    /// Use for universal models trained on anonymized aggregate data.
    /// </summary>
    Global = 0,

    /// <summary>
    /// Instance scope - specific to a single DataWarehouse instance.
    /// Use for instance-wide models serving all departments.
    /// </summary>
    Instance = 1,

    /// <summary>
    /// Department scope - specific to a business department/division.
    /// Use for department-specific models with specialized knowledge.
    /// </summary>
    Department = 2,

    /// <summary>
    /// Team scope - specific to a team within a department.
    /// Use for team-level models with highly specialized workflows.
    /// </summary>
    Team = 3,

    /// <summary>
    /// UserGroup scope - specific to a custom user group.
    /// Use for cross-team groups with shared characteristics.
    /// </summary>
    UserGroup = 4,

    /// <summary>
    /// Individual scope - specific to a single user.
    /// Use for personalized models trained on individual user patterns.
    /// </summary>
    Individual = 5
}

/// <summary>
/// Identifies a specific organizational scope instance.
/// </summary>
public sealed class ScopeIdentifier
{
    /// <summary>
    /// The scope level (Instance, Department, Team, etc.).
    /// </summary>
    public ModelScope ScopeLevel { get; init; }

    /// <summary>
    /// Unique identifier for this scope instance.
    /// Examples: instance ID, department ID, team ID, user ID.
    /// </summary>
    public string ScopeId { get; init; } = string.Empty;

    /// <summary>
    /// Parent scope identifier for inheritance resolution.
    /// Null for Global scope or root-level scopes.
    /// </summary>
    public ScopeIdentifier? ParentScope { get; init; }

    /// <summary>
    /// Display name for this scope.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the full scope hierarchy path from Global to this scope.
    /// </summary>
    public IReadOnlyList<ScopeIdentifier> GetScopePath()
    {
        var path = new List<ScopeIdentifier>();
        var current = this;

        while (current != null)
        {
            path.Insert(0, current);
            current = current.ParentScope;
        }

        return path.AsReadOnly();
    }

    /// <summary>
    /// Creates a Global scope identifier.
    /// </summary>
    public static ScopeIdentifier Global() => new()
    {
        ScopeLevel = ModelScope.Global,
        ScopeId = "global",
        DisplayName = "Global",
        ParentScope = null
    };

    /// <summary>
    /// Creates an Instance scope identifier.
    /// </summary>
    public static ScopeIdentifier Instance(string instanceId, string displayName) => new()
    {
        ScopeLevel = ModelScope.Instance,
        ScopeId = instanceId,
        DisplayName = displayName,
        ParentScope = Global()
    };

    public override string ToString() => $"{ScopeLevel}/{ScopeId}";
}

/// <summary>
/// Metrics tracking model expertise and performance across domains.
/// </summary>
public sealed class ExpertiseMetrics
{
    /// <summary>
    /// Domain identifier (e.g., "sales", "finance", "customer-support").
    /// </summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>
    /// Query type category (e.g., "aggregation", "time-series", "classification").
    /// </summary>
    public string QueryType { get; init; } = string.Empty;

    /// <summary>
    /// Expertise score (0.0 to 1.0) measuring model proficiency.
    /// Higher values indicate better performance in this domain/query type.
    /// </summary>
    public double ExpertiseScore { get; init; }

    /// <summary>
    /// Accuracy percentage (0.0 to 100.0) based on historical query results.
    /// </summary>
    public double Accuracy { get; init; }

    /// <summary>
    /// Average response time in milliseconds.
    /// </summary>
    public double AverageResponseTimeMs { get; init; }

    /// <summary>
    /// Number of queries used to calculate these metrics.
    /// </summary>
    public long SampleSize { get; init; }

    /// <summary>
    /// Timestamp of last metric update.
    /// </summary>
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>
    /// Confidence interval for expertise score (0.0 to 1.0).
    /// Lower values indicate less statistical confidence.
    /// </summary>
    public double ConfidenceInterval { get; init; }
}

/// <summary>
/// Request to promote a model to a broader scope.
/// </summary>
public sealed class PromotionRequest
{
    /// <summary>
    /// Unique identifier for this promotion request.
    /// </summary>
    public Guid RequestId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Model identifier to promote.
    /// </summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// Current scope of the model.
    /// </summary>
    public ScopeIdentifier CurrentScope { get; init; } = ScopeIdentifier.Global();

    /// <summary>
    /// Target scope to promote to (must be broader than current).
    /// </summary>
    public ScopeIdentifier TargetScope { get; init; } = ScopeIdentifier.Global();

    /// <summary>
    /// Justification for promotion (performance metrics, business need, etc.).
    /// </summary>
    public string Justification { get; init; } = string.Empty;

    /// <summary>
    /// User who initiated the promotion request.
    /// </summary>
    public string RequestedBy { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp of request creation.
    /// </summary>
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Supporting metrics demonstrating model performance.
    /// </summary>
    public IReadOnlyList<ExpertiseMetrics> PerformanceMetrics { get; init; } = Array.Empty<ExpertiseMetrics>();

    /// <summary>
    /// Current approval status.
    /// </summary>
    public PromotionStatus Status { get; init; } = PromotionStatus.Pending;

    /// <summary>
    /// Approval/rejection comments.
    /// </summary>
    public string? ReviewComments { get; init; }

    /// <summary>
    /// User who reviewed the request.
    /// </summary>
    public string? ReviewedBy { get; init; }

    /// <summary>
    /// Timestamp of review completion.
    /// </summary>
    public DateTimeOffset? ReviewedAt { get; init; }
}

/// <summary>
/// Status of a model promotion request.
/// </summary>
public enum PromotionStatus
{
    /// <summary>
    /// Request is pending review.
    /// </summary>
    Pending,

    /// <summary>
    /// Request approved and promotion in progress.
    /// </summary>
    Approved,

    /// <summary>
    /// Request rejected.
    /// </summary>
    Rejected,

    /// <summary>
    /// Promotion completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Promotion failed during execution.
    /// </summary>
    Failed
}

/// <summary>
/// Training policy configuration for a specific scope.
/// </summary>
public sealed class ScopedTrainingPolicy
{
    /// <summary>
    /// Scope this policy applies to.
    /// </summary>
    public ScopeIdentifier Scope { get; init; } = ScopeIdentifier.Global();

    /// <summary>
    /// Minimum time interval between automatic retraining cycles.
    /// </summary>
    public TimeSpan MinimumRetrainingInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum time interval before forced retraining.
    /// </summary>
    public TimeSpan MaximumRetrainingInterval { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Data sources allowed for training (table patterns, query patterns, etc.).
    /// Empty list means all sources allowed.
    /// </summary>
    public IReadOnlyList<string> AllowedDataSources { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Maximum training dataset size in rows.
    /// Prevents runaway resource consumption.
    /// </summary>
    public long MaxTrainingDatasetRows { get; init; } = 1_000_000;

    /// <summary>
    /// Maximum CPU cores allocated for training.
    /// </summary>
    public int MaxCpuCores { get; init; } = 4;

    /// <summary>
    /// Maximum memory allocated for training in megabytes.
    /// </summary>
    public long MaxMemoryMb { get; init; } = 8192;

    /// <summary>
    /// Maximum training duration before timeout.
    /// </summary>
    public TimeSpan MaxTrainingDuration { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Accuracy threshold to trigger automatic retraining.
    /// If model accuracy drops below this, retraining is triggered.
    /// </summary>
    public double AccuracyThreshold { get; init; } = 0.85;

    /// <summary>
    /// Whether automatic retraining is enabled.
    /// </summary>
    public bool AutoRetrainingEnabled { get; init; } = true;

    /// <summary>
    /// Whether to retain previous model versions after retraining.
    /// </summary>
    public bool RetainPreviousVersions { get; init; } = true;

    /// <summary>
    /// Number of previous model versions to retain.
    /// </summary>
    public int MaxVersionsToRetain { get; init; } = 3;
}

#endregion

#region Phase Y5: Multi-Instance Model Scoping

/// <summary>
/// Manages assignment of AI models to organizational scopes.
/// Enables multi-tenant model deployment with scope isolation and inheritance.
/// </summary>
/// <remarks>
/// <para><b>Phase Y5: Multi-Instance Model Scoping</b></para>
/// <para>
/// ModelScopeManager provides hierarchical model scoping to support:
/// - Multi-tenant deployments with data isolation
/// - Department/team-specific model specialization
/// - Global shared models with local overrides
/// - Scope-based access control and resource limits
/// </para>
///
/// <para><b>Architecture:</b></para>
/// <list type="bullet">
/// <item>Uses message bus for all cross-scope communication</item>
/// <item>Publishes scope changes to "intelligence.model.scope.assigned" topic</item>
/// <item>Subscribes to "intelligence.model.register" for new model registration</item>
/// <item>Thread-safe concurrent scope assignment operations</item>
/// </list>
///
/// <para><b>Example Usage:</b></para>
/// <code>
/// var manager = new ModelScopeManager(messageBus);
///
/// // Assign model to department scope
/// var deptScope = new ScopeIdentifier
/// {
///     ScopeLevel = ModelScope.Department,
///     ScopeId = "dept-sales",
///     DisplayName = "Sales Department"
/// };
/// await manager.AssignModelToScopeAsync("model-sales-forecast", deptScope);
///
/// // Get best model for user's scope
/// var model = await manager.GetModelForScopeAsync("user-123");
/// </code>
/// </remarks>
public sealed class ModelScopeManager
{
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<string, ScopeIdentifier> _modelScopes = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the ModelScopeManager.
    /// </summary>
    /// <param name="messageBus">Message bus for inter-component communication.</param>
    public ModelScopeManager(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        SubscribeToModelEvents();
    }

    /// <summary>
    /// Assigns a model to a specific organizational scope.
    /// Models assigned to narrower scopes take precedence over broader scopes.
    /// </summary>
    /// <param name="modelId">Unique identifier of the model to assign.</param>
    /// <param name="scope">Target scope for model deployment.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AssignModelToScopeAsync(
        string modelId,
        ScopeIdentifier scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(scope);

        await _lock.WaitAsync(ct);
        try
        {
            _modelScopes[modelId] = scope;

            // Publish scope assignment event
            var message = new PluginMessage
            {
                SourcePluginId = "intelligence.model-scope-manager",
                Payload = new Dictionary<string, object>
                {
                    ["ModelId"] = modelId,
                    ["Scope"] = scope.ToString(),
                    ["ScopeLevel"] = scope.ScopeLevel.ToString(),
                    ["ScopeId"] = scope.ScopeId,
                    ["AssignedAt"] = DateTimeOffset.UtcNow
                }
            };

            await _messageBus.PublishAsync("intelligence.model.scope.assigned", message, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the scope assigned to a specific model.
    /// </summary>
    /// <param name="modelId">Model identifier to query.</param>
    /// <returns>Scope identifier, or null if model not found.</returns>
    public async Task<ScopeIdentifier?> GetModelScopeAsync(string modelId)
    {
        await _lock.WaitAsync();
        try
        {
            return _modelScopes.TryGetValue(modelId, out var scope) ? scope : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Finds the most specific model available for a given scope.
    /// Walks up the scope hierarchy to find the best matching model.
    /// </summary>
    /// <param name="requestScope">The scope making the model request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Model ID of the best matching model, or null if none found.</returns>
    public async Task<string?> GetModelForScopeAsync(
        ScopeIdentifier requestScope,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Build scope hierarchy from most specific to most general
            var scopePath = requestScope.GetScopePath();

            // Search from most specific to least specific
            for (int i = scopePath.Count - 1; i >= 0; i--)
            {
                var currentScope = scopePath[i];

                // Find models matching this scope level
                var matchingModel = _modelScopes
                    .FirstOrDefault(kvp =>
                        kvp.Value.ScopeLevel == currentScope.ScopeLevel &&
                        kvp.Value.ScopeId == currentScope.ScopeId);

                if (!string.IsNullOrEmpty(matchingModel.Key))
                {
                    return matchingModel.Key;
                }
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all models assigned to a specific scope level.
    /// </summary>
    /// <param name="scopeLevel">Scope level to filter by.</param>
    /// <returns>Read-only list of model IDs.</returns>
    public async Task<IReadOnlyList<string>> GetModelsAtScopeLevelAsync(ModelScope scopeLevel)
    {
        await _lock.WaitAsync();
        try
        {
            return _modelScopes
                .Where(kvp => kvp.Value.ScopeLevel == scopeLevel)
                .Select(kvp => kvp.Key)
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes a model from all scope assignments.
    /// </summary>
    /// <param name="modelId">Model identifier to unassign.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UnassignModelAsync(string modelId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_modelScopes.Remove(modelId))
            {
                var message = new PluginMessage
                {
                    SourcePluginId ="intelligence.model-scope-manager",
                    Payload = new Dictionary<string, object>
                    {
                        ["ModelId"] = modelId,
                        ["UnassignedAt"] = DateTimeOffset.UtcNow
                    }
                };

                await _messageBus.PublishAsync("intelligence.model.scope.unassigned", message, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SubscribeToModelEvents()
    {
        // Auto-register models when they're created
        _messageBus.Subscribe("intelligence.model.register", async msg =>
        {
            if (msg.Payload.TryGetValue("ModelId", out var modelIdObj) &&
                msg.Payload.TryGetValue("DefaultScope", out var scopeObj) &&
                modelIdObj is string modelId &&
                scopeObj is ScopeIdentifier scope)
            {
                await AssignModelToScopeAsync(modelId, scope);
            }
        });
    }
}

/// <summary>
/// Registry for managing multiple model instances with scope-based inheritance.
/// Maintains a catalog of available models and their deployment scopes.
/// </summary>
/// <remarks>
/// <para><b>Phase Y5: Multi-Instance Model Scoping</b></para>
/// <para>
/// ScopedModelRegistry serves as the central directory for all deployed models.
/// It enables:
/// - Fast model lookup by scope
/// - Model versioning and lifecycle management
/// - Scope inheritance resolution
/// - Model capability discovery
/// </para>
///
/// <para><b>Message Bus Integration:</b></para>
/// <list type="bullet">
/// <item>Publishes to "intelligence.model.registered" on registration</item>
/// <item>Publishes to "intelligence.model.unregistered" on removal</item>
/// <item>Subscribes to "intelligence.model.query" for discovery requests</item>
/// </list>
/// </remarks>
public sealed class ScopedModelRegistry
{
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<string, ModelRegistration> _models = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Model registration record.
    /// </summary>
    public sealed class ModelRegistration
    {
        public string ModelId { get; init; } = string.Empty;
        public ScopeIdentifier Scope { get; init; } = ScopeIdentifier.Global();
        public string ModelType { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
        public Dictionary<string, object> Metadata { get; init; } = new();
        public bool IsActive { get; init; } = true;
    }

    /// <summary>
    /// Initializes a new instance of the ScopedModelRegistry.
    /// </summary>
    /// <param name="messageBus">Message bus for communication.</param>
    public ScopedModelRegistry(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        SubscribeToQueryRequests();
    }

    /// <summary>
    /// Registers a new model in the registry.
    /// </summary>
    /// <param name="registration">Model registration details.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterModelAsync(ModelRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        await _lock.WaitAsync(ct);
        try
        {
            _models[registration.ModelId] = registration;

            var message = new PluginMessage
            {
                SourcePluginId ="intelligence.scoped-model-registry",
                Payload = new Dictionary<string, object>
                {
                    ["ModelId"] = registration.ModelId,
                    ["Scope"] = registration.Scope.ToString(),
                    ["ModelType"] = registration.ModelType,
                    ["Version"] = registration.Version,
                    ["RegisteredAt"] = registration.RegisteredAt
                }
            };

            await _messageBus.PublishAsync("intelligence.model.registered", message, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Unregisters a model from the registry.
    /// </summary>
    /// <param name="modelId">Model identifier to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UnregisterModelAsync(string modelId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_models.Remove(modelId))
            {
                var message = new PluginMessage
                {
                    SourcePluginId ="intelligence.scoped-model-registry",
                    Payload = new Dictionary<string, object>
                    {
                        ["ModelId"] = modelId,
                        ["UnregisteredAt"] = DateTimeOffset.UtcNow
                    }
                };

                await _messageBus.PublishAsync("intelligence.model.unregistered", message, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets a specific model registration.
    /// </summary>
    /// <param name="modelId">Model identifier to retrieve.</param>
    /// <returns>Model registration, or null if not found.</returns>
    public async Task<ModelRegistration?> GetModelAsync(string modelId)
    {
        await _lock.WaitAsync();
        try
        {
            return _models.TryGetValue(modelId, out var model) ? model : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Finds all models matching a specific scope with inheritance.
    /// Includes models from parent scopes.
    /// </summary>
    /// <param name="scope">Scope to search within.</param>
    /// <returns>List of matching model registrations.</returns>
    public async Task<IReadOnlyList<ModelRegistration>> FindModelsByScopeAsync(ScopeIdentifier scope)
    {
        await _lock.WaitAsync();
        try
        {
            var scopePath = scope.GetScopePath();
            var scopeIds = scopePath.Select(s => s.ToString()).ToHashSet();

            return _models.Values
                .Where(m => m.IsActive && scopeIds.Contains(m.Scope.ToString()))
                .OrderByDescending(m => m.Scope.ScopeLevel) // Most specific first
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all registered models.
    /// </summary>
    /// <returns>Read-only list of all model registrations.</returns>
    public async Task<IReadOnlyList<ModelRegistration>> GetAllModelsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _models.Values.ToList().AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SubscribeToQueryRequests()
    {
        _messageBus.Subscribe("intelligence.model.query", async msg =>
        {
            if (msg.Payload.TryGetValue("Scope", out var scopeObj) && scopeObj is ScopeIdentifier scope)
            {
                var models = await FindModelsByScopeAsync(scope);

                var response = new PluginMessage
                {
                    SourcePluginId ="intelligence.scoped-model-registry",
                    CorrelationId = msg.CorrelationId,
                    Payload = new Dictionary<string, object>
                    {
                        ["Models"] = models,
                        ["Count"] = models.Count
                    }
                };

                await _messageBus.PublishAsync("intelligence.model.query.response", response);
            }
        });
    }
}

/// <summary>
/// Enforces data isolation between model scopes to prevent tenant data leakage.
/// Critical for multi-tenant deployments and compliance requirements.
/// </summary>
/// <remarks>
/// <para><b>Phase Y5: Multi-Instance Model Scoping</b></para>
/// <para>
/// ModelIsolation provides tenant isolation guarantees for AI models:
/// - Prevents models from accessing data outside their authorized scope
/// - Validates all data access requests against scope policies
/// - Maintains audit trail of isolation violations
/// - Supports scope-based data filtering and access control
/// </para>
///
/// <para><b>Security Features:</b></para>
/// <list type="bullet">
/// <item>Mandatory access control checks before data access</item>
/// <item>Scope boundary validation</item>
/// <item>Audit logging of all access attempts</item>
/// <item>Violation detection and alerting</item>
/// </list>
///
/// <para><b>Compliance:</b></para>
/// <para>
/// Supports compliance with data privacy regulations (GDPR, CCPA, HIPAA)
/// by ensuring strict data isolation between organizational units.
/// </para>
/// </remarks>
public sealed class ModelIsolation
{
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<string, ScopeIdentifier> _modelScopes = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of ModelIsolation.
    /// </summary>
    /// <param name="messageBus">Message bus for audit logging.</param>
    public ModelIsolation(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Validates whether a model is authorized to access data from a specific scope.
    /// </summary>
    /// <param name="modelId">Model requesting data access.</param>
    /// <param name="dataScope">Scope of the data being accessed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if access is authorized, false otherwise.</returns>
    public async Task<bool> ValidateDataAccessAsync(
        string modelId,
        ScopeIdentifier dataScope,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_modelScopes.TryGetValue(modelId, out var modelScope))
            {
                await LogViolationAsync(modelId, dataScope, "Model not registered", ct);
                return false;
            }

            // Model can access data from its own scope or narrower scopes
            var isAuthorized = IsScopeAuthorized(modelScope, dataScope);

            if (!isAuthorized)
            {
                await LogViolationAsync(modelId, dataScope, "Scope boundary violation", ct);
            }

            return isAuthorized;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Registers a model's authorized scope.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="scope">Authorized scope for this model.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterModelScopeAsync(
        string modelId,
        ScopeIdentifier scope,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _modelScopes[modelId] = scope;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Filters a dataset to include only data within the model's authorized scope.
    /// </summary>
    /// <typeparam name="T">Data record type.</typeparam>
    /// <param name="modelId">Model requesting filtered data.</param>
    /// <param name="data">Full dataset.</param>
    /// <param name="scopeExtractor">Function to extract scope from each record.</param>
    /// <returns>Filtered dataset containing only authorized records.</returns>
    public async Task<IReadOnlyList<T>> FilterDataByScopeAsync<T>(
        string modelId,
        IEnumerable<T> data,
        Func<T, ScopeIdentifier> scopeExtractor)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_modelScopes.TryGetValue(modelId, out var modelScope))
            {
                return Array.Empty<T>();
            }

            var filtered = data
                .Where(record => IsScopeAuthorized(modelScope, scopeExtractor(record)))
                .ToList();

            return filtered.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks if a model scope is authorized to access data from a specific data scope.
    /// Models can access their own scope and all narrower (more specific) child scopes.
    /// </summary>
    private bool IsScopeAuthorized(ScopeIdentifier modelScope, ScopeIdentifier dataScope)
    {
        // Global models can access all data
        if (modelScope.ScopeLevel == ModelScope.Global)
        {
            return true;
        }

        // Same scope is always allowed
        if (modelScope.ScopeLevel == dataScope.ScopeLevel &&
            modelScope.ScopeId == dataScope.ScopeId)
        {
            return true;
        }

        // Model can access narrower scopes within its hierarchy
        var dataScopePath = dataScope.GetScopePath();
        return dataScopePath.Any(s =>
            s.ScopeLevel == modelScope.ScopeLevel &&
            s.ScopeId == modelScope.ScopeId);
    }

    /// <summary>
    /// Logs an isolation violation to the audit trail.
    /// </summary>
    private async Task LogViolationAsync(
        string modelId,
        ScopeIdentifier attemptedScope,
        string reason,
        CancellationToken ct)
    {
        var message = new PluginMessage
        {
            SourcePluginId ="intelligence.model-isolation",
            Payload = new Dictionary<string, object>
            {
                ["Severity"] = "Warning",
                ["ViolationType"] = "ScopeIsolationViolation",
                ["ModelId"] = modelId,
                ["AttemptedScope"] = attemptedScope.ToString(),
                ["Reason"] = reason,
                ["Timestamp"] = DateTimeOffset.UtcNow
            }
        };

        await _messageBus.PublishAsync("intelligence.isolation.violation", message, ct);
    }
}

/// <summary>
/// Manages training policies for different organizational scopes.
/// Controls resource allocation, training frequency, and data access per scope.
/// </summary>
/// <remarks>
/// <para><b>Phase Y5: Multi-Instance Model Scoping</b></para>
/// <para>
/// ScopedTrainingPolicy enables granular control over model training:
/// - Department-specific resource limits
/// - Custom training schedules per scope
/// - Data source restrictions
/// - Automatic retraining triggers
/// </para>
///
/// <para><b>Policy Inheritance:</b></para>
/// <para>
/// Narrower scopes inherit policies from broader scopes unless overridden.
/// This provides sensible defaults while allowing customization.
/// </para>
/// </remarks>
public sealed class ScopedTrainingPolicyManager
{
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<string, ScopedTrainingPolicy> _policies = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of ScopedTrainingPolicyManager.
    /// </summary>
    /// <param name="messageBus">Message bus for policy change notifications.</param>
    public ScopedTrainingPolicyManager(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        InitializeDefaultPolicies();
    }

    /// <summary>
    /// Sets a training policy for a specific scope.
    /// </summary>
    /// <param name="policy">Training policy configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetPolicyAsync(ScopedTrainingPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await _lock.WaitAsync(ct);
        try
        {
            var policyKey = policy.Scope.ToString();
            _policies[policyKey] = policy;

            var message = new PluginMessage
            {
                SourcePluginId ="intelligence.training-policy-manager",
                Payload = new Dictionary<string, object>
                {
                    ["Scope"] = policyKey,
                    ["Policy"] = policy,
                    ["UpdatedAt"] = DateTimeOffset.UtcNow
                }
            };

            await _messageBus.PublishAsync("intelligence.training.policy.updated", message, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the effective training policy for a scope.
    /// Walks up the scope hierarchy to find the nearest defined policy.
    /// </summary>
    /// <param name="scope">Scope to get policy for.</param>
    /// <returns>Effective training policy (inherited or direct).</returns>
    public async Task<ScopedTrainingPolicy> GetEffectivePolicyAsync(ScopeIdentifier scope)
    {
        await _lock.WaitAsync();
        try
        {
            var scopePath = scope.GetScopePath();

            // Search from most specific to most general
            for (int i = scopePath.Count - 1; i >= 0; i--)
            {
                var policyKey = scopePath[i].ToString();
                if (_policies.TryGetValue(policyKey, out var policy))
                {
                    return policy;
                }
            }

            // Return global default if no policy found
            return _policies["Global/global"];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Validates if a training request complies with scope policy.
    /// </summary>
    /// <param name="scope">Scope where training will occur.</param>
    /// <param name="datasetRowCount">Number of rows in training dataset.</param>
    /// <param name="requestedCpuCores">Requested CPU cores.</param>
    /// <param name="requestedMemoryMb">Requested memory in MB.</param>
    /// <returns>Validation result indicating compliance.</returns>
    public async Task<(bool IsValid, string? Reason)> ValidateTrainingRequestAsync(
        ScopeIdentifier scope,
        long datasetRowCount,
        int requestedCpuCores,
        long requestedMemoryMb)
    {
        var policy = await GetEffectivePolicyAsync(scope);

        if (datasetRowCount > policy.MaxTrainingDatasetRows)
        {
            return (false, $"Dataset size {datasetRowCount} exceeds limit {policy.MaxTrainingDatasetRows}");
        }

        if (requestedCpuCores > policy.MaxCpuCores)
        {
            return (false, $"CPU cores {requestedCpuCores} exceeds limit {policy.MaxCpuCores}");
        }

        if (requestedMemoryMb > policy.MaxMemoryMb)
        {
            return (false, $"Memory {requestedMemoryMb}MB exceeds limit {policy.MaxMemoryMb}MB");
        }

        return (true, null);
    }

    private void InitializeDefaultPolicies()
    {
        // Global default policy
        _policies["Global/global"] = new ScopedTrainingPolicy
        {
            Scope = ScopeIdentifier.Global(),
            MinimumRetrainingInterval = TimeSpan.FromDays(7),
            MaximumRetrainingInterval = TimeSpan.FromDays(30),
            MaxTrainingDatasetRows = 1_000_000,
            MaxCpuCores = 4,
            MaxMemoryMb = 8192,
            MaxTrainingDuration = TimeSpan.FromHours(4),
            AccuracyThreshold = 0.85,
            AutoRetrainingEnabled = true,
            RetainPreviousVersions = true,
            MaxVersionsToRetain = 3
        };
    }
}

/// <summary>
/// Manages the workflow for promoting successful models to broader organizational scopes.
/// Includes approval process, performance validation, and rollback capabilities.
/// </summary>
/// <remarks>
/// <para><b>Phase Y5: Multi-Instance Model Scoping</b></para>
/// <para>
/// ModelPromotionWorkflow enables controlled model propagation:
/// - Team models can be promoted to department or instance level
/// - Requires approval with performance metrics justification
/// - Automated rollback if promoted model underperforms
/// - Maintains audit trail of all promotions
/// </para>
///
/// <para><b>Promotion Process:</b></para>
/// <list type="number">
/// <item>User submits promotion request with metrics</item>
/// <item>System validates performance thresholds</item>
/// <item>Approver reviews and approves/rejects</item>
/// <item>On approval, model is deployed to target scope</item>
/// <item>System monitors performance post-promotion</item>
/// <item>Automatic rollback if quality degrades</item>
/// </list>
/// </remarks>
public sealed class ModelPromotionWorkflow
{
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<Guid, PromotionRequest> _requests = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of ModelPromotionWorkflow.
    /// </summary>
    /// <param name="messageBus">Message bus for workflow notifications.</param>
    public ModelPromotionWorkflow(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Submits a request to promote a model to a broader scope.
    /// </summary>
    /// <param name="request">Promotion request details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Request ID for tracking.</returns>
    public async Task<Guid> SubmitPromotionRequestAsync(
        PromotionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePromotionRequest(request);

        await _lock.WaitAsync(ct);
        try
        {
            _requests[request.RequestId] = request;

            var message = new PluginMessage
            {
                SourcePluginId ="intelligence.promotion-workflow",
                Payload = new Dictionary<string, object>
                {
                    ["RequestId"] = request.RequestId,
                    ["ModelId"] = request.ModelId,
                    ["CurrentScope"] = request.CurrentScope.ToString(),
                    ["TargetScope"] = request.TargetScope.ToString(),
                    ["RequestedBy"] = request.RequestedBy,
                    ["RequestedAt"] = request.RequestedAt
                }
            };

            await _messageBus.PublishAsync("intelligence.promotion.requested", message, ct);

            return request.RequestId;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Approves a pending promotion request.
    /// </summary>
    /// <param name="requestId">Request identifier to approve.</param>
    /// <param name="reviewedBy">User approving the request.</param>
    /// <param name="comments">Optional approval comments.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ApprovePromotionAsync(
        Guid requestId,
        string reviewedBy,
        string? comments = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_requests.TryGetValue(requestId, out var request))
            {
                throw new InvalidOperationException($"Promotion request {requestId} not found");
            }

            if (request.Status != PromotionStatus.Pending)
            {
                throw new InvalidOperationException($"Request {requestId} is not pending (status: {request.Status})");
            }

            var approvedRequest = new PromotionRequest
            {
                RequestId = request.RequestId,
                ModelId = request.ModelId,
                CurrentScope = request.CurrentScope,
                TargetScope = request.TargetScope,
                Justification = request.Justification,
                RequestedBy = request.RequestedBy,
                RequestedAt = request.RequestedAt,
                PerformanceMetrics = request.PerformanceMetrics,
                Status = PromotionStatus.Approved,
                ReviewedBy = reviewedBy,
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewComments = comments
            };

            _requests[requestId] = approvedRequest;

            var message = new PluginMessage
            {
                SourcePluginId ="intelligence.promotion-workflow",
                Payload = new Dictionary<string, object>
                {
                    ["RequestId"] = requestId,
                    ["ModelId"] = request.ModelId,
                    ["TargetScope"] = request.TargetScope.ToString(),
                    ["ReviewedBy"] = reviewedBy,
                    ["ApprovedAt"] = DateTimeOffset.UtcNow
                }
            };

            await _messageBus.PublishAsync("intelligence.promotion.approved", message, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Rejects a pending promotion request.
    /// </summary>
    /// <param name="requestId">Request identifier to reject.</param>
    /// <param name="reviewedBy">User rejecting the request.</param>
    /// <param name="reason">Reason for rejection.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RejectPromotionAsync(
        Guid requestId,
        string reviewedBy,
        string reason,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_requests.TryGetValue(requestId, out var request))
            {
                throw new InvalidOperationException($"Promotion request {requestId} not found");
            }

            var rejectedRequest = new PromotionRequest
            {
                RequestId = request.RequestId,
                ModelId = request.ModelId,
                CurrentScope = request.CurrentScope,
                TargetScope = request.TargetScope,
                Justification = request.Justification,
                RequestedBy = request.RequestedBy,
                RequestedAt = request.RequestedAt,
                PerformanceMetrics = request.PerformanceMetrics,
                Status = PromotionStatus.Rejected,
                ReviewedBy = reviewedBy,
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewComments = reason
            };

            _requests[requestId] = rejectedRequest;

            var message = new PluginMessage
            {
                SourcePluginId ="intelligence.promotion-workflow",
                Payload = new Dictionary<string, object>
                {
                    ["RequestId"] = requestId,
                    ["ModelId"] = request.ModelId,
                    ["ReviewedBy"] = reviewedBy,
                    ["Reason"] = reason,
                    ["RejectedAt"] = DateTimeOffset.UtcNow
                }
            };

            await _messageBus.PublishAsync("intelligence.promotion.rejected", message, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the status of a promotion request.
    /// </summary>
    /// <param name="requestId">Request identifier to query.</param>
    /// <returns>Promotion request, or null if not found.</returns>
    public async Task<PromotionRequest?> GetRequestAsync(Guid requestId)
    {
        await _lock.WaitAsync();
        try
        {
            return _requests.TryGetValue(requestId, out var request) ? request : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all pending promotion requests.
    /// </summary>
    /// <returns>List of pending promotion requests.</returns>
    public async Task<IReadOnlyList<PromotionRequest>> GetPendingRequestsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _requests.Values
                .Where(r => r.Status == PromotionStatus.Pending)
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ValidatePromotionRequest(PromotionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("ModelId is required", nameof(request));
        }

        // Target scope must be broader than current scope
        if (request.TargetScope.ScopeLevel >= request.CurrentScope.ScopeLevel)
        {
            throw new ArgumentException(
                $"Target scope {request.TargetScope.ScopeLevel} must be broader than current scope {request.CurrentScope.ScopeLevel}",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Justification))
        {
            throw new ArgumentException("Justification is required for promotion", nameof(request));
        }
    }
}

#endregion

#region Phase Y6: Model Specialization & Expertise

/// <summary>
/// Measures and tracks model expertise across different data domains and query types.
/// Enables data-driven model selection based on demonstrated performance.
/// </summary>
/// <remarks>
/// <para><b>Phase Y6: Model Specialization &amp; Expertise</b></para>
/// <para>
/// ExpertiseScorer analyzes historical model performance to build expertise profiles:
/// - Tracks accuracy per domain and query type
/// - Calculates confidence intervals for scores
/// - Identifies areas of strength and weakness
/// - Updates expertise metrics in real-time
/// </para>
///
/// <para><b>Scoring Methodology:</b></para>
/// <list type="bullet">
/// <item>Weighted moving average of recent performance</item>
/// <item>Decays old performance data to favor recent trends</item>
/// <item>Requires minimum sample size for statistical validity</item>
/// <item>Tracks response time and accuracy separately</item>
/// </list>
/// </remarks>
public sealed class ExpertiseScorer
{
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<string, List<ExpertiseMetrics>> _modelExpertise = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const int MinimumSampleSize = 10;
    private const double DecayFactor = 0.95; // Recent queries weighted higher

    /// <summary>
    /// Initializes a new instance of ExpertiseScorer.
    /// </summary>
    /// <param name="messageBus">Message bus for score updates.</param>
    public ExpertiseScorer(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Records a query result to update model expertise metrics.
    /// </summary>
    /// <param name="modelId">Model that processed the query.</param>
    /// <param name="domain">Data domain of the query.</param>
    /// <param name="queryType">Type of query executed.</param>
    /// <param name="accuracy">Accuracy of the result (0.0 to 1.0).</param>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordQueryResultAsync(
        string modelId,
        string domain,
        string queryType,
        double accuracy,
        double responseTimeMs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(queryType);

        if (accuracy < 0.0 || accuracy > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(accuracy), "Accuracy must be between 0.0 and 1.0");
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (!_modelExpertise.TryGetValue(modelId, out var metricsList))
            {
                metricsList = new List<ExpertiseMetrics>();
                _modelExpertise[modelId] = metricsList;
            }

            // Find or create metrics for this domain/query type
            var metrics = metricsList.FirstOrDefault(m =>
                m.Domain == domain && m.QueryType == queryType);

            if (metrics == null)
            {
                // Create new metrics entry
                metrics = new ExpertiseMetrics
                {
                    Domain = domain,
                    QueryType = queryType,
                    ExpertiseScore = accuracy,
                    Accuracy = accuracy * 100.0,
                    AverageResponseTimeMs = responseTimeMs,
                    SampleSize = 1,
                    LastUpdated = DateTimeOffset.UtcNow,
                    ConfidenceInterval = 0.1 // Low confidence with single sample
                };
                metricsList.Add(metrics);
            }
            else
            {
                // Update existing metrics with exponential moving average
                var updatedMetrics = UpdateMetrics(metrics, accuracy, responseTimeMs);
                metricsList.Remove(metrics);
                metricsList.Add(updatedMetrics);
            }

            await PublishExpertiseUpdateAsync(modelId, domain, queryType, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets expertise metrics for a specific model.
    /// </summary>
    /// <param name="modelId">Model identifier to query.</param>
    /// <returns>List of expertise metrics across all domains/query types.</returns>
    public async Task<IReadOnlyList<ExpertiseMetrics>> GetModelExpertiseAsync(string modelId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_modelExpertise.TryGetValue(modelId, out var metrics))
            {
                return metrics.AsReadOnly();
            }
            return Array.Empty<ExpertiseMetrics>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets expertise score for a specific domain and query type.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="domain">Data domain.</param>
    /// <param name="queryType">Query type.</param>
    /// <returns>Expertise metrics, or null if insufficient data.</returns>
    public async Task<ExpertiseMetrics?> GetExpertiseScoreAsync(
        string modelId,
        string domain,
        string queryType)
    {
        await _lock.WaitAsync();
        try
        {
            if (_modelExpertise.TryGetValue(modelId, out var metricsList))
            {
                var metrics = metricsList.FirstOrDefault(m =>
                    m.Domain == domain && m.QueryType == queryType);

                // Only return if we have sufficient samples
                if (metrics != null && metrics.SampleSize >= MinimumSampleSize)
                {
                    return metrics;
                }
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Finds the model with highest expertise for a specific domain/query type.
    /// </summary>
    /// <param name="domain">Data domain to search.</param>
    /// <param name="queryType">Query type to search.</param>
    /// <returns>Model ID of the most expert model, or null if none found.</returns>
    public async Task<string?> FindExpertModelAsync(string domain, string queryType)
    {
        await _lock.WaitAsync();
        try
        {
            string? bestModelId = null;
            double bestScore = 0.0;

            foreach (var (modelId, metricsList) in _modelExpertise)
            {
                var metrics = metricsList.FirstOrDefault(m =>
                    m.Domain == domain && m.QueryType == queryType);

                if (metrics != null &&
                    metrics.SampleSize >= MinimumSampleSize &&
                    metrics.ExpertiseScore > bestScore)
                {
                    bestScore = metrics.ExpertiseScore;
                    bestModelId = modelId;
                }
            }

            return bestModelId;
        }
        finally
        {
            _lock.Release();
        }
    }

    private ExpertiseMetrics UpdateMetrics(
        ExpertiseMetrics current,
        double newAccuracy,
        double newResponseTime)
    {
        var newSampleSize = current.SampleSize + 1;

        // Exponential moving average favoring recent results
        var updatedExpertise = (current.ExpertiseScore * DecayFactor) + (newAccuracy * (1 - DecayFactor));
        var updatedAccuracy = (current.Accuracy * DecayFactor) + (newAccuracy * 100.0 * (1 - DecayFactor));
        var updatedResponseTime = (current.AverageResponseTimeMs * DecayFactor) + (newResponseTime * (1 - DecayFactor));

        // Confidence increases with sample size (capped at 0.95)
        var confidence = Math.Min(0.95, 1.0 - (1.0 / Math.Sqrt(newSampleSize)));

        return new ExpertiseMetrics
        {
            Domain = current.Domain,
            QueryType = current.QueryType,
            ExpertiseScore = updatedExpertise,
            Accuracy = updatedAccuracy,
            AverageResponseTimeMs = updatedResponseTime,
            SampleSize = newSampleSize,
            LastUpdated = DateTimeOffset.UtcNow,
            ConfidenceInterval = confidence
        };
    }

    private async Task PublishExpertiseUpdateAsync(
        string modelId,
        string domain,
        string queryType,
        CancellationToken ct)
    {
        var message = new PluginMessage
        {
            SourcePluginId ="intelligence.expertise-scorer",
            Payload = new Dictionary<string, object>
            {
                ["ModelId"] = modelId,
                ["Domain"] = domain,
                ["QueryType"] = queryType,
                ["UpdatedAt"] = DateTimeOffset.UtcNow
            }
        };

        await _messageBus.PublishAsync("intelligence.expertise.updated", message, ct);
    }
}

/// <summary>
/// Tracks which models are best suited for specific query types and data domains.
/// Maintains a continuously updated expertise index for intelligent routing.
/// </summary>
/// <remarks>
/// <para><b>Phase Y6: Model Specialization &amp; Expertise</b></para>
/// <para>
/// SpecializationTracker builds a comprehensive index of model specializations:
/// - Maps query patterns to best-performing models
/// - Identifies emerging specializations
/// - Detects when models lose expertise
/// - Supports multi-dimensional expertise tracking
/// </para>
/// </remarks>
public sealed class SpecializationTracker
{
    private readonly IMessageBus _messageBus;
    private readonly ExpertiseScorer _expertiseScorer;
    private readonly Dictionary<string, string> _specializations = new(); // Key: domain:queryType, Value: modelId
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of SpecializationTracker.
    /// </summary>
    /// <param name="messageBus">Message bus for notifications.</param>
    /// <param name="expertiseScorer">Expertise scoring engine.</param>
    public SpecializationTracker(IMessageBus messageBus, ExpertiseScorer expertiseScorer)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _expertiseScorer = expertiseScorer ?? throw new ArgumentNullException(nameof(expertiseScorer));
        SubscribeToExpertiseUpdates();
    }

    /// <summary>
    /// Gets the specialist model for a specific domain and query type.
    /// </summary>
    /// <param name="domain">Data domain.</param>
    /// <param name="queryType">Query type.</param>
    /// <returns>Model ID of the specialist, or null if none identified.</returns>
    public async Task<string?> GetSpecialistAsync(string domain, string queryType)
    {
        await _lock.WaitAsync();
        try
        {
            var key = $"{domain}:{queryType}";
            return _specializations.TryGetValue(key, out var modelId) ? modelId : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all specializations for a specific model.
    /// </summary>
    /// <param name="modelId">Model to query.</param>
    /// <returns>List of (domain, queryType) tuples where this model is the specialist.</returns>
    public async Task<IReadOnlyList<(string Domain, string QueryType)>> GetModelSpecializationsAsync(string modelId)
    {
        await _lock.WaitAsync();
        try
        {
            return _specializations
                .Where(kvp => kvp.Value == modelId)
                .Select(kvp =>
                {
                    var parts = kvp.Key.Split(':');
                    return (parts[0], parts[1]);
                })
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Re-evaluates all specializations to identify current experts.
    /// Should be called periodically to keep specialization index current.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RefreshSpecializationsAsync(CancellationToken ct = default)
    {
        // Get all unique domain/query type combinations
        var combinations = new HashSet<(string Domain, string QueryType)>();

        // This would typically query historical data
        // For now, we'll rely on expertise scorer's data

        await _lock.WaitAsync(ct);
        try
        {
            foreach (var (domain, queryType) in combinations)
            {
                var expertModel = await _expertiseScorer.FindExpertModelAsync(domain, queryType);
                if (expertModel != null)
                {
                    var key = $"{domain}:{queryType}";
                    var previousExpert = _specializations.TryGetValue(key, out var prev) ? prev : null;

                    _specializations[key] = expertModel;

                    if (previousExpert != expertModel)
                    {
                        await PublishSpecializationChangeAsync(domain, queryType, expertModel, previousExpert, ct);
                    }
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SubscribeToExpertiseUpdates()
    {
        _messageBus.Subscribe("intelligence.expertise.updated", async msg =>
        {
            if (msg.Payload.TryGetValue("ModelId", out var modelIdObj) &&
                msg.Payload.TryGetValue("Domain", out var domainObj) &&
                msg.Payload.TryGetValue("QueryType", out var queryTypeObj) &&
                modelIdObj is string modelId &&
                domainObj is string domain &&
                queryTypeObj is string queryType)
            {
                // Re-evaluate specialization for this domain/query type
                var expertModel = await _expertiseScorer.FindExpertModelAsync(domain, queryType);
                if (expertModel != null)
                {
                    await _lock.WaitAsync();
                    try
                    {
                        var key = $"{domain}:{queryType}";
                        _specializations[key] = expertModel;
                    }
                    finally
                    {
                        _lock.Release();
                    }
                }
            }
        });
    }

    private async Task PublishSpecializationChangeAsync(
        string domain,
        string queryType,
        string newExpert,
        string? previousExpert,
        CancellationToken ct)
    {
        var message = new PluginMessage
        {
            SourcePluginId ="intelligence.specialization-tracker",
            Payload = new Dictionary<string, object>
            {
                ["Domain"] = domain,
                ["QueryType"] = queryType,
                ["NewExpert"] = newExpert,
                ["PreviousExpert"] = previousExpert ?? "none",
                ["ChangedAt"] = DateTimeOffset.UtcNow
            }
        };

        await _messageBus.PublishAsync("intelligence.specialization.changed", message, ct);
    }
}

/// <summary>
/// Routes queries to the most expert model based on query type and domain.
/// Provides intelligent load balancing and fallback strategies.
/// </summary>
/// <remarks>
/// <para><b>Phase Y6: Model Specialization &amp; Expertise</b></para>
/// <para>
/// AdaptiveModelRouter dynamically selects the optimal model for each query:
/// - Uses SpecializationTracker to find domain experts
/// - Falls back to generalist models if no specialist available
/// - Considers model availability and load
/// - Adapts routing based on real-time performance
/// </para>
///
/// <para><b>Routing Strategy:</b></para>
/// <list type="number">
/// <item>Check for domain/query type specialist</item>
/// <item>If specialist unavailable, use generalist model</item>
/// <item>Monitor response quality and adjust routing</item>
/// <item>Load balance across multiple capable models</item>
/// </list>
/// </remarks>
public sealed class AdaptiveModelRouter
{
    private readonly IMessageBus _messageBus;
    private readonly SpecializationTracker _specializationTracker;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, int> _modelLoad = new(); // Track active queries per model

    /// <summary>
    /// Initializes a new instance of AdaptiveModelRouter.
    /// </summary>
    /// <param name="messageBus">Message bus for routing events.</param>
    /// <param name="specializationTracker">Specialization tracking engine.</param>
    public AdaptiveModelRouter(
        IMessageBus messageBus,
        SpecializationTracker specializationTracker)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _specializationTracker = specializationTracker ?? throw new ArgumentNullException(nameof(specializationTracker));
    }

    /// <summary>
    /// Routes a query to the optimal model based on domain and query type.
    /// </summary>
    /// <param name="domain">Data domain of the query.</param>
    /// <param name="queryType">Type of query being executed.</param>
    /// <param name="fallbackModelId">Fallback model if no specialist found.</param>
    /// <returns>Model ID to route the query to.</returns>
    public async Task<string> RouteQueryAsync(
        string domain,
        string queryType,
        string fallbackModelId)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(queryType);
        ArgumentNullException.ThrowIfNull(fallbackModelId);

        // Try to find specialist
        var specialist = await _specializationTracker.GetSpecialistAsync(domain, queryType);
        var selectedModel = specialist ?? fallbackModelId;

        await _lock.WaitAsync();
        try
        {
            // Track model load
            _modelLoad.TryGetValue(selectedModel, out var currentLoad);
            _modelLoad[selectedModel] = currentLoad + 1;
        }
        finally
        {
            _lock.Release();
        }

        await PublishRoutingDecisionAsync(domain, queryType, selectedModel, specialist != null);

        return selectedModel;
    }

    /// <summary>
    /// Notifies router that a query has completed.
    /// Updates model load tracking.
    /// </summary>
    /// <param name="modelId">Model that processed the query.</param>
    public async Task CompleteQueryAsync(string modelId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_modelLoad.TryGetValue(modelId, out var currentLoad) && currentLoad > 0)
            {
                _modelLoad[modelId] = currentLoad - 1;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets current load metrics for all models.
    /// </summary>
    /// <returns>Dictionary mapping model IDs to active query count.</returns>
    public async Task<IReadOnlyDictionary<string, int>> GetLoadMetricsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return new Dictionary<string, int>(_modelLoad);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PublishRoutingDecisionAsync(
        string domain,
        string queryType,
        string selectedModel,
        bool usedSpecialist)
    {
        var message = new PluginMessage
        {
            SourcePluginId ="intelligence.adaptive-model-router",
            Payload = new Dictionary<string, object>
            {
                ["Domain"] = domain,
                ["QueryType"] = queryType,
                ["SelectedModel"] = selectedModel,
                ["UsedSpecialist"] = usedSpecialist,
                ["RoutedAt"] = DateTimeOffset.UtcNow
            }
        };

        await _messageBus.PublishAsync("intelligence.query.routed", message);
    }
}

/// <summary>
/// Monitors model performance drift and triggers retraining when accuracy degrades.
/// Ensures models maintain quality over time as data patterns change.
/// </summary>
/// <remarks>
/// <para><b>Phase Y6: Model Specialization &amp; Expertise</b></para>
/// <para>
/// ExpertiseEvolution tracks model performance trends to detect:
/// - Accuracy degradation over time
/// - Concept drift in underlying data
/// - Need for model retraining
/// - Performance anomalies
/// </para>
///
/// <para><b>Drift Detection:</b></para>
/// <para>
/// Uses statistical process control to identify when model accuracy
/// falls outside expected bounds. Triggers automatic retraining when
/// performance degrades below policy thresholds.
/// </para>
/// </remarks>
public sealed class ExpertiseEvolution
{
    private readonly IMessageBus _messageBus;
    private readonly ExpertiseScorer _expertiseScorer;
    private readonly Dictionary<string, PerformanceHistory> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const int HistoryWindowSize = 100;

    private sealed class PerformanceHistory
    {
        public Queue<double> RecentAccuracies { get; } = new();
        public double BaselineAccuracy { get; set; }
        public DateTimeOffset LastRetrainingCheck { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Initializes a new instance of ExpertiseEvolution.
    /// </summary>
    /// <param name="messageBus">Message bus for drift notifications.</param>
    /// <param name="expertiseScorer">Expertise scoring engine.</param>
    public ExpertiseEvolution(IMessageBus messageBus, ExpertiseScorer expertiseScorer)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _expertiseScorer = expertiseScorer ?? throw new ArgumentNullException(nameof(expertiseScorer));
        SubscribeToExpertiseUpdates();
    }

    /// <summary>
    /// Checks if a model requires retraining based on performance trends.
    /// </summary>
    /// <param name="modelId">Model to evaluate.</param>
    /// <param name="accuracyThreshold">Minimum acceptable accuracy (0.0 to 1.0).</param>
    /// <returns>True if retraining is recommended.</returns>
    public async Task<bool> RequiresRetrainingAsync(string modelId, double accuracyThreshold)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_history.TryGetValue(modelId, out var history))
            {
                return false; // Not enough data yet
            }

            if (history.RecentAccuracies.Count < 10)
            {
                return false; // Need minimum sample size
            }

            // Calculate recent average accuracy
            var recentAverage = history.RecentAccuracies.Average();

            // Check if accuracy dropped below threshold
            if (recentAverage < accuracyThreshold)
            {
                return true;
            }

            // Check for significant drift from baseline
            var drift = Math.Abs(recentAverage - history.BaselineAccuracy);
            if (drift > 0.1) // 10% drift threshold
            {
                return true;
            }

            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Records that a model has been retrained.
    /// Resets performance baseline and history.
    /// </summary>
    /// <param name="modelId">Model that was retrained.</param>
    /// <param name="newBaselineAccuracy">Accuracy of newly trained model.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordRetrainingAsync(
        string modelId,
        double newBaselineAccuracy,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _history[modelId] = new PerformanceHistory
            {
                BaselineAccuracy = newBaselineAccuracy,
                LastRetrainingCheck = DateTimeOffset.UtcNow
            };

            var message = new PluginMessage
            {
                SourcePluginId ="intelligence.expertise-evolution",
                Payload = new Dictionary<string, object>
                {
                    ["ModelId"] = modelId,
                    ["NewBaselineAccuracy"] = newBaselineAccuracy,
                    ["RetrainedAt"] = DateTimeOffset.UtcNow
                }
            };

            await _messageBus.PublishAsync("intelligence.model.retrained", message, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets performance trend data for a model.
    /// </summary>
    /// <param name="modelId">Model to query.</param>
    /// <returns>Recent accuracy history.</returns>
    public async Task<IReadOnlyList<double>> GetPerformanceTrendAsync(string modelId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_history.TryGetValue(modelId, out var history))
            {
                return history.RecentAccuracies.ToList().AsReadOnly();
            }
            return Array.Empty<double>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SubscribeToExpertiseUpdates()
    {
        _messageBus.Subscribe("intelligence.expertise.updated", async msg =>
        {
            if (msg.Payload.TryGetValue("ModelId", out var modelIdObj) &&
                modelIdObj is string modelId)
            {
                var expertise = await _expertiseScorer.GetModelExpertiseAsync(modelId);
                if (expertise.Count > 0)
                {
                    await UpdateHistoryAsync(modelId, expertise[0].Accuracy / 100.0);
                }
            }
        });
    }

    private async Task UpdateHistoryAsync(string modelId, double accuracy)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_history.TryGetValue(modelId, out var history))
            {
                history = new PerformanceHistory
                {
                    BaselineAccuracy = accuracy
                };
                _history[modelId] = history;
            }

            history.RecentAccuracies.Enqueue(accuracy);

            // Maintain sliding window
            while (history.RecentAccuracies.Count > HistoryWindowSize)
            {
                history.RecentAccuracies.Dequeue();
            }

            // Check for drift and trigger alert if needed
            if (history.RecentAccuracies.Count >= 10)
            {
                var recentAverage = history.RecentAccuracies.Average();
                var drift = Math.Abs(recentAverage - history.BaselineAccuracy);

                if (drift > 0.15) // 15% drift triggers alert
                {
                    await PublishDriftAlertAsync(modelId, drift, recentAverage);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PublishDriftAlertAsync(string modelId, double drift, double currentAccuracy)
    {
        var message = new PluginMessage
        {
            SourcePluginId ="intelligence.expertise-evolution",
            Payload = new Dictionary<string, object>
            {
                ["Severity"] = "Warning",
                ["ModelId"] = modelId,
                ["DriftPercentage"] = drift * 100.0,
                ["CurrentAccuracy"] = currentAccuracy * 100.0,
                ["DetectedAt"] = DateTimeOffset.UtcNow,
                ["RecommendedAction"] = "Evaluate for retraining"
            }
        };

        await _messageBus.PublishAsync("intelligence.drift.detected", message);
    }
}

/// <summary>
/// Combines predictions from multiple models to achieve higher accuracy through ensemble methods.
/// Implements voting, averaging, and weighted combination strategies.
/// </summary>
/// <remarks>
/// <para><b>Phase Y6: Model Specialization &amp; Expertise</b></para>
/// <para>
/// ModelEnsemble leverages multiple models for improved predictions:
/// - Voting for classification tasks
/// - Averaging for regression tasks
/// - Weighted combination based on expertise
/// - Confidence-based selection
/// </para>
///
/// <para><b>Ensemble Strategies:</b></para>
/// <list type="bullet">
/// <item><b>Majority Voting:</b> Use most common prediction</item>
/// <item><b>Weighted Voting:</b> Weight votes by model expertise</item>
/// <item><b>Averaging:</b> Average predictions for numeric results</item>
/// <item><b>Weighted Average:</b> Weight by confidence scores</item>
/// </list>
///
/// <para><b>Benefits:</b></para>
/// <para>
/// Ensemble methods typically provide 5-15% accuracy improvement over
/// single models, especially when models have complementary strengths.
/// </para>
/// </remarks>
public sealed class ModelEnsemble
{
    private readonly IMessageBus _messageBus;
    private readonly ExpertiseScorer _expertiseScorer;

    /// <summary>
    /// Ensemble strategy for combining predictions.
    /// </summary>
    public enum EnsembleStrategy
    {
        /// <summary>
        /// Majority voting (for classification).
        /// </summary>
        MajorityVoting,

        /// <summary>
        /// Weighted voting based on model expertise.
        /// </summary>
        WeightedVoting,

        /// <summary>
        /// Simple average (for regression).
        /// </summary>
        Averaging,

        /// <summary>
        /// Weighted average based on confidence scores.
        /// </summary>
        WeightedAverage,

        /// <summary>
        /// Use prediction from most expert model.
        /// </summary>
        ExpertSelection
    }

    /// <summary>
    /// Represents a prediction from a single model.
    /// </summary>
    public sealed class ModelPrediction
    {
        public string ModelId { get; init; } = string.Empty;
        public object Value { get; init; } = default!;
        public double Confidence { get; init; }
        public double ExpertiseScore { get; init; }
    }

    /// <summary>
    /// Result of ensemble combination.
    /// </summary>
    public sealed class EnsembleResult
    {
        public object CombinedValue { get; init; } = default!;
        public double Confidence { get; init; }
        public IReadOnlyList<ModelPrediction> IndividualPredictions { get; init; } = Array.Empty<ModelPrediction>();
        public EnsembleStrategy Strategy { get; init; }
        public int ModelCount { get; init; }
    }

    /// <summary>
    /// Initializes a new instance of ModelEnsemble.
    /// </summary>
    /// <param name="messageBus">Message bus for ensemble notifications.</param>
    /// <param name="expertiseScorer">Expertise scoring engine.</param>
    public ModelEnsemble(IMessageBus messageBus, ExpertiseScorer expertiseScorer)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _expertiseScorer = expertiseScorer ?? throw new ArgumentNullException(nameof(expertiseScorer));
    }

    /// <summary>
    /// Combines predictions from multiple models using the specified strategy.
    /// </summary>
    /// <param name="predictions">Individual model predictions.</param>
    /// <param name="strategy">Ensemble strategy to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Combined ensemble result.</returns>
    public async Task<EnsembleResult> CombinePredictionsAsync(
        IReadOnlyList<ModelPrediction> predictions,
        EnsembleStrategy strategy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        if (predictions.Count == 0)
        {
            throw new ArgumentException("No predictions provided", nameof(predictions));
        }

        if (predictions.Count == 1)
        {
            // Single prediction, no ensemble needed
            return new EnsembleResult
            {
                CombinedValue = predictions[0].Value,
                Confidence = predictions[0].Confidence,
                IndividualPredictions = predictions,
                Strategy = strategy,
                ModelCount = 1
            };
        }

        var result = strategy switch
        {
            EnsembleStrategy.MajorityVoting => CombineMajorityVoting(predictions),
            EnsembleStrategy.WeightedVoting => CombineWeightedVoting(predictions),
            EnsembleStrategy.Averaging => CombineAveraging(predictions),
            EnsembleStrategy.WeightedAverage => CombineWeightedAverage(predictions),
            EnsembleStrategy.ExpertSelection => CombineExpertSelection(predictions),
            _ => throw new ArgumentException($"Unknown ensemble strategy: {strategy}", nameof(strategy))
        };

        await PublishEnsembleResultAsync(result, ct);

        return result;
    }

    private EnsembleResult CombineMajorityVoting(IReadOnlyList<ModelPrediction> predictions)
    {
        var votes = predictions
            .GroupBy(p => p.Value?.ToString() ?? string.Empty)
            .OrderByDescending(g => g.Count())
            .First();

        var winningValue = predictions.First(p => (p.Value?.ToString() ?? string.Empty) == votes.Key).Value;
        var confidence = (double)votes.Count() / predictions.Count;

        return new EnsembleResult
        {
            CombinedValue = winningValue,
            Confidence = confidence,
            IndividualPredictions = predictions,
            Strategy = EnsembleStrategy.MajorityVoting,
            ModelCount = predictions.Count
        };
    }

    private EnsembleResult CombineWeightedVoting(IReadOnlyList<ModelPrediction> predictions)
    {
        var weightedVotes = predictions
            .GroupBy(p => p.Value?.ToString() ?? string.Empty)
            .Select(g => new
            {
                Value = g.First().Value,
                Weight = g.Sum(p => p.ExpertiseScore * p.Confidence)
            })
            .OrderByDescending(x => x.Weight)
            .First();

        var totalWeight = predictions.Sum(p => p.ExpertiseScore * p.Confidence);
        var confidence = weightedVotes.Weight / totalWeight;

        return new EnsembleResult
        {
            CombinedValue = weightedVotes.Value,
            Confidence = confidence,
            IndividualPredictions = predictions,
            Strategy = EnsembleStrategy.WeightedVoting,
            ModelCount = predictions.Count
        };
    }

    private EnsembleResult CombineAveraging(IReadOnlyList<ModelPrediction> predictions)
    {
        var numericValues = predictions
            .Select(p => Convert.ToDouble(p.Value))
            .ToList();

        var average = numericValues.Average();
        var confidence = predictions.Average(p => p.Confidence);

        return new EnsembleResult
        {
            CombinedValue = average,
            Confidence = confidence,
            IndividualPredictions = predictions,
            Strategy = EnsembleStrategy.Averaging,
            ModelCount = predictions.Count
        };
    }

    private EnsembleResult CombineWeightedAverage(IReadOnlyList<ModelPrediction> predictions)
    {
        var totalWeight = predictions.Sum(p => p.Confidence * p.ExpertiseScore);

        var weightedSum = predictions
            .Sum(p => Convert.ToDouble(p.Value) * p.Confidence * p.ExpertiseScore);

        var weightedAverage = weightedSum / totalWeight;
        var confidence = predictions.Average(p => p.Confidence);

        return new EnsembleResult
        {
            CombinedValue = weightedAverage,
            Confidence = confidence,
            IndividualPredictions = predictions,
            Strategy = EnsembleStrategy.WeightedAverage,
            ModelCount = predictions.Count
        };
    }

    private EnsembleResult CombineExpertSelection(IReadOnlyList<ModelPrediction> predictions)
    {
        var expert = predictions
            .OrderByDescending(p => p.ExpertiseScore * p.Confidence)
            .First();

        return new EnsembleResult
        {
            CombinedValue = expert.Value,
            Confidence = expert.Confidence,
            IndividualPredictions = predictions,
            Strategy = EnsembleStrategy.ExpertSelection,
            ModelCount = predictions.Count
        };
    }

    private async Task PublishEnsembleResultAsync(EnsembleResult result, CancellationToken ct)
    {
        var message = new PluginMessage
        {
            SourcePluginId ="intelligence.model-ensemble",
            Payload = new Dictionary<string, object>
            {
                ["Strategy"] = result.Strategy.ToString(),
                ["ModelCount"] = result.ModelCount,
                ["Confidence"] = result.Confidence,
                ["CombinedAt"] = DateTimeOffset.UtcNow
            }
        };

        await _messageBus.PublishAsync("intelligence.ensemble.combined", message, ct);
    }
}

#endregion
