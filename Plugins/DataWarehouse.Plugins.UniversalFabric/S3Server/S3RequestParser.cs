using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;
using DataWarehouse.SDK.Storage.Fabric;

namespace DataWarehouse.Plugins.UniversalFabric.S3Server;

/// <summary>
/// Enumerates all supported S3 API operations that the server can handle.
/// </summary>
public enum S3Operation
{
    /// <summary>List all buckets owned by the authenticated sender.</summary>
    ListBuckets,

    /// <summary>Create a new bucket.</summary>
    CreateBucket,

    /// <summary>Delete an empty bucket.</summary>
    DeleteBucket,

    /// <summary>Retrieve an object's data and metadata.</summary>
    GetObject,

    /// <summary>Store an object in a bucket.</summary>
    PutObject,

    /// <summary>Remove an object from a bucket.</summary>
    DeleteObject,

    /// <summary>Retrieve object metadata without the body.</summary>
    HeadObject,

    /// <summary>List objects in a bucket using the V2 API.</summary>
    ListObjectsV2,

    /// <summary>Begin a multipart upload and obtain an upload ID.</summary>
    InitiateMultipartUpload,

    /// <summary>Upload a single part of a multipart upload.</summary>
    UploadPart,

    /// <summary>Assemble uploaded parts into the final object.</summary>
    CompleteMultipartUpload,

    /// <summary>Cancel an in-progress multipart upload.</summary>
    AbortMultipartUpload,

    /// <summary>Copy an object from one location to another.</summary>
    CopyObject,

    /// <summary>The request could not be mapped to a known operation.</summary>
    Unknown
}

/// <summary>
/// Parses incoming <see cref="HttpListenerRequest"/> instances into S3 operations
/// and typed request DTOs defined in <see cref="DataWarehouse.SDK.Storage.Fabric"/>.
/// </summary>
/// <remarks>
/// Supports path-style addressing (default) and virtual-hosted-style addressing.
/// Path-style: /bucket/key. Virtual-hosted: bucket.s3.localhost/key.
/// </remarks>
public sealed class S3RequestParser
{
    /// <summary>
    /// Determines which S3 operation the incoming HTTP request represents.
    /// </summary>
    /// <param name="request">The raw HTTP listener request.</param>
    /// <returns>The identified <see cref="S3Operation"/>.</returns>
    public S3Operation ParseOperation(HttpListenerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var method = request.HttpMethod.ToUpperInvariant();
        var (bucket, key) = ExtractBucketAndKey(request);
        var queryString = request.Url?.Query ?? string.Empty;
        var queryParams = ParseQueryString(queryString);

        // No bucket => ListBuckets (GET /)
        if (string.IsNullOrEmpty(bucket))
        {
            return method == "GET" ? S3Operation.ListBuckets : S3Operation.Unknown;
        }

        // Bucket-only operations (no key)
        if (string.IsNullOrEmpty(key))
        {
            // GET /bucket?list-type=2 => ListObjectsV2
            if (method == "GET" && queryParams.ContainsKey("list-type"))
            {
                return S3Operation.ListObjectsV2;
            }
            // GET /bucket (without list-type) => also ListObjectsV2 (default list behavior)
            if (method == "GET")
            {
                return S3Operation.ListObjectsV2;
            }

            return method switch
            {
                "PUT" => S3Operation.CreateBucket,
                "DELETE" => S3Operation.DeleteBucket,
                "HEAD" => S3Operation.Unknown, // HeadBucket not implemented
                _ => S3Operation.Unknown
            };
        }

        // Object-level operations (bucket + key present)
        switch (method)
        {
            case "GET":
                return S3Operation.GetObject;

            case "PUT":
                // PUT with x-amz-copy-source => CopyObject
                if (request.Headers["x-amz-copy-source"] != null)
                    return S3Operation.CopyObject;

                // PUT with partNumber + uploadId => UploadPart
                if (queryParams.ContainsKey("partNumber") && queryParams.ContainsKey("uploadId"))
                    return S3Operation.UploadPart;

                return S3Operation.PutObject;

            case "POST":
                // POST /bucket/key?uploads => InitiateMultipartUpload
                if (queryParams.ContainsKey("uploads"))
                    return S3Operation.InitiateMultipartUpload;

                // POST /bucket/key?uploadId=X => CompleteMultipartUpload
                if (queryParams.ContainsKey("uploadId"))
                    return S3Operation.CompleteMultipartUpload;

                return S3Operation.Unknown;

            case "DELETE":
                // DELETE /bucket/key?uploadId=X => AbortMultipartUpload
                if (queryParams.ContainsKey("uploadId"))
                    return S3Operation.AbortMultipartUpload;

                return S3Operation.DeleteObject;

            case "HEAD":
                return S3Operation.HeadObject;

            default:
                return S3Operation.Unknown;
        }
    }

