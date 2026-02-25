using System;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Fabric;

#region Bucket Types

/// <summary>
/// Request to list all accessible buckets. Currently empty; authentication
/// context is handled separately via <see cref="IS3AuthProvider"/>.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3ListBucketsRequest;

/// <summary>
/// Response containing the list of buckets and the owner identifier.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3ListBucketsResponse
{
    /// <summary>The list of buckets accessible to the authenticated user.</summary>
    public required IReadOnlyList<S3Bucket> Buckets { get; init; }

    /// <summary>The owner identifier (access key ID or user name).</summary>
    public required string Owner { get; init; }
}

/// <summary>
/// Represents an S3 bucket with its name and creation timestamp.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3Bucket
{
    /// <summary>The bucket name.</summary>
    public required string Name { get; init; }

    /// <summary>When the bucket was created (UTC).</summary>
    public required DateTime CreationDate { get; init; }
}

/// <summary>
/// Request to create a new S3 bucket.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CreateBucketRequest
{
    /// <summary>The name for the new bucket. Must be globally unique within the server.</summary>
    public required string BucketName { get; init; }

    /// <summary>Optional region constraint for the bucket.</summary>
    public string? Region { get; init; }
}

/// <summary>
/// Response after successfully creating a bucket.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CreateBucketResponse
{
    /// <summary>The name of the created bucket.</summary>
    public required string BucketName { get; init; }

    /// <summary>The location/region of the created bucket.</summary>
    public required string Location { get; init; }
}

#endregion

#region Object Types

/// <summary>
/// Represents an object stored in an S3-compatible bucket.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3Object
{
    /// <summary>The object key (path within the bucket).</summary>
    public required string Key { get; init; }

    /// <summary>The object size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>When the object was last modified (UTC).</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>The entity tag (hash) of the object content.</summary>
    public required string ETag { get; init; }

    /// <summary>The storage class (e.g., STANDARD, GLACIER).</summary>
    public string StorageClass { get; init; } = "STANDARD";
}

/// <summary>
/// Request to retrieve an object from a bucket.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3GetObjectRequest
{
    /// <summary>The bucket containing the object.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>Optional version ID for versioned buckets.</summary>
    public string? VersionId { get; init; }

    /// <summary>Optional HTTP Range header value for partial content retrieval (e.g., "bytes=0-1023").</summary>
    public string? Range { get; init; }
}

/// <summary>
/// Response containing the retrieved object data and metadata.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3GetObjectResponse
{
    /// <summary>The object data stream. Callers must dispose this stream.</summary>
    public required Stream Body { get; init; }

    /// <summary>The MIME type of the object content.</summary>
    public required string ContentType { get; init; }

    /// <summary>The size of the object body in bytes.</summary>
    public required long ContentLength { get; init; }

    /// <summary>The entity tag (hash) of the object content.</summary>
    public required string ETag { get; init; }

    /// <summary>User-defined metadata key-value pairs.</summary>
    public required IDictionary<string, string> Metadata { get; init; }

    /// <summary>When the object was last modified (UTC).</summary>
    public required DateTime LastModified { get; init; }
}

/// <summary>
/// Request to store an object in a bucket.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3PutObjectRequest
{
    /// <summary>The target bucket.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>The object data stream.</summary>
    public required Stream Body { get; init; }

    /// <summary>Optional MIME type. Defaults to "application/octet-stream" if not specified.</summary>
    public string? ContentType { get; init; }

    /// <summary>Optional user-defined metadata key-value pairs.</summary>
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>Optional storage class (e.g., STANDARD, GLACIER).</summary>
    public string? StorageClass { get; init; }
}

/// <summary>
/// Response after successfully storing an object.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3PutObjectResponse
{
    /// <summary>The entity tag (hash) of the stored content.</summary>
    public required string ETag { get; init; }

    /// <summary>The version ID if the bucket has versioning enabled.</summary>
    public string? VersionId { get; init; }
}

/// <summary>
/// Response for HEAD object requests containing metadata without the body.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3HeadObjectResponse
{
    /// <summary>The size of the object in bytes.</summary>
    public required long ContentLength { get; init; }

    /// <summary>The MIME type of the object content.</summary>
    public required string ContentType { get; init; }

    /// <summary>The entity tag (hash) of the object content.</summary>
    public required string ETag { get; init; }

    /// <summary>When the object was last modified (UTC).</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>User-defined metadata key-value pairs.</summary>
    public required IDictionary<string, string> Metadata { get; init; }
}

/// <summary>
/// Request to list objects in a bucket using the V2 list API.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3ListObjectsRequest
{
    /// <summary>The bucket to list objects from.</summary>
    public required string BucketName { get; init; }

    /// <summary>Optional prefix to filter objects by key path.</summary>
    public string? Prefix { get; init; }

    /// <summary>Optional delimiter for grouping keys (typically "/").</summary>
    public string? Delimiter { get; init; }

    /// <summary>Maximum number of keys to return per response. Defaults to 1000 (S3 maximum).</summary>
    public int MaxKeys { get; init; } = 1000;

    /// <summary>Continuation token from a previous truncated response for pagination.</summary>
    public string? ContinuationToken { get; init; }
}

