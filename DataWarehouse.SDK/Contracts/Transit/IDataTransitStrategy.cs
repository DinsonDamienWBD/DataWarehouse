using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Transit
{
    /// <summary>
    /// Core interface for data transit strategy implementations.
    /// All transport-layer strategies must implement this interface to participate
    /// in the orchestrated data transfer pipeline.
    /// </summary>
    /// <remarks>
    /// Strategies provide point-to-point or multi-hop data transfer over specific
    /// protocols (HTTP/2, HTTP/3, gRPC, FTP, SFTP, SCP, etc.).
    /// The orchestrator selects the best strategy based on endpoint protocol,
    /// data size, availability, and capability matching.
    /// </remarks>
    public interface IDataTransitStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this transit strategy.
        /// Convention: "transit-{protocol}" (e.g., "transit-http2", "transit-sftp").
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this transit strategy.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the capabilities supported by this transit strategy.
        /// </summary>
        TransitCapabilities Capabilities { get; }

        /// <summary>
        /// Checks whether this strategy can reach the specified endpoint.
        /// Used by the orchestrator to filter candidate strategies.
        /// </summary>
        /// <param name="endpoint">The target endpoint to check availability for.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the strategy can transfer data to the endpoint; false otherwise.</returns>
        Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);

        /// <summary>
        /// Executes a data transfer from source to destination as specified in the request.
        /// </summary>
        /// <param name="request">The transfer request containing source, destination, data stream, and options.</param>
        /// <param name="progress">Optional progress reporter for tracking transfer status.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the transfer operation including bytes transferred and content hash.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the transfer cannot be initiated.</exception>
        Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);

        /// <summary>
        /// Resumes an interrupted transfer using its transfer ID.
        /// Only supported when <see cref="TransitCapabilities.SupportsResumable"/> is true.
        /// </summary>
        /// <param name="transferId">The unique identifier of the transfer to resume.</param>
        /// <param name="progress">Optional progress reporter for tracking transfer status.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the resumed transfer operation.</returns>
        /// <exception cref="NotSupportedException">Thrown when the strategy does not support resumable transfers.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="transferId"/> is not found.</exception>
        Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);

        /// <summary>
        /// Cancels an active transfer by its transfer ID.
        /// </summary>
        /// <param name="transferId">The unique identifier of the transfer to cancel.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="transferId"/> is not found.</exception>
        Task CancelTransferAsync(string transferId, CancellationToken ct = default);

        /// <summary>
        /// Gets the current health status of this transit strategy.
        /// Used for monitoring and orchestrator decision-making.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The health status including availability, active transfers, and error information.</returns>
        Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Describes the capabilities of a data transit strategy.
    /// Used by the orchestrator to select the optimal strategy for a given transfer request.
    /// </summary>
    public sealed record TransitCapabilities
    {
        /// <summary>
        /// Indicates whether the strategy supports resuming interrupted transfers.
        /// Strategies with this capability can continue from the last transferred byte offset.
        /// </summary>
        public bool SupportsResumable { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports streaming data transfer.
        /// Streaming strategies can handle data without buffering the entire payload in memory.
        /// </summary>
        public bool SupportsStreaming { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports delta/incremental transfers.
        /// Delta-capable strategies transfer only changed blocks, reducing bandwidth.
        /// </summary>
        public bool SupportsDelta { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports multi-path transfer routing.
        /// Multi-path strategies can split traffic across multiple network paths for throughput.
        /// </summary>
        public bool SupportsMultiPath { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports peer-to-peer data transfer.
        /// </summary>
        public bool SupportsP2P { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports offline/store-and-forward transfer.
        /// </summary>
        public bool SupportsOffline { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports built-in compression during transfer.
        /// </summary>
        public bool SupportsCompression { get; init; }

        /// <summary>
        /// Indicates whether the strategy supports built-in encryption during transfer.
        /// </summary>
        public bool SupportsEncryption { get; init; }

        /// <summary>
        /// Maximum transfer size in bytes supported by this strategy.
        /// Use <see cref="long.MaxValue"/> for no practical limit.
        /// </summary>
        public long MaxTransferSizeBytes { get; init; }

        /// <summary>
        /// The list of protocols supported by this strategy (e.g., "http", "https", "ftp", "sftp").
        /// Used by the orchestrator to match strategies to endpoint protocols.
        /// </summary>
        public IReadOnlyList<string> SupportedProtocols { get; init; } = [];
    }
}