    /// <summary>
    /// Extracts the bucket name and object key from the request URL.
    /// Supports path-style (/bucket/key) and virtual-hosted-style (bucket.s3.host/key) addressing.
    /// </summary>
    /// <param name="request">The raw HTTP listener request.</param>
    /// <returns>A tuple of (bucket, key) where either may be null if not present.</returns>
    public (string? Bucket, string? Key) ExtractBucketAndKey(HttpListenerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = request.Headers["Host"] ?? string.Empty;

        // Virtual-hosted-style: bucket.s3.localhost or bucket.s3.example.com
        if (host.Contains(".s3.", StringComparison.OrdinalIgnoreCase))
        {
            var dotIndex = host.IndexOf(".s3.", StringComparison.OrdinalIgnoreCase);
            var bucket = host[..dotIndex];
            var path = request.Url?.AbsolutePath?.TrimStart('/');
            var key = string.IsNullOrEmpty(path) ? null : path;
            return (bucket, key);
        }

        // Path-style: /bucket/key
        var rawPath = request.Url?.AbsolutePath ?? "/";
        var trimmed = rawPath.TrimStart('/');

        if (string.IsNullOrEmpty(trimmed))
            return (null, null);

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex < 0)
            return (trimmed, null);

        var bucketName = trimmed[..slashIndex];
        var objectKey = trimmed[(slashIndex + 1)..];

