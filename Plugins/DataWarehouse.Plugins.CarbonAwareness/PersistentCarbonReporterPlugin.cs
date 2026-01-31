using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Production-ready persistent carbon usage reporter with dual database support.
/// Supports SQLite (embedded, zero-config) and PostgreSQL (enterprise-grade).
/// Features connection pooling, data retention policies, batch operations, and compliance exports.
/// </summary>
public class PersistentCarbonReporterPlugin : CarbonReporterPluginBase
{
    private DbConnection? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly List<CarbonUsageRecord> _batchQueue = new();
    private PersistentCarbonReporterConfig _config = new();
    private Timer? _cleanupTimer;
    private Timer? _batchFlushTimer;
    private bool _isInitialized;

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.reporter.persistent";

    /// <inheritdoc />
    public override string Name => "Persistent Carbon Reporter";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Configures the reporter with database and retention settings.
    /// </summary>
    /// <param name="config">Configuration options.</param>
    public async Task ConfigureAsync(PersistentCarbonReporterConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        await _connectionLock.WaitAsync();
        try
        {
            // Close existing connection if any
            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            // Create new connection based on database type
            _connection = _config.DatabaseType.ToLowerInvariant() switch
            {
                "postgresql" => await CreatePostgreSqlConnectionAsync(),
                "sqlite" or _ => await CreateSqliteConnectionAsync()
            };

            // Initialize database schema
            await InitializeDatabaseSchemaAsync();

            _isInitialized = true;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        // Auto-configure with defaults if not explicitly configured
        if (!_isInitialized)
        {
            await ConfigureAsync(_config);
        }

        // Start background cleanup timer
        if (_config.CleanupIntervalHours > 0)
        {
            _cleanupTimer = new Timer(
                async _ => await RunRetentionCleanupAsync(),
                null,
                TimeSpan.FromHours(_config.CleanupIntervalHours),
                TimeSpan.FromHours(_config.CleanupIntervalHours)
            );
        }

        // Start batch flush timer (flush every 30 seconds)
        _batchFlushTimer = new Timer(
            async _ => await FlushBatchAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        // Stop timers
        if (_cleanupTimer != null)
        {
            await _cleanupTimer.DisposeAsync();
            _cleanupTimer = null;
        }

        if (_batchFlushTimer != null)
        {
            await _batchFlushTimer.DisposeAsync();
            _batchFlushTimer = null;
        }

        // Flush any remaining batched records
        await FlushBatchAsync();

        // Close connection
        await _connectionLock.WaitAsync();
        try
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task StoreUsageAsync(CarbonUsageRecord usage)
    {
        await _batchLock.WaitAsync();
        try
        {
            _batchQueue.Add(usage);

            // Auto-flush when batch size is reached
            if (_batchQueue.Count >= _config.BatchSize)
            {
                await FlushBatchAsync();
            }
        }
        finally
        {
            _batchLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CarbonUsageRecord>> GetUsageRecordsAsync(
        DateTimeOffset start,
        DateTimeOffset end)
    {
        EnsureInitialized();

        await _connectionLock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT operation_id, region_id, energy_kwh, carbon_grams, timestamp, operation_type
                FROM carbon_usage_records
                WHERE timestamp >= @start AND timestamp <= @end
                ORDER BY timestamp";

            AddParameter(cmd, "@start", start.UtcDateTime.ToString("O"));
            AddParameter(cmd, "@end", end.UtcDateTime.ToString("O"));

            var records = new List<CarbonUsageRecord>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                records.Add(new CarbonUsageRecord(
                    OperationId: reader.GetString(0),
                    RegionId: reader.GetString(1),
                    EnergyKwh: reader.GetDouble(2),
                    CarbonGrams: reader.GetDouble(3),
                    Timestamp: DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    OperationType: reader.GetString(5)
                ));
            }

            return records;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<CarbonReportEntry> GroupByGranularity(
        IReadOnlyList<CarbonUsageRecord> records,
        ReportGranularity granularity)
    {
        if (records.Count == 0)
            return Array.Empty<CarbonReportEntry>();

        return granularity switch
        {
            ReportGranularity.Hourly => GroupByHour(records),
            ReportGranularity.Daily => GroupByDay(records),
            ReportGranularity.Weekly => GroupByWeek(records),
            ReportGranularity.Monthly => GroupByMonth(records),
            _ => GroupByDay(records)
        };
    }

    /// <summary>
    /// Gets carbon usage summary by region.
    /// </summary>
    /// <param name="since">Optional start date (null = all time).</param>
    /// <returns>Dictionary mapping region ID to total carbon grams.</returns>
    public async Task<Dictionary<string, double>> GetUsageByRegionAsync(DateTimeOffset? since = null)
    {
        EnsureInitialized();

        await _connectionLock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();

            if (since.HasValue)
            {
                cmd.CommandText = @"
                    SELECT region_id, SUM(carbon_grams) as total
                    FROM carbon_usage_records
                    WHERE timestamp >= @since
                    GROUP BY region_id";
                AddParameter(cmd, "@since", since.Value.UtcDateTime.ToString("O"));
            }
            else
            {
                cmd.CommandText = @"
                    SELECT region_id, SUM(carbon_grams) as total
                    FROM carbon_usage_records
                    GROUP BY region_id";
            }

            var results = new Dictionary<string, double>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results[reader.GetString(0)] = reader.GetDouble(1);
            }

            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Gets carbon usage summary by operation type.
    /// </summary>
    /// <param name="since">Optional start date (null = all time).</param>
    /// <returns>Dictionary mapping operation type to total carbon grams.</returns>
    public async Task<Dictionary<string, double>> GetUsageByOperationTypeAsync(DateTimeOffset? since = null)
    {
        EnsureInitialized();

        await _connectionLock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();

            if (since.HasValue)
            {
                cmd.CommandText = @"
                    SELECT operation_type, SUM(carbon_grams) as total
                    FROM carbon_usage_records
                    WHERE timestamp >= @since
                    GROUP BY operation_type";
                AddParameter(cmd, "@since", since.Value.UtcDateTime.ToString("O"));
            }
            else
            {
                cmd.CommandText = @"
                    SELECT operation_type, SUM(carbon_grams) as total
                    FROM carbon_usage_records
                    GROUP BY operation_type";
            }

            var results = new Dictionary<string, double>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results[reader.GetString(0)] = reader.GetDouble(1);
            }

            return results;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Exports carbon usage data to JSON for compliance reporting.
    /// </summary>
    /// <param name="start">Start date.</param>
    /// <param name="end">End date.</param>
    /// <param name="filePath">Output file path.</param>
    public async Task ExportToJsonAsync(DateTimeOffset start, DateTimeOffset end, string filePath)
    {
        var records = await GetUsageRecordsAsync(start, end);

        var export = new
        {
            ExportDate = DateTimeOffset.UtcNow,
            PeriodStart = start,
            PeriodEnd = end,
            TotalRecords = records.Count,
            TotalCarbonGrams = records.Sum(r => r.CarbonGrams),
            TotalEnergyKwh = records.Sum(r => r.EnergyKwh),
            Records = records.Select(r => new
            {
                r.OperationId,
                r.RegionId,
                r.EnergyKwh,
                r.CarbonGrams,
                Timestamp = r.Timestamp.ToString("O"),
                r.OperationType
            })
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// Exports carbon usage data to CSV for compliance reporting.
    /// </summary>
    /// <param name="start">Start date.</param>
    /// <param name="end">End date.</param>
    /// <param name="filePath">Output file path.</param>
    public async Task ExportToCsvAsync(DateTimeOffset start, DateTimeOffset end, string filePath)
    {
        var records = await GetUsageRecordsAsync(start, end);

        var csv = new StringBuilder();
        csv.AppendLine("OperationId,RegionId,EnergyKwh,CarbonGrams,Timestamp,OperationType");

        foreach (var record in records)
        {
            csv.AppendLine($"{EscapeCsv(record.OperationId)},{EscapeCsv(record.RegionId)},{record.EnergyKwh},{record.CarbonGrams},{record.Timestamp:O},{EscapeCsv(record.OperationType)}");
        }

        await File.WriteAllTextAsync(filePath, csv.ToString());
    }

    /// <summary>
    /// Runs retention cleanup to delete old records.
    /// </summary>
    public async Task RunRetentionCleanupAsync()
    {
        if (_config.RetentionDays <= 0)
            return;

        EnsureInitialized();

        await _connectionLock.WaitAsync();
        try
        {
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-_config.RetentionDays);

            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM carbon_usage_records WHERE timestamp < @cutoff";
            AddParameter(cmd, "@cutoff", cutoffDate.UtcDateTime.ToString("O"));

            var deleted = await cmd.ExecuteNonQueryAsync();

            if (deleted > 0)
            {
                // Vacuum SQLite to reclaim space
                if (_config.DatabaseType.ToLowerInvariant() == "sqlite")
                {
                    await using var vacuumCmd = _connection.CreateCommand();
                    vacuumCmd.CommandText = "VACUUM";
                    await vacuumCmd.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Gets the total number of records in the database.
    /// </summary>
    /// <returns>Record count.</returns>
    public async Task<long> GetRecordCountAsync()
    {
        EnsureInitialized();

        await _connectionLock.WaitAsync();
        try
        {
            await using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM carbon_usage_records";

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Gets the database size in bytes.
    /// </summary>
    /// <returns>Database size in bytes.</returns>
    public async Task<long> GetDatabaseSizeAsync()
    {
        EnsureInitialized();

        if (_config.DatabaseType.ToLowerInvariant() == "sqlite")
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, _config.SqliteDbPath);
            if (File.Exists(dbPath))
            {
                return new FileInfo(dbPath).Length;
            }
            return 0;
        }
        else
        {
            // PostgreSQL - get table size
            await _connectionLock.WaitAsync();
            try
            {
                await using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT pg_total_relation_size('carbon_usage_records')";

                var result = await cmd.ExecuteScalarAsync();
                return Convert.ToInt64(result);
            }
            finally
            {
                _connectionLock.Release();
            }
        }
    }

    private async Task<DbConnection> CreateSqliteConnectionAsync()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, _config.SqliteDbPath);
        var connectionString = _config.ConnectionString ?? $"Data Source={dbPath}";

        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<DbConnection> CreatePostgreSqlConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.ConnectionString))
        {
            throw new InvalidOperationException("Connection string is required for PostgreSQL");
        }

        var conn = new NpgsqlConnection(_config.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task InitializeDatabaseSchemaAsync()
    {
        if (_connection == null)
            throw new InvalidOperationException("Connection not established");

        await using var cmd = _connection.CreateCommand();

        if (_config.DatabaseType.ToLowerInvariant() == "postgresql")
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS carbon_usage_records (
                    id SERIAL PRIMARY KEY,
                    operation_id TEXT NOT NULL,
                    region_id TEXT NOT NULL,
                    energy_kwh DOUBLE PRECISION NOT NULL,
                    carbon_grams DOUBLE PRECISION NOT NULL,
                    timestamp TIMESTAMP NOT NULL,
                    operation_type TEXT NOT NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_timestamp ON carbon_usage_records(timestamp);
                CREATE INDEX IF NOT EXISTS idx_region ON carbon_usage_records(region_id);
                CREATE INDEX IF NOT EXISTS idx_operation_type ON carbon_usage_records(operation_type);
            ";
        }
        else
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS carbon_usage_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    operation_id TEXT NOT NULL,
                    region_id TEXT NOT NULL,
                    energy_kwh REAL NOT NULL,
                    carbon_grams REAL NOT NULL,
                    timestamp TEXT NOT NULL,
                    operation_type TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_timestamp ON carbon_usage_records(timestamp);
                CREATE INDEX IF NOT EXISTS idx_region ON carbon_usage_records(region_id);
                CREATE INDEX IF NOT EXISTS idx_operation_type ON carbon_usage_records(operation_type);
            ";
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task FlushBatchAsync()
    {
        await _batchLock.WaitAsync();
        try
        {
            if (_batchQueue.Count == 0)
                return;

            var batch = new List<CarbonUsageRecord>(_batchQueue);
            _batchQueue.Clear();

            await _connectionLock.WaitAsync();
            try
            {
                EnsureInitialized();

                foreach (var record in batch)
                {
                    await using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO carbon_usage_records
                            (operation_id, region_id, energy_kwh, carbon_grams, timestamp, operation_type)
                        VALUES (@operation_id, @region_id, @energy_kwh, @carbon_grams, @timestamp, @operation_type)";

                    AddParameter(cmd, "@operation_id", record.OperationId);
                    AddParameter(cmd, "@region_id", record.RegionId);
                    AddParameter(cmd, "@energy_kwh", record.EnergyKwh);
                    AddParameter(cmd, "@carbon_grams", record.CarbonGrams);
                    AddParameter(cmd, "@timestamp", record.Timestamp.UtcDateTime.ToString("O"));
                    AddParameter(cmd, "@operation_type", record.OperationType);

                    await cmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        finally
        {
            _batchLock.Release();
        }
    }

    private void AddParameter(DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(param);
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized || _connection == null)
        {
            throw new InvalidOperationException("Reporter not initialized. Call ConfigureAsync or StartAsync first.");
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByHour(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new { Hour = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour, 0, 0, r.Timestamp.Offset), r.RegionId })
            .Select(g => new CarbonReportEntry(
                g.Key.Hour,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0 // Renewable percentage would need intensity data
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByDay(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new { Day = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, 0, 0, 0, r.Timestamp.Offset), r.RegionId })
            .Select(g => new CarbonReportEntry(
                g.Key.Day,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByWeek(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new {
                Week = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, 0, 0, 0, r.Timestamp.Offset)
                    .AddDays(-(int)r.Timestamp.DayOfWeek),
                r.RegionId
            })
            .Select(g => new CarbonReportEntry(
                g.Key.Week,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }

    private static IReadOnlyList<CarbonReportEntry> GroupByMonth(IReadOnlyList<CarbonUsageRecord> records)
    {
        return records
            .GroupBy(r => new { Month = new DateTimeOffset(r.Timestamp.Year, r.Timestamp.Month, 1, 0, 0, 0, r.Timestamp.Offset), r.RegionId })
            .Select(g => new CarbonReportEntry(
                g.Key.Month,
                g.Key.RegionId,
                g.Sum(r => r.CarbonGrams),
                g.Sum(r => r.EnergyKwh),
                0.0
            ))
            .OrderBy(e => e.Period)
            .ThenBy(e => e.RegionId)
            .ToList();
    }
}

/// <summary>
/// Configuration options for the persistent carbon reporter.
/// </summary>
public class PersistentCarbonReporterConfig
{
    /// <summary>
    /// Database connection string. If null, uses default SQLite database.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Database type: "sqlite" (default) or "postgresql".
    /// </summary>
    public string DatabaseType { get; set; } = "sqlite";

    /// <summary>
    /// SQLite database file path (relative to application directory).
    /// </summary>
    public string SqliteDbPath { get; set; } = "carbon_usage.db";

    /// <summary>
    /// Number of days to retain records. Records older than this are auto-deleted.
    /// Set to 0 to disable retention cleanup.
    /// </summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>
    /// Batch size for insert operations.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How often to run retention cleanup (in hours).
    /// Set to 0 to disable automatic cleanup.
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 24;
}
