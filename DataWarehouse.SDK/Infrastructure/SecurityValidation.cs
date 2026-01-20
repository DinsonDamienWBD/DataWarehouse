using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// SECURITY VALIDATION - Path Traversal Prevention & Input Validation
// ============================================================================

/// <summary>
/// Provides security validation utilities to prevent path traversal,
/// injection attacks, and other common vulnerabilities.
/// </summary>
public static class SecurityValidation
{
    /// <summary>
    /// Maximum allowed message size in bytes (default 64MB).
    /// </summary>
    public static long MaxMessageSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Maximum allowed path length.
    /// </summary>
    public const int MaxPathLength = 4096;

    /// <summary>
    /// Characters that are not allowed in path segments.
    /// </summary>
    private static readonly char[] InvalidPathChars = { '\0', '\r', '\n', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>
    /// Dangerous path segments that could lead to traversal.
    /// </summary>
    private static readonly string[] DangerousSegments = { "..", "..\\", "../", "..." };

    /// <summary>
    /// Validates and sanitizes a file path to prevent path traversal attacks.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <param name="basePath">Optional base path that the result must be under.</param>
    /// <returns>The sanitized, canonical path.</returns>
    /// <exception cref="SecurityException">If the path is invalid or attempts traversal.</exception>
    public static string ValidatePath(string path, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new SecurityException("Path cannot be null or empty");

        if (path.Length > MaxPathLength)
            throw new SecurityException($"Path exceeds maximum length of {MaxPathLength}");

        // Check for null bytes (can bypass string checks)
        if (path.Contains('\0'))
            throw new SecurityException("Path contains null bytes");

        // Check for invalid characters
        foreach (var c in InvalidPathChars)
        {
            if (path.Contains(c))
                throw new SecurityException($"Path contains invalid character: '{c}'");
        }

        // Check for dangerous segments BEFORE normalization
        foreach (var segment in DangerousSegments)
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Path contains dangerous segment: '{segment}'");
        }

        // Normalize the path
        var normalizedPath = NormalizePath(path);

        // If base path is specified, ensure the result is under it
        if (!string.IsNullOrEmpty(basePath))
        {
            var normalizedBase = NormalizePath(basePath);
            var fullPath = Path.GetFullPath(Path.Combine(normalizedBase, normalizedPath));
            var fullBase = Path.GetFullPath(normalizedBase);

            // Ensure path is under base (prevent escaping via symlinks or special chars)
            if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Path '{path}' escapes base directory '{basePath}'");

            return fullPath;
        }

        return normalizedPath;
    }

    /// <summary>
    /// Validates a virtual filesystem path (federation VFS paths).
    /// </summary>
    /// <param name="vfsPath">The VFS path to validate.</param>
    /// <returns>The validated VFS path.</returns>
    public static string ValidateVfsPath(string vfsPath)
    {
        if (string.IsNullOrWhiteSpace(vfsPath))
            return "/";

        // VFS paths must start with /
        if (!vfsPath.StartsWith('/'))
            vfsPath = "/" + vfsPath;

        // Remove double slashes
        while (vfsPath.Contains("//"))
            vfsPath = vfsPath.Replace("//", "/");

        // Check for traversal attempts
        foreach (var segment in DangerousSegments)
        {
            if (vfsPath.Contains(segment, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"VFS path contains dangerous segment: '{segment}'");
        }

        // Validate each segment
        var segments = vfsPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
                throw new SecurityException($"VFS path contains navigation segment: '{segment}'");

            foreach (var c in InvalidPathChars)
            {
                if (segment.Contains(c))
                    throw new SecurityException($"VFS path segment contains invalid character: '{c}'");
            }
        }

        return vfsPath;
    }

