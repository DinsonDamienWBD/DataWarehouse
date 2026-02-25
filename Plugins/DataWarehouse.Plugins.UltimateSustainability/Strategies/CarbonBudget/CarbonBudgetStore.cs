using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonBudgetEnforcement;

/// <summary>
/// Persistent per-tenant carbon budget storage with atomic updates.
/// Stores budgets in a ConcurrentDictionary for fast in-memory access
/// and persists to a JSON file for durability across restarts.
/// Thread-safe via SemaphoreSlim for file I/O and ConcurrentDictionary for memory access.
/// </summary>
public sealed class CarbonBudgetStore : IDisposable, IAsyncDisposable
{
    private readonly BoundedDictionary<string, MutableBudgetEntry> _budgets = new BoundedDictionary<string, MutableBudgetEntry>(1000);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _filePath;
    private Timer? _debounceSaveTimer;
    private volatile bool _dirty;
    private volatile bool _disposed;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Initializes a new CarbonBudgetStore with the specified file path for persistence.
    /// </summary>
    /// <param name="dataDirectory">Directory to store the budget JSON file.</param>
    public CarbonBudgetStore(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "sustainability");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "carbon-budgets.json");
    }

    /// <summary>
    /// Gets the number of stored budgets.
    /// </summary>
    public int Count => _budgets.Count;

    /// <summary>
    /// Retrieves the carbon budget for a tenant, or null if not found.
    /// </summary>
    public Task<SDK.Contracts.Carbon.CarbonBudget?> GetAsync(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (_budgets.TryGetValue(tenantId, out var entry))
        {
            return Task.FromResult<SDK.Contracts.Carbon.CarbonBudget?>(entry.ToImmutable());
        }
        return Task.FromResult<SDK.Contracts.Carbon.CarbonBudget?>(null);
    }

    /// <summary>
    /// Creates or updates the carbon budget for a tenant.
    /// </summary>
    public Task SetAsync(string tenantId, SDK.Contracts.Carbon.CarbonBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(budget);

        var entry = MutableBudgetEntry.FromImmutable(budget);
        _budgets[tenantId] = entry;
        MarkDirty();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Atomically increments the carbon usage for a tenant.
    /// Returns false if the operation would exceed the hard limit.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="carbonGramsCO2e">Carbon grams to record.</param>
    /// <returns>True if recorded successfully; false if hard limit would be exceeded.</returns>
    public Task<bool> RecordUsageAsync(string tenantId, double carbonGramsCO2e)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!_budgets.TryGetValue(tenantId, out var entry))
        {
            return Task.FromResult(false);
        }

        // Atomic check-and-increment under lock
        lock (entry.Lock)
        {
            var usagePercent = entry.BudgetGramsCO2e > 0
                ? ((entry.UsedGramsCO2e + carbonGramsCO2e) / entry.BudgetGramsCO2e) * 100.0
                : 0;

            if (usagePercent > entry.HardLimitPercent)
            {
                return Task.FromResult(false);
            }

            entry.UsedGramsCO2e += carbonGramsCO2e;
        }

        MarkDirty();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Checks all budgets and resets usage to zero for any that have expired.
    /// Advances the period window forward based on the BudgetPeriod.
    /// </summary>
    public Task ResetExpiredBudgetsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        bool anyReset = false;

        foreach (var kvp in _budgets)
        {
            var entry = kvp.Value;
            lock (entry.Lock)
            {
                if (entry.PeriodEnd < now)
                {
                    // Advance period: new start = old end, compute new end
                    var newStart = entry.PeriodEnd;
                    var newEnd = ComputePeriodEnd(newStart, entry.BudgetPeriod);

                    // Handle multiple expired periods (catch up to current time)
                    while (newEnd < now)
                    {
                        newStart = newEnd;
                        newEnd = ComputePeriodEnd(newStart, entry.BudgetPeriod);
                    }

                    entry.PeriodStart = newStart;
                    entry.PeriodEnd = newEnd;
                    entry.UsedGramsCO2e = 0;
                    anyReset = true;
                }
            }
        }

        if (anyReset)
        {
            MarkDirty();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads budgets from disk. Should be called during initialization.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return;

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<Dictionary<string, StoredBudgetEntry>>(json, s_jsonOptions);

            if (entries != null)
            {
                _budgets.Clear();
                foreach (var kvp in entries)
                {
                    _budgets[kvp.Key] = MutableBudgetEntry.FromStored(kvp.Value);
                }
            }
        }
        catch (JsonException ex)
        {

            // Corrupted file -- start fresh. The file will be overwritten on next save.
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Persists all budgets to disk immediately.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var storedEntries = new Dictionary<string, StoredBudgetEntry>();
            foreach (var kvp in _budgets)
            {
                lock (kvp.Value.Lock)
                {
                    storedEntries[kvp.Key] = kvp.Value.ToStored();
                }
            }

            var json = JsonSerializer.Serialize(storedEntries, s_jsonOptions);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, _filePath, overwrite: true);
            _dirty = false;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Gets all tenant IDs with budgets.
    /// </summary>
    public IReadOnlyList<string> GetAllTenantIds()
    {
        return _budgets.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets the current usage percentage for a tenant.
    /// Returns 0 if the tenant has no budget.
    /// </summary>
    public double GetUsagePercent(string tenantId)
    {
        if (!_budgets.TryGetValue(tenantId, out var entry))
            return 0;

        lock (entry.Lock)
        {
            return entry.BudgetGramsCO2e > 0
                ? (entry.UsedGramsCO2e / entry.BudgetGramsCO2e) * 100.0
                : 0;
        }
    }

    /// <summary>
    /// Computes the period end date based on the budget period granularity.
    /// </summary>
    public static DateTimeOffset ComputePeriodEnd(DateTimeOffset periodStart, CarbonBudgetPeriod period)
    {
        return period switch
        {
            CarbonBudgetPeriod.Hourly => periodStart.AddHours(1),
            CarbonBudgetPeriod.Daily => periodStart.AddDays(1),
            CarbonBudgetPeriod.Weekly => periodStart.AddDays(7),
            CarbonBudgetPeriod.Monthly => periodStart.AddMonths(1),
            CarbonBudgetPeriod.Quarterly => periodStart.AddMonths(3),
            CarbonBudgetPeriod.Annual => periodStart.AddYears(1),
            _ => periodStart.AddDays(1)
        };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = null;

        // Final save if dirty
        if (_dirty)
        {
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _fileLock.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = null;

        // Final save if dirty — Dispose() is synchronous; use Task.Run to avoid
        // deadlocks on synchronization-context-bound threads. Prefer DisposeAsync()
        // for callers that can await.
        if (_dirty)
        {
            Task.Run(() => SaveAsync(CancellationToken.None)).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        _fileLock.Dispose();
    }

    private void MarkDirty()
    {
        _dirty = true;
        // Debounce save: schedule save in 5 seconds, reset timer if already pending
        _debounceSaveTimer?.Dispose();
        _debounceSaveTimer = new Timer(
            _ => _ = SaveAsync(CancellationToken.None),
            null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan);
    }

    #region Internal Mutable Entry

    /// <summary>
    /// Mutable in-memory representation of a budget entry.
    /// Lock on <see cref="Lock"/> for thread-safe access to mutable fields.
    /// </summary>
    private sealed class MutableBudgetEntry
    {
        public readonly object Lock = new();
        public string TenantId { get; set; } = string.Empty;
        public CarbonBudgetPeriod BudgetPeriod { get; set; }
        public double BudgetGramsCO2e { get; set; }
        public double UsedGramsCO2e { get; set; }
        public double ThrottleThresholdPercent { get; set; } = 80.0;
        public double HardLimitPercent { get; set; } = 100.0;
        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }

        public SDK.Contracts.Carbon.CarbonBudget ToImmutable()
        {
            lock (Lock)
            {
                return new SDK.Contracts.Carbon.CarbonBudget
                {
                    TenantId = TenantId,
                    BudgetPeriod = BudgetPeriod,
                    BudgetGramsCO2e = BudgetGramsCO2e,
                    UsedGramsCO2e = UsedGramsCO2e,
                    ThrottleThresholdPercent = ThrottleThresholdPercent,
                    HardLimitPercent = HardLimitPercent,
                    PeriodStart = PeriodStart,
                    PeriodEnd = PeriodEnd
                };
            }
        }

        public StoredBudgetEntry ToStored()
        {
            return new StoredBudgetEntry
            {
                TenantId = TenantId,
                BudgetPeriod = BudgetPeriod,
                BudgetGramsCO2e = BudgetGramsCO2e,
                UsedGramsCO2e = UsedGramsCO2e,
                ThrottleThresholdPercent = ThrottleThresholdPercent,
                HardLimitPercent = HardLimitPercent,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd
            };
        }

        public static MutableBudgetEntry FromImmutable(SDK.Contracts.Carbon.CarbonBudget budget)
        {
            return new MutableBudgetEntry
            {
                TenantId = budget.TenantId,
                BudgetPeriod = budget.BudgetPeriod,
                BudgetGramsCO2e = budget.BudgetGramsCO2e,
                UsedGramsCO2e = budget.UsedGramsCO2e,
                ThrottleThresholdPercent = budget.ThrottleThresholdPercent,
                HardLimitPercent = budget.HardLimitPercent,
                PeriodStart = budget.PeriodStart,
                PeriodEnd = budget.PeriodEnd
            };
        }

        public static MutableBudgetEntry FromStored(StoredBudgetEntry stored)
        {
            return new MutableBudgetEntry
            {
                TenantId = stored.TenantId,
                BudgetPeriod = stored.BudgetPeriod,
                BudgetGramsCO2e = stored.BudgetGramsCO2e,
                UsedGramsCO2e = stored.UsedGramsCO2e,
                ThrottleThresholdPercent = stored.ThrottleThresholdPercent,
                HardLimitPercent = stored.HardLimitPercent,
                PeriodStart = stored.PeriodStart,
                PeriodEnd = stored.PeriodEnd
            };
        }
    }

    #endregion

    #region Serialization DTO

    /// <summary>
    /// JSON-serializable representation of a budget entry for disk persistence.
    /// </summary>
    private sealed class StoredBudgetEntry
    {
        public string TenantId { get; set; } = string.Empty;
        public CarbonBudgetPeriod BudgetPeriod { get; set; }
        public double BudgetGramsCO2e { get; set; }
        public double UsedGramsCO2e { get; set; }
        public double ThrottleThresholdPercent { get; set; } = 80.0;
        public double HardLimitPercent { get; set; } = 100.0;
        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }
    }

    #endregion
}
