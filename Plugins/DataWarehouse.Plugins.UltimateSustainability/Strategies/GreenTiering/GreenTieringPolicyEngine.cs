using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenTiering;

/// <summary>
/// Defines the migration schedule for green tiering operations.
/// Controls when cold data is migrated to lower-carbon backends.
/// </summary>
public enum GreenMigrationSchedule
{
    /// <summary>Migrate immediately when cold data is detected on high-carbon backend.</summary>
    Immediate,

    /// <summary>Only migrate during low-carbon intensity windows (below 24h average).</summary>
    LowCarbonWindowOnly,

    /// <summary>Batch migrations daily at a configured hour (default 02:00 UTC).</summary>
    DailyBatch,

    /// <summary>Batch migrations weekly on a configured day (default Sunday) at configured hour.</summary>
    WeeklyBatch
}

/// <summary>
/// Per-tenant green tiering policy configuration.
/// Controls cold data detection thresholds, migration scheduling, and budget constraints.
/// </summary>
public sealed record GreenTieringPolicy
{
    /// <summary>
    /// Tenant identifier that owns this policy.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Duration since last access after which data is considered cold.
    /// Default: 30 days.
    /// </summary>
    public TimeSpan ColdThreshold { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Minimum green score (0-100) required for a target backend.
    /// Only backends scoring at or above this value are eligible migration targets.
    /// Default: 80.0.
    /// </summary>
    public double TargetGreenScore { get; init; } = 80.0;

    /// <summary>
    /// Schedule controlling when migrations are executed.
    /// </summary>
    public GreenMigrationSchedule MigrationSchedule { get; init; } = GreenMigrationSchedule.LowCarbonWindowOnly;

    /// <summary>
    /// Maximum total size of objects in a single migration batch (bytes).
    /// Default: 10 GB.
    /// </summary>
    public long MaxMigrationBatchSizeBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent migration operations per batch.
    /// Default: 5.
    /// </summary>
    public int MaxConcurrentMigrations { get; init; } = 5;

    /// <summary>
    /// Whether migrations must respect the tenant's carbon budget.
    /// When true, migrations that would exhaust the budget are skipped.
    /// Default: true.
    /// </summary>
    public bool RespectCarbonBudget { get; init; } = true;

    /// <summary>
    /// Whether green tiering is enabled for this tenant.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Hour of day (0-23 UTC) at which daily/weekly batches run.
    /// Default: 2 (02:00 UTC).
    /// </summary>
    public int BatchHourUtc { get; init; } = 2;

    /// <summary>
    /// Day of week on which weekly batches run.
    /// Default: Sunday.
    /// </summary>
    public DayOfWeek BatchDayOfWeek { get; init; } = DayOfWeek.Sunday;
}

/// <summary>
/// Manages per-tenant green tiering policies with persistent JSON storage.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/> and debounced file persistence.
/// </summary>
public sealed class GreenTieringPolicyEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, GreenTieringPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _persistencePath;
    private Timer? _saveTimer;
    private volatile bool _dirty;
    private volatile bool _disposed;
    private readonly object _saveLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new TimeSpanJsonConverter() }
    };

    /// <summary>
    /// Creates a new policy engine with the specified data directory for persistence.
    /// </summary>
    /// <param name="dataDirectory">Directory where policy JSON file is stored.</param>
    public GreenTieringPolicyEngine(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _persistencePath = Path.Combine(dataDirectory, "green-tiering-policies.json");
        LoadFromDisk();

        // Debounced save: check every 5 seconds, write only if dirty
        _saveTimer = new Timer(_ => FlushIfDirty(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Sets or updates the green tiering policy for a tenant.
    /// Validates policy constraints before storing.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="policy">The policy to set.</param>
    /// <exception cref="ArgumentException">Thrown when policy contains invalid values.</exception>
    public void SetPolicy(string tenantId, GreenTieringPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(policy);

        ValidatePolicy(policy);

        // Ensure tenantId matches
        var normalizedPolicy = policy with { TenantId = tenantId };
        _policies[tenantId] = normalizedPolicy;
        _dirty = true;
    }

    /// <summary>
    /// Gets the green tiering policy for a tenant.
    /// Returns a default policy if no policy has been explicitly set.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <returns>The tenant's policy, or a default policy for unknown tenants.</returns>
    public GreenTieringPolicy GetPolicy(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_policies.TryGetValue(tenantId, out var policy))
            return policy;

        // Return default policy for unknown tenants
        return new GreenTieringPolicy { TenantId = tenantId };
    }

    /// <summary>
    /// Gets all tenant identifiers with green tiering enabled.
    /// </summary>
    /// <returns>List of tenant IDs where green tiering is active.</returns>
    public IReadOnlyList<string> GetEnabledTenants()
    {
        return _policies.Values
            .Where(p => p.Enabled)
            .Select(p => p.TenantId)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Removes a tenant's policy, reverting them to defaults.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <returns>True if a policy was removed; false if the tenant had no explicit policy.</returns>
    public bool RemovePolicy(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var removed = _policies.TryRemove(tenantId, out _);
        if (removed) _dirty = true;
        return removed;
    }

    /// <summary>
    /// Gets the total number of registered policies.
    /// </summary>
    public int PolicyCount => _policies.Count;

    /// <summary>
    /// Forces an immediate flush of any pending policy changes to disk.
    /// </summary>
    public void Flush()
    {
        FlushIfDirty();
    }

    private static void ValidatePolicy(GreenTieringPolicy policy)
    {
        if (policy.ColdThreshold <= TimeSpan.Zero)
            throw new ArgumentException("ColdThreshold must be positive.", nameof(policy));

        if (policy.TargetGreenScore is < 0 or > 100)
            throw new ArgumentException("TargetGreenScore must be between 0 and 100.", nameof(policy));

        if (policy.MaxMigrationBatchSizeBytes <= 0)
            throw new ArgumentException("MaxMigrationBatchSizeBytes must be positive.", nameof(policy));

        if (policy.MaxConcurrentMigrations <= 0)
            throw new ArgumentException("MaxConcurrentMigrations must be positive.", nameof(policy));

        if (policy.BatchHourUtc is < 0 or > 23)
            throw new ArgumentException("BatchHourUtc must be between 0 and 23.", nameof(policy));
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_persistencePath)) return;

            var json = File.ReadAllText(_persistencePath);
            var policies = JsonSerializer.Deserialize<List<GreenTieringPolicy>>(json, JsonOptions);
            if (policies == null) return;

            foreach (var policy in policies)
            {
                if (!string.IsNullOrWhiteSpace(policy.TenantId))
                    _policies[policy.TenantId] = policy;
            }
        }
        catch
        {
            // If persistence file is corrupt, start fresh -- policies can be re-set
        }
    }

    private void FlushIfDirty()
    {
        if (!_dirty || _disposed) return;

        lock (_saveLock)
        {
            if (!_dirty) return;

            try
            {
                var directory = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var policies = _policies.Values.ToList();
                var json = JsonSerializer.Serialize(policies, JsonOptions);

                // Atomic write: write to temp file then rename
                var tempPath = _persistencePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _persistencePath, overwrite: true);

                _dirty = false;
            }
            catch
            {
                // Persistence failure is non-fatal -- policies remain in memory
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _saveTimer?.Dispose();
        _saveTimer = null;

        // Final flush on dispose
        FlushIfDirty();
    }

    /// <summary>
    /// Custom JSON converter for TimeSpan serialization as ISO 8601 duration string.
    /// </summary>
    private sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var str = reader.GetString();
            if (string.IsNullOrEmpty(str)) return TimeSpan.Zero;

            // Try parsing as standard TimeSpan format first, then as total days
            if (TimeSpan.TryParse(str, out var ts))
                return ts;

            if (double.TryParse(str, out var days))
                return TimeSpan.FromDays(days);

            return TimeSpan.Zero;
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
