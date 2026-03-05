namespace DataWarehouse.SDK.Contracts.Distribution;

/// <summary>
/// Represents a CDN edge location with geographic and operational information.
/// </summary>
/// <param name="LocationId">Unique identifier for the edge location.</param>
/// <param name="City">The city where the edge location is situated.</param>
/// <param name="Country">The country where the edge location is situated.</param>
/// <param name="Region">The geographic region identifier (e.g., "us-east", "eu-central").</param>
/// <param name="Latitude">Geographic latitude coordinate.</param>
/// <param name="Longitude">Geographic longitude coordinate.</param>
/// <param name="IsActive">Indicates whether the edge location is currently active and serving traffic.</param>
/// <param name="Capacity">Optional capacity information (e.g., bandwidth, storage).</param>
/// <param name="LastHealthCheck">Timestamp of the last health check, if available.</param>
public sealed record EdgeLocation(
    string LocationId,
    string City,
    string Country,
    string Region,
    double Latitude,
    double Longitude,
    bool IsActive,
    string? Capacity = null,
    DateTimeOffset? LastHealthCheck = null)
{
    /// <summary>
    /// Returns a human-readable description of the edge location.
    /// </summary>
    public override string ToString()
        => $"{City}, {Country} ({Region}) [{LocationId}] {(IsActive ? "Active" : "Inactive")}";
}

/// <summary>
/// Defines cache behavior policies for content distribution.
/// </summary>
/// <param name="TimeToLive">
/// The duration content should remain cached before being considered stale.
/// Null indicates no explicit TTL (use CDN defaults).
/// </param>
/// <param name="MinTimeToLive">
/// The minimum TTL regardless of origin cache headers.
/// Null indicates no minimum.
/// </param>
/// <param name="MaxTimeToLive">
/// The maximum TTL regardless of origin cache headers.
/// Null indicates no maximum.
/// </param>
/// <param name="CachingDisabled">
/// When true, disables caching entirely for this content (always fetch from origin).
/// </param>
/// <param name="QueryStringCaching">
/// Specifies how query string parameters affect caching behavior.
/// </param>
/// <param name="HeadersToInclude">
/// HTTP headers that should be considered when determining cache keys.
/// Empty set means only URL is used for cache key.
/// </param>
/// <param name="CookiesToInclude">
/// Cookies that should be considered when determining cache keys.
/// Empty set means cookies are ignored for caching.
/// </param>
public sealed record CachePolicy(
    TimeSpan? TimeToLive = null,
    TimeSpan? MinTimeToLive = null,
    TimeSpan? MaxTimeToLive = null,
    bool CachingDisabled = false,
    QueryStringCachingBehavior QueryStringCaching = QueryStringCachingBehavior.IgnoreAll,
    IReadOnlySet<string>? HeadersToInclude = null,
    IReadOnlySet<string>? CookiesToInclude = null)
{
    /// <summary>
    /// Gets a default cache policy suitable for static assets (1 day TTL).
    /// </summary>
    public static CachePolicy StaticAssets => new(TimeToLive: TimeSpan.FromDays(1));

    /// <summary>
    /// Gets a default cache policy suitable for dynamic content (5 minutes TTL).
    /// </summary>
    public static CachePolicy DynamicContent => new(TimeToLive: TimeSpan.FromMinutes(5));

    /// <summary>
    /// Gets a cache policy that disables caching entirely.
    /// </summary>
    public static CachePolicy NoCache => new(CachingDisabled: true);
}

/// <summary>
/// Specifies how query string parameters affect CDN caching behavior.
/// </summary>
public enum QueryStringCachingBehavior
{
    /// <summary>
    /// Query strings are ignored entirely when caching (all variants cached as one).
    /// </summary>
    IgnoreAll = 0,

    /// <summary>
    /// All query strings are included in the cache key (different parameters = different cache entries).
    /// </summary>
    IncludeAll = 1,

    /// <summary>
    /// Only specific query string parameters are included in the cache key.
    /// </summary>
    IncludeSpecified = 2,

    /// <summary>
    /// All query strings except specific ones are included in the cache key.
    /// </summary>
    ExcludeSpecified = 3
}

/// <summary>
/// Represents a request to purge (invalidate) cached content.
/// </summary>
/// <param name="Paths">
/// Collection of content paths to invalidate. May support wildcards depending on CDN.
/// </param>
/// <param name="PurgeAll">
/// When true, purges all cached content for the distribution.
/// </param>
/// <param name="WaitForCompletion">
/// When true, the operation waits until purge completes across all edge locations.
/// </param>
/// <param name="Tags">
/// Optional tags for tracking or organizing purge requests.
/// </param>
public sealed record PurgeRequest(
    IReadOnlyCollection<string> Paths,
    bool PurgeAll = false,
    bool WaitForCompletion = false,
    IReadOnlyDictionary<string, string>? Tags = null)
{
    /// <summary>
    /// Creates a purge request for a single path.
    /// </summary>
    public static PurgeRequest ForPath(string path) => new(new[] { path });

    /// <summary>
    /// Creates a purge request that invalidates all content.
    /// </summary>
    public static PurgeRequest PurgeEverything => new(Array.Empty<string>(), PurgeAll: true);
}

