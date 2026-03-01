using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using DataWarehouse.SDK.Storage.Fabric;

namespace DataWarehouse.Plugins.UniversalFabric.S3Server;

/// <summary>
/// Implements AWS Signature Version 4 verification for incoming S3 requests.
/// Supports both header-based Authorization and query-string (presigned URL) authentication.
/// </summary>
/// <remarks>
/// <para>
/// This implementation follows the exact AWS Signature Version 4 signing process:
/// 1. Create a canonical request
/// 2. Create a string to sign
/// 3. Calculate the signature using a derived signing key
/// 4. Compare signatures using constant-time comparison
/// </para>
/// <para>
/// Reference: https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-authenticating-requests.html
/// </para>
/// </remarks>
public sealed class S3SignatureV4 : IS3AuthProvider
{
    private readonly S3CredentialStore _credentialStore;

    /// <summary>
    /// Maximum allowed timestamp skew in minutes before a request is rejected.
    /// AWS uses 15 minutes; we follow the same convention.
    /// </summary>
    private const int MaxTimestampSkewMinutes = 15;

    /// <summary>
    /// The signing algorithm identifier used in AWS Signature V4.
    /// </summary>
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Initializes a new instance of <see cref="S3SignatureV4"/> with the specified credential store.
    /// </summary>
    /// <param name="credentialStore">The credential store for looking up access key/secret key pairs.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="credentialStore"/> is null.</exception>
    public S3SignatureV4(S3CredentialStore credentialStore)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    /// <inheritdoc />
    public Task<S3AuthResult> AuthenticateAsync(S3AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            // Determine if this is header-based or query-string (presigned URL) auth
            if (!string.IsNullOrEmpty(context.AuthorizationHeader))
            {
                return Task.FromResult(VerifyHeaderAuth(context));
            }

            // Check for presigned URL parameters
            if (!string.IsNullOrEmpty(context.QueryString) &&
                context.QueryString.Contains("X-Amz-Algorithm", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(VerifyPresignedUrlAuth(context));
            }

            return Task.FromResult(new S3AuthResult
            {
                IsAuthenticated = false,
                ErrorMessage = "No authentication credentials provided"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new S3AuthResult
            {
                IsAuthenticated = false,
                ErrorMessage = $"Authentication error: {ex.Message}"
            });
        }
    }

    /// <inheritdoc />
    public Task<S3Credentials> GetCredentialsAsync(string accessKeyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accessKeyId);

        var creds = _credentialStore.GetCredentials(accessKeyId);
        if (creds is null)
        {
            // P2-4574: Do not include the access key ID in the error — information disclosure.
            throw new KeyNotFoundException("No credentials found for the provided access key.");
        }
        return Task.FromResult(creds);
    }