    /// <summary>
    /// Validates an object ID to ensure it's a valid hex string.
    /// </summary>
    public static string ValidateObjectId(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new SecurityException("Object ID cannot be null or empty");

        // Object IDs should be hex strings (SHA256 = 64 chars)
        if (objectId.Length != 64)
            throw new SecurityException($"Object ID must be 64 hex characters, got {objectId.Length}");

        if (!Regex.IsMatch(objectId, "^[0-9a-fA-F]{64}$"))
            throw new SecurityException("Object ID contains invalid characters");

        return objectId.ToLowerInvariant();
    }

    /// <summary>
    /// Validates message size to prevent DoS attacks.
    /// </summary>
    public static void ValidateMessageSize(long size, long? maxSize = null)
    {
        var limit = maxSize ?? MaxMessageSize;
        if (size < 0)
            throw new SecurityException("Message size cannot be negative");

        if (size > limit)
            throw new SecurityException($"Message size {size} exceeds maximum allowed size of {limit} bytes");
    }

    /// <summary>
    /// Validates a node ID format.
    /// </summary>
    public static string ValidateNodeId(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new SecurityException("Node ID cannot be null or empty");

        // Node IDs are 64 hex characters (32 bytes)
        if (nodeId.Length != 64)
            throw new SecurityException($"Node ID must be 64 hex characters, got {nodeId.Length}");

        if (!Regex.IsMatch(nodeId, "^[0-9a-fA-F]{64}$"))
            throw new SecurityException("Node ID contains invalid characters");

        return nodeId.ToLowerInvariant();
    }

    /// <summary>
    /// Validates a hostname/IP address.
    /// </summary>
    public static string ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new SecurityException("Host cannot be null or empty");

        // Check for basic injection attempts
        if (host.Contains('\0') || host.Contains('\n') || host.Contains('\r'))
            throw new SecurityException("Host contains control characters");

        // Validate as IP address or hostname
        if (System.Net.IPAddress.TryParse(host, out _))
            return host;

        // Validate as hostname (simplified RFC 1123)
        if (!Regex.IsMatch(host, @"^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*$"))
            throw new SecurityException($"Invalid hostname format: '{host}'");

        if (host.Length > 253)
            throw new SecurityException("Hostname exceeds maximum length of 253 characters");

        return host.ToLowerInvariant();
    }

    /// <summary>
    /// Validates a port number.
    /// </summary>
    public static int ValidatePort(int port)
    {
        if (port < 1 || port > 65535)
            throw new SecurityException($"Port must be between 1 and 65535, got {port}");

        return port;
    }

    /// <summary>
    /// Validates a URL to prevent SSRF attacks.
    /// </summary>
    public static Uri ValidateUrl(string url, bool allowLocal = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new SecurityException("URL cannot be null or empty");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new SecurityException($"Invalid URL format: '{url}'");

        // Only allow http and https
        if (uri.Scheme != "http" && uri.Scheme != "https")
            throw new SecurityException($"URL scheme must be http or https, got '{uri.Scheme}'");

        // Block local addresses unless explicitly allowed
        if (!allowLocal)
        {
            var host = uri.Host.ToLowerInvariant();
            if (host == "localhost" || host == "127.0.0.1" || host == "::1" ||
                host.StartsWith("192.168.") || host.StartsWith("10.") || host.StartsWith("172.16."))
                throw new SecurityException($"URL cannot reference local/private addresses: '{host}'");
        }

        return uri;
    }

    /// <summary>
    /// Normalizes a path by resolving . and removing redundant separators.
    /// Does NOT resolve .. (that would be a security risk).
    /// </summary>
    private static string NormalizePath(string path)
    {
        // Replace backslashes with forward slashes
        path = path.Replace('\\', '/');

        // Remove redundant slashes
        while (path.Contains("//"))
            path = path.Replace("//", "/");

        // Remove trailing slash (unless root)
        if (path.Length > 1 && path.EndsWith('/'))
            path = path[..^1];

        // Replace single dots representing current directory
        path = Regex.Replace(path, @"(?<=^|/)\./", "");
        path = Regex.Replace(path, @"/\.$", "");

        return path;
    }
}