/// <summary>
/// Response containing the listed objects and pagination information.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3ListObjectsResponse
{
    /// <summary>The list of objects matching the request criteria.</summary>
    public required IReadOnlyList<S3Object> Contents { get; init; }

    /// <summary>Common prefixes when a delimiter is used (virtual directory listing).</summary>
    public required IReadOnlyList<string> CommonPrefixes { get; init; }

    /// <summary>Whether the response is truncated and more results are available.</summary>
    public required bool IsTruncated { get; init; }

    /// <summary>Token to use in the next request to retrieve additional results.</summary>
    public string? NextContinuationToken { get; init; }

    /// <summary>The number of keys returned in this response.</summary>
    public required int KeyCount { get; init; }
}

#endregion

#region Multipart Upload Types

/// <summary>
/// Request to initiate a multipart upload for large objects.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3InitiateMultipartRequest
{
    /// <summary>The target bucket.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>Optional MIME type for the final assembled object.</summary>
    public string? ContentType { get; init; }

    /// <summary>Optional user-defined metadata for the final assembled object.</summary>
    public IDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Response after initiating a multipart upload, containing the upload ID.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3InitiateMultipartResponse
{
    /// <summary>The upload ID to use for subsequent part upload and completion requests.</summary>
    public required string UploadId { get; init; }

    /// <summary>The target bucket.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }
}

/// <summary>
/// Request to upload a single part of a multipart upload.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3UploadPartRequest
{
    /// <summary>The target bucket.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>The upload ID from the initiate multipart response.</summary>
    public required string UploadId { get; init; }

    /// <summary>The part number (1-10000).</summary>
    public required int PartNumber { get; init; }

    /// <summary>The part data stream.</summary>
    public required Stream Body { get; init; }
}

/// <summary>
/// Response after uploading a part, containing the ETag for completion.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3UploadPartResponse
{
    /// <summary>The entity tag (hash) of the uploaded part. Required for completion.</summary>
    public required string ETag { get; init; }

    /// <summary>The part number that was uploaded.</summary>
    public required int PartNumber { get; init; }
}

/// <summary>
/// Represents a completed part reference used when finalizing a multipart upload.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CompletedPart
{
    /// <summary>The part number.</summary>
    public required int PartNumber { get; init; }

    /// <summary>The ETag returned when the part was uploaded.</summary>
    public required string ETag { get; init; }
}

/// <summary>
/// Request to complete a multipart upload by assembling uploaded parts.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CompleteMultipartRequest
{
    /// <summary>The target bucket.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>The upload ID from the initiate multipart response.</summary>
    public required string UploadId { get; init; }

    /// <summary>The list of completed parts with their ETags, in part number order.</summary>
    public required IReadOnlyList<S3CompletedPart> Parts { get; init; }
}

/// <summary>
/// Response after completing a multipart upload.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CompleteMultipartResponse
{
    /// <summary>The entity tag of the assembled object.</summary>
    public required string ETag { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>The bucket name.</summary>
    public required string BucketName { get; init; }
}

#endregion

#region Presigned URL Types

/// <summary>
/// HTTP methods supported for presigned URL generation.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public enum S3PresignedMethod
{
    /// <summary>Presigned URL for downloading an object.</summary>
    GET,

    /// <summary>Presigned URL for uploading an object.</summary>
    PUT,

    /// <summary>Presigned URL for deleting an object.</summary>
    DELETE
}

/// <summary>
/// Request to generate a time-limited presigned URL for an object.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3PresignedUrlRequest
{
    /// <summary>The bucket containing the object.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>The HTTP method the presigned URL will authorize.</summary>
    public required S3PresignedMethod Method { get; init; }

    /// <summary>How long the presigned URL remains valid.</summary>
    public required TimeSpan Expiration { get; init; }
}

#endregion

#region Copy Types

/// <summary>
/// Request to copy an object from one location to another.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CopyObjectRequest
{
    /// <summary>The source bucket.</summary>
    public required string SourceBucket { get; init; }

    /// <summary>The source object key.</summary>
    public required string SourceKey { get; init; }

    /// <summary>The destination bucket.</summary>
    public required string DestBucket { get; init; }

    /// <summary>The destination object key.</summary>
    public required string DestKey { get; init; }
}

/// <summary>
/// Response after copying an object.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public record S3CopyObjectResponse
{
    /// <summary>The entity tag of the copied object.</summary>
    public required string ETag { get; init; }

    /// <summary>When the copy was completed (UTC).</summary>
    public required DateTime LastModified { get; init; }
}

#endregion