    /// <summary>
    /// Verifies an Authorization header-based AWS Signature V4 request.
    /// </summary>
    private S3AuthResult VerifyHeaderAuth(S3AuthContext context)
    {
        // Parse Authorization header:
        // AWS4-HMAC-SHA256 Credential=AKID/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;..., Signature=SIG
        var authHeader = context.AuthorizationHeader!;
        if (!authHeader.StartsWith(Algorithm + " ", StringComparison.Ordinal))
        {
            return Fail("Unsupported authorization algorithm");
        }

        var parts = ParseAuthorizationHeader(authHeader);
        if (parts is null)
        {
            return Fail("Malformed Authorization header");
        }

        var (accessKeyId, credentialScope, signedHeadersList, providedSignature) = parts.Value;

        // Look up credentials
        var credentials = _credentialStore.GetCredentials(accessKeyId);
        if (credentials is null)
        {
            // P2-4574: Do not leak access key ID in error response — use generic message.
            return Fail("InvalidAccessKeyId: The AWS access key ID provided does not exist.");
        }

        // Parse credential scope: date/region/service/aws4_request
        var scopeParts = credentialScope.Split('/');
        if (scopeParts.Length != 4 || scopeParts[3] != "aws4_request")
        {
            return Fail("Invalid credential scope format");
        }

        var dateStamp = scopeParts[0];
        var region = scopeParts[1];
        var service = scopeParts[2];

        // Validate timestamp skew
        var timestamp = GetTimestamp(context.Headers);
        if (timestamp is null)
        {
            return Fail("Missing X-Amz-Date or Date header");
        }

        if (!ValidateTimestamp(timestamp))
        {
            return Fail("Request timestamp is outside the allowed time window");
        }

        // Build canonical request
        var signedHeaders = signedHeadersList.Split(';');
        var canonicalRequest = BuildCanonicalRequest(
            context.HttpMethod,
            context.Path,
            context.QueryString,
            context.Headers,
            signedHeaders,
            GetPayloadHash(context));

        // Build string to sign
        var stringToSign = BuildStringToSign(timestamp, $"{dateStamp}/{region}/{service}/aws4_request", canonicalRequest);

        // Derive signing key
        var signingKey = DeriveSigningKey(credentials.SecretAccessKey, dateStamp, region, service);

        // Calculate expected signature
        var expectedSignature = CalculateSignature(signingKey, stringToSign);

        // Constant-time comparison
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);

        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            return Fail("Signature does not match");
        }

        return new S3AuthResult
        {
            IsAuthenticated = true,
            AccessKeyId = accessKeyId,
            UserId = credentials.UserId
        };
    }

    /// <summary>
    /// Verifies a presigned URL (query string) based AWS Signature V4 request.
    /// </summary>
    private S3AuthResult VerifyPresignedUrlAuth(S3AuthContext context)
    {
        var queryParams = ParseQueryString(context.QueryString);

        if (!queryParams.TryGetValue("X-Amz-Algorithm", out var algorithm) || algorithm != Algorithm)
        {
            return Fail("Unsupported presigned URL algorithm");
        }

        if (!queryParams.TryGetValue("X-Amz-Credential", out var credential) ||
            !queryParams.TryGetValue("X-Amz-Signature", out var providedSignature) ||
            !queryParams.TryGetValue("X-Amz-SignedHeaders", out var signedHeadersList) ||
            !queryParams.TryGetValue("X-Amz-Date", out var timestamp))
        {
            return Fail("Missing required presigned URL parameters");
        }

        // Parse credential: AKID/date/region/service/aws4_request
        var credParts = credential.Split('/');
        if (credParts.Length != 5)
        {
            return Fail("Invalid X-Amz-Credential format");
        }

        var accessKeyId = credParts[0];
        var dateStamp = credParts[1];
        var region = credParts[2];
        var service = credParts[3];

        // Validate timestamp and expiration
        if (!ValidateTimestamp(timestamp))
        {
            return Fail("Presigned URL timestamp is outside the allowed time window");
        }

        if (queryParams.TryGetValue("X-Amz-Expires", out var expiresStr) &&
            int.TryParse(expiresStr, out var expiresSeconds))
        {
            if (DateTime.TryParseExact(timestamp, "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var signedAt))
            {
                if (DateTime.UtcNow > signedAt.AddSeconds(expiresSeconds))
                {
                    return Fail("Presigned URL has expired");
                }
            }
        }

        var credentials = _credentialStore.GetCredentials(accessKeyId);
        if (credentials is null)
        {
            // P2-4574: Do not leak access key ID in error response — use generic message.
            return Fail("InvalidAccessKeyId: The AWS access key ID provided does not exist.");
        }

        // For presigned URLs, build canonical query string WITHOUT X-Amz-Signature
        var canonicalQueryString = BuildCanonicalQueryStringExcluding(context.QueryString, "X-Amz-Signature");

        var signedHeaders = signedHeadersList.Split(';');
        var canonicalHeaders = BuildCanonicalHeaders(context.Headers, signedHeaders);

        // For presigned URLs, payload hash is always UNSIGNED-PAYLOAD
        var canonicalRequest = string.Join("\n",
            context.HttpMethod,
            CanonicalizeUri(context.Path),
            canonicalQueryString,
            canonicalHeaders,
            signedHeadersList,
            "UNSIGNED-PAYLOAD");

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = BuildStringToSign(timestamp, credentialScope, canonicalRequest);
        var signingKey = DeriveSigningKey(credentials.SecretAccessKey, dateStamp, region, service);
        var expectedSignature = CalculateSignature(signingKey, stringToSign);

        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);

        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            return Fail("Presigned URL signature does not match");
        }

        return new S3AuthResult
        {
            IsAuthenticated = true,
            AccessKeyId = accessKeyId,
            UserId = credentials.UserId
        };
    }

    /// <summary>
    /// Parses the Authorization header into its component parts.
    /// </summary>
    /// <returns>
    /// A tuple of (accessKeyId, credentialScope, signedHeaders, signature), or null if parsing fails.
    /// </returns>
    private static (string AccessKeyId, string CredentialScope, string SignedHeaders, string Signature)?
        ParseAuthorizationHeader(string authHeader)
    {
        // Remove algorithm prefix
        var content = authHeader.Substring(Algorithm.Length + 1).Trim();

        string? credential = null;
        string? signedHeaders = null;
        string? signature = null;

        // Parse key=value pairs separated by ", "
        foreach (var part in content.Split(',', StringSplitOptions.TrimEntries))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = part.Substring(0, eqIdx).Trim();
            var value = part.Substring(eqIdx + 1).Trim();

            switch (key)
            {
                case "Credential":
                    credential = value;
                    break;
                case "SignedHeaders":
                    signedHeaders = value;
                    break;
                case "Signature":
                    signature = value;
                    break;
            }
        }

        if (credential is null || signedHeaders is null || signature is null)
        {
            return null;
        }

        // Extract access key ID (everything before the first '/')
        var slashIdx = credential.IndexOf('/');
        if (slashIdx < 0) return null;

        var accessKeyId = credential.Substring(0, slashIdx);
        var credentialScope = credential.Substring(slashIdx + 1);

        return (accessKeyId, credentialScope, signedHeaders, signature);
    }

    /// <summary>
    /// Builds the canonical request string per the AWS Signature V4 specification.
    /// </summary>
    private static string BuildCanonicalRequest(
        string httpMethod,
        string path,
        string queryString,
        IDictionary<string, string> headers,
        string[] signedHeaders,
        string payloadHash)
    {
        var canonicalUri = CanonicalizeUri(path);
        var canonicalQueryString = BuildCanonicalQueryString(queryString);
        var canonicalHeaders = BuildCanonicalHeaders(headers, signedHeaders);
        var signedHeadersList = string.Join(";", signedHeaders.Select(h => h.ToLowerInvariant()).OrderBy(h => h));

        return string.Join("\n",
            httpMethod,
            canonicalUri,
            canonicalQueryString,
            canonicalHeaders,
            signedHeadersList,
            payloadHash);
    }

    /// <summary>
    /// URL-encodes the URI path, preserving forward slashes.
    /// </summary>
    private static string CanonicalizeUri(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        // Ensure leading slash
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // Encode each path segment individually, preserving '/'
        var segments = path.Split('/');
        var encoded = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0) encoded.Append('/');
            encoded.Append(Uri.EscapeDataString(segments[i]));
        }

        return encoded.ToString();
    }

    /// <summary>
    /// Builds the canonical query string with parameters sorted by name and URL-encoded.
    /// </summary>
    private static string BuildCanonicalQueryString(string queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        // Remove leading '?' if present
        if (queryString.StartsWith('?'))
        {
            queryString = queryString.Substring(1);
        }

        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx < 0)
                {
                    return (Key: Uri.EscapeDataString(pair), Value: string.Empty);
                }
                return (Key: Uri.EscapeDataString(Uri.UnescapeDataString(pair.Substring(0, eqIdx))),
                        Value: Uri.EscapeDataString(Uri.UnescapeDataString(pair.Substring(eqIdx + 1))));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>
    /// Builds the canonical query string excluding the specified parameter (used for presigned URLs).
    /// </summary>
    private static string BuildCanonicalQueryStringExcluding(string queryString, string excludeParam)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        if (queryString.StartsWith('?'))
        {
            queryString = queryString.Substring(1);
        }

        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx < 0)
                {
                    return (Key: pair, EncodedKey: Uri.EscapeDataString(pair), Value: string.Empty);
                }
                var rawKey = Uri.UnescapeDataString(pair.Substring(0, eqIdx));
                return (Key: rawKey,
                        EncodedKey: Uri.EscapeDataString(rawKey),
                        Value: Uri.EscapeDataString(Uri.UnescapeDataString(pair.Substring(eqIdx + 1))));
            })
            .Where(p => !p.Key.Equals(excludeParam, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.EncodedKey, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join("&", pairs.Select(p => $"{p.EncodedKey}={p.Value}"));
    }

    /// <summary>
    /// Builds the canonical headers string: lowercase, trimmed, sorted alphabetically.
    /// Each header ends with a newline.
    /// </summary>
    private static string BuildCanonicalHeaders(IDictionary<string, string> headers, string[] signedHeaders)
    {
        var sortedHeaders = signedHeaders
            .Select(h => h.ToLowerInvariant().Trim())
            .OrderBy(h => h, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var header in sortedHeaders)
        {
            if (headers.TryGetValue(header, out var value))
            {
                sb.Append(header).Append(':').Append(value.Trim()).Append('\n');
            }
            else
            {
                // Try case-insensitive lookup
                var match = headers.FirstOrDefault(h =>
                    h.Key.Equals(header, StringComparison.OrdinalIgnoreCase));
                if (match.Key is not null)
                {
                    sb.Append(header).Append(':').Append(match.Value.Trim()).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the string to sign: algorithm + timestamp + scope + hash(canonical request).
    /// </summary>
    private static string BuildStringToSign(string timestamp, string credentialScope, string canonicalRequest)
    {
        var canonicalRequestHash = HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        return string.Join("\n",
            Algorithm,
            timestamp,
            credentialScope,
            canonicalRequestHash);
    }

    /// <summary>
    /// Derives the signing key through the HMAC-SHA256 chain:
    /// kDate = HMAC("AWS4" + secretKey, dateStamp)
    /// kRegion = HMAC(kDate, region)
    /// kService = HMAC(kRegion, service)
    /// kSigning = HMAC(kService, "aws4_request")
    /// </summary>
    private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, "aws4_request");
        return kSigning;
    }

    /// <summary>
    /// Calculates the final signature as a lowercase hex string.
    /// </summary>
    private static string CalculateSignature(byte[] signingKey, string stringToSign)
    {
        var signatureBytes = HmacSha256(signingKey, stringToSign);
        return HexEncode(signatureBytes);
    }

    /// <summary>
    /// Retrieves the payload hash from the context. Uses x-amz-content-sha256 header
    /// if present, otherwise computes from PayloadHash property.
    /// </summary>
    private static string GetPayloadHash(S3AuthContext context)
    {
        // Check for x-amz-content-sha256 header (case-insensitive)
        foreach (var header in context.Headers)
        {
            if (header.Key.Equals("x-amz-content-sha256", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        // Use the provided payload hash bytes
        if (context.PayloadHash is not null)
        {
            return HexEncode(context.PayloadHash);
        }

        // Default to UNSIGNED-PAYLOAD for streaming/chunked uploads
        return "UNSIGNED-PAYLOAD";
    }

    /// <summary>
    /// Gets the request timestamp from X-Amz-Date or Date header.
    /// </summary>
    private static string? GetTimestamp(IDictionary<string, string> headers)
    {
        // Prefer X-Amz-Date
        foreach (var header in headers)
        {
            if (header.Key.Equals("x-amz-date", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        // Fall back to Date header, converting to ISO 8601 basic format
        foreach (var header in headers)
        {
            if (header.Key.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(header.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var date))
                {
                    return date.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that the request timestamp is within the allowed skew window.
    /// </summary>
    private static bool ValidateTimestamp(string timestamp)
    {
        if (!DateTime.TryParseExact(timestamp, "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var requestTime))
        {
            return false;
        }

        var skew = Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes);
        return skew <= MaxTimestampSkewMinutes;
    }

    /// <summary>
    /// Parses a query string into a dictionary.
    /// </summary>
    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        // Finding 4587: AWS query param names are case-sensitive — use Ordinal, not OrdinalIgnoreCase.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(queryString))
        {
            return result;
        }

        if (queryString.StartsWith('?'))
        {
            queryString = queryString.Substring(1);
        }

        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                result[Uri.UnescapeDataString(pair.Substring(0, eqIdx))] =
                    Uri.UnescapeDataString(pair.Substring(eqIdx + 1));
            }
        }

        return result;
    }

    /// <summary>
    /// Computes HMAC-SHA256 of the given data using the specified key.
    /// </summary>
    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>
    /// Converts a byte array to a lowercase hexadecimal string.
    /// </summary>
    private static string HexEncode(byte[] data)
    {
        return Convert.ToHexStringLower(data);
    }

    /// <summary>
    /// Creates a failed authentication result with the specified error message.
    /// </summary>
    private static S3AuthResult Fail(string message)
    {
        return new S3AuthResult
        {
            IsAuthenticated = false,
            ErrorMessage = message
        };
    }
}