/// <summary>
/// Configuration for message size limits.
/// </summary>
public sealed class MessageSizeLimits
{
    /// <summary>Maximum size of a single message.</summary>
    public long MaxMessageSize { get; set; } = 64 * 1024 * 1024; // 64MB

    /// <summary>Maximum size of a chunked transfer chunk.</summary>
    public int MaxChunkSize { get; set; } = 4 * 1024 * 1024; // 4MB

    /// <summary>Maximum number of chunks in a chunked transfer.</summary>
    public int MaxChunkCount { get; set; } = 10000;

    /// <summary>Maximum size of message headers.</summary>
    public int MaxHeaderSize { get; set; } = 64 * 1024; // 64KB

    /// <summary>Maximum number of headers per message.</summary>
    public int MaxHeaderCount { get; set; } = 100;

    /// <summary>Maximum size of a capability token.</summary>
    public int MaxCapabilityTokenSize { get; set; } = 8 * 1024; // 8KB

    /// <summary>Maximum number of concurrent large transfers.</summary>
    public int MaxConcurrentLargeTransfers { get; set; } = 10;

    /// <summary>Default limits for production.</summary>
    public static MessageSizeLimits Default => new();

    /// <summary>Strict limits for constrained environments.</summary>
    public static MessageSizeLimits Strict => new()
    {
        MaxMessageSize = 16 * 1024 * 1024,
        MaxChunkSize = 1 * 1024 * 1024,
        MaxChunkCount = 1000,
        MaxHeaderSize = 16 * 1024,
        MaxHeaderCount = 50,
        MaxCapabilityTokenSize = 4 * 1024,
        MaxConcurrentLargeTransfers = 5
    };

    /// <summary>Relaxed limits for high-throughput scenarios.</summary>
    public static MessageSizeLimits Relaxed => new()
    {
        MaxMessageSize = 256 * 1024 * 1024,
        MaxChunkSize = 16 * 1024 * 1024,
        MaxChunkCount = 100000,
        MaxHeaderSize = 256 * 1024,
        MaxHeaderCount = 500,
        MaxCapabilityTokenSize = 32 * 1024,
        MaxConcurrentLargeTransfers = 50
    };
}

/// <summary>
/// Exception thrown for security validation failures.
/// </summary>
public class SecurityException : Exception
{
    public SecurityException(string message) : base(message) { }
    public SecurityException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Utility for validating and sanitizing input data.
/// </summary>
public static class InputSanitizer
{
    /// <summary>
    /// Sanitizes a string for safe logging (removes control chars, truncates).
    /// </summary>
    public static string SanitizeForLogging(string? input, int maxLength = 1000)
    {
        if (string.IsNullOrEmpty(input))
            return "[empty]";

        // Remove control characters
        var sanitized = Regex.Replace(input, @"[\x00-\x1F\x7F]", "");

        // Truncate if too long
        if (sanitized.Length > maxLength)
            sanitized = sanitized[..maxLength] + "...[truncated]";

        return sanitized;
    }

    /// <summary>
    /// Validates that a string contains only printable ASCII characters.
    /// </summary>
    public static bool IsPrintableAscii(string input)
    {
        return input.All(c => c >= 0x20 && c <= 0x7E);
    }

    /// <summary>
    /// Sanitizes a filename by removing/replacing dangerous characters.
    /// </summary>
    public static string SanitizeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "unnamed";

        // Replace dangerous characters with underscore
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
            filename = filename.Replace(c, '_');

        // Remove leading/trailing dots and spaces
        filename = filename.Trim('.', ' ');

        // Ensure not empty
        if (string.IsNullOrWhiteSpace(filename))
            filename = "unnamed";

        // Truncate to reasonable length
        if (filename.Length > 255)
            filename = filename[..255];

        return filename;
    }
}