        return (bucketName, string.IsNullOrEmpty(objectKey) ? null : objectKey);
    }

    /// <summary>
    /// Parses a GET object request from the HTTP request.
    /// </summary>
    public S3GetObjectRequest ParseGetObject(HttpListenerRequest req, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNullOrEmpty(key);

        var queryParams = ParseQueryString(req.Url?.Query ?? string.Empty);

        return new S3GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = queryParams.GetValueOrDefault("versionId"),
            Range = req.Headers["Range"]
        };
    }

    /// <summary>
    /// Parses a PUT object request from the HTTP request.
    /// </summary>
    public S3PutObjectRequest ParsePutObject(HttpListenerRequest req, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNullOrEmpty(key);

        var metadata = ExtractUserMetadata(req);

        return new S3PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            Body = req.InputStream,
            ContentType = req.ContentType ?? "application/octet-stream",
            Metadata = metadata.Count > 0 ? metadata : null,
            StorageClass = req.Headers["x-amz-storage-class"]
        };
    }

    /// <summary>
    /// Parses a list objects (V2) request from the HTTP request.
    /// </summary>
    public S3ListObjectsRequest ParseListObjects(HttpListenerRequest req, string bucket)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);

        var queryParams = ParseQueryString(req.Url?.Query ?? string.Empty);

        int maxKeys = 1000;
        if (queryParams.TryGetValue("max-keys", out var maxKeysStr) &&
            int.TryParse(maxKeysStr, out var parsedMaxKeys))
        {
            maxKeys = Math.Clamp(parsedMaxKeys, 1, 1000);
        }

        return new S3ListObjectsRequest
        {
            BucketName = bucket,
            Prefix = queryParams.GetValueOrDefault("prefix"),
            Delimiter = queryParams.GetValueOrDefault("delimiter"),
            MaxKeys = maxKeys,
            ContinuationToken = queryParams.GetValueOrDefault("continuation-token")
        };
    }

    /// <summary>
    /// Parses an initiate multipart upload request.
    /// </summary>
    public S3InitiateMultipartRequest ParseInitiateMultipart(HttpListenerRequest req, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNullOrEmpty(key);

        var metadata = ExtractUserMetadata(req);

        return new S3InitiateMultipartRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = req.ContentType,
            Metadata = metadata.Count > 0 ? metadata : null
        };
    }

    /// <summary>
    /// Parses an upload part request.
    /// </summary>
    public S3UploadPartRequest ParseUploadPart(HttpListenerRequest req, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNullOrEmpty(key);

        var queryParams = ParseQueryString(req.Url?.Query ?? string.Empty);

        if (!queryParams.TryGetValue("partNumber", out var partNumStr) ||
            !int.TryParse(partNumStr, out var partNumber))
        {
            throw new ArgumentException("Missing or invalid partNumber query parameter.");
        }

        if (!queryParams.TryGetValue("uploadId", out var uploadId) ||
            string.IsNullOrEmpty(uploadId))
        {
            throw new ArgumentException("Missing uploadId query parameter.");
        }

        return new S3UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            Body = req.InputStream
        };
    }

    /// <summary>
    /// Parses a complete multipart upload request. The request body contains XML listing completed parts.
    /// </summary>
    public async Task<S3CompleteMultipartRequest> ParseCompleteMultipartAsync(
        HttpListenerRequest req, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNullOrEmpty(key);

        var queryParams = ParseQueryString(req.Url?.Query ?? string.Empty);

        if (!queryParams.TryGetValue("uploadId", out var uploadId) ||
            string.IsNullOrEmpty(uploadId))
        {
            throw new ArgumentException("Missing uploadId query parameter.");
        }

        // Parse the XML body: <CompleteMultipartUpload><Part><PartNumber>N</PartNumber><ETag>...</ETag></Part>...</CompleteMultipartUpload>
        var parts = new List<S3CompletedPart>();

        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var doc = XDocument.Parse(body);
            var root = doc.Root;
            if (root != null)
            {
                // Handle with or without namespace
                var ns = root.GetDefaultNamespace();
                foreach (var partElement in root.Descendants(ns + "Part"))
                {
                    var partNumberElement = partElement.Element(ns + "PartNumber");
                    var etagElement = partElement.Element(ns + "ETag");

                    if (partNumberElement != null && etagElement != null &&
                        int.TryParse(partNumberElement.Value, out var pn))
                    {
                        parts.Add(new S3CompletedPart
                        {
                            PartNumber = pn,
                            ETag = etagElement.Value.Trim('"')
                        });
                    }
                }
            }
        }

        return new S3CompleteMultipartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            Parts = parts.AsReadOnly()
        };
    }

    /// <summary>
    /// Parses a copy object request from the x-amz-copy-source header.
    /// </summary>
    public S3CopyObjectRequest ParseCopyObject(HttpListenerRequest req, string destBucket, string destKey)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNullOrEmpty(destBucket);
        ArgumentNullException.ThrowIfNullOrEmpty(destKey);

        var copySource = req.Headers["x-amz-copy-source"];
        if (string.IsNullOrEmpty(copySource))
        {
            throw new ArgumentException("Missing x-amz-copy-source header for CopyObject.");
        }

        // x-amz-copy-source format: /bucket/key or bucket/key (URL-encoded)
        var decoded = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slashIndex = decoded.IndexOf('/');

        if (slashIndex < 0)
        {
            throw new ArgumentException($"Invalid x-amz-copy-source format: '{copySource}'. Expected /bucket/key.");
        }

        var sourceBucket = decoded[..slashIndex];
        var sourceKey = decoded[(slashIndex + 1)..];

        return new S3CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            DestBucket = destBucket,
            DestKey = destKey
        };
    }

    #region Private Helpers

    /// <summary>
    /// Extracts user-defined metadata from x-amz-meta-* headers.
    /// </summary>
    private static Dictionary<string, string> ExtractUserMetadata(HttpListenerRequest req)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string prefix = "x-amz-meta-";

        for (int i = 0; i < req.Headers.Count; i++)
        {
            var headerName = req.Headers.GetKey(i);
            if (headerName != null &&
                headerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var metaKey = headerName[prefix.Length..];
                var metaValue = req.Headers[headerName];
                if (!string.IsNullOrEmpty(metaValue))
                {
                    metadata[metaKey] = metaValue;
                }
            }
        }

        return metadata;
    }

    /// <summary>
    /// Parses a query string into a dictionary, handling the leading '?' if present.
    /// </summary>
    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(queryString))
            return result;

        var qs = queryString.TrimStart('?');
        if (string.IsNullOrEmpty(qs))
            return result;

        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex >= 0)
            {
                var k = Uri.UnescapeDataString(pair[..eqIndex]);
                var v = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
                result[k] = v;
            }
            else
            {
                // Key-only parameter (e.g., ?uploads)
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
        }

        return result;
    }

    #endregion
}
