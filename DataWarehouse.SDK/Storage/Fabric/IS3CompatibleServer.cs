using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Fabric;

/// <summary>
/// Contract for an S3-compatible HTTP endpoint that enables DataWarehouse
/// to serve as a drop-in replacement for MinIO/S3 object storage.
/// </summary>
/// <remarks>
/// <para>
/// Implementations expose a standards-compliant S3 API over HTTP, supporting
/// bucket management, object CRUD, multipart uploads, presigned URLs, and object copy.
/// AWS Signature V4 authentication is delegated to <see cref="IS3AuthProvider"/>.
/// </para>
/// <para>
/// The actual HTTP server implementation is provided by plan 63-06.
/// This interface defines the operational contract only.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server")]
public interface IS3CompatibleServer : IDisposable, IAsyncDisposable
{
    #region Server Lifecycle

    /// <summary>
    /// Starts the S3-compatible HTTP server with the specified options.
    /// </summary>
    /// <param name="options">Server configuration including host, port, TLS, and auth settings.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(S3ServerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops the S3-compatible HTTP server, draining active requests.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets whether the server is currently listening for requests.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the URL the server is listening on, or null if not running.
    /// </summary>
    string? ListenUrl { get; }

    #endregion

    #region Bucket Operations

    /// <summary>Lists all buckets accessible to the authenticated user.</summary>
    Task<S3ListBucketsResponse> ListBucketsAsync(S3ListBucketsRequest request, CancellationToken ct = default);

    /// <summary>Creates a new bucket with the specified name and optional region.</summary>
    Task<S3CreateBucketResponse> CreateBucketAsync(S3CreateBucketRequest request, CancellationToken ct = default);

    /// <summary>Deletes an empty bucket. Fails if the bucket contains objects.</summary>
    Task DeleteBucketAsync(string bucketName, CancellationToken ct = default);

    /// <summary>Checks whether a bucket with the given name exists.</summary>
    Task<bool> BucketExistsAsync(string bucketName, CancellationToken ct = default);

    #endregion

    #region Object Operations

    /// <summary>Retrieves an object's data and metadata from a bucket.</summary>
    Task<S3GetObjectResponse> GetObjectAsync(S3GetObjectRequest request, CancellationToken ct = default);

    /// <summary>Stores an object in a bucket, creating or overwriting as needed.</summary>
    Task<S3PutObjectResponse> PutObjectAsync(S3PutObjectRequest request, CancellationToken ct = default);

    /// <summary>Permanently deletes an object from a bucket.</summary>
    Task DeleteObjectAsync(string bucketName, string key, CancellationToken ct = default);

    /// <summary>Retrieves object metadata without fetching the object body.</summary>
    Task<S3HeadObjectResponse> HeadObjectAsync(string bucketName, string key, CancellationToken ct = default);

    /// <summary>Lists objects in a bucket using the V2 list API with optional prefix filtering and pagination.</summary>
    Task<S3ListObjectsResponse> ListObjectsV2Async(S3ListObjectsRequest request, CancellationToken ct = default);

    #endregion

    #region Multipart Upload

    /// <summary>Initiates a multipart upload and returns an upload ID for subsequent part uploads.</summary>
    Task<S3InitiateMultipartResponse> InitiateMultipartUploadAsync(S3InitiateMultipartRequest request, CancellationToken ct = default);

    /// <summary>Uploads a single part of a multipart upload.</summary>
    Task<S3UploadPartResponse> UploadPartAsync(S3UploadPartRequest request, CancellationToken ct = default);

    /// <summary>Completes a multipart upload by assembling previously uploaded parts.</summary>
    Task<S3CompleteMultipartResponse> CompleteMultipartUploadAsync(S3CompleteMultipartRequest request, CancellationToken ct = default);

    /// <summary>Aborts an in-progress multipart upload, releasing uploaded parts.</summary>
    Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken ct = default);

    #endregion

    #region Presigned URLs

    /// <summary>Generates a time-limited presigned URL for accessing an object without authentication.</summary>
    Task<string> GeneratePresignedUrlAsync(S3PresignedUrlRequest request, CancellationToken ct = default);

    #endregion

    #region Copy

    /// <summary>Copies an object from one location to another, potentially across buckets.</summary>
    Task<S3CopyObjectResponse> CopyObjectAsync(S3CopyObjectRequest request, CancellationToken ct = default);

    #endregion
}

/// <summary>
/// Configuration options for the S3-compatible HTTP server.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server configuration")]
public record S3ServerOptions
{
    /// <summary>The network interface to bind to. Defaults to all interfaces.</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>The TCP port to listen on (1-65535). Defaults to 9000 (MinIO default).</summary>
    public int Port
    {
        get => _port;
        init
        {
            if (value < 1 || value > 65535)
                throw new ArgumentOutOfRangeException(nameof(Port), value, "Port must be between 1 and 65535.");
            _port = value;
        }
    }
    private readonly int _port = 9000;

    /// <summary>Whether to enable TLS/HTTPS for the endpoint.</summary>
    public bool UseTls { get; init; }

    /// <summary>Path to the TLS certificate file (PEM or PFX). Required when <see cref="UseTls"/> is true.</summary>
    public string? TlsCertPath { get; init; }

    /// <summary>Path to the TLS private key file. Required for PEM certificates.</summary>
    public string? TlsKeyPath { get; init; }

    /// <summary>The default AWS region for this server. Defaults to "us-east-1".</summary>
    public string DefaultRegion { get; init; } = "us-east-1";

    /// <summary>Maximum allowed request body size in bytes. Defaults to 5 GB.</summary>
    public long MaxRequestBodyBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Minimum multipart chunk size in bytes. Defaults to 5 MB (S3 minimum). Must be at least 5 MB (S3 requirement).</summary>
    public int MultipartChunkSize
    {
        get => _multipartChunkSize;
        init
        {
            const int S3MinChunkSize = 5 * 1024 * 1024; // 5 MB
            if (value < S3MinChunkSize)
                throw new ArgumentOutOfRangeException(nameof(MultipartChunkSize), value,
                    $"MultipartChunkSize must be at least {S3MinChunkSize} bytes (5 MB, per S3 specification).");
            _multipartChunkSize = value;
        }
    }
    private readonly int _multipartChunkSize = 5 * 1024 * 1024;

    /// <summary>Maximum time allowed for a single request. Defaults to 30 minutes for large uploads.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Authentication provider for AWS Signature V4 verification.
    /// <para>
    /// <strong>Security warning</strong>: When <see langword="null"/>, the server operates in
    /// anonymous (open) mode and any client can access all buckets and objects without authentication.
    /// Always supply an <see cref="IS3AuthProvider"/> in production deployments.
    /// </para>
    /// </summary>
    public IS3AuthProvider? AuthProvider { get; init; }

    /// <summary>
    /// Secret key used to sign presigned URLs (HMAC-SHA256).
    /// Must be set to a strong, randomly-generated secret before enabling presigned URL generation.
    /// Throws <see cref="InvalidOperationException"/> at URL generation time if not configured.
    /// </summary>
    public string? PresignSecret { get; init; }
}
