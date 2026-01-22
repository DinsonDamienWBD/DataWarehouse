// =============================================================================
// SMB TIER FEATURES - Network/Server Storage Implementation
// Production-ready implementations for enterprise network storage
// =============================================================================

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.SDK.Infrastructure;

#region 1. S3-Compatible API with Real XML Parsing

/// <summary>
/// Production-ready S3-compatible API server implementation.
/// Provides full S3 API compatibility with XML request/response parsing.
/// </summary>
public sealed class S3CompatibleApiServer : IAsyncDisposable
{
    private readonly IS3StorageBackend _storageBackend;
    private readonly S3ApiConfiguration _configuration;
    private readonly IS3AuthenticationProvider _authProvider;
    private readonly ConcurrentDictionary<string, MultipartUploadSession> _multipartUploads;
    private readonly ConcurrentDictionary<string, BucketVersioningConfig> _versioningConfigs;
    private readonly ConcurrentDictionary<string, BucketPolicy> _bucketPolicies;
    private readonly ConcurrentDictionary<string, BucketAcl> _bucketAcls;
    private readonly SemaphoreSlim _operationLock;
    private readonly IMetricsProvider? _metrics;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the S3-compatible API server.
    /// </summary>
    /// <param name="storageBackend">The storage backend for persisting objects.</param>
    /// <param name="configuration">Server configuration settings.</param>
    /// <param name="authProvider">Authentication provider for request validation.</param>
    /// <param name="metrics">Optional metrics provider for monitoring.</param>
    public S3CompatibleApiServer(
        IS3StorageBackend storageBackend,
        S3ApiConfiguration configuration,
        IS3AuthenticationProvider authProvider,
        IMetricsProvider? metrics = null)
    {
        _storageBackend = storageBackend ?? throw new ArgumentNullException(nameof(storageBackend));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _metrics = metrics;
        _multipartUploads = new ConcurrentDictionary<string, MultipartUploadSession>();
        _versioningConfigs = new ConcurrentDictionary<string, BucketVersioningConfig>();
        _bucketPolicies = new ConcurrentDictionary<string, BucketPolicy>();
        _bucketAcls = new ConcurrentDictionary<string, BucketAcl>();
        _operationLock = new SemaphoreSlim(configuration.MaxConcurrentOperations, configuration.MaxConcurrentOperations);
    }

    #region Bucket Operations

