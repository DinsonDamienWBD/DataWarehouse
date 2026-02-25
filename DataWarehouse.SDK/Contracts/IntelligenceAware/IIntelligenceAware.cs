using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>
    /// Interface for plugins that can leverage Universal Intelligence (T90) capabilities.
    /// Implementing this interface allows plugins to automatically discover and use
    /// AI-powered features such as embeddings, classification, prediction, and NLP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plugins implementing <see cref="IIntelligenceAware"/> gain the ability to:
    /// </para>
    /// <list type="bullet">
    ///   <item>Auto-discover T90 Universal Intelligence on startup</item>
    ///   <item>Query available AI capabilities dynamically</item>
    ///   <item>Request AI services via message bus (no direct plugin references)</item>
    ///   <item>Gracefully degrade when Intelligence is unavailable</item>
    /// </list>
    /// <para>
    /// The discovery protocol uses a 500ms timeout by default. If T90 does not respond
    /// within this window, Intelligence is assumed unavailable and the plugin operates
    /// in fallback mode without AI enhancements.
    /// </para>
    /// </remarks>
    public interface IIntelligenceAware
    {
        /// <summary>
        /// Gets whether Universal Intelligence (T90) is currently available.
        /// </summary>
        /// <value>
        /// <c>true</c> if Intelligence has been discovered and is responding;
        /// <c>false</c> if Intelligence is unavailable or discovery has not been performed.
        /// </value>
        /// <remarks>
        /// This property returns the cached result of the last discovery attempt.
        /// Call <see cref="DiscoverIntelligenceAsync"/> to refresh the discovery state.
        /// Plugins should always check this property before attempting to use AI features
        /// and provide fallback behavior when Intelligence is unavailable.
        /// </remarks>
        bool IsIntelligenceAvailable { get; }

        /// <summary>
        /// Gets the capabilities provided by the discovered Intelligence system.
        /// </summary>
        /// <value>
        /// A flags enum containing all capabilities advertised by T90,
        /// or <see cref="IntelligenceCapabilities.None"/> if Intelligence is unavailable.
        /// </value>
        /// <remarks>
        /// Plugins should check specific capabilities before requesting AI services.
        /// For example, check <see cref="IntelligenceCapabilities.Embeddings"/> before
        /// calling <c>RequestEmbeddingsAsync</c>.
        /// </remarks>
        IntelligenceCapabilities AvailableCapabilities { get; }

        /// <summary>
        /// Attempts to discover Universal Intelligence (T90) via the message bus.
        /// </summary>
        /// <param name="ct">Cancellation token for the discovery operation.</param>
        /// <returns>
        /// A task that resolves to <c>true</c> if Intelligence was discovered and responded
        /// within the timeout period; <c>false</c> otherwise.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method sends a discovery request to the <see cref="IntelligenceTopics.Discover"/>
        /// topic and waits up to 500ms for a response. The result is cached for 60 seconds
        /// to avoid excessive discovery traffic.
        /// </para>
        /// <para>
        /// The method is safe to call multiple times and will return the cached result
        /// if called within the TTL period. Force a fresh discovery by waiting for the
        /// TTL to expire or calling after a significant time gap.
        /// </para>
        /// </remarks>
        Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Extended interface for plugins that want notifications when Intelligence availability changes.
    /// </summary>
    /// <remarks>
    /// Plugins implementing this interface will receive callbacks when T90 becomes
    /// available or unavailable, allowing them to adapt their behavior dynamically.
    /// </remarks>
    public interface IIntelligenceAwareNotifiable : IIntelligenceAware
    {
        /// <summary>
        /// Called when Universal Intelligence becomes available.
        /// </summary>
        /// <param name="capabilities">The capabilities now available.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the async operation.</returns>
        /// <remarks>
        /// This method is called on the message bus thread. Implementations should
        /// not perform long-running operations here. Instead, queue work for later
        /// processing or simply update internal state.
        /// </remarks>
        Task OnIntelligenceAvailableAsync(IntelligenceCapabilities capabilities, CancellationToken ct = default);

        /// <summary>
        /// Called when Universal Intelligence becomes unavailable.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the async operation.</returns>
        /// <remarks>
        /// This method is called when Intelligence stops responding or explicitly
        /// announces shutdown. Plugins should switch to fallback behavior.
        /// </remarks>
        Task OnIntelligenceUnavailableAsync(CancellationToken ct = default);
    }
}
