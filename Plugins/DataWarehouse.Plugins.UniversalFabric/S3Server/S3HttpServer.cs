using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Storage.Fabric;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalFabric.S3Server;

/// <summary>
/// Internal state tracking for an in-progress multipart upload.
/// </summary>
internal sealed class MultipartUploadState
{
    public required string UploadId { get; init; }
    public required string BucketName { get; init; }
    public required string Key { get; init; }
    public string? ContentType { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
    public DateTime Initiated { get; init; } = DateTime.UtcNow;
    public BoundedDictionary<int, PartInfo> Parts { get; } = new BoundedDictionary<int, PartInfo>(1000);
}

/// <summary>
/// Tracks individual part data for multipart uploads.
/// </summary>
internal sealed class PartInfo
{
    public required int PartNumber { get; init; }
    public required string ETag { get; init; }
    public required byte[] Data { get; init; }
}

/// <summary>
/// Production-ready S3-compatible HTTP server that implements <see cref="IS3CompatibleServer"/>.
/// Exposes all storage backends registered in <see cref="IStorageFabric"/> through the standard
/// S3 API, enabling any S3 client (AWS CLI, boto3, MinIO client) to connect to DataWarehouse.
/// </summary>
/// <remarks>
/// <para>
/// Buckets map to dw:// namespaces. For example, a PUT to /mybucket/path/to/object stores data
/// at dw://mybucket/path/to/object via the fabric's routing layer.
/// </para>
/// <para>
/// Authentication is delegated to <see cref="IS3AuthProvider"/> when configured via
/// <see cref="S3ServerOptions.AuthProvider"/>. When no auth provider is set, the server
/// operates in anonymous/open mode.
/// </para>
/// <para>
/// Multipart uploads are tracked in-memory and concatenated upon completion. Parts are buffered
/// individually and assembled into the final object when CompleteMultipartUpload is called.
/// </para>
/// </remarks>
public sealed class S3HttpServer : IS3CompatibleServer
{
    private readonly IStorageFabric _fabric;
    private readonly S3RequestParser _parser = new();
    private readonly S3ResponseWriter _writer = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private S3ServerOptions _options = new();

    /// <summary>
    /// Tracks active multipart uploads keyed by upload ID.
    /// </summary>
    private readonly BoundedDictionary<string, MultipartUploadState> _activeUploads = new BoundedDictionary<string, MultipartUploadState>(1000);

    /// <summary>
    /// In-memory bucket registry tracking known bucket names and creation dates.
    /// In production, this would be persisted; here it is backed by fabric namespace discovery.
    /// </summary>
    private readonly BoundedDictionary<string, DateTime> _bucketRegistry = new BoundedDictionary<string, DateTime>(1000);

    /// <summary>
    /// Creates a new S3HttpServer backed by the specified storage fabric.
    /// </summary>
    /// <param name="fabric">The storage fabric to delegate all storage operations to.</param>
    public S3HttpServer(IStorageFabric fabric)
    {
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
    }

    #region IS3CompatibleServer - Server Lifecycle

    /// <inheritdoc />
    public bool IsRunning => _listener?.IsListening ?? false;

    /// <inheritdoc />
    public string? ListenUrl { get; private set; }