    /// <summary>
    /// Lists all buckets owned by the authenticated user.
    /// </summary>
    /// <param name="request">The list buckets request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML response containing bucket list.</returns>
    public async Task<S3Response> ListBucketsAsync(S3Request request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.list_buckets");

            var buckets = await _storageBackend.ListBucketsAsync(authResult.UserId, cancellationToken);

            var xml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(S3Namespace.S3 + "ListAllMyBucketsResult",
                    new XElement(S3Namespace.S3 + "Owner",
                        new XElement(S3Namespace.S3 + "ID", authResult.UserId),
                        new XElement(S3Namespace.S3 + "DisplayName", authResult.DisplayName)
                    ),
                    new XElement(S3Namespace.S3 + "Buckets",
                        buckets.Select(b => new XElement(S3Namespace.S3 + "Bucket",
                            new XElement(S3Namespace.S3 + "Name", b.Name),
                            new XElement(S3Namespace.S3 + "CreationDate", b.CreationDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                        ))
                    )
                )
            );

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/xml",
                Body = xml.ToString(),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Creates a new bucket with the specified name.
    /// </summary>
    /// <param name="request">The create bucket request.</param>
    /// <param name="bucketName">Name of the bucket to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> CreateBucketAsync(S3Request request, string bucketName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsValidBucketName(bucketName))
        {
            return CreateErrorResponse(S3ErrorCode.InvalidBucketName, "The specified bucket name is not valid.", request.RequestId);
        }

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.create_bucket");

            var exists = await _storageBackend.BucketExistsAsync(bucketName, cancellationToken);
            if (exists)
            {
                return CreateErrorResponse(S3ErrorCode.BucketAlreadyExists, "The requested bucket name is not available.", request.RequestId);
            }

            // Parse location constraint from request body if present
            string? locationConstraint = null;
            if (!string.IsNullOrEmpty(request.Body))
            {
                var doc = XDocument.Parse(request.Body);
                locationConstraint = doc.Root?.Element(S3Namespace.S3 + "LocationConstraint")?.Value;
            }

            var bucketInfo = new S3BucketInfo
            {
                Name = bucketName,
                OwnerId = authResult.UserId,
                CreationDate = DateTime.UtcNow,
                Region = locationConstraint ?? _configuration.DefaultRegion
            };

            await _storageBackend.CreateBucketAsync(bucketInfo, cancellationToken);

            // Initialize default ACL
            _bucketAcls[bucketName] = new BucketAcl
            {
                OwnerId = authResult.UserId,
                Grants = new List<S3Grant>
                {
                    new S3Grant
                    {
                        Grantee = new S3Grantee { Type = GranteeType.CanonicalUser, Id = authResult.UserId },
                        Permission = S3Permission.FullControl
                    }
                }
            };

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                Headers = new Dictionary<string, string>
                {
                    ["Location"] = $"/{bucketName}"
                },
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Deletes an existing bucket.
    /// </summary>
    /// <param name="request">The delete bucket request.</param>
    /// <param name="bucketName">Name of the bucket to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> DeleteBucketAsync(S3Request request, string bucketName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.delete_bucket");

            var exists = await _storageBackend.BucketExistsAsync(bucketName, cancellationToken);
            if (!exists)
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchBucket, "The specified bucket does not exist.", request.RequestId);
            }

            // Check if bucket is empty
            var objects = await _storageBackend.ListObjectsAsync(bucketName, new ListObjectsRequest { MaxKeys = 1 }, cancellationToken);
            if (objects.Objects.Count > 0)
            {
                return CreateErrorResponse(S3ErrorCode.BucketNotEmpty, "The bucket you tried to delete is not empty.", request.RequestId);
            }

            // Check bucket ownership
            var bucketInfo = await _storageBackend.GetBucketInfoAsync(bucketName, cancellationToken);
            if (bucketInfo?.OwnerId != authResult.UserId && !authResult.IsAdmin)
            {
                return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
            }

            await _storageBackend.DeleteBucketAsync(bucketName, cancellationToken);
            _bucketAcls.TryRemove(bucketName, out _);
            _bucketPolicies.TryRemove(bucketName, out _);
            _versioningConfigs.TryRemove(bucketName, out _);

            return new S3Response
            {
                StatusCode = HttpStatusCode.NoContent,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    #endregion

    #region Object Operations

    /// <summary>
    /// Uploads an object to the specified bucket.
    /// </summary>
    /// <param name="request">The put object request.</param>
    /// <param name="bucketName">Name of the target bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="data">Object data stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response with ETag.</returns>
    public async Task<S3Response> PutObjectAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        Stream data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        if (!await CheckBucketAccessAsync(bucketName, authResult.UserId, S3Permission.Write, cancellationToken))
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.put_object");

            var exists = await _storageBackend.BucketExistsAsync(bucketName, cancellationToken);
            if (!exists)
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchBucket, "The specified bucket does not exist.", request.RequestId);
            }

            // Calculate MD5 hash for ETag
            using var md5 = MD5.Create();
            using var memStream = new MemoryStream();
            await data.CopyToAsync(memStream, cancellationToken);
            var dataBytes = memStream.ToArray();
            var hash = md5.ComputeHash(dataBytes);
            var etag = $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";

            // Handle versioning
            string? versionId = null;
            if (_versioningConfigs.TryGetValue(bucketName, out var versionConfig) && versionConfig.Status == VersioningStatus.Enabled)
            {
                versionId = GenerateVersionId();
            }

            // Extract metadata from headers
            var metadata = new Dictionary<string, string>();
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)))
            {
                metadata[header.Key["x-amz-meta-".Length..]] = header.Value;
            }

            var objectInfo = new S3ObjectInfo
            {
                BucketName = bucketName,
                Key = objectKey,
                Size = dataBytes.Length,
                ETag = etag,
                LastModified = DateTime.UtcNow,
                ContentType = request.Headers.GetValueOrDefault("Content-Type", "application/octet-stream"),
                VersionId = versionId,
                Metadata = metadata,
                StorageClass = request.Headers.GetValueOrDefault("x-amz-storage-class", "STANDARD")
            };

            memStream.Position = 0;
            await _storageBackend.PutObjectAsync(objectInfo, new MemoryStream(dataBytes), cancellationToken);

            var responseHeaders = new Dictionary<string, string>
            {
                ["ETag"] = etag
            };

            if (versionId != null)
            {
                responseHeaders["x-amz-version-id"] = versionId;
            }

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                Headers = responseHeaders,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Retrieves an object from the specified bucket.
    /// </summary>
    /// <param name="request">The get object request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="versionId">Optional version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response with object data.</returns>
    public async Task<S3Response> GetObjectAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        if (!await CheckBucketAccessAsync(bucketName, authResult.UserId, S3Permission.Read, cancellationToken))
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.get_object");

            var objectInfo = await _storageBackend.GetObjectInfoAsync(bucketName, objectKey, versionId, cancellationToken);
            if (objectInfo == null)
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchKey, "The specified key does not exist.", request.RequestId);
            }

            // Handle conditional requests
            if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == objectInfo.ETag)
            {
                return new S3Response
                {
                    StatusCode = HttpStatusCode.NotModified,
                    RequestId = request.RequestId
                };
            }

            if (request.Headers.TryGetValue("If-Modified-Since", out var ifModifiedSince))
            {
                if (DateTime.TryParse(ifModifiedSince, out var modifiedSince) && objectInfo.LastModified <= modifiedSince)
                {
                    return new S3Response
                    {
                        StatusCode = HttpStatusCode.NotModified,
                        RequestId = request.RequestId
                    };
                }
            }

            // Handle range requests
            long? rangeStart = null, rangeEnd = null;
            if (request.Headers.TryGetValue("Range", out var rangeHeader))
            {
                var rangeMatch = Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
                if (rangeMatch.Success)
                {
                    if (!string.IsNullOrEmpty(rangeMatch.Groups[1].Value))
                        rangeStart = long.Parse(rangeMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(rangeMatch.Groups[2].Value))
                        rangeEnd = long.Parse(rangeMatch.Groups[2].Value);
                }
            }

            var dataStream = await _storageBackend.GetObjectDataAsync(bucketName, objectKey, versionId, rangeStart, rangeEnd, cancellationToken);
            if (dataStream == null)
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchKey, "The specified key does not exist.", request.RequestId);
            }

            using var memStream = new MemoryStream();
            await dataStream.CopyToAsync(memStream, cancellationToken);

            var responseHeaders = new Dictionary<string, string>
            {
                ["ETag"] = objectInfo.ETag,
                ["Last-Modified"] = objectInfo.LastModified.ToString("R"),
                ["Content-Type"] = objectInfo.ContentType,
                ["Content-Length"] = memStream.Length.ToString()
            };

            if (objectInfo.VersionId != null)
            {
                responseHeaders["x-amz-version-id"] = objectInfo.VersionId;
            }

            foreach (var meta in objectInfo.Metadata)
            {
                responseHeaders[$"x-amz-meta-{meta.Key}"] = meta.Value;
            }

            return new S3Response
            {
                StatusCode = rangeStart.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK,
                Headers = responseHeaders,
                BodyBytes = memStream.ToArray(),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Deletes an object from the specified bucket.
    /// </summary>
    /// <param name="request">The delete object request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="versionId">Optional version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> DeleteObjectAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        if (!await CheckBucketAccessAsync(bucketName, authResult.UserId, S3Permission.Write, cancellationToken))
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.delete_object");

            // Handle versioning - create delete marker if versioning is enabled
            string? deleteMarkerVersionId = null;
            if (_versioningConfigs.TryGetValue(bucketName, out var versionConfig) &&
                versionConfig.Status == VersioningStatus.Enabled &&
                versionId == null)
            {
                deleteMarkerVersionId = GenerateVersionId();
                await _storageBackend.CreateDeleteMarkerAsync(bucketName, objectKey, deleteMarkerVersionId, cancellationToken);
            }
            else
            {
                await _storageBackend.DeleteObjectAsync(bucketName, objectKey, versionId, cancellationToken);
            }

            var responseHeaders = new Dictionary<string, string>();
            if (deleteMarkerVersionId != null)
            {
                responseHeaders["x-amz-delete-marker"] = "true";
                responseHeaders["x-amz-version-id"] = deleteMarkerVersionId;
            }

            return new S3Response
            {
                StatusCode = HttpStatusCode.NoContent,
                Headers = responseHeaders,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Retrieves metadata for an object without returning the object itself.
    /// </summary>
    /// <param name="request">The head object request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="versionId">Optional version ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response with object metadata.</returns>
    public async Task<S3Response> HeadObjectAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.head_object");

            var objectInfo = await _storageBackend.GetObjectInfoAsync(bucketName, objectKey, versionId, cancellationToken);
            if (objectInfo == null)
            {
                return new S3Response
                {
                    StatusCode = HttpStatusCode.NotFound,
                    RequestId = request.RequestId
                };
            }

            var responseHeaders = new Dictionary<string, string>
            {
                ["ETag"] = objectInfo.ETag,
                ["Last-Modified"] = objectInfo.LastModified.ToString("R"),
                ["Content-Type"] = objectInfo.ContentType,
                ["Content-Length"] = objectInfo.Size.ToString(),
                ["x-amz-storage-class"] = objectInfo.StorageClass
            };

            if (objectInfo.VersionId != null)
            {
                responseHeaders["x-amz-version-id"] = objectInfo.VersionId;
            }

            foreach (var meta in objectInfo.Metadata)
            {
                responseHeaders[$"x-amz-meta-{meta.Key}"] = meta.Value;
            }

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                Headers = responseHeaders,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Lists objects in a bucket with support for pagination, prefix, and delimiter.
    /// </summary>
    /// <param name="request">The list objects request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="listRequest">List parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML response containing object list.</returns>
    public async Task<S3Response> ListObjectsV2Async(
        S3Request request,
        string bucketName,
        ListObjectsRequest listRequest,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        if (!await CheckBucketAccessAsync(bucketName, authResult.UserId, S3Permission.Read, cancellationToken))
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.list_objects_v2");

            var exists = await _storageBackend.BucketExistsAsync(bucketName, cancellationToken);
            if (!exists)
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchBucket, "The specified bucket does not exist.", request.RequestId);
            }

            var result = await _storageBackend.ListObjectsAsync(bucketName, listRequest, cancellationToken);

            var xml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(S3Namespace.S3 + "ListBucketResult",
                    new XElement(S3Namespace.S3 + "Name", bucketName),
                    listRequest.Prefix != null ? new XElement(S3Namespace.S3 + "Prefix", listRequest.Prefix) : null,
                    new XElement(S3Namespace.S3 + "MaxKeys", listRequest.MaxKeys),
                    new XElement(S3Namespace.S3 + "IsTruncated", result.IsTruncated.ToString().ToLower()),
                    new XElement(S3Namespace.S3 + "KeyCount", result.Objects.Count),
                    result.ContinuationToken != null ? new XElement(S3Namespace.S3 + "ContinuationToken", result.ContinuationToken) : null,
                    result.NextContinuationToken != null ? new XElement(S3Namespace.S3 + "NextContinuationToken", result.NextContinuationToken) : null,
                    listRequest.Delimiter != null ? new XElement(S3Namespace.S3 + "Delimiter", listRequest.Delimiter) : null,
                    result.Objects.Select(obj => new XElement(S3Namespace.S3 + "Contents",
                        new XElement(S3Namespace.S3 + "Key", obj.Key),
                        new XElement(S3Namespace.S3 + "LastModified", obj.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                        new XElement(S3Namespace.S3 + "ETag", obj.ETag),
                        new XElement(S3Namespace.S3 + "Size", obj.Size),
                        new XElement(S3Namespace.S3 + "StorageClass", obj.StorageClass)
                    )),
                    result.CommonPrefixes.Select(prefix => new XElement(S3Namespace.S3 + "CommonPrefixes",
                        new XElement(S3Namespace.S3 + "Prefix", prefix)
                    ))
                )
            );

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/xml",
                Body = xml.ToString(),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    #endregion

    #region Multipart Upload Operations

    /// <summary>
    /// Initiates a multipart upload.
    /// </summary>
    /// <param name="request">The initiate multipart upload request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML response with upload ID.</returns>
    public async Task<S3Response> InitiateMultipartUploadAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.initiate_multipart_upload");

            var uploadId = GenerateUploadId();
            var session = new MultipartUploadSession
            {
                UploadId = uploadId,
                BucketName = bucketName,
                ObjectKey = objectKey,
                InitiatedAt = DateTime.UtcNow,
                OwnerId = authResult.UserId,
                ContentType = request.Headers.GetValueOrDefault("Content-Type", "application/octet-stream"),
                Metadata = request.Headers
                    .Where(h => h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(h => h.Key["x-amz-meta-".Length..], h => h.Value)
            };

            _multipartUploads[uploadId] = session;

            var xml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(S3Namespace.S3 + "InitiateMultipartUploadResult",
                    new XElement(S3Namespace.S3 + "Bucket", bucketName),
                    new XElement(S3Namespace.S3 + "Key", objectKey),
                    new XElement(S3Namespace.S3 + "UploadId", uploadId)
                )
            );

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/xml",
                Body = xml.ToString(),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Uploads a part for a multipart upload.
    /// </summary>
    /// <param name="request">The upload part request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="uploadId">Upload ID from InitiateMultipartUpload.</param>
    /// <param name="partNumber">Part number (1-10000).</param>
    /// <param name="data">Part data stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response with ETag for the part.</returns>
    public async Task<S3Response> UploadPartAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        string uploadId,
        int partNumber,
        Stream data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (partNumber < 1 || partNumber > 10000)
        {
            return CreateErrorResponse(S3ErrorCode.InvalidArgument, "Part number must be between 1 and 10000.", request.RequestId);
        }

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.upload_part");

            if (!_multipartUploads.TryGetValue(uploadId, out var session))
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchUpload, "The specified upload does not exist.", request.RequestId);
            }

            if (session.BucketName != bucketName || session.ObjectKey != objectKey)
            {
                return CreateErrorResponse(S3ErrorCode.InvalidArgument, "Bucket or key does not match upload.", request.RequestId);
            }

            using var memStream = new MemoryStream();
            await data.CopyToAsync(memStream, cancellationToken);
            var partData = memStream.ToArray();

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(partData);
            var etag = $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";

            var partInfo = new MultipartUploadPart
            {
                PartNumber = partNumber,
                ETag = etag,
                Size = partData.Length,
                Data = partData
            };

            session.Parts[partNumber] = partInfo;

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                Headers = new Dictionary<string, string>
                {
                    ["ETag"] = etag
                },
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Completes a multipart upload by assembling previously uploaded parts.
    /// </summary>
    /// <param name="request">The complete multipart upload request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="uploadId">Upload ID from InitiateMultipartUpload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML response with the completed object details.</returns>
    public async Task<S3Response> CompleteMultipartUploadAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.complete_multipart_upload");

            if (!_multipartUploads.TryGetValue(uploadId, out var session))
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchUpload, "The specified upload does not exist.", request.RequestId);
            }

            // Parse the completion request to get part list
            var doc = XDocument.Parse(request.Body);
            var parts = doc.Root?.Elements(S3Namespace.S3 + "Part")
                .Select(p => new
                {
                    PartNumber = int.Parse(p.Element(S3Namespace.S3 + "PartNumber")?.Value ?? "0"),
                    ETag = p.Element(S3Namespace.S3 + "ETag")?.Value
                })
                .OrderBy(p => p.PartNumber)
                .ToList() ?? new();

            // Validate parts
            foreach (var part in parts)
            {
                if (!session.Parts.TryGetValue(part.PartNumber, out var uploadedPart))
                {
                    return CreateErrorResponse(S3ErrorCode.InvalidPart, $"Part {part.PartNumber} not found.", request.RequestId);
                }

                if (uploadedPart.ETag != part.ETag)
                {
                    return CreateErrorResponse(S3ErrorCode.InvalidPart, $"Part {part.PartNumber} ETag does not match.", request.RequestId);
                }
            }

            // Assemble the object
            using var assembledStream = new MemoryStream();
            foreach (var part in parts)
            {
                var partData = session.Parts[part.PartNumber].Data;
                await assembledStream.WriteAsync(partData, cancellationToken);
            }

            assembledStream.Position = 0;

            // Calculate final ETag (MD5 of part ETags concatenated + "-" + part count)
            using var md5 = MD5.Create();
            var combinedHash = new List<byte>();
            foreach (var part in parts)
            {
                var partEtag = session.Parts[part.PartNumber].ETag.Trim('"');
                combinedHash.AddRange(Convert.FromHexString(partEtag));
            }
            var finalHash = md5.ComputeHash(combinedHash.ToArray());
            var etag = $"\"{Convert.ToHexString(finalHash).ToLowerInvariant()}-{parts.Count}\"";

            // Handle versioning
            string? versionId = null;
            if (_versioningConfigs.TryGetValue(bucketName, out var versionConfig) && versionConfig.Status == VersioningStatus.Enabled)
            {
                versionId = GenerateVersionId();
            }

            var objectInfo = new S3ObjectInfo
            {
                BucketName = bucketName,
                Key = objectKey,
                Size = assembledStream.Length,
                ETag = etag,
                LastModified = DateTime.UtcNow,
                ContentType = session.ContentType,
                VersionId = versionId,
                Metadata = session.Metadata,
                StorageClass = "STANDARD"
            };

            await _storageBackend.PutObjectAsync(objectInfo, assembledStream, cancellationToken);

            // Cleanup
            _multipartUploads.TryRemove(uploadId, out _);

            var xml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(S3Namespace.S3 + "CompleteMultipartUploadResult",
                    new XElement(S3Namespace.S3 + "Location", $"http://{_configuration.Hostname}/{bucketName}/{objectKey}"),
                    new XElement(S3Namespace.S3 + "Bucket", bucketName),
                    new XElement(S3Namespace.S3 + "Key", objectKey),
                    new XElement(S3Namespace.S3 + "ETag", etag)
                )
            );

            var response = new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/xml",
                Body = xml.ToString(),
                RequestId = request.RequestId
            };

            if (versionId != null)
            {
                response.Headers["x-amz-version-id"] = versionId;
            }

            return response;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Aborts a multipart upload.
    /// </summary>
    /// <param name="request">The abort multipart upload request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="uploadId">Upload ID from InitiateMultipartUpload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> AbortMultipartUploadAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.abort_multipart_upload");

            if (!_multipartUploads.TryRemove(uploadId, out var session))
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchUpload, "The specified upload does not exist.", request.RequestId);
            }

            return new S3Response
            {
                StatusCode = HttpStatusCode.NoContent,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    #endregion

    #region Versioning Operations

    /// <summary>
    /// Configures versioning for a bucket.
    /// </summary>
    /// <param name="request">The put bucket versioning request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> PutBucketVersioningAsync(
        S3Request request,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var doc = XDocument.Parse(request.Body);
            var statusElement = doc.Root?.Element(S3Namespace.S3 + "Status");

            if (statusElement == null)
            {
                return CreateErrorResponse(S3ErrorCode.MalformedXML, "Versioning status not specified.", request.RequestId);
            }

            var status = statusElement.Value switch
            {
                "Enabled" => VersioningStatus.Enabled,
                "Suspended" => VersioningStatus.Suspended,
                _ => VersioningStatus.Disabled
            };

            _versioningConfigs[bucketName] = new BucketVersioningConfig
            {
                Status = status,
                MfaDelete = doc.Root?.Element(S3Namespace.S3 + "MfaDelete")?.Value == "Enabled"
            };

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Gets the versioning configuration for a bucket.
    /// </summary>
    /// <param name="request">The get bucket versioning request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML response with versioning configuration.</returns>
    public async Task<S3Response> GetBucketVersioningAsync(
        S3Request request,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var config = _versioningConfigs.GetValueOrDefault(bucketName, new BucketVersioningConfig());

            var xml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(S3Namespace.S3 + "VersioningConfiguration",
                    config.Status != VersioningStatus.Disabled
                        ? new XElement(S3Namespace.S3 + "Status", config.Status.ToString())
                        : null,
                    config.MfaDelete
                        ? new XElement(S3Namespace.S3 + "MfaDelete", "Enabled")
                        : null
                )
            );

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/xml",
                Body = xml.ToString(),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    #endregion

    #region Pre-signed URLs

    /// <summary>
    /// Generates a pre-signed URL for object access.
    /// </summary>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="method">HTTP method (GET, PUT).</param>
    /// <param name="expiration">URL expiration time.</param>
    /// <param name="additionalHeaders">Additional headers to sign.</param>
    /// <returns>Pre-signed URL string.</returns>
    public string GeneratePresignedUrl(
        string bucketName,
        string objectKey,
        HttpMethod method,
        TimeSpan expiration,
        Dictionary<string, string>? additionalHeaders = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var expirationTime = DateTimeOffset.UtcNow.Add(expiration);
        var timestamp = DateTimeOffset.UtcNow;
        var dateStamp = timestamp.ToString("yyyyMMdd");
        var amzDate = timestamp.ToString("yyyyMMddTHHmmssZ");

        var credentialScope = $"{dateStamp}/{_configuration.DefaultRegion}/s3/aws4_request";
        var canonicalUri = $"/{bucketName}/{Uri.EscapeDataString(objectKey)}";

        var queryParams = new SortedDictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = Uri.EscapeDataString($"{_configuration.AccessKeyId}/{credentialScope}"),
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = ((int)expiration.TotalSeconds).ToString(),
            ["X-Amz-SignedHeaders"] = "host"
        };

        if (additionalHeaders != null)
        {
            foreach (var header in additionalHeaders)
            {
                queryParams[Uri.EscapeDataString(header.Key)] = Uri.EscapeDataString(header.Value);
            }
        }

        var canonicalQueryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var canonicalHeaders = $"host:{_configuration.Hostname}\n";
        var signedHeaders = "host";

        var canonicalRequest = $"{method.Method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\nUNSIGNED-PAYLOAD";

        using var sha256 = SHA256.Create();
        var canonicalRequestHash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();

        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

        var signature = CalculateSignature(stringToSign, dateStamp, _configuration.DefaultRegion);
        queryParams["X-Amz-Signature"] = signature;

        var finalQueryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        return $"https://{_configuration.Hostname}{canonicalUri}?{finalQueryString}";
    }

    /// <summary>
    /// Validates a pre-signed URL.
    /// </summary>
    /// <param name="url">The pre-signed URL to validate.</param>
    /// <returns>True if the URL is valid and not expired.</returns>
    public bool ValidatePresignedUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);

            var amzDate = queryParams["X-Amz-Date"];
            var expires = queryParams["X-Amz-Expires"];

            if (string.IsNullOrEmpty(amzDate) || string.IsNullOrEmpty(expires))
            {
                return false;
            }

            var signedAt = DateTime.ParseExact(amzDate, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var expiresIn = int.Parse(expires);
            var expirationTime = signedAt.AddSeconds(expiresIn);

            return DateTime.UtcNow < expirationTime;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Bucket Policies and ACLs

    /// <summary>
    /// Sets the bucket policy.
    /// </summary>
    /// <param name="request">The put bucket policy request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> PutBucketPolicyAsync(
        S3Request request,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var policy = JsonSerializer.Deserialize<BucketPolicy>(request.Body);
            if (policy == null)
            {
                return CreateErrorResponse(S3ErrorCode.MalformedPolicy, "Invalid policy document.", request.RequestId);
            }

            _bucketPolicies[bucketName] = policy;

            return new S3Response
            {
                StatusCode = HttpStatusCode.NoContent,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Gets the bucket policy.
    /// </summary>
    /// <param name="request">The get bucket policy request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON response with bucket policy.</returns>
    public async Task<S3Response> GetBucketPolicyAsync(
        S3Request request,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_bucketPolicies.TryGetValue(bucketName, out var policy))
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchBucketPolicy, "The bucket policy does not exist.", request.RequestId);
            }

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(policy),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Sets the bucket ACL.
    /// </summary>
    /// <param name="request">The put bucket ACL request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response indicating success or failure.</returns>
    public async Task<S3Response> PutBucketAclAsync(
        S3Request request,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            // Check for canned ACL in header
            if (request.Headers.TryGetValue("x-amz-acl", out var cannedAcl))
            {
                _bucketAcls[bucketName] = CreateCannedAcl(cannedAcl, authResult.UserId);
            }
            else
            {
                // Parse ACL from body
                var doc = XDocument.Parse(request.Body);
                var acl = ParseAclFromXml(doc);
                _bucketAcls[bucketName] = acl;
            }

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Gets the bucket ACL.
    /// </summary>
    /// <param name="request">The get bucket ACL request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML response with bucket ACL.</returns>
    public async Task<S3Response> GetBucketAclAsync(
        S3Request request,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_bucketAcls.TryGetValue(bucketName, out var acl))
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchBucket, "The specified bucket does not exist.", request.RequestId);
            }

            var xml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(S3Namespace.S3 + "AccessControlPolicy",
                    new XElement(S3Namespace.S3 + "Owner",
                        new XElement(S3Namespace.S3 + "ID", acl.OwnerId),
                        new XElement(S3Namespace.S3 + "DisplayName", acl.OwnerId)
                    ),
                    new XElement(S3Namespace.S3 + "AccessControlList",
                        acl.Grants.Select(g => new XElement(S3Namespace.S3 + "Grant",
                            new XElement(S3Namespace.S3 + "Grantee",
                                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                                new XAttribute("{http://www.w3.org/2001/XMLSchema-instance}type", g.Grantee.Type.ToString()),
                                g.Grantee.Type == GranteeType.CanonicalUser
                                    ? new object[] {
                                        new XElement(S3Namespace.S3 + "ID", g.Grantee.Id),
                                        new XElement(S3Namespace.S3 + "DisplayName", g.Grantee.DisplayName ?? g.Grantee.Id)
                                    }
                                    : new object[] {
                                        new XElement(S3Namespace.S3 + "URI", g.Grantee.Uri)
                                    }
                            ),
                            new XElement(S3Namespace.S3 + "Permission", g.Permission.ToString().ToUpperInvariant())
                        ))
                    )
                )
            );

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/xml",
                Body = xml.ToString(),
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    #endregion

    #region S3 Select

    /// <summary>
    /// Executes SQL queries against object content.
    /// </summary>
    /// <param name="request">The select object content request.</param>
    /// <param name="bucketName">Name of the bucket.</param>
    /// <param name="objectKey">Key of the object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3 response with query results.</returns>
    public async Task<S3Response> SelectObjectContentAsync(
        S3Request request,
        string bucketName,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authResult = await _authProvider.AuthenticateAsync(request, cancellationToken);
        if (!authResult.IsAuthenticated)
        {
            return CreateErrorResponse(S3ErrorCode.AccessDenied, "Access Denied", request.RequestId);
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _metrics?.IncrementCounter("s3.select_object_content");

            // Parse select request
            var doc = XDocument.Parse(request.Body);
            var expression = doc.Root?.Element(S3Namespace.S3 + "Expression")?.Value;
            var expressionType = doc.Root?.Element(S3Namespace.S3 + "ExpressionType")?.Value;
            var inputSerialization = doc.Root?.Element(S3Namespace.S3 + "InputSerialization");
            var outputSerialization = doc.Root?.Element(S3Namespace.S3 + "OutputSerialization");

            if (string.IsNullOrEmpty(expression) || expressionType != "SQL")
            {
                return CreateErrorResponse(S3ErrorCode.InvalidArgument, "Invalid expression or expression type.", request.RequestId);
            }

            // Get object data
            var dataStream = await _storageBackend.GetObjectDataAsync(bucketName, objectKey, null, null, null, cancellationToken);
            if (dataStream == null)
            {
                return CreateErrorResponse(S3ErrorCode.NoSuchKey, "The specified key does not exist.", request.RequestId);
            }

            using var reader = new StreamReader(dataStream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            // Execute SQL query
            var result = ExecuteS3SelectQuery(expression, content, inputSerialization, outputSerialization);

            return new S3Response
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/octet-stream",
                Body = result,
                RequestId = request.RequestId
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private string ExecuteS3SelectQuery(string expression, string content, XElement? inputSerialization, XElement? outputSerialization)
    {
        // Parse input format
        var inputFormat = inputSerialization?.Element(S3Namespace.S3 + "CSV") != null ? "CSV" :
                          inputSerialization?.Element(S3Namespace.S3 + "JSON") != null ? "JSON" : "CSV";

        var fieldDelimiter = inputSerialization?.Element(S3Namespace.S3 + "CSV")?.Element(S3Namespace.S3 + "FieldDelimiter")?.Value ?? ",";
        var recordDelimiter = inputSerialization?.Element(S3Namespace.S3 + "CSV")?.Element(S3Namespace.S3 + "RecordDelimiter")?.Value ?? "\n";
        var hasHeader = inputSerialization?.Element(S3Namespace.S3 + "CSV")?.Element(S3Namespace.S3 + "FileHeaderInfo")?.Value == "USE";

        // Parse the SQL expression
        var selectMatch = Regex.Match(expression, @"SELECT\s+(.+?)\s+FROM\s+S3Object(?:\s+WHERE\s+(.+))?", RegexOptions.IgnoreCase);
        if (!selectMatch.Success)
        {
            return string.Empty;
        }

        var selectClause = selectMatch.Groups[1].Value.Trim();
        var whereClause = selectMatch.Groups[2].Value.Trim();

        if (inputFormat == "CSV")
        {
            var lines = content.Split(new[] { recordDelimiter }, StringSplitOptions.RemoveEmptyEntries);
            var results = new List<string>();

            string[]? headers = null;
            var startIndex = 0;
            if (hasHeader && lines.Length > 0)
            {
                headers = lines[0].Split(fieldDelimiter[0]);
                startIndex = 1;
            }

            for (int i = startIndex; i < lines.Length; i++)
            {
                var fields = lines[i].Split(fieldDelimiter[0]);
                var record = new Dictionary<string, string>();

                for (int j = 0; j < fields.Length; j++)
                {
                    var key = headers != null && j < headers.Length ? headers[j] : $"_{ j + 1}";
                    record[key] = fields[j];
                }

                // Apply WHERE clause
                if (!string.IsNullOrEmpty(whereClause) && !EvaluateWhereClause(whereClause, record))
                {
                    continue;
                }

                // Apply SELECT clause
                var selectedFields = SelectFields(selectClause, record, fields);
                results.Add(string.Join(fieldDelimiter, selectedFields));
            }

            return string.Join(recordDelimiter, results);
        }
        else if (inputFormat == "JSON")
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(content);
                var results = new List<string>();

                if (jsonDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in jsonDoc.RootElement.EnumerateArray())
                    {
                        var record = new Dictionary<string, string>();
                        foreach (var prop in element.EnumerateObject())
                        {
                            record[prop.Name] = prop.Value.ToString();
                        }

                        if (!string.IsNullOrEmpty(whereClause) && !EvaluateWhereClause(whereClause, record))
                        {
                            continue;
                        }

                        var selectedFields = SelectFieldsFromJson(selectClause, element);
                        results.Add(selectedFields);
                    }
                }

                return string.Join("\n", results);
            }
            catch
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private bool EvaluateWhereClause(string whereClause, Dictionary<string, string> record)
    {
        // Simple WHERE clause evaluation supporting =, <>, >, <, >=, <=, LIKE, AND, OR
        var conditions = Regex.Split(whereClause, @"\s+(AND|OR)\s+", RegexOptions.IgnoreCase);

        bool result = true;
        string logicalOp = "AND";

        for (int i = 0; i < conditions.Length; i++)
        {
            var condition = conditions[i].Trim();

            if (condition.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
                condition.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                logicalOp = condition.ToUpperInvariant();
                continue;
            }

            bool conditionResult = EvaluateCondition(condition, record);

            result = logicalOp == "AND" ? result && conditionResult : result || conditionResult;
        }

        return result;
    }

    private bool EvaluateCondition(string condition, Dictionary<string, string> record)
    {
        var match = Regex.Match(condition, @"(\w+|\*)\s*(=|<>|!=|>=|<=|>|<|LIKE)\s*'?([^']*)'?", RegexOptions.IgnoreCase);
        if (!match.Success) return true;

        var field = match.Groups[1].Value;
        var op = match.Groups[2].Value.ToUpperInvariant();
        var value = match.Groups[3].Value;

        if (!record.TryGetValue(field, out var fieldValue))
        {
            return false;
        }

        return op switch
        {
            "=" => fieldValue == value,
            "<>" or "!=" => fieldValue != value,
            ">" => double.TryParse(fieldValue, out var fv1) && double.TryParse(value, out var v1) && fv1 > v1,
            "<" => double.TryParse(fieldValue, out var fv2) && double.TryParse(value, out var v2) && fv2 < v2,
            ">=" => double.TryParse(fieldValue, out var fv3) && double.TryParse(value, out var v3) && fv3 >= v3,
            "<=" => double.TryParse(fieldValue, out var fv4) && double.TryParse(value, out var v4) && fv4 <= v4,
            "LIKE" => Regex.IsMatch(fieldValue, "^" + Regex.Escape(value).Replace("%", ".*").Replace("_", ".") + "$"),
            _ => true
        };
    }

    private IEnumerable<string> SelectFields(string selectClause, Dictionary<string, string> record, string[] originalFields)
    {
        if (selectClause == "*")
        {
            return originalFields;
        }

        var fields = selectClause.Split(',').Select(f => f.Trim());
        return fields.Select(f => record.GetValueOrDefault(f, string.Empty));
    }

    private string SelectFieldsFromJson(string selectClause, JsonElement element)
    {
        if (selectClause == "*")
        {
            return element.GetRawText();
        }

        var fields = selectClause.Split(',').Select(f => f.Trim());
        var result = new Dictionary<string, JsonElement>();

        foreach (var field in fields)
        {
            if (element.TryGetProperty(field, out var prop))
            {
                result[field] = prop;
            }
        }

        return JsonSerializer.Serialize(result);
    }

    #endregion

    #region Helper Methods

    private static bool IsValidBucketName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 3 || name.Length > 63)
            return false;

        // Must start and end with alphanumeric
        if (!char.IsLetterOrDigit(name[0]) || !char.IsLetterOrDigit(name[^1]))
            return false;

        // Must be lowercase and contain only alphanumerics, hyphens, and periods
        if (!Regex.IsMatch(name, @"^[a-z0-9][a-z0-9\-\.]*[a-z0-9]$"))
            return false;

        // Cannot be formatted as IP address
        if (Regex.IsMatch(name, @"^\d+\.\d+\.\d+\.\d+$"))
            return false;

        // Cannot contain consecutive periods
        if (name.Contains(".."))
            return false;

        return true;
    }

    private S3Response CreateErrorResponse(S3ErrorCode code, string message, string requestId)
    {
        var xml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(S3Namespace.S3 + "Error",
                new XElement(S3Namespace.S3 + "Code", code.ToString()),
                new XElement(S3Namespace.S3 + "Message", message),
                new XElement(S3Namespace.S3 + "RequestId", requestId)
            )
        );

        var statusCode = code switch
        {
            S3ErrorCode.AccessDenied => HttpStatusCode.Forbidden,
            S3ErrorCode.NoSuchBucket or S3ErrorCode.NoSuchKey => HttpStatusCode.NotFound,
            S3ErrorCode.BucketAlreadyExists or S3ErrorCode.BucketNotEmpty => HttpStatusCode.Conflict,
            S3ErrorCode.InvalidBucketName or S3ErrorCode.InvalidArgument or S3ErrorCode.MalformedXML => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError
        };

        return new S3Response
        {
            StatusCode = statusCode,
            ContentType = "application/xml",
            Body = xml.ToString(),
            RequestId = requestId
        };
    }

    private static string GenerateVersionId()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }

    private static string GenerateUploadId()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }

    private string CalculateSignature(string stringToSign, string dateStamp, string region)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + _configuration.SecretAccessKey);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, "s3");
        var kSigning = HmacSha256(kService, "aws4_request");
        return Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private async Task<bool> CheckBucketAccessAsync(string bucketName, string userId, S3Permission requiredPermission, CancellationToken ct)
    {
        // Check bucket ACL
        if (_bucketAcls.TryGetValue(bucketName, out var acl))
        {
            if (acl.OwnerId == userId)
                return true;

            foreach (var grant in acl.Grants)
            {
                if (grant.Grantee.Type == GranteeType.CanonicalUser && grant.Grantee.Id == userId)
                {
                    if (grant.Permission == S3Permission.FullControl || grant.Permission == requiredPermission)
                        return true;
                }
                else if (grant.Grantee.Type == GranteeType.Group && grant.Grantee.Uri == "http://acs.amazonaws.com/groups/global/AllUsers")
                {
                    if (grant.Permission == S3Permission.FullControl || grant.Permission == requiredPermission)
                        return true;
                }
            }
        }

        // Check bucket policy
        if (_bucketPolicies.TryGetValue(bucketName, out var policy))
        {
            foreach (var statement in policy.Statement)
            {
                var principalMatch = statement.Principal == "*" ||
                                    (statement.Principal is string p && p == userId) ||
                                    (statement.Principal is JsonElement je && je.ValueKind == JsonValueKind.Array &&
                                     je.EnumerateArray().Any(e => e.GetString() == userId));

                if (principalMatch)
                {
                    var actionMatch = statement.Action == "*" ||
                                     (statement.Action is string a && MatchesS3Action(a, requiredPermission)) ||
                                     (statement.Action is JsonElement ae && ae.ValueKind == JsonValueKind.Array &&
                                      ae.EnumerateArray().Any(e => MatchesS3Action(e.GetString() ?? "", requiredPermission)));

                    if (actionMatch)
                    {
                        return statement.Effect == "Allow";
                    }
                }
            }
        }

        // Check bucket ownership
        var bucketInfo = await _storageBackend.GetBucketInfoAsync(bucketName, ct);
        return bucketInfo?.OwnerId == userId;
    }

    private static bool MatchesS3Action(string action, S3Permission permission)
    {
        return permission switch
        {
            S3Permission.Read => action is "s3:GetObject" or "s3:*" or "*",
            S3Permission.Write => action is "s3:PutObject" or "s3:DeleteObject" or "s3:*" or "*",
            S3Permission.ReadAcp => action is "s3:GetObjectAcl" or "s3:*" or "*",
            S3Permission.WriteAcp => action is "s3:PutObjectAcl" or "s3:*" or "*",
            S3Permission.FullControl => action is "s3:*" or "*",
            _ => false
        };
    }

    private BucketAcl CreateCannedAcl(string cannedAcl, string ownerId)
    {
        var acl = new BucketAcl
        {
            OwnerId = ownerId,
            Grants = new List<S3Grant>
            {
                new S3Grant
                {
                    Grantee = new S3Grantee { Type = GranteeType.CanonicalUser, Id = ownerId },
                    Permission = S3Permission.FullControl
                }
            }
        };

        switch (cannedAcl.ToLowerInvariant())
        {
            case "public-read":
                acl.Grants.Add(new S3Grant
                {
                    Grantee = new S3Grantee { Type = GranteeType.Group, Uri = "http://acs.amazonaws.com/groups/global/AllUsers" },
                    Permission = S3Permission.Read
                });
                break;

            case "public-read-write":
                acl.Grants.Add(new S3Grant
                {
                    Grantee = new S3Grantee { Type = GranteeType.Group, Uri = "http://acs.amazonaws.com/groups/global/AllUsers" },
                    Permission = S3Permission.Read
                });
                acl.Grants.Add(new S3Grant
                {
                    Grantee = new S3Grantee { Type = GranteeType.Group, Uri = "http://acs.amazonaws.com/groups/global/AllUsers" },
                    Permission = S3Permission.Write
                });
                break;

            case "authenticated-read":
                acl.Grants.Add(new S3Grant
                {
                    Grantee = new S3Grantee { Type = GranteeType.Group, Uri = "http://acs.amazonaws.com/groups/global/AuthenticatedUsers" },
                    Permission = S3Permission.Read
                });
                break;
        }

        return acl;
    }

    private BucketAcl ParseAclFromXml(XDocument doc)
    {
        var acl = new BucketAcl
        {
            OwnerId = doc.Root?.Element(S3Namespace.S3 + "Owner")?.Element(S3Namespace.S3 + "ID")?.Value ?? "",
            Grants = new List<S3Grant>()
        };

        var grantElements = doc.Root?.Element(S3Namespace.S3 + "AccessControlList")?.Elements(S3Namespace.S3 + "Grant");
        if (grantElements != null)
        {
            foreach (var grantElement in grantElements)
            {
                var granteeElement = grantElement.Element(S3Namespace.S3 + "Grantee");
                var permissionElement = grantElement.Element(S3Namespace.S3 + "Permission");

                if (granteeElement != null && permissionElement != null)
                {
                    var granteeType = granteeElement.Attribute("{http://www.w3.org/2001/XMLSchema-instance}type")?.Value;

                    var grant = new S3Grant
                    {
                        Grantee = new S3Grantee
                        {
                            Type = granteeType == "Group" ? GranteeType.Group : GranteeType.CanonicalUser,
                            Id = granteeElement.Element(S3Namespace.S3 + "ID")?.Value,
                            DisplayName = granteeElement.Element(S3Namespace.S3 + "DisplayName")?.Value,
                            Uri = granteeElement.Element(S3Namespace.S3 + "URI")?.Value
                        },
                        Permission = Enum.TryParse<S3Permission>(permissionElement.Value, true, out var perm) ? perm : S3Permission.Read
                    };

                    acl.Grants.Add(grant);
                }
            }
        }

        return acl;
    }

    #endregion

    /// <summary>
    /// Disposes resources used by the S3 API server.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _multipartUploads.Clear();
        _operationLock.Dispose();
        await Task.CompletedTask;
    }
}

