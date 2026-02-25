namespace DataWarehouse.SDK.Contracts.Distribution;

/// <summary>
/// Defines the contract for content distribution network (CDN) strategies that manage
/// content delivery, edge caching, cache invalidation, and geo-distribution.
/// </summary>
/// <remarks>
/// <para>
/// Implementations provide CDN capabilities including:
/// <list type="bullet">
/// <item><description>Content pushing to edge locations</description></item>
/// <item><description>Cache invalidation and purging</description></item>
/// <item><description>Edge location discovery and management</description></item>
/// <item><description>Signed URL generation for secure content delivery</description></item>
/// <item><description>Geographic restrictions and access control</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Strategy Examples:</strong>
/// <list type="bullet">
/// <item><description>CloudFront (AWS) integration</description></item>
/// <item><description>Azure CDN implementation</description></item>
/// <item><description>Cloudflare CDN integration</description></item>
/// <item><description>Fastly CDN implementation</description></item>
/// <item><description>Akamai Edge platform</description></item>
/// <item><description>Custom origin-based CDN</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Implementations should be thread-safe for concurrent CDN operations.
/// </para>
/// </remarks>
public interface IContentDistributionStrategy
{
    /// <summary>
    /// Gets the capabilities of this content distribution strategy, including caching,
    /// geo-restriction, purging, and signed URL support.
    /// </summary>
    /// <value>
    /// A <see cref="DistributionCapabilities"/> instance describing the strategy's capabilities.
    /// </value>
    DistributionCapabilities Capabilities { get; }

    /// <summary>
    /// Pushes content to the CDN edge locations asynchronously.
    /// </summary>
    /// <param name="content">The content stream to distribute.</param>
    /// <param name="path">The path/key where the content should be accessible.</param>
    /// <param name="config">Configuration options for the distribution.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the CDN URL where the content is accessible.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="content"/>, <paramref name="path"/>, or <paramref name="config"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="path"/> is invalid or contains unsupported characters.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when content push fails due to CDN errors or quota limits.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The content is uploaded to the origin and distributed to edge locations based on demand.
    /// Initial propagation to all edge locations may take several minutes depending on the CDN.
    /// </para>
    /// <para>
    /// The returned URL represents the primary CDN endpoint. Actual content delivery occurs
    /// from the nearest edge location to the requesting client.
    /// </para>
    /// </remarks>
    Task<Uri> PushAsync(
        Stream content,
        string path,
        DistributionConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates (purges) cached content at the specified paths asynchronously.
    /// </summary>
    /// <param name="request">The purge request specifying paths and options.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result indicates
    /// whether the invalidation was successful.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when cache purging is not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when invalidation fails due to CDN errors or rate limits.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Cache invalidation removes content from edge caches, forcing the next request
    /// to fetch fresh content from the origin. This operation may take several minutes
    /// to propagate across all edge locations.
    /// </para>
    /// <para>
    /// Some CDNs charge for invalidation requests or impose rate limits. Check
    /// <see cref="DistributionCapabilities.SupportsPurge"/> before calling.
    /// </para>
    /// <para>
    /// Wildcard invalidations (e.g., "/images/*") may be supported depending on the CDN.
    /// </para>
    /// </remarks>
    Task<bool> InvalidateAsync(
        PurgeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves information about available edge locations asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// a collection of edge location information including geographic details and status.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when edge location discovery fails due to CDN API errors.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Edge locations represent physical data centers where cached content is served.
    /// This information is useful for monitoring geographic distribution and performance.
    /// </para>
    /// <para>
    /// The returned collection may be cached internally as edge locations change infrequently.
    /// </para>
    /// </remarks>
    Task<IReadOnlyCollection<EdgeLocation>> GetEdgeLocationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a signed URL for secure, time-limited access to content.
    /// </summary>
    /// <param name="path">The content path to generate a signed URL for.</param>
    /// <param name="options">Options controlling the signed URL generation.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the signed URL that provides time-limited access to the content.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="path"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when signed URLs are not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when URL generation fails due to missing credentials or configuration errors.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Signed URLs contain authentication tokens that expire after the specified duration.
    /// They are used to provide temporary access to private content without exposing credentials.
    /// </para>
    /// <para>
    /// Check <see cref="DistributionCapabilities.SupportsSignedUrls"/> before calling this method.
    /// </para>
    /// <para>
    /// The generated URL includes query parameters or cookies for authentication,
    /// depending on the CDN implementation.
    /// </para>
    /// </remarks>
    Task<Uri> GenerateSignedUrlAsync(
        string path,
        SignedUrlOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the distribution configuration for existing content.
    /// </summary>
    /// <param name="path">The content path to update configuration for.</param>
    /// <param name="config">The new distribution configuration.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="path"/> or <paramref name="config"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configuration update fails or the path does not exist.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Configuration changes may take several minutes to propagate to all edge locations.
    /// This includes changes to cache policies, geo-restrictions, and access controls.
    /// </para>
    /// </remarks>
    Task UpdateConfigurationAsync(
        string path,
        DistributionConfig config,
        CancellationToken cancellationToken = default);
}
