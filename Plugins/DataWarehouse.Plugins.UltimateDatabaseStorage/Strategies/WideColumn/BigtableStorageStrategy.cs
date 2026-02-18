using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Google.Cloud.Bigtable.V2;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Protobuf;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.WideColumn;

/// <summary>
/// Google Cloud Bigtable wide-column storage strategy with production-ready features:
/// - Petabyte-scale distributed storage
/// - Sub-10ms latency
/// - HBase-compatible API
/// - Automatic scaling
/// - Strong consistency per row
/// - Replication across zones/regions
/// - Integration with Google Cloud ecosystem
/// </summary>
public sealed class BigtableStorageStrategy : DatabaseStorageStrategyBase
{
    private BigtableClient? _client;
    private BigtableTableAdminClient? _adminClient;
    private string _projectId = "";
    private string _instanceId = "";
    private string _tableId = "storage";
    private string _columnFamily = "cf";
    private TableName? _tableName;

    public override string StrategyId => "bigtable";
    public override string Name => "Google Cloud Bigtable Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.NoSQL; // WideColumn not available in SDK
    public override string Engine => "Bigtable";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = true,
        SupportsLocking = false,
        SupportsVersioning = true, // Cell versioning
        SupportsTiering = false,
        SupportsEncryption = true, // Google-managed or CMEK
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024 * 1024, // 10MB cell limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong // Per-row strong consistency
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _projectId = GetConfiguration<string?>("ProjectId", null)
            ?? throw new InvalidOperationException("ProjectId is required");
        _instanceId = GetConfiguration<string?>("InstanceId", null)
            ?? throw new InvalidOperationException("InstanceId is required");
        _tableId = GetConfiguration("TableId", "storage");
        _columnFamily = GetConfiguration("ColumnFamily", "cf");

        _client = await BigtableClient.CreateAsync();
        _adminClient = await BigtableTableAdminClient.CreateAsync();
        _tableName = new TableName(_projectId, _instanceId, _tableId);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Verify table exists
        var table = await _adminClient!.GetTableAsync(_tableName);
        if (table == null)
        {
            throw new InvalidOperationException($"Table not found: {_tableId}");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client = null;
        _adminClient = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        try
        {
            await _adminClient!.GetTableAsync(_tableName);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // Create table with column family
            var createTableRequest = new CreateTableRequest
            {
                ParentAsInstanceName = new InstanceName(_projectId, _instanceId),
                TableId = _tableId,
                Table = new Table
                {
                    Granularity = Table.Types.TimestampGranularity.Millis,
                    ColumnFamilies =
                    {
                        { _columnFamily, new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 1 } } }
                    }
                }
            };

            await _adminClient!.CreateTableAsync(createTableRequest);
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : "";
        var timestamp = new BigtableVersion(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var mutations = new[]
        {
            Mutations.SetCell(_columnFamily, "data", ByteString.CopyFrom(data), timestamp),
            Mutations.SetCell(_columnFamily, "size", data.LongLength.ToString(), timestamp),
            Mutations.SetCell(_columnFamily, "contentType", contentType ?? "", timestamp),
            Mutations.SetCell(_columnFamily, "etag", etag, timestamp),
            Mutations.SetCell(_columnFamily, "metadata", metadataJson, timestamp),
            Mutations.SetCell(_columnFamily, "createdAt", now.ToString("o"), timestamp),
            Mutations.SetCell(_columnFamily, "modifiedAt", now.ToString("o"), timestamp)
        };

        await _client!.MutateRowAsync(_tableName, key, mutations);

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
        var filter = RowFilters.ColumnQualifierExact("data");
        var row = await _client!.ReadRowAsync(_tableName, key, filter);

        if (row == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var cell = row.Families
            .FirstOrDefault(f => f.Name == _columnFamily)?
            .Columns.FirstOrDefault(c => c.Qualifier.ToStringUtf8() == "data")?
            .Cells.FirstOrDefault();

        if (cell == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return cell.Value.ToByteArray();
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var mutation = Mutations.DeleteFromRow();
        await _client!.MutateRowAsync(_tableName, key, mutation);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("etag"),
            RowFilters.CellsPerColumnLimit(1));

        var row = await _client!.ReadRowAsync(_tableName, key, filter);
        return row != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = _tableName,
            Filter = RowFilters.CellsPerColumnLimit(1)
        };

        if (!string.IsNullOrEmpty(prefix))
        {
            request.Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8(prefix),
                        EndKeyOpen = ByteString.CopyFromUtf8(prefix + "\uffff")
                    }
                }
            };
        }

        await foreach (var row in _client!.ReadRows(request))
        {
            ct.ThrowIfCancellationRequested();

            var key = row.Key.ToStringUtf8();
            var cells = new Dictionary<string, string>();

            foreach (var family in row.Families.Where(f => f.Name == _columnFamily))
            {
                foreach (var column in family.Columns)
                {
                    var qualifier = column.Qualifier.ToStringUtf8();
                    var value = column.Cells.FirstOrDefault()?.Value.ToStringUtf8() ?? "";
                    cells[qualifier] = value;
                }
            }

            long.TryParse(cells.GetValueOrDefault("size", "0"), out var size);

            Dictionary<string, string>? customMetadata = null;
            if (cells.TryGetValue("metadata", out var metaJson) && !string.IsNullOrEmpty(metaJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                ContentType = cells.GetValueOrDefault("contentType"),
                ETag = cells.GetValueOrDefault("etag"),
                CustomMetadata = customMetadata,
                Created = DateTime.TryParse(cells.GetValueOrDefault("createdAt"), out var created) ? created : DateTime.UtcNow,
                Modified = DateTime.TryParse(cells.GetValueOrDefault("modifiedAt"), out var modified) ? modified : DateTime.UtcNow,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var filter = RowFilters.CellsPerColumnLimit(1);
        var row = await _client!.ReadRowAsync(_tableName, key, filter);

        if (row == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var cells = new Dictionary<string, string>();
        foreach (var family in row.Families.Where(f => f.Name == _columnFamily))
        {
            foreach (var column in family.Columns)
            {
                var qualifier = column.Qualifier.ToStringUtf8();
                var value = column.Cells.FirstOrDefault()?.Value.ToStringUtf8() ?? "";
                cells[qualifier] = value;
            }
        }

        long.TryParse(cells.GetValueOrDefault("size", "0"), out var size);

        Dictionary<string, string>? customMetadata = null;
        if (cells.TryGetValue("metadata", out var metaJson) && !string.IsNullOrEmpty(metaJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = size,
            ContentType = cells.GetValueOrDefault("contentType"),
            ETag = cells.GetValueOrDefault("etag"),
            CustomMetadata = customMetadata,
            Created = DateTime.TryParse(cells.GetValueOrDefault("createdAt"), out var created) ? created : DateTime.UtcNow,
            Modified = DateTime.TryParse(cells.GetValueOrDefault("modifiedAt"), out var modified) ? modified : DateTime.UtcNow,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var table = await _adminClient!.GetTableAsync(_tableName);
            return table != null;
        }
        catch
        {
            return false;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client = null;
        _adminClient = null;
        await base.DisposeAsyncCore();
    }
}