#region S3 Types and Interfaces

/// <summary>
/// S3 XML namespace constants.
/// </summary>
public static class S3Namespace
{
    /// <summary>
    /// The S3 XML namespace.
    /// </summary>
    public static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
}

/// <summary>
/// S3 API configuration settings.
/// </summary>
public sealed class S3ApiConfiguration
{
    /// <summary>Gets or sets the server hostname.</summary>
    public string Hostname { get; set; } = "localhost";

    /// <summary>Gets or sets the server port.</summary>
    public int Port { get; set; } = 9000;

    /// <summary>Gets or sets the default region.</summary>
    public string DefaultRegion { get; set; } = "us-east-1";

    /// <summary>Gets or sets the access key ID.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret access key.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum concurrent operations.</summary>
    public int MaxConcurrentOperations { get; set; } = 100;

    /// <summary>Gets or sets the maximum object size in bytes.</summary>
    public long MaxObjectSize { get; set; } = 5L * 1024 * 1024 * 1024; // 5GB

    /// <summary>Gets or sets the maximum part size for multipart uploads.</summary>
    public long MaxPartSize { get; set; } = 5L * 1024 * 1024 * 1024; // 5GB

    /// <summary>Gets or sets the minimum part size for multipart uploads.</summary>
    public long MinPartSize { get; set; } = 5 * 1024 * 1024; // 5MB
}