    /// <inheritdoc />
    public async Task StartAsync(S3ServerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (IsRunning)
            throw new InvalidOperationException("S3 HTTP server is already running.");

        _options = options;
        _listener = new HttpListener();
        var scheme = options.UseTls ? "https" : "http";
        var prefix = $"{scheme}://{options.Host}:{options.Port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        ListenUrl = $"{scheme}://{options.Host}:{options.Port}";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerTask = AcceptLoopAsync(_cts.Token);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_listener is { IsListening: true })
        {
            _listener.Stop();
        }

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {

                // Expected during shutdown
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
            catch (HttpListenerException ex)
            {

                // Expected when listener is stopped
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        ListenUrl = null;
    }

    #endregion

    #region IS3CompatibleServer - Bucket Operations

    /// <inheritdoc />
    public Task<S3ListBucketsResponse> ListBucketsAsync(S3ListBucketsRequest request, CancellationToken ct = default)
    {
        var buckets = _bucketRegistry.Select(kvp => new S3Bucket
        {
            Name = kvp.Key,
            CreationDate = kvp.Value
        }).ToList();

        return Task.FromResult(new S3ListBucketsResponse
        {
            Buckets = buckets.AsReadOnly(),
            Owner = "DataWarehouse"
        });
    }

    /// <inheritdoc />
    public Task<S3CreateBucketResponse> CreateBucketAsync(S3CreateBucketRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bucketRegistry.TryAdd(request.BucketName, DateTime.UtcNow))
        {
            throw new InvalidOperationException($"Bucket '{request.BucketName}' already exists.");
        }

        return Task.FromResult(new S3CreateBucketResponse
        {
            BucketName = request.BucketName,
            Location = $"/{request.BucketName}"
        });
    }

    /// <inheritdoc />
    public Task DeleteBucketAsync(string bucketName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(bucketName);

        if (!_bucketRegistry.TryRemove(bucketName, out _))
        {
            throw new KeyNotFoundException($"Bucket '{bucketName}' not found.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> BucketExistsAsync(string bucketName, CancellationToken ct = default)
    {
        return Task.FromResult(_bucketRegistry.ContainsKey(bucketName));
    }

    #endregion

    #region IS3CompatibleServer - Object Operations

    /// <inheritdoc />
    public async Task<S3GetObjectResponse> GetObjectAsync(S3GetObjectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBucketExists(request.BucketName);

        var address = StorageAddress.FromDwBucket(request.BucketName, request.Key);
        var stream = await _fabric.RetrieveAsync(address, ct).ConfigureAwait(false);
        var metadata = await _fabric.GetMetadataAsync(address, ct).ConfigureAwait(false);

        return new S3GetObjectResponse
        {
            Body = stream,
            ContentType = metadata.ContentType ?? "application/octet-stream",
            ContentLength = metadata.Size,
            ETag = metadata.ETag ?? ComputeETag(Array.Empty<byte>()),
            Metadata = metadata.CustomMetadata != null
                ? new Dictionary<string, string>(metadata.CustomMetadata)
                : new Dictionary<string, string>(),
            LastModified = metadata.Modified
        };
    }

    /// <inheritdoc />
    public async Task<S3PutObjectResponse> PutObjectAsync(S3PutObjectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBucketExists(request.BucketName);

        var address = StorageAddress.FromDwBucket(request.BucketName, request.Key);

        // Read the body into a MemoryStream for ETag computation and storage
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var data = buffer.ToArray();
        var etag = ComputeETag(data);

        var storageMetadata = new Dictionary<string, string>();
        if (request.ContentType != null)
            storageMetadata["Content-Type"] = request.ContentType;
        if (request.Metadata != null)
        {
            foreach (var kvp in request.Metadata)
                storageMetadata[kvp.Key] = kvp.Value;
        }

        using var storeStream = new MemoryStream(data);
        await _fabric.StoreAsync(address, storeStream,
            hints: null,
            metadata: storageMetadata.Count > 0 ? storageMetadata : null,
            ct: ct).ConfigureAwait(false);

        return new S3PutObjectResponse
        {
            ETag = etag
        };
    }

    /// <inheritdoc />
    public async Task DeleteObjectAsync(string bucketName, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(bucketName);
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        EnsureBucketExists(bucketName);

        var address = StorageAddress.FromDwBucket(bucketName, key);
        await _fabric.DeleteAsync(address, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<S3HeadObjectResponse> HeadObjectAsync(string bucketName, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(bucketName);
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        EnsureBucketExists(bucketName);

        var address = StorageAddress.FromDwBucket(bucketName, key);
        var metadata = await _fabric.GetMetadataAsync(address, ct).ConfigureAwait(false);

        return new S3HeadObjectResponse
        {
            ContentLength = metadata.Size,
            ContentType = metadata.ContentType ?? "application/octet-stream",
            ETag = metadata.ETag ?? ComputeETag(Array.Empty<byte>()),
            LastModified = metadata.Modified,
            Metadata = metadata.CustomMetadata != null
                ? new Dictionary<string, string>(metadata.CustomMetadata)
                : new Dictionary<string, string>()
        };
    }

    /// <inheritdoc />
    public async Task<S3ListObjectsResponse> ListObjectsV2Async(S3ListObjectsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBucketExists(request.BucketName);

        var prefix = $"dw://{request.BucketName}/";
        var fullPrefix = request.Prefix != null ? $"{prefix}{request.Prefix}" : prefix;

        var allObjects = new List<S3Object>();
        var commonPrefixes = new HashSet<string>();

        await foreach (var item in _fabric.ListAsync(fullPrefix, ct).ConfigureAwait(false))
        {
            // Strip the dw://bucket/ prefix from keys for the S3 response
            var objectKey = item.Key;
            if (objectKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                objectKey = objectKey[prefix.Length..];
            }

            // Handle delimiter-based grouping (virtual directories)
            if (request.Delimiter != null && objectKey.Contains(request.Delimiter))
            {
                var afterPrefix = request.Prefix != null && objectKey.StartsWith(request.Prefix)
                    ? objectKey[request.Prefix.Length..]
                    : objectKey;

                var delimIndex = afterPrefix.IndexOf(request.Delimiter, StringComparison.Ordinal);
                if (delimIndex >= 0)
                {
                    var commonPrefix = (request.Prefix ?? "") + afterPrefix[..(delimIndex + request.Delimiter.Length)];
                    commonPrefixes.Add(commonPrefix);
                    continue;
                }
            }

            allObjects.Add(new S3Object
            {
                Key = objectKey,
                Size = item.Size,
                LastModified = item.Modified,
                ETag = item.ETag ?? ComputeETag(Array.Empty<byte>()),
                StorageClass = "STANDARD"
            });
        }

        // Handle pagination via HMAC-signed opaque continuation token (finding 4534).
        // The token encodes the offset signed with a server secret to prevent client probing.
        var startIndex = 0;
        if (request.ContinuationToken != null)
        {
            startIndex = DecodeAndVerifyContinuationToken(request.ContinuationToken);
        }

        var pageObjects = allObjects
            .Skip(startIndex)
            .Take(request.MaxKeys)
            .ToList();

        var isTruncated = startIndex + request.MaxKeys < allObjects.Count;
        string? nextToken = isTruncated ? CreateContinuationToken(startIndex + request.MaxKeys) : null;

        return new S3ListObjectsResponse
        {
            Contents = pageObjects.AsReadOnly(),
            CommonPrefixes = commonPrefixes.ToList().AsReadOnly(),
            IsTruncated = isTruncated,
            NextContinuationToken = nextToken,
            KeyCount = pageObjects.Count
        };
    }

    #endregion

    #region IS3CompatibleServer - Multipart Upload

    /// <inheritdoc />
    public Task<S3InitiateMultipartResponse> InitiateMultipartUploadAsync(
        S3InitiateMultipartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBucketExists(request.BucketName);

        var uploadId = Guid.NewGuid().ToString("N");
        var state = new MultipartUploadState
        {
            UploadId = uploadId,
            BucketName = request.BucketName,
            Key = request.Key,
            ContentType = request.ContentType,
            Metadata = request.Metadata
        };

        _activeUploads[uploadId] = state;

        return Task.FromResult(new S3InitiateMultipartResponse
        {
            UploadId = uploadId,
            BucketName = request.BucketName,
            Key = request.Key
        });
    }

    /// <inheritdoc />
    public async Task<S3UploadPartResponse> UploadPartAsync(S3UploadPartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_activeUploads.TryGetValue(request.UploadId, out var state))
        {
            throw new KeyNotFoundException($"No active upload with ID '{request.UploadId}'.");
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var data = buffer.ToArray();
        var etag = ComputeETag(data);

        var partInfo = new PartInfo
        {
            PartNumber = request.PartNumber,
            ETag = etag,
            Data = data
        };

        state.Parts[request.PartNumber] = partInfo;

        return new S3UploadPartResponse
        {
            ETag = etag,
            PartNumber = request.PartNumber
        };
    }

    /// <inheritdoc />
    public async Task<S3CompleteMultipartResponse> CompleteMultipartUploadAsync(
        S3CompleteMultipartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_activeUploads.TryRemove(request.UploadId, out var state))
        {
            throw new KeyNotFoundException($"No active upload with ID '{request.UploadId}'.");
        }

        // Concatenate parts in order
        using var combined = new MemoryStream();
        foreach (var part in request.Parts.OrderBy(p => p.PartNumber))
        {
            if (state.Parts.TryGetValue(part.PartNumber, out var partInfo))
            {
                await combined.WriteAsync(partInfo.Data, ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException($"Part {part.PartNumber} not found for upload '{request.UploadId}'.");
            }
        }

        var finalData = combined.ToArray();
        var etag = ComputeETag(finalData);

        // Store the assembled object
        var address = StorageAddress.FromDwBucket(state.BucketName, state.Key);
        var metadata = new Dictionary<string, string>();
        if (state.ContentType != null)
            metadata["Content-Type"] = state.ContentType;
        if (state.Metadata != null)
        {
            foreach (var kvp in state.Metadata)
                metadata[kvp.Key] = kvp.Value;
        }

        using var storeStream = new MemoryStream(finalData);
        await _fabric.StoreAsync(address, storeStream,
            hints: null,
            metadata: metadata.Count > 0 ? metadata : null,
            ct: ct).ConfigureAwait(false);

        return new S3CompleteMultipartResponse
        {
            ETag = etag,
            Key = state.Key,
            BucketName = state.BucketName
        };
    }

    /// <inheritdoc />
    public Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(uploadId);

        _activeUploads.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    #endregion

    #region IS3CompatibleServer - Presigned URLs

    /// <inheritdoc />
    public Task<string> GeneratePresignedUrlAsync(S3PresignedUrlRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(_options.PresignSecret))
            throw new InvalidOperationException(
                "S3 presigned URL generation requires a configured PresignSecret. " +
                "Set S3ServerOptions.PresignSecret to a strong, randomly-generated secret.");

        var expiry = DateTimeOffset.UtcNow.Add(request.Expiration).ToUnixTimeSeconds();
        var stringToSign = $"{request.Method}:{request.BucketName}:{request.Key}:{expiry}";
        var signatureBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.PresignSecret),
            Encoding.UTF8.GetBytes(stringToSign));
        var signature = Convert.ToHexStringLower(signatureBytes);

        var baseUrl = ListenUrl ?? "http://localhost:9000";
        var encodedKey = Uri.EscapeDataString(request.Key);
        var url = $"{baseUrl}/{request.BucketName}/{encodedKey}" +
                  $"?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
                  $"&X-Amz-Expires={request.Expiration.TotalSeconds:F0}" +
                  $"&X-Amz-SignedHeaders=host" +
                  $"&X-Amz-Signature={signature}" +
                  $"&X-Amz-Date={DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";

        return Task.FromResult(url);
    }

    #endregion

    #region IS3CompatibleServer - Copy

    /// <inheritdoc />
    public async Task<S3CopyObjectResponse> CopyObjectAsync(S3CopyObjectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBucketExists(request.SourceBucket);
        EnsureBucketExists(request.DestBucket);

        var source = StorageAddress.FromDwBucket(request.SourceBucket, request.SourceKey);
        var destination = StorageAddress.FromDwBucket(request.DestBucket, request.DestKey);

        await _fabric.CopyAsync(source, destination, ct).ConfigureAwait(false);

        var destMeta = await _fabric.GetMetadataAsync(destination, ct).ConfigureAwait(false);

        return new S3CopyObjectResponse
        {
            ETag = destMeta.ETag ?? ComputeETag(Array.Empty<byte>()),
            LastModified = destMeta.Modified
        };
    }

    #endregion

    #region IDisposable / IAsyncDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        (_listener as IDisposable)?.Dispose();
        _cts?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener?.Close();
        _cts?.Dispose();
    }

    #endregion

    #region Accept Loop & Request Dispatch

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // Fire-and-forget per request (each gets its own error handling)
            _ = Task.Run(() => HandleRequestAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            // Authentication check if auth provider is configured
            if (_options.AuthProvider != null)
            {
                var authResult = await AuthenticateRequest(context.Request, ct).ConfigureAwait(false);
                if (!authResult.IsAuthenticated)
                {
                    _writer.WriteErrorResponse(context.Response, 403, "AccessDenied",
                        authResult.ErrorMessage ?? "Access denied.");
                    return;
                }
            }

            var operation = _parser.ParseOperation(context.Request);
            var (bucket, key) = _parser.ExtractBucketAndKey(context.Request);

            switch (operation)
            {
                case S3Operation.ListBuckets:
                    await HandleListBuckets(context, ct).ConfigureAwait(false);
                    break;

                case S3Operation.CreateBucket:
                    await HandleCreateBucket(context, bucket!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.DeleteBucket:
                    await HandleDeleteBucket(context, bucket!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.GetObject:
                    await HandleGetObject(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.PutObject:
                    await HandlePutObject(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.DeleteObject:
                    await HandleDeleteObject(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.HeadObject:
                    await HandleHeadObject(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.ListObjectsV2:
                    await HandleListObjects(context, bucket!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.InitiateMultipartUpload:
                    await HandleInitiateMultipartUpload(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.UploadPart:
                    await HandleUploadPart(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.CompleteMultipartUpload:
                    await HandleCompleteMultipartUpload(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.AbortMultipartUpload:
                    await HandleAbortMultipartUpload(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                case S3Operation.CopyObject:
                    await HandleCopyObject(context, bucket!, key!, ct).ConfigureAwait(false);
                    break;

                default:
                    _writer.WriteErrorResponse(context.Response, 400, "InvalidArgument",
                        "Unsupported S3 operation.");
                    break;
            }
        }
        catch (KeyNotFoundException ex)
        {
            _writer.WriteErrorResponse(context.Response, 404, "NoSuchKey", ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            _writer.WriteErrorResponse(context.Response, 409, "BucketAlreadyExists", ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found") || ex.Message.Contains("does not exist"))
        {
            _writer.WriteErrorResponse(context.Response, 404, "NoSuchBucket", ex.Message);
        }
        catch (ArgumentException ex)
        {
            _writer.WriteErrorResponse(context.Response, 400, "InvalidArgument", ex.Message);
        }
        catch (Exception ex)
        {
            _writer.WriteErrorResponse(context.Response, 500, "InternalError", ex.Message);
        }
    }

    #endregion

    #region Request Handlers

    private async Task HandleListBuckets(HttpListenerContext context, CancellationToken ct)
    {
        var response = await ListBucketsAsync(new S3ListBucketsRequest(), ct).ConfigureAwait(false);
        _writer.WriteListBucketsResponse(context.Response, response);
    }

    private async Task HandleCreateBucket(HttpListenerContext context, string bucket, CancellationToken ct)
    {
        var response = await CreateBucketAsync(new S3CreateBucketRequest
        {
            BucketName = bucket
        }, ct).ConfigureAwait(false);
        _writer.WriteCreateBucketResponse(context.Response, response);
    }

    private async Task HandleDeleteBucket(HttpListenerContext context, string bucket, CancellationToken ct)
    {
        await DeleteBucketAsync(bucket, ct).ConfigureAwait(false);
        _writer.WriteNoContentResponse(context.Response);
    }

    private async Task HandleGetObject(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        var request = _parser.ParseGetObject(context.Request, bucket, key);
        var response = await GetObjectAsync(request, ct).ConfigureAwait(false);

        _writer.SetGetObjectHeaders(context.Response, response);
        context.Response.StatusCode = 200;

        await using (response.Body)
        {
            await response.Body.CopyToAsync(context.Response.OutputStream, ct).ConfigureAwait(false);
        }

        context.Response.OutputStream.Close();
    }

    private async Task HandlePutObject(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        // Auto-create bucket if it does not exist (S3-compatible convenience)
        _bucketRegistry.TryAdd(bucket, DateTime.UtcNow);

        var request = _parser.ParsePutObject(context.Request, bucket, key);
        var response = await PutObjectAsync(request, ct).ConfigureAwait(false);
        _writer.WritePutObjectResponse(context.Response, response);
    }

    private async Task HandleDeleteObject(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        await DeleteObjectAsync(bucket, key, ct).ConfigureAwait(false);
        _writer.WriteNoContentResponse(context.Response);
    }

    private async Task HandleHeadObject(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        var response = await HeadObjectAsync(bucket, key, ct).ConfigureAwait(false);
        _writer.SetObjectHeaders(context.Response, response);
        context.Response.StatusCode = 200;
        context.Response.ContentLength64 = response.ContentLength;
        context.Response.Close();
    }

    private async Task HandleListObjects(HttpListenerContext context, string bucket, CancellationToken ct)
    {
        var request = _parser.ParseListObjects(context.Request, bucket);
        var response = await ListObjectsV2Async(request, ct).ConfigureAwait(false);
        _writer.WriteListObjectsResponse(context.Response, response, bucket);
    }

    private async Task HandleInitiateMultipartUpload(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        // Auto-create bucket if it does not exist
        _bucketRegistry.TryAdd(bucket, DateTime.UtcNow);

        var request = _parser.ParseInitiateMultipart(context.Request, bucket, key);
        var response = await InitiateMultipartUploadAsync(request, ct).ConfigureAwait(false);
        _writer.WriteInitiateMultipartResponse(context.Response, response);
    }

    private async Task HandleUploadPart(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        var request = _parser.ParseUploadPart(context.Request, bucket, key);
        var response = await UploadPartAsync(request, ct).ConfigureAwait(false);
        _writer.WriteUploadPartResponse(context.Response, response);
    }

    private async Task HandleCompleteMultipartUpload(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        var request = await _parser.ParseCompleteMultipartAsync(context.Request, bucket, key).ConfigureAwait(false);
        var response = await CompleteMultipartUploadAsync(request, ct).ConfigureAwait(false);
        _writer.WriteCompleteMultipartResponse(context.Response, response);
    }

    private async Task HandleAbortMultipartUpload(HttpListenerContext context, string bucket, string key, CancellationToken ct)
    {
        var queryParams = context.Request.Url?.Query ?? string.Empty;
        var uploadId = ExtractQueryParam(queryParams, "uploadId");

        if (string.IsNullOrEmpty(uploadId))
        {
            _writer.WriteErrorResponse(context.Response, 400, "InvalidArgument", "Missing uploadId parameter.");
            return;
        }

        await AbortMultipartUploadAsync(bucket, key, uploadId, ct).ConfigureAwait(false);
        _writer.WriteNoContentResponse(context.Response);
    }

    private async Task HandleCopyObject(HttpListenerContext context, string destBucket, string destKey, CancellationToken ct)
    {
        var request = _parser.ParseCopyObject(context.Request, destBucket, destKey);
        var response = await CopyObjectAsync(request, ct).ConfigureAwait(false);
        _writer.WriteCopyObjectResponse(context.Response, response);
    }

    #endregion

    #region Private Helpers

    private async Task<S3AuthResult> AuthenticateRequest(HttpListenerRequest request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>();
        for (int i = 0; i < request.Headers.Count; i++)
        {
            var headerName = request.Headers.GetKey(i);
            if (headerName != null)
            {
                headers[headerName] = request.Headers[headerName] ?? string.Empty;
            }
        }

        var authContext = new S3AuthContext
        {
            HttpMethod = request.HttpMethod,
            Path = request.Url?.AbsolutePath ?? "/",
            QueryString = request.Url?.Query ?? string.Empty,
            Headers = headers,
            AuthorizationHeader = request.Headers["Authorization"]
        };

        return await _options.AuthProvider!.AuthenticateAsync(authContext, ct).ConfigureAwait(false);
    }

    private void EnsureBucketExists(string bucketName)
    {
        if (!_bucketRegistry.ContainsKey(bucketName))
            throw new KeyNotFoundException(
                $"NoSuchBucket: The specified bucket '{bucketName}' does not exist. " +
                "Create the bucket with CreateBucketAsync before accessing it.");
    }

    // Continuation token signing key — derived per server instance.
    // In a clustered deployment, use a shared secret from configuration.
    private static readonly byte[] _tokenSigningKey = RandomNumberGenerator.GetBytes(32);

    private static string CreateContinuationToken(int offset)
    {
        // Encode offset as 4-byte big-endian and sign with HMACSHA256 to prevent client tampering.
        var payload = BitConverter.GetBytes(offset);
        if (BitConverter.IsLittleEndian) Array.Reverse(payload);
        using var hmac = new HMACSHA256(_tokenSigningKey);
        var sig = hmac.ComputeHash(payload);
        // Token = Base64(offset_bytes + sig_bytes)
        var combined = new byte[payload.Length + sig.Length];
        payload.CopyTo(combined, 0);
        sig.CopyTo(combined, payload.Length);
        return Convert.ToBase64String(combined);
    }

    private static int DecodeAndVerifyContinuationToken(string token)
    {
        try
        {
            var combined = Convert.FromBase64String(token);
            if (combined.Length < 4 + 32)
                return 0; // malformed token, start from beginning
            var payload = combined[..4];
            var sig = combined[4..];
            using var hmac = new HMACSHA256(_tokenSigningKey);
            var expected = hmac.ComputeHash(payload);
            if (!sig.SequenceEqual(expected))
                return 0; // invalid signature, restart from beginning
            if (BitConverter.IsLittleEndian) Array.Reverse(payload);
            return BitConverter.ToInt32(payload);
        }
        catch
        {
            return 0; // invalid token, start from beginning
        }
    }

    private static string ComputeETag(byte[] data)
    {
        // Use SHA-256 instead of MD5: MD5 is cryptographically broken and unsuitable
        // for integrity verification. Truncate to 128 bits (16 bytes) to keep ETags compact
        // while maintaining collision resistance appropriate for object identity.
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static string? ExtractQueryParam(string queryString, string paramName)
    {
        if (string.IsNullOrEmpty(queryString))
            return null;

        var qs = queryString.TrimStart('?');
        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex >= 0)
            {
                var k = Uri.UnescapeDataString(pair[..eqIndex]);
                if (k.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
                }
            }
        }

        return null;
    }

    #endregion
}
