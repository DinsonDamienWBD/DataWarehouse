using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.Runtime;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Polly;
using Polly.Retry;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Amazon S3 cloud storage connector plugin.
/// Provides object storage operations with bucket management, streaming, multipart upload, and credential chain support.
/// </summary>
public class S3ConnectorPlugin : DataConnectorPluginBase
{
    private IAmazonS3? _s3Client;
    private ITransferUtility? _transferUtility;
    private string? _bucketName;
    private S3ConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private AsyncRetryPolicy? _retryPolicy;

    // Multipart upload threshold: files larger than this use multipart
    private const long MultipartThreshold = 100 * 1024 * 1024; // 100 MB
    private const long MaxSingleUploadSize = 5L * 1024 * 1024 * 1024; // 5 GB AWS limit

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.s3";

    /// <inheritdoc />
    public override string Name => "Amazon S3 Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "s3";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCategory ConnectorCategory => ConnectorCategory.CloudStorage;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.BulkOperations;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    public void Configure(S3ConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        InitializeRetryPolicy();
    }

    /// <summary>
    /// Initializes the Polly retry policy for network resilience.
    /// </summary>
    private void InitializeRetryPolicy()
    {
        _retryPolicy = Policy
            .Handle<AmazonS3Exception>(ex => IsTransientError(ex))
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                _config.MaxRetries,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    // Log retry attempt (could be enhanced with proper logging)
                    Console.WriteLine($"S3 operation retry {retryCount}/{_config.MaxRetries} after {timeSpan.TotalSeconds}s due to: {exception.Message}");
                });
    }

    /// <summary>
    /// Determines if an S3 exception is transient and should be retried.
    /// </summary>
    private static bool IsTransientError(AmazonS3Exception ex)
    {
        return ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
               ex.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
               ex.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
               ex.ErrorCode == "RequestTimeout" ||
               ex.ErrorCode == "SlowDown" ||
               ex.ErrorCode == "InternalError";
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var props = (IReadOnlyDictionary<string, string?>)config.Properties;

            var accessKey = props.GetValueOrDefault("AccessKey", "") ?? "";
            var secretKey = props.GetValueOrDefault("SecretKey", "") ?? "";
            var region = props.GetValueOrDefault("Region", "us-east-1") ?? "us-east-1";
            var useCredentialChain = (props.GetValueOrDefault("UseCredentialChain", "false") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            var profileName = props.GetValueOrDefault("ProfileName", "") ?? "";
            _bucketName = props.GetValueOrDefault("BucketName", "") ?? "";

            var s3Config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region),
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds),
                MaxErrorRetry = _config.MaxRetries
            };

            // Support AWS credential provider chain
            if (useCredentialChain)
            {
                // Use FallbackCredentialsFactory which checks:
                // 1. Environment variables (AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY)
                // 2. AWS credentials file (~/.aws/credentials)
                // 3. IAM role for EC2 instances
                // 4. ECS task role
                _s3Client = new AmazonS3Client(s3Config);
            }
            else if (!string.IsNullOrWhiteSpace(profileName))
            {
                // Use named profile from credentials file
                var credentials = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
                if (credentials.TryGetAWSCredentials(profileName, out var awsCredentials))
                {
                    _s3Client = new AmazonS3Client(awsCredentials, s3Config);
                }
                else
                {
                    return new ConnectionResult(false, $"Profile '{profileName}' not found in credentials file", null);
                }
            }
            else if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
            {
                // Use explicit credentials
                _s3Client = new AmazonS3Client(accessKey, secretKey, s3Config);
            }
            else
            {
                return new ConnectionResult(false, "AWS credentials are required. Provide AccessKey/SecretKey, ProfileName, or set UseCredentialChain=true", null);
            }

            // Initialize TransferUtility for multipart uploads
            _transferUtility = new TransferUtility(_s3Client);

            // Initialize retry policy
            InitializeRetryPolicy();

            // Test connection by listing buckets
            var bucketsResponse = await _retryPolicy!.ExecuteAsync(() => _s3Client.ListBucketsAsync(ct));

            var serverInfo = new Dictionary<string, object>
            {
                ["Region"] = region,
                ["BucketCount"] = bucketsResponse.Buckets.Count,
                ["DefaultBucket"] = _bucketName ?? "none",
                ["Endpoint"] = s3Config.ServiceURL,
                ["MultipartThresholdMB"] = MultipartThreshold / (1024 * 1024),
                ["CredentialMode"] = useCredentialChain ? "CredentialChain" :
                                     !string.IsNullOrWhiteSpace(profileName) ? $"Profile:{profileName}" : "Explicit"
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (AmazonS3Exception ex)
        {
            return new ConnectionResult(false, $"S3 connection failed: {ex.Message} (ErrorCode: {ex.ErrorCode})", null);
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
            _transferUtility?.Dispose();
            _transferUtility = null;
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
        if (_retryPolicy == null) return false;

        try
        {
            await _retryPolicy.ExecuteAsync(() => _s3Client.ListBucketsAsync());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_s3Client == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

        var buckets = await _retryPolicy.ExecuteAsync(() => _s3Client.ListBucketsAsync());

        return new DataSchema(
            Name: "s3-storage",
            Fields: new[]
            {
                new DataSchemaField("key", "string", false, null, null),
                new DataSchemaField("size", "long", false, null, null),
                new DataSchemaField("lastModified", "datetime", false, null, null),
                new DataSchemaField("etag", "string", false, null, null),
                new DataSchemaField("storageClass", "string", false, null, null),
                new DataSchemaField("bucket", "string", false, null, null)
            },
            PrimaryKeys: new[] { "bucket", "key" },
            Metadata: new Dictionary<string, object>
            {
                ["BucketCount"] = buckets.Buckets.Count,
                ["Buckets"] = buckets.Buckets.Select(b => b.BucketName).ToArray()
            }
        );
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_s3Client == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

        var bucket = query.TableOrCollection ?? _bucketName ?? throw new ArgumentException("Bucket name is required");
        var prefix = query.Filter ?? "";

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
            response = await _retryPolicy.ExecuteAsync(() => _s3Client.ListObjectsV2Async(request, ct));

            foreach (var obj in response.S3Objects)
            {
                if (ct.IsCancellationRequested) yield break;

                yield return new DataRecord(
                    Values: new Dictionary<string, object?>
                    {
                        ["key"] = obj.Key,
                        ["size"] = obj.Size,
                        ["lastModified"] = obj.LastModified,
                        ["etag"] = obj.ETag,
                        ["storageClass"] = obj.StorageClass.Value,
                        ["bucket"] = bucket
                    },
                    Position: position++,
                    Timestamp: DateTimeOffset.UtcNow
                );
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true && !ct.IsCancellationRequested);
    }

    /// <summary>
    /// Downloads an object from S3 with streaming support.
    /// </summary>
    /// <param name="bucket">Bucket name</param>
    /// <param name="key">Object key</param>
    /// <param name="destination">Destination stream</param>
    /// <param name="ct">Cancellation token</param>
    public async Task DownloadObjectAsync(string bucket, string key, Stream destination, CancellationToken ct = default)
    {
        if (_s3Client == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

        await _retryPolicy.ExecuteAsync(async () =>
        {
            var request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            };

            using var response = await _s3Client.GetObjectAsync(request, ct);
            await response.ResponseStream.CopyToAsync(destination, 81920, ct); // 80KB buffer
        });
    }

    /// <summary>
    /// Downloads a range of bytes from an S3 object with streaming support.
    /// </summary>
    /// <param name="bucket">Bucket name</param>
    /// <param name="key">Object key</param>
    /// <param name="destination">Destination stream</param>
    /// <param name="startByte">Start byte position</param>
    /// <param name="endByte">End byte position (inclusive)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task DownloadObjectRangeAsync(string bucket, string key, Stream destination, long startByte, long endByte, CancellationToken ct = default)
    {
        if (_s3Client == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

        await _retryPolicy.ExecuteAsync(async () =>
        {
            var request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ByteRange = new ByteRange(startByte, endByte)
            };

            using var response = await _s3Client.GetObjectAsync(request, ct);
            await response.ResponseStream.CopyToAsync(destination, 81920, ct);
        });
    }

    /// <summary>
    /// Uploads an object to S3 with automatic multipart upload for large files.
    /// </summary>
    /// <param name="bucket">Bucket name</param>
    /// <param name="key">Object key</param>
    /// <param name="source">Source stream</param>
    /// <param name="metadata">Optional metadata</param>
    /// <param name="ct">Cancellation token</param>
    public async Task UploadObjectAsync(string bucket, string key, Stream source, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        if (_s3Client == null || _transferUtility == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

        // Validate size
        if (source.CanSeek)
        {
            var size = source.Length;
            if (size > MaxSingleUploadSize && size > MultipartThreshold)
            {
                // Use multipart upload for very large files
                await UploadLargeObjectAsync(bucket, key, source, metadata, ct);
                return;
            }
        }

        await _retryPolicy.ExecuteAsync(async () =>
        {
            // Use TransferUtility which automatically handles multipart for large streams
            var uploadRequest = new TransferUtilityUploadRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = source,
                PartSize = _config.MultipartPartSizeMB * 1024 * 1024, // Convert MB to bytes
                AutoCloseStream = false
            };

            if (metadata != null)
            {
                foreach (var (k, v) in metadata)
                {
                    uploadRequest.Metadata.Add(k, v);
                }
            }

            await _transferUtility.UploadAsync(uploadRequest, ct);
        });
    }

    /// <summary>
    /// Uploads a large object using manual multipart upload for maximum control.
    /// </summary>
    private async Task UploadLargeObjectAsync(string bucket, string key, Stream source, Dictionary<string, string>? metadata, CancellationToken ct)
    {
        if (_s3Client == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

        var partSize = _config.MultipartPartSizeMB * 1024 * 1024L;
        var uploadId = "";

        try
        {
            // Initiate multipart upload
            var initiateRequest = new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key
            };

            if (metadata != null)
            {
                foreach (var (k, v) in metadata)
                {
                    initiateRequest.Metadata.Add(k, v);
                }
            }

            var initiateResponse = await _retryPolicy.ExecuteAsync(() => _s3Client.InitiateMultipartUploadAsync(initiateRequest, ct));
            uploadId = initiateResponse.UploadId;

            var partETags = new List<PartETag>();
            var partNumber = 1;
            var buffer = new byte[partSize];

            // Upload parts
            while (true)
            {
                var bytesRead = 0;
                var totalBytesRead = 0;

                // Fill buffer
                while (totalBytesRead < partSize)
                {
                    bytesRead = await source.ReadAsync(buffer.AsMemory(totalBytesRead, (int)partSize - totalBytesRead), ct);
                    if (bytesRead == 0) break;
                    totalBytesRead += bytesRead;
                }

                if (totalBytesRead == 0) break;

                // Upload part with retry
                var partRequest = new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = totalBytesRead,
                    InputStream = new MemoryStream(buffer, 0, totalBytesRead)
                };

                var partResponse = await _retryPolicy.ExecuteAsync(() => _s3Client.UploadPartAsync(partRequest, ct));
                partETags.Add(new PartETag(partNumber, partResponse.ETag));

                partNumber++;

                if (totalBytesRead < partSize) break; // Last part
            }

            // Complete multipart upload
            var completeRequest = new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = partETags
            };

            await _retryPolicy.ExecuteAsync(() => _s3Client.CompleteMultipartUploadAsync(completeRequest, ct));
        }
        catch
        {
            // Abort multipart upload on failure
            if (!string.IsNullOrEmpty(uploadId))
            {
                try
                {
                    await _s3Client.AbortMultipartUploadAsync(bucket, key, uploadId, ct);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_s3Client == null || _transferUtility == null || _retryPolicy == null)
            throw new InvalidOperationException("Not connected to S3");

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

                // Support both byte[] and Stream content
                Stream? contentStream = null;
                var shouldDisposeStream = false;

                if (record.Values.TryGetValue("content", out var contentObj))
                {
                    if (contentObj is byte[] bytes)
                    {
                        contentStream = new MemoryStream(bytes);
                        shouldDisposeStream = true;
                    }
                    else if (contentObj is Stream stream)
                    {
                        contentStream = stream;
                    }
                    else
                    {
                        throw new ArgumentException("Content must be byte[] or Stream");
                    }
                }
                else
                {
                    throw new ArgumentException("Content is required");
                }

                try
                {
                    // Validate size if available
                    if (contentStream.CanSeek && contentStream.Length > MaxSingleUploadSize)
                    {
                        throw new ArgumentException($"File size {contentStream.Length} bytes exceeds maximum {MaxSingleUploadSize} bytes");
                    }

                    // Extract metadata if present
                    Dictionary<string, string>? metadata = null;
                    if (record.Values.TryGetValue("metadata", out var metadataObj) &&
                        metadataObj is Dictionary<string, string> meta)
                    {
                        metadata = meta;
                    }

                    // Use the streaming upload method
                    await UploadObjectAsync(bucket, key, contentStream, metadata, ct);
                    written++;
                }
                finally
                {
                    if (shouldDisposeStream)
                    {
                        contentStream?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");

                if (!_config.ContinueOnError)
                {
                    throw;
                }
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the S3 connector.
/// </summary>
public class S3ConnectorConfig
{
    /// <summary>
    /// Timeout in seconds for S3 operations.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300; // 5 minutes for large uploads

    /// <summary>
    /// Maximum number of retries for transient errors.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Part size in MB for multipart uploads (minimum 5MB, maximum 5GB).
    /// </summary>
    public int MultipartPartSizeMB { get; set; } = 10; // 10 MB parts

    /// <summary>
    /// Continue processing records on error instead of throwing.
    /// </summary>
    public bool ContinueOnError { get; set; } = false;
}