/// <summary>
/// Represents an S3 request.
/// </summary>
public sealed class S3Request
{
    /// <summary>Gets or sets the HTTP method.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Gets or sets the request path.</summary>
    public string Path { get; set; } = "/";

    /// <summary>Gets or sets the query parameters.</summary>
    public Dictionary<string, string> QueryParams { get; set; } = new();

    /// <summary>Gets or sets the request headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Gets or sets the request body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets the request ID.</summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the source IP address.</summary>
    public string? SourceIp { get; set; }
}

/// <summary>
/// Represents an S3 response.
/// </summary>
public sealed class S3Response
{
    /// <summary>Gets or sets the HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; set; }

    /// <summary>Gets or sets the content type.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the response body as string.</summary>
    public string? Body { get; set; }

    /// <summary>Gets or sets the response body as bytes.</summary>
    public byte[]? BodyBytes { get; set; }

    /// <summary>Gets or sets the response headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Gets or sets the request ID.</summary>
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
/// S3 error codes.
/// </summary>
public enum S3ErrorCode
{
    /// <summary>Access denied.</summary>
    AccessDenied,
    /// <summary>Bucket already exists.</summary>
    BucketAlreadyExists,
    /// <summary>Bucket not empty.</summary>
    BucketNotEmpty,
    /// <summary>Invalid bucket name.</summary>
    InvalidBucketName,
    /// <summary>Invalid argument.</summary>
    InvalidArgument,
    /// <summary>Invalid part.</summary>
    InvalidPart,
    /// <summary>Malformed XML.</summary>
    MalformedXML,
    /// <summary>Malformed policy.</summary>
    MalformedPolicy,
    /// <summary>No such bucket.</summary>
    NoSuchBucket,
    /// <summary>No such key.</summary>
    NoSuchKey,
    /// <summary>No such upload.</summary>
    NoSuchUpload,
    /// <summary>No such bucket policy.</summary>
    NoSuchBucketPolicy,
    /// <summary>Internal error.</summary>
    InternalError
}

/// <summary>
/// S3 bucket information.
/// </summary>
public sealed class S3BucketInfo
{
    /// <summary>Gets or sets the bucket name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the owner ID.</summary>
    public required string OwnerId { get; set; }

