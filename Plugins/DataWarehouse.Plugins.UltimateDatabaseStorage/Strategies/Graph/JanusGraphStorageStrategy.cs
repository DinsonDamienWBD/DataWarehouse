using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Remote;
using Gremlin.Net.Process.Traversal;
using Gremlin.Net.Structure.IO.GraphSON;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// JanusGraph storage strategy with production-ready features:
/// - Distributed graph database
/// - Gremlin query language (TinkerPop)
/// - Pluggable storage backends (Cassandra, HBase, etc.)
/// - Elasticsearch integration for search
/// - ACID transactions
/// - Horizontal scaling
/// - Graph analytics
/// </summary>
public sealed class JanusGraphStorageStrategy : DatabaseStorageStrategyBase
{
    private GremlinClient? _client;
    private GraphTraversalSource? _g;
    private string _vertexLabel = "StorageObject";

    public override string StrategyId => "janusgraph";
    public override string Name => "JanusGraph Graph Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Graph;
    public override string Engine => "JanusGraph";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true, // Depends on backend
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024 * 1024, // 10MB property limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _vertexLabel = GetConfiguration("VertexLabel", "StorageObject");

        var connectionString = GetConnectionString();
        var uri = new Uri(connectionString);

        var server = new GremlinServer(
            uri.Host,
            uri.Port,
            enableSsl: uri.Scheme == "wss",
            username: GetConfiguration<string?>("Username", null),
            password: GetConfiguration<string?>("Password", null));

        _client = new GremlinClient(
            server,
            new GraphSON3MessageSerializer(),
            connectionPoolSettings: new ConnectionPoolSettings
            {
                MaxInProcessPerConnection = 32,
                PoolSize = 8,
                ReconnectionAttempts = 3,
                ReconnectionBaseDelay = TimeSpan.FromSeconds(1)
            });

        _g = AnonymousTraversalSource.Traversal()
            .With(new DriverRemoteConnection(_client));

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Test connection with a simple query
        var result = await _client!.SubmitAsync<object>("g.V().limit(1)");
        await Task.CompletedTask;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _g = null;
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        // Create schema using Gremlin
        var schemaQuery = $@"
            mgmt = graph.openManagement()
            if (!mgmt.containsVertexLabel('{_vertexLabel}')) {{
                label = mgmt.makeVertexLabel('{_vertexLabel}').make()
                keyProp = mgmt.makePropertyKey('key').dataType(String.class).make()
                mgmt.buildIndex('byKey', Vertex.class).addKey(keyProp).unique().buildCompositeIndex()
            }}
            mgmt.commit()";

        try
        {
            await _client!.SubmitAsync<object>(schemaQuery);
        }
        catch
        {

            // Schema might already exist
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var dataBase64 = Convert.ToBase64String(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        var query = $@"
            g.V().has('{_vertexLabel}', 'key', key).fold()
            .coalesce(
                unfold().property('data', data).property('size', size)
                        .property('contentType', contentType).property('etag', etag)
                        .property('metadata', metadata).property('modifiedAt', modifiedAt),
                addV('{_vertexLabel}').property('key', key).property('data', data)
                        .property('size', size).property('contentType', contentType)
                        .property('etag', etag).property('metadata', metadata)
                        .property('createdAt', createdAt).property('modifiedAt', modifiedAt)
            )";

        var bindings = new Dictionary<string, object>
        {
            ["key"] = key,
            ["data"] = dataBase64,
            ["size"] = data.LongLength,
            ["contentType"] = contentType ?? "",
            ["etag"] = etag,
            ["metadata"] = metadataJson ?? "",
            ["createdAt"] = now.ToString("o"),
            ["modifiedAt"] = now.ToString("o")
        };

        await _client!.SubmitAsync<object>(query, bindings);

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
        var query = $"g.V().has('{_vertexLabel}', 'key', key).values('data')";
        var bindings = new Dictionary<string, object> { ["key"] = key };

        var result = await _client!.SubmitAsync<string>(query, bindings);
        var dataBase64 = result.FirstOrDefault();

        if (string.IsNullOrEmpty(dataBase64))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return Convert.FromBase64String(dataBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var query = $"g.V().has('{_vertexLabel}', 'key', key).drop()";
        var bindings = new Dictionary<string, object> { ["key"] = key };

        await _client!.SubmitAsync<object>(query, bindings);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var query = $"g.V().has('{_vertexLabel}', 'key', key).count()";
        var bindings = new Dictionary<string, object> { ["key"] = key };

        var result = await _client!.SubmitAsync<long>(query, bindings);
        return result.FirstOrDefault() > 0;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var query = string.IsNullOrEmpty(prefix)
            ? $"g.V().hasLabel('{_vertexLabel}').valueMap(true)"
            : $"g.V().hasLabel('{_vertexLabel}').has('key', TextP.startingWith(prefix)).valueMap(true)";

        var bindings = string.IsNullOrEmpty(prefix)
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["prefix"] = prefix };

        var results = await _client!.SubmitAsync<IDictionary<object, object>>(query, bindings);

        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();

            var key = GetPropertyValue<string>(result, "key");
            if (string.IsNullOrEmpty(key)) continue;

            Dictionary<string, string>? customMetadata = null;
            var metadataJson = GetPropertyValue<string>(result, "metadata");
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = GetPropertyValue<long>(result, "size"),
                ContentType = GetPropertyValue<string>(result, "contentType"),
                ETag = GetPropertyValue<string>(result, "etag"),
                CustomMetadata = customMetadata,
                Created = DateTime.TryParse(GetPropertyValue<string>(result, "createdAt"), out var created) ? created : DateTime.UtcNow,
                Modified = DateTime.TryParse(GetPropertyValue<string>(result, "modifiedAt"), out var modified) ? modified : DateTime.UtcNow,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var query = $"g.V().has('{_vertexLabel}', 'key', key).valueMap(true)";
        var bindings = new Dictionary<string, object> { ["key"] = key };

        var results = await _client!.SubmitAsync<IDictionary<object, object>>(query, bindings);
        var result = results.FirstOrDefault();

        if (result == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        Dictionary<string, string>? customMetadata = null;
        var metadataJson = GetPropertyValue<string>(result, "metadata");
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = GetPropertyValue<long>(result, "size"),
            ContentType = GetPropertyValue<string>(result, "contentType"),
            ETag = GetPropertyValue<string>(result, "etag"),
            CustomMetadata = customMetadata,
            Created = DateTime.TryParse(GetPropertyValue<string>(result, "createdAt"), out var created) ? created : DateTime.UtcNow,
            Modified = DateTime.TryParse(GetPropertyValue<string>(result, "modifiedAt"), out var modified) ? modified : DateTime.UtcNow,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await _client!.SubmitAsync<object>("g.V().limit(1)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static T? GetPropertyValue<T>(IDictionary<object, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return default;

        if (value is IList<object> list && list.Count > 0)
        {
            value = list[0];
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _g = null;
        _client?.Dispose();
        await base.DisposeAsyncCore();
    }
}
