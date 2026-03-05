using System.Diagnostics;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Dashboards;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies;

/// <summary>
/// Dashboard persistence service providing data persistence across restarts,
/// multi-tenant isolation, widget configuration storage, and sharing/embedding support.
/// </summary>
public sealed class DashboardPersistenceService
{
    private readonly BoundedDictionary<string, PersistedDashboard> _dashboards = new BoundedDictionary<string, PersistedDashboard>(1000);
    private readonly BoundedDictionary<string, List<DashboardVersion>> _versionHistory = new BoundedDictionary<string, List<DashboardVersion>>(1000);
    private readonly BoundedDictionary<string, WidgetConfiguration> _widgetConfigs = new BoundedDictionary<string, WidgetConfiguration>(1000);
    private readonly BoundedDictionary<string, DashboardShareLink> _shareLinks = new BoundedDictionary<string, DashboardShareLink>(1000);
    private readonly BoundedDictionary<string, EmbedConfiguration> _embedConfigs = new BoundedDictionary<string, EmbedConfiguration>(1000);
    private readonly string _storagePath;

    public DashboardPersistenceService(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "dashboards");
    }

    /// <summary>
    /// Saves a dashboard with tenant isolation.
    /// </summary>
    public PersistedDashboard Save(string tenantId, Dashboard dashboard, string userId)
    {
        // P2-3263: guard null/empty to prevent key corruption across tenant boundaries
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var id = dashboard.Id ?? Guid.NewGuid().ToString("N");
        var persisted = new PersistedDashboard
        {
            Id = id,
            TenantId = tenantId,
            Dashboard = dashboard with { Id = id },
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };

        var key = BuildKey(tenantId, id);
        _dashboards[key] = persisted;

        // Track version history
        RecordVersion(key, persisted);

        return persisted;
    }

    /// <summary>
    /// Updates an existing dashboard.
    /// </summary>
    public PersistedDashboard? Update(string tenantId, string dashboardId, Dashboard dashboard, string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(dashboardId);
        var key = BuildKey(tenantId, dashboardId);
        if (!_dashboards.TryGetValue(key, out var existing)) return null;

        var updated = existing with
        {
            Dashboard = dashboard with { Id = dashboardId, Version = existing.Version + 1 },
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = userId,
            Version = existing.Version + 1
        };
        _dashboards[key] = updated;

        RecordVersion(key, updated);
        return updated;
    }

    /// <summary>
    /// Gets a dashboard by ID with tenant isolation.
    /// </summary>
    public PersistedDashboard? Get(string tenantId, string dashboardId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(dashboardId);
        var key = BuildKey(tenantId, dashboardId);
        return _dashboards.TryGetValue(key, out var dashboard) ? dashboard : null;
    }

    /// <summary>
    /// Lists all dashboards for a tenant.
    /// </summary>
    public IReadOnlyList<PersistedDashboard> List(string tenantId, DashboardListOptions? options = null)
    {
        var query = _dashboards.Values.Where(d => d.TenantId == tenantId);

        if (options?.CreatedBy != null)
            query = query.Where(d => d.CreatedBy == options.CreatedBy);

        if (options?.Tag != null)
            query = query.Where(d => d.Tags.Contains(options.Tag));

        return query
            .OrderByDescending(d => d.UpdatedAt)
            .Skip(options?.Offset ?? 0)
            .Take(options?.Limit ?? 100)
            .ToList().AsReadOnly();
    }

    /// <summary>
    /// Deletes a dashboard.
    /// </summary>
    public bool Delete(string tenantId, string dashboardId)
    {
        var key = BuildKey(tenantId, dashboardId);
        // Also clean up related data
        _shareLinks.TryRemove(key, out _);
        _embedConfigs.TryRemove(key, out _);
        return _dashboards.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets version history for a dashboard.
    /// </summary>
    public IReadOnlyList<DashboardVersion> GetVersionHistory(string tenantId, string dashboardId)
    {
        var key = BuildKey(tenantId, dashboardId);
        return _versionHistory.TryGetValue(key, out var history)
            ? history.AsReadOnly()
            : Array.Empty<DashboardVersion>();
    }

    /// <summary>
    /// Restores a dashboard to a previous version.
    /// </summary>
    public PersistedDashboard? RestoreVersion(string tenantId, string dashboardId, int targetVersion, string userId)
    {
        var key = BuildKey(tenantId, dashboardId);
        if (!_versionHistory.TryGetValue(key, out var history)) return null;

        var targetVersionEntry = history.FirstOrDefault(v => v.Version == targetVersion);
        if (targetVersionEntry == null) return null;

        return Update(tenantId, dashboardId, targetVersionEntry.Snapshot, userId);
    }

    #region Widget Configuration

    /// <summary>
    /// Saves widget configuration for a dashboard.
    /// </summary>
    public void SaveWidgetConfig(string tenantId, string dashboardId, string widgetId, WidgetConfiguration config)
    {
        var key = $"{tenantId}:{dashboardId}:{widgetId}";
        _widgetConfigs[key] = config;
    }

    /// <summary>
    /// Gets widget configuration.
    /// </summary>
    public WidgetConfiguration? GetWidgetConfig(string tenantId, string dashboardId, string widgetId)
    {
        var key = $"{tenantId}:{dashboardId}:{widgetId}";
        return _widgetConfigs.TryGetValue(key, out var config) ? config : null;
    }

    /// <summary>
    /// Lists all widget configurations for a dashboard.
    /// </summary>
    public IReadOnlyList<WidgetConfiguration> ListWidgetConfigs(string tenantId, string dashboardId)
    {
        var prefix = $"{tenantId}:{dashboardId}:";
        return _widgetConfigs
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .ToList().AsReadOnly();
    }

    #endregion

    #region Sharing & Embedding

    /// <summary>
    /// Creates a share link for a dashboard.
    /// </summary>
    public DashboardShareLink CreateShareLink(string tenantId, string dashboardId, SharePermission permission, TimeSpan? expiresIn = null)
    {
        var key = BuildKey(tenantId, dashboardId);
        var link = new DashboardShareLink
        {
            LinkId = Guid.NewGuid().ToString("N")[..12],
            DashboardId = dashboardId,
            TenantId = tenantId,
            Permission = permission,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresIn.HasValue ? DateTimeOffset.UtcNow.Add(expiresIn.Value) : null,
            IsActive = true
        };
        _shareLinks[link.LinkId] = link;
        return link;
    }

    /// <summary>
    /// Validates and retrieves a share link.
    /// </summary>
    public DashboardShareLink? ValidateShareLink(string linkId)
    {
        if (!_shareLinks.TryGetValue(linkId, out var link)) return null;
        if (!link.IsActive) return null;
        if (link.ExpiresAt.HasValue && DateTimeOffset.UtcNow > link.ExpiresAt.Value) return null;
        return link;
    }

    /// <summary>
    /// Creates an embed configuration for a dashboard.
    /// </summary>
    public EmbedConfiguration CreateEmbedConfig(string tenantId, string dashboardId, EmbedOptions options)
    {
        var key = BuildKey(tenantId, dashboardId);
        var config = new EmbedConfiguration
        {
            EmbedId = Guid.NewGuid().ToString("N")[..12],
            DashboardId = dashboardId,
            TenantId = tenantId,
            Options = options,
            CreatedAt = DateTimeOffset.UtcNow,
            IframeUrl = $"/embed/dashboard/{dashboardId}?embed={Guid.NewGuid():N}"
        };
        _embedConfigs[config.EmbedId] = config;
        return config;
    }

    /// <summary>
    /// Gets embed configuration.
    /// </summary>
    public EmbedConfiguration? GetEmbedConfig(string embedId) =>
        _embedConfigs.TryGetValue(embedId, out var config) ? config : null;

    #endregion

    /// <summary>
    /// Persists all dashboards to storage (for restart survival).
    /// </summary>
    public async Task FlushToStorageAsync(CancellationToken ct = default)
    {
        var dir = _storagePath;
        Directory.CreateDirectory(dir);

        foreach (var (key, dashboard) in _dashboards)
        {
            var filePath = Path.Combine(dir, $"{key.Replace(':', '_')}.json");
            var json = JsonSerializer.Serialize(dashboard, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, ct);
        }
    }

    /// <summary>
    /// Loads dashboards from storage (after restart).
    /// </summary>
    public async Task LoadFromStorageAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_storagePath)) return;

        foreach (var file in Directory.GetFiles(_storagePath, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var dashboard = JsonSerializer.Deserialize<PersistedDashboard>(json);
                if (dashboard != null)
                {
                    var key = BuildKey(dashboard.TenantId, dashboard.Id);
                    _dashboards[key] = dashboard;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // P2-3259: Catch IOException and UnauthorizedAccessException in addition to
                // JsonException so a single bad file doesn't abort the entire load.
                // Trace is visible in Release builds; Debug.WriteLine is stripped.
                Trace.TraceWarning($"[DashboardPersistenceService] Skipping file '{file}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets dashboard count by tenant.
    /// </summary>
    public Dictionary<string, int> GetDashboardCountsByTenant() =>
        _dashboards.Values
            .GroupBy(d => d.TenantId)
            .ToDictionary(g => g.Key, g => g.Count());

    // P2-3262: Use U+001F (unit separator) as key separator to prevent collisions when
    // tenantId or dashboardId contains a colon character.
    private static string BuildKey(string tenantId, string dashboardId) => $"{tenantId}\x1F{dashboardId}";

    private void RecordVersion(string key, PersistedDashboard dashboard)
    {
        var version = new DashboardVersion
        {
            Version = dashboard.Version,
            Snapshot = dashboard.Dashboard,
            SavedAt = DateTimeOffset.UtcNow,
            SavedBy = dashboard.UpdatedBy ?? dashboard.CreatedBy
        };

        _versionHistory.AddOrUpdate(
            key,
            _ => new List<DashboardVersion> { version },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(version);
                    while (list.Count > 50) list.RemoveAt(0); // Keep last 50 versions
                }
                return list;
            });
    }
}

#region Models

public sealed record PersistedDashboard
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required Dashboard Dashboard { get; init; }
    public required string CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int Version { get; init; }
    public List<string> Tags { get; init; } = new();
}