    /// <summary>Gets or sets the creation date.</summary>
    public DateTime CreationDate { get; set; }

    /// <summary>Gets or sets the region.</summary>
    public string Region { get; set; } = "us-east-1";
}

/// <summary>
/// S3 object information.
/// </summary>
public sealed class S3ObjectInfo
{
    /// <summary>Gets or sets the bucket name.</summary>
    public required string BucketName { get; set; }

    /// <summary>Gets or sets the object key.</summary>
    public required string Key { get; set; }

    /// <summary>Gets or sets the object size.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the ETag.</summary>
    public required string ETag { get; set; }

    /// <summary>Gets or sets the last modified date.</summary>
    public DateTime LastModified { get; set; }

    /// <summary>Gets or sets the content type.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Gets or sets the version ID.</summary>
    public string? VersionId { get; set; }

    /// <summary>Gets or sets the object metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Gets or sets the storage class.</summary>
    public string StorageClass { get; set; } = "STANDARD";

    /// <summary>Gets or sets whether this is a delete marker.</summary>
    public bool IsDeleteMarker { get; set; }
}

/// <summary>
/// List objects request parameters.
/// </summary>
public sealed class ListObjectsRequest
{
    /// <summary>Gets or sets the prefix filter.</summary>
    public string? Prefix { get; set; }

    /// <summary>Gets or sets the delimiter for hierarchy.</summary>
    public string? Delimiter { get; set; }

    /// <summary>Gets or sets the maximum keys to return.</summary>
    public int MaxKeys { get; set; } = 1000;

    /// <summary>Gets or sets the continuation token.</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Gets or sets the start after key.</summary>
    public string? StartAfter { get; set; }
}

/// <summary>
/// List objects response.
/// </summary>
public sealed class ListObjectsResponse
{
    /// <summary>Gets or sets the objects.</summary>
    public List<S3ObjectInfo> Objects { get; set; } = new();

    /// <summary>Gets or sets the common prefixes.</summary>
    public List<string> CommonPrefixes { get; set; } = new();

    /// <summary>Gets or sets whether the response is truncated.</summary>
    public bool IsTruncated { get; set; }

    /// <summary>Gets or sets the continuation token.</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Gets or sets the next continuation token.</summary>
    public string? NextContinuationToken { get; set; }
}

/// <summary>
/// Multipart upload session.
/// </summary>
public sealed class MultipartUploadSession
{
    /// <summary>Gets or sets the upload ID.</summary>
    public required string UploadId { get; set; }

    /// <summary>Gets or sets the bucket name.</summary>
    public required string BucketName { get; set; }

    /// <summary>Gets or sets the object key.</summary>
    public required string ObjectKey { get; set; }

    /// <summary>Gets or sets the initiation time.</summary>
    public DateTime InitiatedAt { get; set; }

    /// <summary>Gets or sets the owner ID.</summary>
    public required string OwnerId { get; set; }

    /// <summary>Gets or sets the content type.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Gets or sets the object metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Gets or sets the uploaded parts.</summary>
    public ConcurrentDictionary<int, MultipartUploadPart> Parts { get; set; } = new();
}

/// <summary>
/// Multipart upload part.
/// </summary>
public sealed class MultipartUploadPart
{
    /// <summary>Gets or sets the part number.</summary>
    public int PartNumber { get; set; }

    /// <summary>Gets or sets the ETag.</summary>
    public required string ETag { get; set; }

    /// <summary>Gets or sets the part size.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the part data.</summary>
    public required byte[] Data { get; set; }
}

/// <summary>
/// Bucket versioning configuration.
/// </summary>
public sealed class BucketVersioningConfig
{
    /// <summary>Gets or sets the versioning status.</summary>
    public VersioningStatus Status { get; set; } = VersioningStatus.Disabled;

    /// <summary>Gets or sets whether MFA delete is enabled.</summary>
    public bool MfaDelete { get; set; }
}

/// <summary>
/// Versioning status.
/// </summary>
public enum VersioningStatus
{
    /// <summary>Versioning is disabled.</summary>
    Disabled,
    /// <summary>Versioning is enabled.</summary>
    Enabled,
    /// <summary>Versioning is suspended.</summary>
    Suspended
}

/// <summary>
/// Bucket policy.
/// </summary>
public sealed class BucketPolicy
{
    /// <summary>Gets or sets the policy version.</summary>
    [JsonPropertyName("Version")]
    public string Version { get; set; } = "2012-10-17";

    /// <summary>Gets or sets the policy ID.</summary>
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the policy statements.</summary>
    [JsonPropertyName("Statement")]
    public List<PolicyStatement> Statement { get; set; } = new();
}

/// <summary>
/// Policy statement.
/// </summary>
public sealed class PolicyStatement
{
    /// <summary>Gets or sets the statement ID.</summary>
    [JsonPropertyName("Sid")]
    public string? Sid { get; set; }

    /// <summary>Gets or sets the effect.</summary>
    [JsonPropertyName("Effect")]
    public string Effect { get; set; } = "Allow";

    /// <summary>Gets or sets the principal.</summary>
    [JsonPropertyName("Principal")]
    public object Principal { get; set; } = "*";

    /// <summary>Gets or sets the action.</summary>
    [JsonPropertyName("Action")]
    public object Action { get; set; } = "*";

    /// <summary>Gets or sets the resource.</summary>
    [JsonPropertyName("Resource")]
    public object Resource { get; set; } = "*";

    /// <summary>Gets or sets the condition.</summary>
    [JsonPropertyName("Condition")]
    public Dictionary<string, Dictionary<string, string>>? Condition { get; set; }
}

/// <summary>
/// Bucket ACL.
/// </summary>
public sealed class BucketAcl
{
    /// <summary>Gets or sets the owner ID.</summary>
    public required string OwnerId { get; set; }

    /// <summary>Gets or sets the grants.</summary>
    public List<S3Grant> Grants { get; set; } = new();
}

/// <summary>
/// S3 grant.
/// </summary>
public sealed class S3Grant
{
    /// <summary>Gets or sets the grantee.</summary>
    public required S3Grantee Grantee { get; set; }

    /// <summary>Gets or sets the permission.</summary>
    public S3Permission Permission { get; set; }
}

/// <summary>
/// S3 grantee.
/// </summary>
public sealed class S3Grantee
{
    /// <summary>Gets or sets the grantee type.</summary>
    public GranteeType Type { get; set; }

    /// <summary>Gets or sets the grantee ID.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the URI for group grantees.</summary>
    public string? Uri { get; set; }

