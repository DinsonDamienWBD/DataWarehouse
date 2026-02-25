using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.TimeSeries;

/// <summary>
/// InfluxDB time-series database storage strategy with production-ready features:
/// - Purpose-built for time-series data
/// - High write throughput
/// - Flux query language
/// - Data retention policies
/// - Continuous queries
/// - Task automation
/// - Telegraf integration
/// - Compression for efficiency
/// </summary>
public sealed class InfluxDbStorageStrategy : DatabaseStorageStrategyBase
{
    private InfluxDBClient? _client;
    private string _bucket = "datawarehouse";
    private string _organization = "default";
    private string _measurement = "storage";
    private string? _token;

    public override string StrategyId => "influxdb";
    public override string Name => "InfluxDB Time-Series Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.TimeSeries;
    public override string Engine => "InfluxDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true, // Via retention policies
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024 * 1024, // 10MB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _bucket = GetConfiguration("Bucket", "datawarehouse");
        _organization = GetConfiguration("Organization", "default");
        _measurement = GetConfiguration("Measurement", "storage");
        _token = GetConfiguration<string?>("Token", null);

        var connectionString = GetConnectionString();

        if (string.IsNullOrEmpty(_token))
        {
            throw new InvalidOperationException("InfluxDB token is required");
        }

        _client = new InfluxDBClient(connectionString, _token);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var isHealthy = await _client!.PingAsync();
        if (!isHealthy)
        {
            throw new InvalidOperationException("InfluxDB ping check failed: server not reachable");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        // Ensure bucket exists
        var bucketsApi = _client!.GetBucketsApi();
        var bucket = await bucketsApi.FindBucketByNameAsync(_bucket);

        if (bucket == null)
        {
            var orgsApi = _client.GetOrganizationsApi();
            var org = (await orgsApi.FindOrganizationsAsync(org: _organization)).FirstOrDefault();

            if (org == null)
            {
                throw new InvalidOperationException($"Organization not found: {_organization}");
            }

            await bucketsApi.CreateBucketAsync(_bucket, org.Id);
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var dataBase64 = Convert.ToBase64String(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : "";

        var writeApi = _client!.GetWriteApiAsync();

        var point = PointData.Measurement(_measurement)
            .Tag("key", key)
            .Tag("contentType", contentType ?? "application/octet-stream")
            .Field("data", dataBase64)
            .Field("size", data.LongLength)
            .Field("etag", etag)
            .Field("metadata", metadataJson)
            .Timestamp(now, WritePrecision.Ns);

        await writeApi.WritePointAsync(point, _bucket, _organization, ct);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var queryApi = _client!.GetQueryApi();

        var query = $@"
            from(bucket: ""{_bucket}"")
            |> range(start: -100y)
            |> filter(fn: (r) => r._measurement == ""{_measurement}"")
            |> filter(fn: (r) => r.key == ""{EscapeFluxString(key)}"")
            |> filter(fn: (r) => r._field == ""data"")
            |> last()";

        var tables = await queryApi.QueryAsync(query, _organization, ct);
        var record = tables.SelectMany(t => t.Records).FirstOrDefault();

        if (record == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var dataBase64 = record.GetValueByKey("_value")?.ToString();
        if (string.IsNullOrEmpty(dataBase64))
        {
            throw new InvalidOperationException($"Data is empty for: {key}");
        }

        return Convert.FromBase64String(dataBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        // Get size before deletion
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var deleteApi = _client!.GetDeleteApi();

        var start = DateTime.UtcNow.AddYears(-100);
        var stop = DateTime.UtcNow.AddYears(100);
        var predicate = $"key=\"{EscapeFluxString(key)}\"";

        await deleteApi.Delete(start, stop, predicate, _bucket, _organization, ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var queryApi = _client!.GetQueryApi();

        var query = $@"
            from(bucket: ""{_bucket}"")
            |> range(start: -100y)
            |> filter(fn: (r) => r._measurement == ""{_measurement}"")
            |> filter(fn: (r) => r.key == ""{EscapeFluxString(key)}"")
            |> count()
            |> yield(name: ""count"")";

        var tables = await queryApi.QueryAsync(query, _organization, ct);
        var record = tables.SelectMany(t => t.Records).FirstOrDefault();

        return record != null && Convert.ToInt64(record.GetValueByKey("_value")) > 0;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var queryApi = _client!.GetQueryApi();

        var filterClause = string.IsNullOrEmpty(prefix)
            ? ""
            : $@"|> filter(fn: (r) => strings.hasPrefix(v: r.key, prefix: ""{EscapeFluxString(prefix)}""))";

        var query = $@"
            import ""strings""

            from(bucket: ""{_bucket}"")
            |> range(start: -100y)
            |> filter(fn: (r) => r._measurement == ""{_measurement}"")
            {filterClause}
            |> group(columns: [""key""])
            |> last()
            |> pivot(rowKey: [""_time""], columnKey: [""_field""], valueColumn: ""_value"")";

        var tables = await queryApi.QueryAsync(query, _organization, ct);

        foreach (var table in tables)
        {
            foreach (var record in table.Records)
            {
                ct.ThrowIfCancellationRequested();

                var key = record.GetValueByKey("key")?.ToString();
                if (string.IsNullOrEmpty(key)) continue;

                var size = Convert.ToInt64(record.GetValueByKey("size") ?? 0);
                var contentType = record.GetValueByKey("contentType")?.ToString();
                var etag = record.GetValueByKey("etag")?.ToString();
                var time = record.GetTimeInDateTime() ?? DateTime.UtcNow;

                Dictionary<string, string>? customMetadata = null;
                var metadataJson = record.GetValueByKey("metadata")?.ToString();
                if (!string.IsNullOrEmpty(metadataJson))
                {
                    customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
                }

                yield return new StorageObjectMetadata
                {
                    Key = key,
                    Size = size,
                    ContentType = contentType,
                    ETag = etag,
                    CustomMetadata = customMetadata,
                    Created = time,
                    Modified = time,
                    Tier = Tier
                };
            }
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var queryApi = _client!.GetQueryApi();

        var query = $@"
            from(bucket: ""{_bucket}"")
            |> range(start: -100y)
            |> filter(fn: (r) => r._measurement == ""{_measurement}"")
            |> filter(fn: (r) => r.key == ""{EscapeFluxString(key)}"")
            |> last()
            |> pivot(rowKey: [""_time""], columnKey: [""_field""], valueColumn: ""_value"")";

        var tables = await queryApi.QueryAsync(query, _organization, ct);
        var record = tables.SelectMany(t => t.Records).FirstOrDefault();

        if (record == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var size = Convert.ToInt64(record.GetValueByKey("size") ?? 0);
        var contentType = record.GetValueByKey("contentType")?.ToString();
        var etag = record.GetValueByKey("etag")?.ToString();
        var time = record.GetTimeInDateTime() ?? DateTime.UtcNow;

        Dictionary<string, string>? customMetadata = null;
        var metadataJson = record.GetValueByKey("metadata")?.ToString();
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = size,
            ContentType = contentType,
            ETag = etag,
            CustomMetadata = customMetadata,
            Created = time,
            Modified = time,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            return await _client!.PingAsync();
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeFluxString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client?.Dispose();
        await base.DisposeAsyncCore();
    }
}
