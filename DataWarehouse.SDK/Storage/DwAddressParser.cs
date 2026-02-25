using DataWarehouse.SDK.Contracts;
using System;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Storage;

/// <summary>
/// Parser for the dw:// URI scheme, converting dw:// URIs into typed <see cref="StorageAddress"/> variants.
/// </summary>
/// <remarks>
/// <para>Supported URI formats:</para>
/// <list type="bullet">
/// <item><description><c>dw://bucket/path/to/object</c> -> <see cref="DwBucketAddress"/></description></item>
/// <item><description><c>dw://node@hostname/path</c> -> <see cref="DwNodeAddress"/></description></item>
/// <item><description><c>dw://cluster:name/key</c> -> <see cref="DwClusterAddress"/></description></item>
/// </list>
/// <para>
/// Detection rules: The parser examines the authority portion of the URI to determine the addressing mode.
/// If it contains '@', it's node-based. If it starts with 'cluster:', it's cluster-based.
/// Otherwise, it's bucket-based.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: dw:// universal namespace")]
public static class DwAddressParser
{
    private const int MaxObjectPathLength = 1024;
    private const int MinBucketNameLength = 3;
    private const int MaxBucketNameLength = 63;
    private const int MaxClusterNameLength = 255;

    private static readonly Regex BucketNameRegex = new(
        @"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?$",
        RegexOptions.Compiled);

    private static readonly Regex ClusterNameRegex = new(
        @"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$",
        RegexOptions.Compiled);

    private static readonly Regex HostnameRegex = new(
        @"^([a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?\.)*[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?$",
        RegexOptions.Compiled);