    /// <summary>Gets or sets the email address.</summary>
    public string? EmailAddress { get; set; }
}

/// <summary>
/// Grantee type.
/// </summary>
public enum GranteeType
{
    /// <summary>Canonical user.</summary>
    CanonicalUser,
    /// <summary>Email address.</summary>
    AmazonCustomerByEmail,
    /// <summary>Group.</summary>
    Group
}

/// <summary>
/// S3 permission.
/// </summary>
public enum S3Permission
{
    /// <summary>Read permission.</summary>
    Read,
    /// <summary>Write permission.</summary>
    Write,
    /// <summary>Read ACP permission.</summary>
    ReadAcp,
    /// <summary>Write ACP permission.</summary>
    WriteAcp,
    /// <summary>Full control.</summary>
    FullControl
}

/// <summary>
/// S3 authentication result.
/// </summary>
public sealed class S3AuthenticationResult
{
    /// <summary>Gets or sets whether authentication succeeded.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets the user ID.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the user is an admin.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Interface for S3 authentication providers.
/// </summary>
public interface IS3AuthenticationProvider
{
    /// <summary>
    /// Authenticates an S3 request.
    /// </summary>
    /// <param name="request">The S3 request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result.</returns>
    Task<S3AuthenticationResult> AuthenticateAsync(S3Request request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for S3 storage backend.
/// </summary>
public interface IS3StorageBackend
{
    /// <summary>Lists buckets for a user.</summary>
    Task<IReadOnlyList<S3BucketInfo>> ListBucketsAsync(string userId, CancellationToken ct = default);

    /// <summary>Creates a bucket.</summary>
    Task CreateBucketAsync(S3BucketInfo bucketInfo, CancellationToken ct = default);

    /// <summary>Deletes a bucket.</summary>
    Task DeleteBucketAsync(string bucketName, CancellationToken ct = default);

    /// <summary>Checks if a bucket exists.</summary>
    Task<bool> BucketExistsAsync(string bucketName, CancellationToken ct = default);

    /// <summary>Gets bucket information.</summary>
    Task<S3BucketInfo?> GetBucketInfoAsync(string bucketName, CancellationToken ct = default);

    /// <summary>Lists objects in a bucket.</summary>
    Task<ListObjectsResponse> ListObjectsAsync(string bucketName, ListObjectsRequest request, CancellationToken ct = default);

    /// <summary>Puts an object.</summary>
    Task PutObjectAsync(S3ObjectInfo objectInfo, Stream data, CancellationToken ct = default);

    /// <summary>Gets object information.</summary>
    Task<S3ObjectInfo?> GetObjectInfoAsync(string bucketName, string key, string? versionId, CancellationToken ct = default);

    /// <summary>Gets object data.</summary>
    Task<Stream?> GetObjectDataAsync(string bucketName, string key, string? versionId, long? rangeStart, long? rangeEnd, CancellationToken ct = default);

    /// <summary>Deletes an object.</summary>
    Task DeleteObjectAsync(string bucketName, string key, string? versionId, CancellationToken ct = default);

    /// <summary>Creates a delete marker.</summary>
    Task CreateDeleteMarkerAsync(string bucketName, string key, string versionId, CancellationToken ct = default);
}

#endregion

#endregion

#region 2. Web Dashboard with Authentication

/// <summary>
/// Production-ready web dashboard authentication service with JWT, MFA, and role-based access control.
/// Implements secure session management, CSRF protection, and comprehensive audit logging.
/// </summary>
public sealed class DashboardAuthenticationService : IAsyncDisposable
{
    private readonly DashboardAuthConfiguration _config;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITotpProvider _totpProvider;
    private readonly ConcurrentDictionary<string, UserSession> _activeSessions;
    private readonly ConcurrentDictionary<string, LoginAttemptTracker> _loginAttempts;
    private readonly ConcurrentDictionary<string, List<string>> _passwordHistory;
    private readonly IDashboardUserStore _userStore;
    private readonly IDashboardAuditLogger _auditLogger;
    private readonly Timer _sessionCleanupTimer;
    private readonly RSA _jwtSigningKey;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the dashboard authentication service.
    /// </summary>
    /// <param name="config">Authentication configuration.</param>
    /// <param name="userStore">User data store.</param>
    /// <param name="auditLogger">Audit logger for tracking actions.</param>
    public DashboardAuthenticationService(
        DashboardAuthConfiguration config,
        IDashboardUserStore userStore,
        IDashboardAuditLogger auditLogger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _passwordHasher = new Argon2PasswordHasher();
        _totpProvider = new TotpProvider();
        _activeSessions = new ConcurrentDictionary<string, UserSession>();
        _loginAttempts = new ConcurrentDictionary<string, LoginAttemptTracker>();
        _passwordHistory = new ConcurrentDictionary<string, List<string>>();
        _jwtSigningKey = RSA.Create(2048);
        _sessionCleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Authenticates a user with username and password.
    /// </summary>
    /// <param name="request">Login request containing credentials.</param>
    /// <param name="clientInfo">Client information for audit logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result with session token if successful.</returns>
    public async Task<LoginResult> LoginAsync(
        LoginRequest request,
        ClientInfo clientInfo,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check rate limiting
        var rateLimitKey = $"{clientInfo.IpAddress}:{request.Username}";
        if (!CheckRateLimit(rateLimitKey))
        {
            await _auditLogger.LogAsync(new DashboardAuditEvent
            {
                EventType = AuditEventType.LoginRateLimited,
                Username = request.Username,
                IpAddress = clientInfo.IpAddress,
                UserAgent = clientInfo.UserAgent,
                Success = false,
                Details = "Rate limit exceeded"
            }, cancellationToken);

            return new LoginResult
            {
                Success = false,
                ErrorCode = LoginErrorCode.RateLimitExceeded,
                ErrorMessage = "Too many login attempts. Please try again later.",
                RetryAfterSeconds = _config.LockoutDurationMinutes * 60
            };
        }

        // Get user
        var user = await _userStore.GetUserByUsernameAsync(request.Username, cancellationToken);
        if (user == null)
        {
            RecordFailedAttempt(rateLimitKey);
            await _auditLogger.LogAsync(new DashboardAuditEvent
            {
                EventType = AuditEventType.LoginFailed,
                Username = request.Username,
                IpAddress = clientInfo.IpAddress,
                UserAgent = clientInfo.UserAgent,
                Success = false,
                Details = "User not found"
            }, cancellationToken);

            return new LoginResult
            {
                Success = false,
                ErrorCode = LoginErrorCode.InvalidCredentials,
                ErrorMessage = "Invalid username or password."
            };
        }

        // Check if account is locked
        if (user.IsLocked && user.LockoutEnd > DateTime.UtcNow)
        {
            await _auditLogger.LogAsync(new DashboardAuditEvent
            {
                EventType = AuditEventType.LoginFailed,
                UserId = user.Id,
                Username = user.Username,
                IpAddress = clientInfo.IpAddress,
                UserAgent = clientInfo.UserAgent,
                Success = false,
                Details = "Account locked"
            }, cancellationToken);

            return new LoginResult
            {
                Success = false,
                ErrorCode = LoginErrorCode.AccountLocked,
                ErrorMessage = "Account is locked. Please contact an administrator.",
                RetryAfterSeconds = (int)(user.LockoutEnd.Value - DateTime.UtcNow).TotalSeconds
            };
        }

        // Verify password
        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            RecordFailedAttempt(rateLimitKey);
            user.FailedLoginAttempts++;

            if (user.FailedLoginAttempts >= _config.MaxFailedAttempts)
            {
                user.IsLocked = true;
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(_config.LockoutDurationMinutes);
            }

            await _userStore.UpdateUserAsync(user, cancellationToken);

            await _auditLogger.LogAsync(new DashboardAuditEvent
            {
                EventType = AuditEventType.LoginFailed,
                UserId = user.Id,
                Username = user.Username,
                IpAddress = clientInfo.IpAddress,
                UserAgent = clientInfo.UserAgent,
                Success = false,
                Details = $"Invalid password. Attempt {user.FailedLoginAttempts}/{_config.MaxFailedAttempts}"
            }, cancellationToken);

            return new LoginResult
            {
                Success = false,
                ErrorCode = LoginErrorCode.InvalidCredentials,
                ErrorMessage = "Invalid username or password."
            };
        }

        // Check password expiration
        if (_config.PasswordExpirationDays > 0 && user.PasswordChangedAt.HasValue)
        {
            var passwordAge = DateTime.UtcNow - user.PasswordChangedAt.Value;
            if (passwordAge.TotalDays > _config.PasswordExpirationDays)
            {
                return new LoginResult
                {
                    Success = false,
                    ErrorCode = LoginErrorCode.PasswordExpired,
                    ErrorMessage = "Password has expired. Please reset your password.",
                    RequiresPasswordChange = true
                };
            }
        }

        // Check MFA requirement
        if (user.MfaEnabled)
        {
            if (string.IsNullOrEmpty(request.TotpCode))
            {
                return new LoginResult
                {
                    Success = false,
                    ErrorCode = LoginErrorCode.MfaRequired,
                    ErrorMessage = "Multi-factor authentication code required.",
                    RequiresMfa = true
                };
            }

            if (!_totpProvider.ValidateCode(user.MfaSecret!, request.TotpCode))
            {
                RecordFailedAttempt(rateLimitKey);
                await _auditLogger.LogAsync(new DashboardAuditEvent
                {
                    EventType = AuditEventType.MfaFailed,
                    UserId = user.Id,
                    Username = user.Username,
                    IpAddress = clientInfo.IpAddress,
                    UserAgent = clientInfo.UserAgent,
                    Success = false,
                    Details = "Invalid TOTP code"
                }, cancellationToken);

                return new LoginResult
                {
                    Success = false,
                    ErrorCode = LoginErrorCode.InvalidMfaCode,
                    ErrorMessage = "Invalid authentication code."
                };
            }
        }

        // Reset failed attempts on successful login
        user.FailedLoginAttempts = 0;
        user.IsLocked = false;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = clientInfo.IpAddress;
        await _userStore.UpdateUserAsync(user, cancellationToken);

        // Create session
        var session = await CreateSessionAsync(user, clientInfo, cancellationToken);

        // Generate tokens
        var accessToken = GenerateJwtToken(user, session, TokenType.Access);
        var refreshToken = GenerateJwtToken(user, session, TokenType.Refresh);

        // Generate CSRF token
        var csrfToken = GenerateCsrfToken();
        session.CsrfToken = csrfToken;

        await _auditLogger.LogAsync(new DashboardAuditEvent
        {
            EventType = AuditEventType.LoginSuccess,
            UserId = user.Id,
            Username = user.Username,
            IpAddress = clientInfo.IpAddress,
            UserAgent = clientInfo.UserAgent,
            Success = true,
            SessionId = session.SessionId,
            Details = $"Login successful. Session created."
        }, cancellationToken);

        ClearFailedAttempts(rateLimitKey);

        return new LoginResult
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            CsrfToken = csrfToken,
            ExpiresIn = (int)_config.AccessTokenLifetime.TotalSeconds,
            User = new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Roles = user.Roles.ToList(),
                Permissions = GetEffectivePermissions(user)
            }
        };
    }

