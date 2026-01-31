using System.Runtime.CompilerServices;
using Google.Cloud.Storage.V1;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Google Cloud Storage connector plugin.
/// Provides cloud object storage with bucket management and IAM integration.
/// </summary>
public class GcsConnectorPlugin : DataConnectorPluginBase
{
    private StorageClient? _storageClient;
    private string? _bucketName;
    private GcsConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.gcs";

    /// <inheritdoc />
    public override string Name => "Google Cloud Storage Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "gcs";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCategory ConnectorCategory => ConnectorCategory.CloudStorage;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.BulkOperations |
        ConnectorCapabilities.Streaming;

    public void Configure(GcsConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var props = (IReadOnlyDictionary<string, string?>)config.Properties;
            var projectId = props.GetValueOrDefault("ProjectId", null);
            _bucketName = props.GetValueOrDefault("BucketName", "") ?? "";

            // Create client (uses Application Default Credentials or service account JSON)
            _storageClient = await StorageClient.CreateAsync();

            // Test connection by listing buckets
            var buckets = new List<string>();
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                foreach (var bucket in _storageClient.ListBuckets(projectId))
                {
                    buckets.Add(bucket.Name);
                    if (buckets.Count >= 100) break; // Limit for test
                }
            }

            var serverInfo = new Dictionary<string, object>
            {
                ["ProjectId"] = projectId ?? "default",
                ["BucketCount"] = buckets.Count,
                ["DefaultBucket"] = _bucketName ?? "none"
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Google.GoogleApiException ex)
        {
            return new ConnectionResult(false, $"GCS connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _storageClient?.Dispose();
            _storageClient = null;
            _bucketName = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override Task<bool> PingAsync()
    {
        return Task.FromResult(_storageClient != null);
    }

    /// <inheritdoc />
    protected override Task<DataSchema> FetchSchemaAsync()
    {
        if (_storageClient == null)
            throw new InvalidOperationException("Not connected to GCS");

        return Task.FromResult(new DataSchema(
            Name: "gcs-storage",
            Fields: new[]
            {
                new DataSchemaField("name", "string", false, null, null),
                new DataSchemaField("size", "long", false, null, null),
                new DataSchemaField("updated", "datetime", false, null, null),
                new DataSchemaField("contentType", "string", true, null, null),
                new DataSchemaField("bucket", "string", false, null, null),
                new DataSchemaField("storageClass", "string", false, null, null),
                new DataSchemaField("content", "stream", true, null, null)
            },
            PrimaryKeys: new[] { "bucket", "name" },
            Metadata: new Dictionary<string, object>
            {
                ["Provider"] = "Google Cloud Storage",
                ["SupportsStreaming"] = true
            }
        ));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_storageClient == null)
            throw new InvalidOperationException("Not connected to GCS");

        var bucket = query.TableOrCollection ?? _bucketName ?? throw new ArgumentException("Bucket name is required");
        var prefix = query.Filter ?? "";
        var downloadContent = false;

        long position = 0;

        foreach (var obj in _storageClient.ListObjects(bucket, prefix))
        {
            if (ct.IsCancellationRequested) yield break;

            var values = new Dictionary<string, object?>
            {
                ["name"] = obj.Name,
                ["size"] = (long?)obj.Size ?? 0,
                ["updated"] = obj.UpdatedDateTimeOffset ?? DateTimeOffset.MinValue,
                ["contentType"] = obj.ContentType,
                ["bucket"] = bucket,
                ["storageClass"] = obj.StorageClass
            };

            // Add streaming download capability
            if (downloadContent)
            {
                var stream = new MemoryStream();
                await _storageClient.DownloadObjectAsync(bucket, obj.Name, stream, cancellationToken: ct);
                stream.Position = 0;
                values["content"] = stream;
            }

            yield return new DataRecord(
                Values: values,
                Position: position++,
                Timestamp: DateTimeOffset.UtcNow
            );

            await Task.Yield(); // Allow cancellation
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_storageClient == null)
            throw new InvalidOperationException("Not connected to GCS");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var bucket = options.TargetTable ?? _bucketName ?? throw new ArgumentException("Bucket name is required");

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var objectName = record.Values.GetValueOrDefault("name")?.ToString()
                    ?? throw new ArgumentException("Object name is required");
                var contentType = record.Values.GetValueOrDefault("contentType")?.ToString() ?? "application/octet-stream";

                // Get content as stream or byte array
                Stream? contentStream = null;
                bool disposeStream = false;

                if (record.Values.TryGetValue("content", out var contentValue))
                {
                    if (contentValue is Stream existingStream)
                    {
                        contentStream = existingStream;
                        disposeStream = false; // Caller owns the stream
                    }
                    else if (contentValue is byte[] bytes)
                    {
                        // Size validation before upload
                        if (_config.MaxFileSizeBytes > 0 && bytes.Length > _config.MaxFileSizeBytes)
                        {
                            throw new InvalidOperationException(
                                $"File size {bytes.Length} bytes exceeds maximum allowed size of {_config.MaxFileSizeBytes} bytes");
                        }

                        contentStream = new MemoryStream(bytes);
                        disposeStream = true;
                    }
                    else
                    {
                        throw new ArgumentException("Content must be a Stream or byte array");
                    }
                }
                else
                {
                    throw new ArgumentException("Content is required");
                }

                try
                {
                    // Direct streaming upload without loading to MemoryStream first
                    await _storageClient.UploadObjectAsync(
                        bucket,
                        objectName,
                        contentType,
                        contentStream,
                        cancellationToken: ct
                    );

                    written++;
                }
                finally
                {
                    if (disposeStream && contentStream != null)
                    {
                        await contentStream.DisposeAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

public class GcsConnectorConfig
{
    public int TimeoutSeconds { get; set; } = 30;
    public long MaxFileSizeBytes { get; set; } = 5L * 1024 * 1024 * 1024; // 5 GB default
}
