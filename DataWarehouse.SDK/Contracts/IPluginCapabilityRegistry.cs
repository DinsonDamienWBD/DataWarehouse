using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Capability category for grouping related capabilities.
/// </summary>
public enum CapabilityCategory
{
    Storage = 0,
    Encryption = 1,
    Compression = 2,
    KeyManagement = 3,
    Metadata = 4,
    Search = 5,
    AI = 6,
    Analytics = 7,
    Governance = 8,
    Security = 9,
    Replication = 10,
    RAID = 11,
    Pipeline = 12,
    Transit = 13,
    Connector = 14,
    Intelligence = 15,
    Observability = 16,
    Database = 17,
    DataManagement = 18,
    Resilience = 19,
    Deployment = 20,
    Sustainability = 21,
    Transport = 22,
    Compute = 23,
    Archival = 24,
    Infrastructure = 25,
    Hardware = 26,
    Compliance = 27,
    TamperProof = 28,
    Interface = 29,
    AccessControl = 30,
    Custom = 100
}

/// <summary>
/// Represents a registered capability in the system.
/// Includes the plugin that provides it and full metadata.
/// </summary>
public record RegisteredCapability
{
    /// <summary>
    /// Unique capability identifier (e.g., "encryption.aes-256-gcm").
    /// </summary>
    public required string CapabilityId { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Detailed description of what this capability does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Category for grouping.
    /// </summary>
    public required CapabilityCategory Category { get; init; }

    /// <summary>
    /// Sub-category for finer grouping (e.g., "AEAD", "PostQuantum").
    /// </summary>
    public string? SubCategory { get; init; }

    /// <summary>
    /// Plugin ID that provides this capability.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Plugin name for display.
    /// </summary>
    public required string PluginName { get; init; }

    /// <summary>
    /// Plugin version.
    /// </summary>
    public required string PluginVersion { get; init; }

    /// <summary>
    /// When this capability was registered.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether capability is currently available (plugin loaded and healthy).
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// Tags for discovery (e.g., ["fips", "hardware-accelerated", "streaming"]).
    /// </summary>
    public string[] Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Capability-specific metadata (parameters, constraints, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// JSON schema for parameters this capability accepts.
    /// </summary>
    public string? ParameterSchema { get; init; }

    /// <summary>
    /// Dependencies on other capabilities.
    /// </summary>
    public string[] Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Capabilities this one conflicts with (mutually exclusive).
    /// </summary>
    public string[] ConflictsWith { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Priority/quality level (higher = preferred when multiple options).
    /// </summary>
    public int Priority { get; init; } = 50;

    /// <summary>
    /// Semantic description for AI-driven discovery.
    /// </summary>
    public string? SemanticDescription { get; init; }
}

/// <summary>
/// Query for discovering capabilities.
/// </summary>
public record CapabilityQuery
{
    /// <summary>
    /// Filter by category.
    /// </summary>
    public CapabilityCategory? Category { get; init; }

    /// <summary>
    /// Filter by sub-category.
    /// </summary>
    public string? SubCategory { get; init; }

    /// <summary>
    /// Filter by plugin ID.
    /// </summary>
    public string? PluginId { get; init; }

    /// <summary>
    /// Filter by tags (all must match).
    /// </summary>
    public string[]? RequiredTags { get; init; }

    /// <summary>
    /// Filter by any of these tags.
    /// </summary>
    public string[]? AnyOfTags { get; init; }

    /// <summary>
    /// Exclude capabilities with these tags.
    /// </summary>
    public string[]? ExcludeTags { get; init; }

    /// <summary>
    /// Text search in name/description.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// Only return available capabilities.
    /// </summary>
    public bool OnlyAvailable { get; init; } = true;

    /// <summary>
    /// Minimum priority level.
    /// </summary>
    public int? MinPriority { get; init; }

    /// <summary>
    /// Maximum results to return.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Sort by field.
    /// </summary>
    public string? SortBy { get; init; }

    /// <summary>
    /// Sort descending.
    /// </summary>
    public bool SortDescending { get; init; }
}

/// <summary>
/// Result of a capability query.
/// </summary>
public record CapabilityQueryResult
{
    /// <summary>
    /// Matching capabilities.
    /// </summary>
    public required IReadOnlyList<RegisteredCapability> Capabilities { get; init; }

    /// <summary>
    /// Total matching (before limit).
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// Query execution time.
    /// </summary>
    public TimeSpan QueryTime { get; init; }

    /// <summary>
    /// Categories represented in results.
    /// </summary>
    public IReadOnlyDictionary<CapabilityCategory, int> CategoryCounts { get; init; } =
        new Dictionary<CapabilityCategory, int>();
}

/// <summary>
/// Central registry for plugin capabilities.
/// Enables discovery of "who can do what" across the entire system.
/// </summary>
public interface IPluginCapabilityRegistry
{
    #region Registration

    /// <summary>
    /// Register a capability provided by a plugin.
    /// </summary>
    /// <param name="capability">Capability to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if registered, false if already exists.</returns>
    Task<bool> RegisterAsync(RegisteredCapability capability, CancellationToken ct = default);

    /// <summary>
    /// Register multiple capabilities at once (batch registration).
    /// </summary>
    /// <param name="capabilities">Capabilities to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of capabilities registered.</returns>
    Task<int> RegisterBatchAsync(IEnumerable<RegisteredCapability> capabilities, CancellationToken ct = default);

    /// <summary>
    /// Unregister a capability.
    /// </summary>
    /// <param name="capabilityId">Capability ID to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if removed, false if not found.</returns>
    Task<bool> UnregisterAsync(string capabilityId, CancellationToken ct = default);

    /// <summary>
    /// Unregister all capabilities for a plugin.
    /// Called when a plugin is unloaded.
    /// </summary>
    /// <param name="pluginId">Plugin ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of capabilities removed.</returns>
    Task<int> UnregisterPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// Update availability status for a plugin's capabilities.
    /// </summary>
    /// <param name="pluginId">Plugin ID.</param>
    /// <param name="isAvailable">Availability status.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetPluginAvailabilityAsync(string pluginId, bool isAvailable, CancellationToken ct = default);

    #endregion

    #region Discovery

    /// <summary>
    /// Get a specific capability by ID.
    /// </summary>
    /// <param name="capabilityId">Capability ID.</param>
    /// <returns>Capability or null if not found.</returns>
    RegisteredCapability? GetCapability(string capabilityId);

    /// <summary>
    /// Check if a capability exists and is available.
    /// </summary>
    /// <param name="capabilityId">Capability ID.</param>
    /// <returns>True if exists and available.</returns>
    bool IsCapabilityAvailable(string capabilityId);

    /// <summary>
    /// Query capabilities with filters.
    /// </summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result with matching capabilities.</returns>
    Task<CapabilityQueryResult> QueryAsync(CapabilityQuery query, CancellationToken ct = default);

    /// <summary>
    /// Get all capabilities in a category.
    /// </summary>
    /// <param name="category">Category to filter.</param>
    /// <returns>Capabilities in category.</returns>
    IReadOnlyList<RegisteredCapability> GetByCategory(CapabilityCategory category);

    /// <summary>
    /// Get all capabilities provided by a plugin.
    /// </summary>
    /// <param name="pluginId">Plugin ID.</param>
    /// <returns>Plugin's capabilities.</returns>
    IReadOnlyList<RegisteredCapability> GetByPlugin(string pluginId);

    /// <summary>
    /// Get capabilities matching any of the specified tags.
    /// </summary>
    /// <param name="tags">Tags to match.</param>
    /// <returns>Matching capabilities.</returns>
    IReadOnlyList<RegisteredCapability> GetByTags(params string[] tags);

    /// <summary>
    /// Find the best capability for a requirement.
    /// Returns the highest priority available capability matching criteria.
    /// </summary>
    /// <param name="category">Required category.</param>
    /// <param name="requiredTags">Required tags (all must match).</param>
    /// <returns>Best matching capability or null.</returns>
    RegisteredCapability? FindBest(CapabilityCategory category, params string[] requiredTags);

    /// <summary>
    /// Get all registered capabilities.
    /// </summary>
    /// <returns>All capabilities.</returns>
    IReadOnlyList<RegisteredCapability> GetAll();

    /// <summary>
    /// Get registry statistics.
    /// </summary>
    /// <returns>Statistics about registered capabilities.</returns>
    CapabilityRegistryStatistics GetStatistics();

    #endregion

    #region Kernel Routing

    /// <summary>
    /// Get the plugin ID that provides a specific capability.
    /// Used by kernel for message routing.
    /// </summary>
    /// <param name="capabilityId">Capability ID.</param>
    /// <returns>Plugin ID or null if not found.</returns>
    string? GetPluginIdForCapability(string capabilityId);

    /// <summary>
    /// Get all plugin IDs that provide capabilities in a category.
    /// Used by kernel for broadcast routing.
    /// </summary>
    /// <param name="category">Capability category.</param>
    /// <returns>List of plugin IDs.</returns>
    IReadOnlyList<string> GetPluginIdsForCategory(CapabilityCategory category);

    #endregion

    #region Events

    /// <summary>
    /// Subscribe to capability registration events.
    /// </summary>
    /// <param name="handler">Event handler.</param>
    /// <returns>Disposable to unsubscribe.</returns>
    IDisposable OnCapabilityRegistered(Action<RegisteredCapability> handler);

    /// <summary>
    /// Subscribe to capability unregistration events.
    /// </summary>
    /// <param name="handler">Event handler.</param>
    /// <returns>Disposable to unsubscribe.</returns>
    IDisposable OnCapabilityUnregistered(Action<string> handler);

    /// <summary>
    /// Subscribe to availability change events.
    /// </summary>
    /// <param name="handler">Event handler (capabilityId, isAvailable).</param>
    /// <returns>Disposable to unsubscribe.</returns>
    IDisposable OnAvailabilityChanged(Action<string, bool> handler);

    #endregion
}

/// <summary>
/// Statistics about the capability registry.
/// </summary>
public record CapabilityRegistryStatistics
{
    /// <summary>
    /// Total registered capabilities.
    /// </summary>
    public int TotalCapabilities { get; init; }

    /// <summary>
    /// Currently available capabilities.
    /// </summary>
    public int AvailableCapabilities { get; init; }

    /// <summary>
    /// Number of registered plugins.
    /// </summary>
    public int RegisteredPlugins { get; init; }

    /// <summary>
    /// Capabilities per category.
    /// </summary>
    public IReadOnlyDictionary<CapabilityCategory, int> ByCategory { get; init; } =
        new Dictionary<CapabilityCategory, int>();

    /// <summary>
    /// Capabilities per plugin.
    /// </summary>
    public IReadOnlyDictionary<string, int> ByPlugin { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// Most common tags.
    /// </summary>
    public IReadOnlyList<(string Tag, int Count)> TopTags { get; init; } =
        Array.Empty<(string, int)>();

    /// <summary>
    /// When statistics were generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Extension methods for capability registry.
/// </summary>
public static class CapabilityRegistryExtensions
{
    /// <summary>
    /// Register a simple capability with minimal metadata.
    /// </summary>
    public static Task<bool> RegisterSimpleAsync(
        this IPluginCapabilityRegistry registry,
        string capabilityId,
        string displayName,
        CapabilityCategory category,
        string pluginId,
        string pluginName,
        string pluginVersion,
        string[]? tags = null,
        CancellationToken ct = default)
    {
        return registry.RegisterAsync(new RegisteredCapability
        {
            CapabilityId = capabilityId,
            DisplayName = displayName,
            Category = category,
            PluginId = pluginId,
            PluginName = pluginName,
            PluginVersion = pluginVersion,
            Tags = tags ?? Array.Empty<string>()
        }, ct);
    }

    /// <summary>
    /// Check if any capability with the required tags exists.
    /// </summary>
    public static bool HasCapabilityWithTags(
        this IPluginCapabilityRegistry registry,
        CapabilityCategory category,
        params string[] tags)
    {
        return registry.FindBest(category, tags) != null;
    }

    /// <summary>
    /// Get the plugin ID that provides a capability.
    /// </summary>
    public static string? GetProviderPlugin(
        this IPluginCapabilityRegistry registry,
        string capabilityId)
    {
        return registry.GetCapability(capabilityId)?.PluginId;
    }
}