    /// <summary>
    /// Logs out a user and invalidates their session.
    /// </summary>
    /// <param name="sessionId">Session ID to invalidate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LogoutAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeSessions.TryRemove(sessionId, out var session))
        {
            await _auditLogger.LogAsync(new DashboardAuditEvent
            {
                EventType = AuditEventType.Logout,
                UserId = session.UserId,
                Username = session.Username,
                SessionId = sessionId,
                Success = true,
                Details = "User logged out"
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Validates an access token and returns the session.
    /// </summary>
    /// <param name="accessToken">The JWT access token.</param>
    /// <param name="csrfToken">The CSRF token from request header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session validation result.</returns>
    public async Task<SessionValidationResult> ValidateSessionAsync(
        string accessToken,
        string? csrfToken = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var claims = ValidateJwtToken(accessToken);
            if (claims == null)
            {
                return new SessionValidationResult { IsValid = false, ErrorMessage = "Invalid token" };
            }

            var sessionId = claims.GetValueOrDefault("sid");
            if (string.IsNullOrEmpty(sessionId) || !_activeSessions.TryGetValue(sessionId, out var session))
            {
                return new SessionValidationResult { IsValid = false, ErrorMessage = "Session not found" };
            }

            // Check session expiration
            if (session.ExpiresAt < DateTime.UtcNow)
            {
                _activeSessions.TryRemove(sessionId, out _);
                return new SessionValidationResult { IsValid = false, ErrorMessage = "Session expired" };
            }

            // Validate CSRF token for state-changing requests
            if (_config.RequireCsrfValidation && !string.IsNullOrEmpty(csrfToken))
            {
                if (session.CsrfToken != csrfToken)
                {
                    await _auditLogger.LogAsync(new DashboardAuditEvent
                    {
                        EventType = AuditEventType.CsrfValidationFailed,
                        UserId = session.UserId,
                        Username = session.Username,
                        SessionId = sessionId,
                        Success = false,
                        Details = "CSRF token mismatch"
                    }, cancellationToken);

                    return new SessionValidationResult { IsValid = false, ErrorMessage = "Invalid CSRF token" };
                }
            }

            // Refresh session activity
            session.LastActivityAt = DateTime.UtcNow;

            return new SessionValidationResult
            {
                IsValid = true,
                Session = session,
                UserId = session.UserId,
                Username = session.Username,
                Roles = session.Roles,
                Permissions = session.Permissions
            };
        }
        catch (Exception ex)
        {
            return new SessionValidationResult { IsValid = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Refreshes an access token using a refresh token.
    /// </summary>
    /// <param name="refreshToken">The refresh token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New token pair.</returns>
    public async Task<TokenRefreshResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var claims = ValidateJwtToken(refreshToken);
        if (claims == null || claims.GetValueOrDefault("type") != "refresh")
        {
            return new TokenRefreshResult { Success = false, ErrorMessage = "Invalid refresh token" };
        }

        var sessionId = claims.GetValueOrDefault("sid");
        if (string.IsNullOrEmpty(sessionId) || !_activeSessions.TryGetValue(sessionId, out var session))
        {
            return new TokenRefreshResult { Success = false, ErrorMessage = "Session not found" };
        }

        var user = await _userStore.GetUserByIdAsync(session.UserId, cancellationToken);
        if (user == null || user.IsLocked)
        {
            return new TokenRefreshResult { Success = false, ErrorMessage = "User not available" };
        }

        // Generate new tokens
        var newAccessToken = GenerateJwtToken(user, session, TokenType.Access);
        var newRefreshToken = GenerateJwtToken(user, session, TokenType.Refresh);

        // Extend session
        session.ExpiresAt = DateTime.UtcNow.Add(_config.SessionTimeout);
        session.LastActivityAt = DateTime.UtcNow;

        return new TokenRefreshResult
        {
            Success = true,
            AccessToken = newAccessToken,
            RefreshToken = newRefreshToken,
            ExpiresIn = (int)_config.AccessTokenLifetime.TotalSeconds
        };
    }

    /// <summary>
    /// Enables MFA for a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>MFA setup information including secret and QR code URI.</returns>
    public async Task<MfaSetupResult> SetupMfaAsync(string userId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var user = await _userStore.GetUserByIdAsync(userId, cancellationToken);
        if (user == null)
        {
            return new MfaSetupResult { Success = false, ErrorMessage = "User not found" };
        }

        var secret = _totpProvider.GenerateSecret();
        var qrCodeUri = _totpProvider.GetQrCodeUri(user.Email ?? user.Username, _config.ApplicationName, secret);

        // Store secret temporarily until confirmed
        user.PendingMfaSecret = secret;
        await _userStore.UpdateUserAsync(user, cancellationToken);

        return new MfaSetupResult
        {
            Success = true,
            Secret = secret,
            QrCodeUri = qrCodeUri,
            BackupCodes = GenerateBackupCodes()
        };
    }

    /// <summary>
    /// Confirms MFA setup by validating a TOTP code.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="totpCode">TOTP code to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confirmation result.</returns>
    public async Task<MfaConfirmResult> ConfirmMfaAsync(string userId, string totpCode, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var user = await _userStore.GetUserByIdAsync(userId, cancellationToken);
        if (user == null || string.IsNullOrEmpty(user.PendingMfaSecret))
        {
            return new MfaConfirmResult { Success = false, ErrorMessage = "MFA setup not initiated" };
        }

        if (!_totpProvider.ValidateCode(user.PendingMfaSecret, totpCode))
        {
            return new MfaConfirmResult { Success = false, ErrorMessage = "Invalid verification code" };
        }

        user.MfaSecret = user.PendingMfaSecret;
        user.PendingMfaSecret = null;
        user.MfaEnabled = true;
        await _userStore.UpdateUserAsync(user, cancellationToken);

        await _auditLogger.LogAsync(new DashboardAuditEvent
        {
            EventType = AuditEventType.MfaEnabled,
            UserId = userId,
            Username = user.Username,
            Success = true,
            Details = "MFA enabled"
        }, cancellationToken);

        return new MfaConfirmResult { Success = true };
    }

    /// <summary>
    /// Changes a user's password.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="currentPassword">Current password.</param>
    /// <param name="newPassword">New password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Password change result.</returns>
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var user = await _userStore.GetUserByIdAsync(userId, cancellationToken);
        if (user == null)
        {
            return new PasswordChangeResult { Success = false, ErrorMessage = "User not found" };
        }

        // Verify current password
        if (!_passwordHasher.VerifyPassword(currentPassword, user.PasswordHash))
        {
            await _auditLogger.LogAsync(new DashboardAuditEvent
            {
                EventType = AuditEventType.PasswordChangeFailed,
                UserId = userId,
                Username = user.Username,
                Success = false,
                Details = "Invalid current password"
            }, cancellationToken);

            return new PasswordChangeResult { Success = false, ErrorMessage = "Current password is incorrect" };
        }

        // Validate new password policy
        var validationResult = ValidatePasswordPolicy(newPassword, user);
        if (!validationResult.IsValid)
        {
            return new PasswordChangeResult { Success = false, ErrorMessage = validationResult.ErrorMessage };
        }

        // Check password history
        if (_config.PasswordHistoryCount > 0)
        {
            var history = _passwordHistory.GetOrAdd(userId, _ => new List<string>());
            if (history.Any(h => _passwordHasher.VerifyPassword(newPassword, h)))
            {
                return new PasswordChangeResult
                {
                    Success = false,
                    ErrorMessage = $"Cannot reuse any of your last {_config.PasswordHistoryCount} passwords"
                };
            }
        }

        // Update password
        var newHash = _passwordHasher.HashPassword(newPassword);
        user.PasswordHash = newHash;
        user.PasswordChangedAt = DateTime.UtcNow;
        await _userStore.UpdateUserAsync(user, cancellationToken);

        // Update history
        if (_config.PasswordHistoryCount > 0)
        {
            var history = _passwordHistory.GetOrAdd(userId, _ => new List<string>());
            history.Add(newHash);
            while (history.Count > _config.PasswordHistoryCount)
            {
                history.RemoveAt(0);
            }
        }

        await _auditLogger.LogAsync(new DashboardAuditEvent
        {
            EventType = AuditEventType.PasswordChanged,
            UserId = userId,
            Username = user.Username,
            Success = true,
            Details = "Password changed successfully"
        }, cancellationToken);

        return new PasswordChangeResult { Success = true };
    }

    /// <summary>
    /// Checks if a user has a specific permission.
    /// </summary>
    /// <param name="session">User session.</param>
    /// <param name="permission">Required permission.</param>
    /// <returns>True if user has the permission.</returns>
    public bool HasPermission(UserSession session, string permission)
    {
        if (session.Roles.Contains(DashboardRole.Admin))
            return true;

        return session.Permissions.Contains(permission) ||
               session.Permissions.Contains("*");
    }

    /// <summary>
    /// Checks if a user has a specific role.
    /// </summary>
    /// <param name="session">User session.</param>
    /// <param name="role">Required role.</param>
    /// <returns>True if user has the role.</returns>
    public bool HasRole(UserSession session, string role)
    {
        return session.Roles.Contains(role);
    }

    #region Private Methods

    private bool CheckRateLimit(string key)
    {
        if (!_loginAttempts.TryGetValue(key, out var tracker))
        {
            return true;
        }

        // Clean up old attempts
        var cutoff = DateTime.UtcNow.AddMinutes(-_config.RateLimitWindowMinutes);
        tracker.Attempts.RemoveAll(a => a < cutoff);

        return tracker.Attempts.Count < _config.MaxAttemptsPerWindow;
    }

    private void RecordFailedAttempt(string key)
    {
        var tracker = _loginAttempts.GetOrAdd(key, _ => new LoginAttemptTracker());
        tracker.Attempts.Add(DateTime.UtcNow);
    }

    private void ClearFailedAttempts(string key)
    {
        _loginAttempts.TryRemove(key, out _);
    }

    private async Task<UserSession> CreateSessionAsync(DashboardUser user, ClientInfo clientInfo, CancellationToken ct)
    {
        var session = new UserSession
        {
            SessionId = GenerateSessionId(),
            UserId = user.Id,
            Username = user.Username,
            Roles = user.Roles.ToList(),
            Permissions = GetEffectivePermissions(user),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_config.SessionTimeout),
            LastActivityAt = DateTime.UtcNow,
            IpAddress = clientInfo.IpAddress,
            UserAgent = clientInfo.UserAgent
        };

        _activeSessions[session.SessionId] = session;
        return session;
    }

    private List<string> GetEffectivePermissions(DashboardUser user)
    {
        var permissions = new HashSet<string>(user.DirectPermissions ?? Enumerable.Empty<string>());

        foreach (var role in user.Roles)
        {
            var rolePermissions = GetRolePermissions(role);
            foreach (var perm in rolePermissions)
            {
                permissions.Add(perm);
            }
        }

        return permissions.ToList();
    }

    private IEnumerable<string> GetRolePermissions(string role)
    {
        return role switch
        {
            DashboardRole.Admin => new[]
            {
                "dashboard.view", "dashboard.admin", "users.view", "users.create", "users.edit", "users.delete",
                "storage.view", "storage.manage", "settings.view", "settings.edit", "audit.view", "metrics.view"
            },
            DashboardRole.Operator => new[]
            {
                "dashboard.view", "storage.view", "storage.manage", "metrics.view", "audit.view"
            },
            DashboardRole.Viewer => new[]
            {
                "dashboard.view", "storage.view", "metrics.view"
            },
            _ => Array.Empty<string>()
        };
    }

    private string GenerateJwtToken(DashboardUser user, UserSession session, TokenType type)
    {
        var now = DateTime.UtcNow;
        var expires = type == TokenType.Access
            ? now.Add(_config.AccessTokenLifetime)
            : now.Add(_config.RefreshTokenLifetime);

        var header = new { alg = "RS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["sub"] = user.Id,
            ["name"] = user.DisplayName ?? user.Username,
            ["email"] = user.Email ?? "",
            ["roles"] = user.Roles,
            ["sid"] = session.SessionId,
            ["type"] = type == TokenType.Access ? "access" : "refresh",
            ["iat"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["exp"] = new DateTimeOffset(expires).ToUnixTimeSeconds(),
            ["iss"] = _config.TokenIssuer,
            ["aud"] = _config.TokenAudience
        };

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var dataToSign = $"{headerBase64}.{payloadBase64}";
        var signature = _jwtSigningKey.SignData(Encoding.UTF8.GetBytes(dataToSign), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Base64UrlEncode(signature);

        return $"{headerBase64}.{payloadBase64}.{signatureBase64}";
    }

    private Dictionary<string, string>? ValidateJwtToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
                return null;

            var headerBase64 = parts[0];
            var payloadBase64 = parts[1];
            var signatureBase64 = parts[2];

            // Verify signature
            var dataToVerify = $"{headerBase64}.{payloadBase64}";
            var signature = Base64UrlDecode(signatureBase64);

            if (!_jwtSigningKey.VerifyData(Encoding.UTF8.GetBytes(dataToVerify), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;

            // Parse payload
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadBase64));
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);
            if (payload == null)
                return null;

            // Check expiration
            if (payload.TryGetValue("exp", out var expElement))
            {
                var exp = expElement.GetInt64();
                if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow)
                    return null;
            }

            return payload.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ValueKind == JsonValueKind.String ? kvp.Value.GetString()! : kvp.Value.GetRawText()
            );
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateSessionId()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateCsrfToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string[] GenerateBackupCodes()
    {
        var codes = new string[10];
        for (int i = 0; i < 10; i++)
        {
            var bytes = new byte[4];
            RandomNumberGenerator.Fill(bytes);
            codes[i] = Convert.ToHexString(bytes).ToLowerInvariant();
        }
        return codes;
    }

    private PasswordValidationResult ValidatePasswordPolicy(string password, DashboardUser user)
    {
        if (password.Length < _config.MinPasswordLength)
        {
            return new PasswordValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Password must be at least {_config.MinPasswordLength} characters long"
            };
        }

        if (_config.RequireUppercase && !password.Any(char.IsUpper))
        {
            return new PasswordValidationResult
            {
                IsValid = false,
                ErrorMessage = "Password must contain at least one uppercase letter"
            };
        }

        if (_config.RequireLowercase && !password.Any(char.IsLower))
        {
            return new PasswordValidationResult
            {
                IsValid = false,
                ErrorMessage = "Password must contain at least one lowercase letter"
            };
        }

        if (_config.RequireDigit && !password.Any(char.IsDigit))
        {
            return new PasswordValidationResult
            {
                IsValid = false,
                ErrorMessage = "Password must contain at least one digit"
            };
        }

        if (_config.RequireSpecialChar && !password.Any(c => !char.IsLetterOrDigit(c)))
        {
            return new PasswordValidationResult
            {
                IsValid = false,
                ErrorMessage = "Password must contain at least one special character"
            };
        }

        // Check if password contains username
        if (password.Contains(user.Username, StringComparison.OrdinalIgnoreCase))
        {
            return new PasswordValidationResult
            {
                IsValid = false,
                ErrorMessage = "Password cannot contain your username"
            };
        }

        return new PasswordValidationResult { IsValid = true };
    }

    private void CleanupExpiredSessions(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredSessions = _activeSessions
            .Where(kvp => kvp.Value.ExpiresAt < now || kvp.Value.LastActivityAt.AddMinutes(_config.IdleTimeoutMinutes) < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in expiredSessions)
        {
            _activeSessions.TryRemove(sessionId, out _);
        }
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace("-", "+").Replace("_", "/");
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionCleanupTimer.Dispose();
        _jwtSigningKey.Dispose();
        _activeSessions.Clear();
        await Task.CompletedTask;
    }
}

#region Dashboard Authentication Types

/// <summary>
/// Dashboard authentication configuration.
/// </summary>
public sealed class DashboardAuthConfiguration
{
    /// <summary>Gets or sets the application name for MFA.</summary>
    public string ApplicationName { get; set; } = "DataWarehouse";

    /// <summary>Gets or sets the token issuer.</summary>
    public string TokenIssuer { get; set; } = "DataWarehouse";

    /// <summary>Gets or sets the token audience.</summary>
    public string TokenAudience { get; set; } = "DataWarehouse";

    /// <summary>Gets or sets the access token lifetime.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets the refresh token lifetime.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Gets or sets the session timeout.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Gets or sets the idle timeout in minutes.</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>Gets or sets the maximum failed login attempts.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Gets or sets the lockout duration in minutes.</summary>
    public int LockoutDurationMinutes { get; set; } = 15;

    /// <summary>Gets or sets the rate limit window in minutes.</summary>
    public int RateLimitWindowMinutes { get; set; } = 5;

    /// <summary>Gets or sets the maximum attempts per window.</summary>
    public int MaxAttemptsPerWindow { get; set; } = 10;

    /// <summary>Gets or sets the minimum password length.</summary>
    public int MinPasswordLength { get; set; } = 12;

    /// <summary>Gets or sets whether uppercase is required.</summary>
    public bool RequireUppercase { get; set; } = true;

    /// <summary>Gets or sets whether lowercase is required.</summary>
    public bool RequireLowercase { get; set; } = true;

    /// <summary>Gets or sets whether digit is required.</summary>
    public bool RequireDigit { get; set; } = true;

    /// <summary>Gets or sets whether special char is required.</summary>
    public bool RequireSpecialChar { get; set; } = true;

    /// <summary>Gets or sets the password history count.</summary>
    public int PasswordHistoryCount { get; set; } = 12;

    /// <summary>Gets or sets the password expiration days.</summary>
    public int PasswordExpirationDays { get; set; } = 90;

    /// <summary>Gets or sets whether CSRF validation is required.</summary>
    public bool RequireCsrfValidation { get; set; } = true;
}

/// <summary>
/// Dashboard role constants.
/// </summary>
public static class DashboardRole
{
    /// <summary>Administrator role with full access.</summary>
    public const string Admin = "Admin";

    /// <summary>Operator role with management access.</summary>
    public const string Operator = "Operator";