public sealed record DashboardVersion
{
    public int Version { get; init; }
    public required Dashboard Snapshot { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public required string SavedBy { get; init; }
}

public sealed record DashboardListOptions
{
    public string? CreatedBy { get; init; }
    public string? Tag { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record WidgetConfiguration
{
    public required string WidgetId { get; init; }
    public required string WidgetType { get; init; }
    public required string Title { get; init; }
    public Dictionary<string, object> DataSource { get; init; } = new();
    public Dictionary<string, object> Visualization { get; init; } = new();
    public Dictionary<string, object> Filters { get; init; } = new();
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public int Width { get; init; } = 4;
    public int Height { get; init; } = 3;
    public int RefreshIntervalSeconds { get; init; } = 30;
}

public sealed record DashboardShareLink
{
    public required string LinkId { get; init; }
    public required string DashboardId { get; init; }
    public required string TenantId { get; init; }
    public SharePermission Permission { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; }
}

public enum SharePermission { ViewOnly, Edit, Admin }

public sealed record EmbedConfiguration
{
    public required string EmbedId { get; init; }
    public required string DashboardId { get; init; }
    public required string TenantId { get; init; }
    public required EmbedOptions Options { get; init; }
    public required string IframeUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record EmbedOptions
{
    public bool ShowHeader { get; init; } = true;
    public bool ShowFilters { get; init; } = true;
    public bool AllowInteraction { get; init; } = true;
    public string? Theme { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string[] AllowedDomains { get; init; } = Array.Empty<string>();
}

#endregion