    private static readonly Regex IpAddressRegex = new(
        @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a dw:// URI string into the appropriate <see cref="StorageAddress"/> variant.
    /// </summary>
    /// <param name="dwUri">A URI string starting with <c>dw://</c>.</param>
    /// <returns>A typed <see cref="StorageAddress"/> variant.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dwUri"/> is null or empty.</exception>
    /// <exception cref="FormatException">Thrown when the URI is malformed or fails validation.</exception>
    public static StorageAddress Parse(string dwUri)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(dwUri);

        if (!dwUri.StartsWith("dw://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Invalid dw:// URI: must start with 'dw://'. Got: '{dwUri}'");

        var body = dwUri.Substring(5); // Remove "dw://"

        if (string.IsNullOrEmpty(body))
            throw new FormatException("Invalid dw:// URI: empty body after 'dw://'");

        return ParseBody(body);
    }

    /// <summary>
    /// Parses a <see cref="Uri"/> with a dw scheme into the appropriate <see cref="StorageAddress"/> variant.
    /// </summary>
    /// <param name="uri">A <see cref="Uri"/> with scheme <c>dw</c>.</param>
    /// <returns>A typed <see cref="StorageAddress"/> variant.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="uri"/> is null.</exception>
    /// <exception cref="FormatException">Thrown when the URI is malformed or fails validation.</exception>
    public static StorageAddress Parse(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!string.Equals(uri.Scheme, "dw", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Invalid URI scheme: expected 'dw', got '{uri.Scheme}'");

        // Re-parse from the original string to avoid .NET Uri class mangling custom schemes
        return Parse(uri.OriginalString);
    }

    /// <summary>
    /// Attempts to parse a dw:// URI string into a <see cref="StorageAddress"/> variant without throwing.
    /// </summary>
    /// <param name="dwUri">A URI string to parse.</param>
    /// <param name="address">The parsed <see cref="StorageAddress"/> if successful; otherwise, null.</param>
    /// <returns><c>true</c> if parsing succeeded; otherwise, <c>false</c>.</returns>
    public static bool TryParse(string dwUri, out StorageAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(dwUri))
            return false;

        try
        {
            address = Parse(dwUri);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static StorageAddress ParseBody(string body)
    {
        // Detect node-based: dw://node@hostname/path
        if (body.StartsWith("node@", StringComparison.Ordinal))
        {
            return ParseNodeAddress(body.Substring(5)); // Remove "node@"
        }

        // Detect cluster-based: dw://cluster:name/key
        if (body.StartsWith("cluster:", StringComparison.Ordinal))
        {
            return ParseClusterAddress(body.Substring(8)); // Remove "cluster:"
        }

        // Default: bucket-based dw://bucket/path
        return ParseBucketAddress(body);
    }

    private static DwBucketAddress ParseBucketAddress(string body)
    {
        var slashIndex = body.IndexOf('/');
        if (slashIndex < 0)
            throw new FormatException(
                $"Invalid dw:// bucket URI: missing object path. Expected format: dw://bucket/path. Got body: '{body}'");

        var bucket = body.Substring(0, slashIndex);
        var objectPath = body.Substring(slashIndex + 1);

        ValidateBucketName(bucket);
        ValidateObjectPath(objectPath);

        return new DwBucketAddress(bucket, objectPath);
    }

    private static DwNodeAddress ParseNodeAddress(string body)
    {
        // body is: hostname/path (after removing "node@")
        var slashIndex = body.IndexOf('/');
        if (slashIndex < 0)
            throw new FormatException(
                $"Invalid dw:// node URI: missing object path. Expected format: dw://node@hostname/path. Got: '{body}'");

        var nodeId = body.Substring(0, slashIndex);
        var objectPath = body.Substring(slashIndex + 1);

        ValidateNodeId(nodeId);
        ValidateObjectPath(objectPath);

        return new DwNodeAddress(nodeId, objectPath);
    }

    private static DwClusterAddress ParseClusterAddress(string body)
    {
        // body is: name/key (after removing "cluster:")
        var slashIndex = body.IndexOf('/');
        if (slashIndex < 0)
            throw new FormatException(
                $"Invalid dw:// cluster URI: missing key. Expected format: dw://cluster:name/key. Got: '{body}'");

        var clusterName = body.Substring(0, slashIndex);
        var key = body.Substring(slashIndex + 1);

        ValidateClusterName(clusterName);

        if (string.IsNullOrEmpty(key))
            throw new FormatException("Invalid dw:// cluster URI: key cannot be empty");

        return new DwClusterAddress(clusterName, key);
    }

    private static void ValidateBucketName(string bucket)
    {
        if (string.IsNullOrEmpty(bucket))
            throw new FormatException("Invalid dw:// URI: bucket name cannot be empty");

        if (bucket.Length < MinBucketNameLength)
            throw new FormatException(
                $"Invalid dw:// URI: bucket name '{bucket}' is too short (minimum {MinBucketNameLength} characters)");

        if (bucket.Length > MaxBucketNameLength)
            throw new FormatException(
                $"Invalid dw:// URI: bucket name '{bucket}' is too long (maximum {MaxBucketNameLength} characters)");

        if (!BucketNameRegex.IsMatch(bucket))
            throw new FormatException(
                $"Invalid dw:// URI: bucket name '{bucket}' must be lowercase alphanumeric with hyphens, no leading/trailing hyphen");
    }

    private static void ValidateNodeId(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
            throw new FormatException("Invalid dw:// node URI: node ID cannot be empty");

        if (!HostnameRegex.IsMatch(nodeId) && !IpAddressRegex.IsMatch(nodeId))
            throw new FormatException(
                $"Invalid dw:// node URI: node ID '{nodeId}' must be a valid hostname or IP address");
    }

    private static void ValidateClusterName(string clusterName)
    {
        if (string.IsNullOrEmpty(clusterName))
            throw new FormatException("Invalid dw:// cluster URI: cluster name cannot be empty");

        if (clusterName.Length > MaxClusterNameLength)
            throw new FormatException(
                $"Invalid dw:// cluster URI: cluster name '{clusterName}' exceeds maximum length of {MaxClusterNameLength}");

        if (!ClusterNameRegex.IsMatch(clusterName))
            throw new FormatException(
                $"Invalid dw:// cluster URI: cluster name '{clusterName}' must be alphanumeric with hyphens and dots");
    }

    private static void ValidateObjectPath(string objectPath)
    {
        if (string.IsNullOrEmpty(objectPath))
            throw new FormatException("Invalid dw:// URI: object path cannot be empty");

        if (objectPath.Length > MaxObjectPathLength)
            throw new FormatException(
                $"Invalid dw:// URI: object path exceeds maximum length of {MaxObjectPathLength} characters");

        if (objectPath.Contains('\0'))
            throw new FormatException("Invalid dw:// URI: object path cannot contain null bytes");

        if (objectPath.Contains(".."))
            throw new FormatException("Invalid dw:// URI: object path cannot contain path traversal (..)");
    }
}