    /// <summary>Viewer role with read-only access.</summary>
    public const string Viewer = "Viewer";
}

/// <summary>
/// Token type enumeration.
/// </summary>
public enum TokenType
{
    /// <summary>Access token.</summary>
    Access,
    /// <summary>Refresh token.</summary>
    Refresh
}

/// <summary>
/// Login request.
/// </summary>
public sealed class LoginRequest
{
    /// <summary>Gets or sets the username.</summary>
    public required string Username { get; set; }

    /// <summary>Gets or sets the password.</summary>
    public required string Password { get; set; }

    /// <summary>Gets or sets the TOTP code for MFA.</summary>
    public string? TotpCode { get; set; }

    /// <summary>Gets or sets whether to remember the session.</summary>
    public bool RememberMe { get; set; }
}

/// <summary>
/// Login result.
/// </summary>
public sealed class LoginResult
{
    /// <summary>Gets or sets whether login was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error code.</summary>
    public LoginErrorCode? ErrorCode { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the access token.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Gets or sets the refresh token.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Gets or sets the CSRF token.</summary>
    public string? CsrfToken { get; set; }

    /// <summary>Gets or sets the token expiration in seconds.</summary>
    public int ExpiresIn { get; set; }

    /// <summary>Gets or sets whether MFA is required.</summary>
    public bool RequiresMfa { get; set; }

    /// <summary>Gets or sets whether password change is required.</summary>
    public bool RequiresPasswordChange { get; set; }

    /// <summary>Gets or sets retry after seconds.</summary>
    public int? RetryAfterSeconds { get; set; }

    /// <summary>Gets or sets the user info.</summary>
    public UserInfo? User { get; set; }
}

/// <summary>
/// Login error codes.
/// </summary>
public enum LoginErrorCode
{
    /// <summary>Invalid credentials.</summary>
    InvalidCredentials,
    /// <summary>Account locked.</summary>
    AccountLocked,
    /// <summary>MFA required.</summary>
    MfaRequired,
    /// <summary>Invalid MFA code.</summary>
    InvalidMfaCode,
    /// <summary>Password expired.</summary>
    PasswordExpired,
    /// <summary>Rate limit exceeded.</summary>
    RateLimitExceeded
}

/// <summary>
/// User information.
/// </summary>
public sealed class UserInfo
{
    /// <summary>Gets or sets the user ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the username.</summary>
    public required string Username { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the email.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets the roles.</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>Gets or sets the permissions.</summary>
    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// Client information.
/// </summary>
public sealed class ClientInfo
{
    /// <summary>Gets or sets the IP address.</summary>
    public required string IpAddress { get; set; }

    /// <summary>Gets or sets the user agent.</summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// User session.
/// </summary>
public sealed class UserSession
{
    /// <summary>Gets or sets the session ID.</summary>
    public required string SessionId { get; set; }

    /// <summary>Gets or sets the user ID.</summary>
    public required string UserId { get; set; }

    /// <summary>Gets or sets the username.</summary>
    public required string Username { get; set; }

    /// <summary>Gets or sets the roles.</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>Gets or sets the permissions.</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>Gets or sets the creation time.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets the expiration time.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Gets or sets the last activity time.</summary>
    public DateTime LastActivityAt { get; set; }

    /// <summary>Gets or sets the CSRF token.</summary>
    public string? CsrfToken { get; set; }

    /// <summary>Gets or sets the IP address.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Gets or sets the user agent.</summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// Session validation result.
/// </summary>
public sealed class SessionValidationResult
{
    /// <summary>Gets or sets whether the session is valid.</summary>
    public bool IsValid { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the session.</summary>
    public UserSession? Session { get; set; }

    /// <summary>Gets or sets the user ID.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the username.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the roles.</summary>
    public List<string>? Roles { get; set; }

    /// <summary>Gets or sets the permissions.</summary>
    public List<string>? Permissions { get; set; }
}

/// <summary>
/// Token refresh result.
/// </summary>
public sealed class TokenRefreshResult
{
    /// <summary>Gets or sets whether refresh was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the new access token.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Gets or sets the new refresh token.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Gets or sets the expiration in seconds.</summary>
    public int ExpiresIn { get; set; }
}

/// <summary>
/// MFA setup result.
/// </summary>
public sealed class MfaSetupResult
{
    /// <summary>Gets or sets whether setup was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the secret.</summary>
    public string? Secret { get; set; }

    /// <summary>Gets or sets the QR code URI.</summary>
    public string? QrCodeUri { get; set; }

    /// <summary>Gets or sets the backup codes.</summary>
    public string[]? BackupCodes { get; set; }
}

/// <summary>
/// MFA confirm result.
/// </summary>
public sealed class MfaConfirmResult
{
    /// <summary>Gets or sets whether confirmation was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Password change result.
/// </summary>
public sealed class PasswordChangeResult
{
    /// <summary>Gets or sets whether change was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Password validation result.
/// </summary>
public sealed class PasswordValidationResult
{
    /// <summary>Gets or sets whether password is valid.</summary>
    public bool IsValid { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Login attempt tracker.
/// </summary>
public sealed class LoginAttemptTracker
{
    /// <summary>Gets the list of attempts.</summary>
    public List<DateTime> Attempts { get; } = new();
}

/// <summary>
/// Dashboard user.
/// </summary>
public sealed class DashboardUser
{
    /// <summary>Gets or sets the user ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the username.</summary>
    public required string Username { get; set; }

    /// <summary>Gets or sets the password hash.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the email.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets the roles.</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>Gets or sets the direct permissions.</summary>
    public List<string>? DirectPermissions { get; set; }

    /// <summary>Gets or sets whether MFA is enabled.</summary>
    public bool MfaEnabled { get; set; }

    /// <summary>Gets or sets the MFA secret.</summary>
    public string? MfaSecret { get; set; }

    /// <summary>Gets or sets the pending MFA secret.</summary>
    public string? PendingMfaSecret { get; set; }

    /// <summary>Gets or sets whether account is locked.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Gets or sets the lockout end time.</summary>
    public DateTime? LockoutEnd { get; set; }

    /// <summary>Gets or sets the failed login attempts.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>Gets or sets the password changed date.</summary>
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>Gets or sets the last login date.</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Gets or sets the last login IP.</summary>
    public string? LastLoginIp { get; set; }
}

/// <summary>
/// Dashboard audit event.
/// </summary>
public sealed class DashboardAuditEvent
{
    /// <summary>Gets or sets the event ID.</summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the event type.</summary>
    public AuditEventType EventType { get; set; }

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the user ID.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the username.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the session ID.</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the IP address.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Gets or sets the user agent.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Gets or sets whether the operation was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the details.</summary>
    public string? Details { get; set; }
}

/// <summary>
/// Audit event types.
/// </summary>
public enum AuditEventType
{
    /// <summary>Login success.</summary>
    LoginSuccess,
    /// <summary>Login failed.</summary>
    LoginFailed,
    /// <summary>Login rate limited.</summary>
    LoginRateLimited,
    /// <summary>Logout.</summary>
    Logout,
    /// <summary>MFA enabled.</summary>
    MfaEnabled,
    /// <summary>MFA disabled.</summary>
    MfaDisabled,
    /// <summary>MFA failed.</summary>
    MfaFailed,
    /// <summary>Password changed.</summary>
    PasswordChanged,
    /// <summary>Password change failed.</summary>
    PasswordChangeFailed,
    /// <summary>CSRF validation failed.</summary>
    CsrfValidationFailed,
    /// <summary>User created.</summary>
    UserCreated,
    /// <summary>User updated.</summary>
    UserUpdated,
    /// <summary>User deleted.</summary>
    UserDeleted,
    /// <summary>Permission changed.</summary>
    PermissionChanged
}

/// <summary>
/// Interface for dashboard user store.
/// </summary>
public interface IDashboardUserStore
{
    /// <summary>Gets a user by username.</summary>
    Task<DashboardUser?> GetUserByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Gets a user by ID.</summary>
    Task<DashboardUser?> GetUserByIdAsync(string userId, CancellationToken ct = default);

    /// <summary>Creates a user.</summary>
    Task<DashboardUser> CreateUserAsync(DashboardUser user, CancellationToken ct = default);

    /// <summary>Updates a user.</summary>
    Task UpdateUserAsync(DashboardUser user, CancellationToken ct = default);

    /// <summary>Deletes a user.</summary>
    Task DeleteUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Lists all users.</summary>
    Task<IReadOnlyList<DashboardUser>> ListUsersAsync(CancellationToken ct = default);
}

/// <summary>
/// Interface for dashboard audit logger.
/// </summary>
public interface IDashboardAuditLogger
{
    /// <summary>Logs an audit event.</summary>
    Task LogAsync(DashboardAuditEvent evt, CancellationToken ct = default);

    /// <summary>Queries audit events.</summary>
    Task<IReadOnlyList<DashboardAuditEvent>> QueryAsync(AuditQueryParameters parameters, CancellationToken ct = default);
}

/// <summary>
/// Audit query parameters.
/// </summary>
public sealed class AuditQueryParameters
{
    /// <summary>Gets or sets the start time.</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>Gets or sets the user ID filter.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the event type filter.</summary>
    public AuditEventType? EventType { get; set; }

    /// <summary>Gets or sets the limit.</summary>
    public int Limit { get; set; } = 100;

    /// <summary>Gets or sets the offset.</summary>
    public int Offset { get; set; }
}

/// <summary>
/// Interface for password hasher.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a password.</summary>
    string HashPassword(string password);

    /// <summary>Verifies a password.</summary>
    bool VerifyPassword(string password, string hash);
}

// =============================================================================
// ARGON2 PASSWORD HASHER MOVED TO: DataWarehouse.Plugins.Encryption
// =============================================================================
// The Argon2PasswordHasher implementation has been moved to
// DataWarehouse.Plugins.Encryption.Providers.Argon2PasswordHasher
//
// Use DataWarehouse.Plugins.Encryption.EncryptionProviderRegistry.CreatePasswordHasher()
// to obtain a password hasher instance.
// =============================================================================

/// <summary>
/// Interface for TOTP provider.
/// </summary>
public interface ITotpProvider
{
    /// <summary>Generates a secret.</summary>
    string GenerateSecret();

    /// <summary>Gets the QR code URI.</summary>
    string GetQrCodeUri(string accountName, string issuer, string secret);

    /// <summary>Validates a TOTP code.</summary>
    bool ValidateCode(string secret, string code);
}

/// <summary>
/// TOTP provider implementation.
/// </summary>
public sealed class TotpProvider : ITotpProvider
{
    private const int SecretLength = 20;
    private const int CodeDigits = 6;
    private const int TimeStep = 30;
    private const int TimeWindow = 1; // Allow 1 step before/after

    /// <summary>
    /// Generates a random TOTP secret.
    /// </summary>
    public string GenerateSecret()
    {
        var secretBytes = new byte[SecretLength];
        RandomNumberGenerator.Fill(secretBytes);
        return Base32Encode(secretBytes);
    }

    /// <summary>
    /// Gets the QR code URI for authenticator apps.
    /// </summary>
    public string GetQrCodeUri(string accountName, string issuer, string secret)
    {
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={CodeDigits}&period={TimeStep}";
    }

    /// <summary>
    /// Validates a TOTP code.
    /// </summary>
    public bool ValidateCode(string secret, string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != CodeDigits)
            return false;

        var secretBytes = Base32Decode(secret);
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TimeStep;

        for (int i = -TimeWindow; i <= TimeWindow; i++)
        {
            var expectedCode = GenerateCode(secretBytes, currentTime + i);
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(code),
                Encoding.UTF8.GetBytes(expectedCode)))
            {
                return true;
            }
        }

        return false;
    }

    private string GenerateCode(byte[] secret, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes);

        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24) |
                   ((hash[offset + 1] & 0xFF) << 16) |
                   ((hash[offset + 2] & 0xFF) << 8) |
                   (hash[offset + 3] & 0xFF);

        code %= (int)Math.Pow(10, CodeDigits);
        return code.ToString().PadLeft(CodeDigits, '0');
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder((data.Length * 8 + 4) / 5);

        int buffer = 0;
        int bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                result.Append(alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            result.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return result.ToString();
    }

    private static byte[] Base32Decode(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        encoded = encoded.ToUpperInvariant().TrimEnd('=');

        var result = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var c in encoded)
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) continue;

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return result.ToArray();
    }
}

#endregion

#endregion
