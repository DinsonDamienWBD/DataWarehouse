using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Backblaze B2 cloud storage connector plugin.
/// Uses S3-compatible API for object storage operations.
/// </summary>
public class BackblazeB2ConnectorPlugin : DataConnectorPluginBase
{
    private IAmazonS3? _s3Client;
    private string? _bucketName;
    private BackblazeB2ConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.backblazeb2";

    /// <inheritdoc />
    public override string Name => "Backblaze B2 Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "backblazeb2";

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

    public void Configure(BackblazeB2ConnectorConfig config)
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
            var keyId = props.GetValueOrDefault("KeyId", "") ?? "";
            var applicationKey = props.GetValueOrDefault("ApplicationKey", "") ?? "";
            var endpoint = props.GetValueOrDefault("Endpoint", "s3.us-west-000.backblazeb2.com") ?? "s3.us-west-000.backblazeb2.com";
            _bucketName = props.GetValueOrDefault("BucketName", "") ?? "";

            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(applicationKey))
            {
                return new ConnectionResult(false, "Backblaze B2 credentials are required", null);
            }

            var s3Config = new AmazonS3Config
            {
                ServiceURL = $"https://{endpoint}",
                ForcePathStyle = true
            };

            _s3Client = new AmazonS3Client(keyId, applicationKey, s3Config);

            // Test connection
            var bucketsResponse = await _s3Client.ListBucketsAsync(ct);

            var serverInfo = new Dictionary<string, object>
            {
                ["Endpoint"] = endpoint,
                ["BucketCount"] = bucketsResponse.Buckets.Count,
                ["DefaultBucket"] = _bucketName ?? "none"
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (AmazonS3Exception ex)
        {
            return new ConnectionResult(false, $"Backblaze B2 connection failed: {ex.Message}", null);
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
            _s3Client?.Dispose();
            _s3Client = null;
            _bucketName = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_s3Client == null) return false;

        try
        {
            await _s3Client.ListBucketsAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override Task<DataSchema> FetchSchemaAsync()
    {
        if (_s3Client == null)
            throw new InvalidOperationException("Not connected to Backblaze B2");

        return Task.FromResult(new DataSchema(
            Name: "b2-storage",
            Fields: new[]
            {
                new DataSchemaField("key", "string", false, null, null),
                new DataSchemaField("size", "long", false, null, null),
                new DataSchemaField("lastModified", "datetime", false, null, null),
                new DataSchemaField("etag", "string", false, null, null),
                new DataSchemaField("bucket", "string", false, null, null),
                new DataSchemaField("content", "stream", true, null, null)
            },
            PrimaryKeys: new[] { "bucket", "key" },
            Metadata: new Dictionary<string, object>
            {
                ["Provider"] = "Backblaze B2",
                ["SupportsStreaming"] = true
            }
        ));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_s3Client == null)
            throw new InvalidOperationException("Not connected to Backblaze B2");

        var bucket = query.TableOrCollection ?? _bucketName ?? throw new ArgumentException("Bucket name is required");
        var prefix = query.Filter ?? "";
        var downloadContent = false;

        var request = new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
            MaxKeys = query.Limit ?? 1000
        };

        long position = 0;
        ListObjectsV2Response? response;

        do
        {
            response = await _s3Client.ListObjectsV2Async(request, ct);

            foreach (var obj in response.S3Objects)
            {
                if (ct.IsCancellationRequested) yield break;

                var values = new Dictionary<string, object?>
                {
                    ["key"] = obj.Key,
                    ["size"] = obj.Size,
                    ["lastModified"] = obj.LastModified,
                    ["etag"] = obj.ETag,
                    ["bucket"] = bucket
                };

                // Add streaming download capability
                if (downloadContent)
                {
                    var getRequest = new GetObjectRequest
                    {
                        BucketName = bucket,
                        Key = obj.Key
                    };

                    var getResponse = await _s3Client.GetObjectAsync(getRequest, ct);
                    values["content"] = getResponse.ResponseStream;
                }

                yield return new DataRecord(
                    Values: values,
                    Position: position++,
                    Timestamp: DateTimeOffset.UtcNow
                );
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true && !ct.IsCancellationRequested);
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_s3Client == null)
            throw new InvalidOperationException("Not connected to Backblaze B2");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var bucket = options.TargetTable ?? _bucketName ?? throw new ArgumentException("Bucket name is required");

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var key = record.Values.GetValueOrDefault("key")?.ToString()
                    ?? throw new ArgumentException("Object key is required");

                // Get content as stream or byte array
                Stream? contentStream = null;
                bool disposeStream = false;
                long? contentLength = null;

                if (record.Values.TryGetValue("content", out var contentValue))
                {
                    if (contentValue is Stream existingStream)
                    {
                        contentStream = existingStream;
                        disposeStream = false; // Caller owns the stream

                        // Try to get stream length for validation
                        if (existingStream.CanSeek)
                        {
                            contentLength = existingStream.Length;
                        }
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
                        contentLength = bytes.Length;
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
                    // Validate stream length if available
                    if (contentLength.HasValue && _config.MaxFileSizeBytes > 0 && contentLength.Value > _config.MaxFileSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"File size {contentLength.Value} bytes exceeds maximum allowed size of {_config.MaxFileSizeBytes} bytes");
                    }

                    // Direct streaming upload without loading to MemoryStream
                    var putRequest = new PutObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        InputStream = contentStream
                    };

                    await _s3Client.PutObjectAsync(putRequest, ct);
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

public class BackblazeB2ConnectorConfig
{
    public int TimeoutSeconds { get; set; } = 30;
    public long MaxFileSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default
}