/// <summary>
/// Options for generating signed URLs with time-limited access.
/// </summary>
/// <param name="ExpiresAt">
/// The absolute expiration time for the signed URL.
/// </param>
/// <param name="AllowedIpAddresses">
/// Optional IP address restrictions. Only these IPs can access the content.
/// Empty set means no IP restrictions.
/// </param>
/// <param name="CustomPolicy">
/// Optional custom access policy in JSON format (CDN-specific).
/// </param>
public sealed record SignedUrlOptions(
    DateTimeOffset ExpiresAt,
    IReadOnlySet<string>? AllowedIpAddresses = null,
    string? CustomPolicy = null)
{
    /// <summary>
    /// Creates signed URL options that expire after the specified duration.
    /// </summary>
    public static SignedUrlOptions ExpiresIn(TimeSpan duration)
        => new(DateTimeOffset.UtcNow.Add(duration));

    /// <summary>
    /// Creates signed URL options that expire after 1 hour.
    /// </summary>
    public static SignedUrlOptions OneHour => ExpiresIn(TimeSpan.FromHours(1));

    /// <summary>
    /// Creates signed URL options that expire after 24 hours.
    /// </summary>
    public static SignedUrlOptions OneDay => ExpiresIn(TimeSpan.FromDays(1));
}

/// <summary>
/// Defines geographic access restrictions for content.
/// </summary>
/// <param name="RestrictionType">
/// The type of geographic restriction to apply.
/// </param>
/// <param name="Countries">
/// ISO 3166-1 alpha-2 country codes affected by the restriction.
/// </param>
public sealed record GeoRestriction(
    GeoRestrictionType RestrictionType,
    IReadOnlySet<string> Countries)
{
    /// <summary>
    /// Creates a geo-restriction that allows access only from specified countries (allowlist).
    /// </summary>
    public static GeoRestriction Allowlist(params string[] countryCodes)
        => new(GeoRestrictionType.Allowlist, new HashSet<string>(countryCodes));

    /// <summary>
    /// Creates a geo-restriction that blocks access from specified countries (denylist).
    /// </summary>
    public static GeoRestriction Denylist(params string[] countryCodes)
        => new(GeoRestrictionType.Denylist, new HashSet<string>(countryCodes));

    /// <summary>
    /// No geographic restrictions (content available worldwide).
    /// </summary>
    public static GeoRestriction None => new(GeoRestrictionType.None, new HashSet<string>());

    // Backward-compatible aliases
    /// <summary>Obsolete: use <see cref="Allowlist"/> instead.</summary>
    [Obsolete("Use Allowlist instead. Whitelist/Blacklist terminology is deprecated.")]
    public static GeoRestriction Whitelist(params string[] countryCodes) => Allowlist(countryCodes);

    /// <summary>Obsolete: use <see cref="Denylist"/> instead.</summary>
    [Obsolete("Use Denylist instead. Whitelist/Blacklist terminology is deprecated.")]
    public static GeoRestriction Blacklist(params string[] countryCodes) => Denylist(countryCodes);
}

/// <summary>
/// Specifies the type of geographic restriction.
/// </summary>
public enum GeoRestrictionType
{
    /// <summary>
    /// No geographic restrictions applied.
    /// </summary>
    None = 0,

    /// <summary>
    /// Allow access only from specified countries.
    /// </summary>
    Allowlist = 1,

    /// <summary>
    /// Block access from specified countries.
    /// </summary>
    Denylist = 2
}

/// <summary>
/// Configuration for content distribution including caching, security, and geographic restrictions.
/// </summary>
/// <param name="CachePolicy">
/// The cache behavior policy for this content.
/// </param>
/// <param name="GeoRestriction">
/// Geographic access restrictions, if any.
/// </param>
/// <param name="EnableHttps">
/// Indicates whether HTTPS should be enforced for content delivery.
/// </param>
/// <param name="EnableCompression">
/// Indicates whether automatic compression should be applied.
/// </param>
/// <param name="CustomHeaders">
/// Custom HTTP headers to include in responses.
/// </param>
/// <param name="ContentType">
/// The MIME type of the content.
/// </param>
/// <param name="Metadata">
/// Additional metadata key-value pairs for the distribution.
/// </param>
public sealed record DistributionConfig(
    CachePolicy CachePolicy,
    GeoRestriction? GeoRestriction = null,
    bool EnableHttps = true,
    bool EnableCompression = true,
    IReadOnlyDictionary<string, string>? CustomHeaders = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Gets a default configuration suitable for static assets (long caching, compression enabled).
    /// </summary>
    public static DistributionConfig StaticAssets => new(
        CachePolicy: CachePolicy.StaticAssets,
        EnableHttps: true,
        EnableCompression: true);

    /// <summary>
    /// Gets a default configuration suitable for dynamic content (short caching).
    /// </summary>
    public static DistributionConfig DynamicContent => new(
        CachePolicy: CachePolicy.DynamicContent,
        EnableHttps: true,
        EnableCompression: false);

    /// <summary>
    /// Gets a configuration that disables caching entirely.
    /// </summary>
    public static DistributionConfig NoCache => new(
        CachePolicy: CachePolicy.NoCache,
        EnableHttps: true);
}
