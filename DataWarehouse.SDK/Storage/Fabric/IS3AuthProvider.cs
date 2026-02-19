using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Fabric;

/// <summary>
/// Contract for verifying AWS Signature V4 authentication on incoming S3 requests.
/// </summary>
/// <remarks>
/// <para>
/// Implementations validate the Authorization header (or query-string parameters for presigned URLs)
/// against stored credentials. This supports both header-based and presigned URL authentication flows.
/// </para>
/// <para>
/// The canonical implementation will maintain an access key registry and compute HMAC-SHA256
/// signatures per the AWS Signature Version 4 specification.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server authentication")]
public interface IS3AuthProvider
{
    /// <summary>
    /// Validates an incoming S3 request's authorization against the AWS Signature V4 specification.
    /// </summary>
    /// <param name="context">The authentication context containing HTTP method, path, headers, and payload hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An authentication result indicating success or failure with details.</returns>
    Task<S3AuthResult> AuthenticateAsync(S3AuthContext context, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the credentials associated with a given access key ID.
    /// </summary>
    /// <param name="accessKeyId">The AWS-style access key ID to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The credentials including secret key and permissions.</returns>
    Task<S3Credentials> GetCredentialsAsync(string accessKeyId, CancellationToken ct = default);
}

/// <summary>
/// Context data extracted from an incoming HTTP request for S3 authentication verification.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server authentication")]
public record S3AuthContext
{
    /// <summary>The HTTP method (GET, PUT, POST, DELETE, HEAD).</summary>
    public required string HttpMethod { get; init; }

    /// <summary>The request URI path (e.g., "/bucket/key").</summary>
    public required string Path { get; init; }

    /// <summary>The query string portion of the request URI.</summary>
    public required string QueryString { get; init; }

    /// <summary>The HTTP request headers used in signature computation.</summary>
    public required IDictionary<string, string> Headers { get; init; }

    /// <summary>The Authorization header value, if present (header-based auth).</summary>
    public string? AuthorizationHeader { get; init; }

    /// <summary>SHA-256 hash of the request payload, or null for unsigned payloads.</summary>
    public byte[]? PayloadHash { get; init; }
}

/// <summary>
/// Result of S3 authentication verification.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server authentication")]
public record S3AuthResult
{
    /// <summary>Whether the request was successfully authenticated.</summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>The access key ID of the authenticated caller, if successful.</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>Error description when authentication fails.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The internal user ID associated with the access key, if available.</summary>
    public string? UserId { get; init; }
}

/// <summary>
/// S3-compatible credentials with access permissions.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 63: S3-Compatible Server authentication")]
public record S3Credentials
{
    /// <summary>The AWS-style access key ID.</summary>
    public required string AccessKeyId { get; init; }

    /// <summary>The AWS-style secret access key used for signature computation.</summary>
    public required string SecretAccessKey { get; init; }

    /// <summary>Optional internal user ID this credential belongs to.</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Optional set of bucket names this credential is authorized to access.
    /// Null means access to all buckets.
    /// </summary>
    public IReadOnlySet<string>? AllowedBuckets { get; init; }

    /// <summary>Whether this credential has administrative privileges.</summary>
    public bool IsAdmin { get; init; }
}
