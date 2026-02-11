using System;
using System.Collections.Generic;
using System.IO;

namespace DataWarehouse.SDK.Contracts.Transit
{
    /// <summary>
    /// Represents a network endpoint for data transit operations.
    /// Encapsulates the URI, protocol, authentication, and additional options for a transfer endpoint.
    /// </summary>
    public sealed record TransitEndpoint
    {
        /// <summary>
        /// The URI of the endpoint (e.g., "https://host:port/path", "sftp://host/path").
        /// </summary>
        public required Uri Uri { get; init; }

        /// <summary>
        /// The protocol identifier (e.g., "http2", "grpc", "sftp").
        /// If null, inferred from the URI scheme.
        /// </summary>
        public string? Protocol { get; init; }

        /// <summary>
        /// Authentication token or credential string for the endpoint.
        /// Format is protocol-dependent (e.g., Bearer token, username:password, SSH key path).
        /// </summary>
        public string? AuthToken { get; init; }

        /// <summary>
        /// Additional protocol-specific options for the endpoint.
        /// </summary>
        public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Represents a data transfer request with source, destination, data stream, and transfer options.
    /// </summary>
    public sealed record TransitRequest
    {
        /// <summary>
        /// Unique identifier for this transfer. Used for tracking, resuming, and cancellation.
        /// </summary>
        public required string TransferId { get; init; }

        /// <summary>
        /// The source endpoint from which data is read.
        /// </summary>
        public required TransitEndpoint Source { get; init; }

        /// <summary>
        /// The destination endpoint to which data is written.
        /// </summary>
        public required TransitEndpoint Destination { get; init; }

        /// <summary>
        /// The data stream to transfer. May be null for pull-based transfers.
        /// </summary>
        public Stream? DataStream { get; init; }

        /// <summary>
        /// The total size of the data in bytes. Used for progress reporting and strategy selection.
        /// Zero or negative indicates unknown size.
        /// </summary>
        public long SizeBytes { get; init; }

        /// <summary>
        /// The SHA-256 content hash of the source data for integrity verification.
        /// Null if not pre-computed.
        /// </summary>
        public string? ContentHash { get; init; }

        /// <summary>
        /// Quality of Service policy governing bandwidth, priority, and cost constraints.
        /// </summary>
        public TransitQoSPolicy? QoSPolicy { get; init; }

        /// <summary>
        /// Layer configuration for compression and encryption during transit.
        /// </summary>
        public TransitLayerConfig? Layers { get; init; }

        /// <summary>
        /// Additional metadata associated with the transfer request.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Represents the result of a data transfer operation.
    /// </summary>
    public sealed record TransitResult
    {
        /// <summary>
        /// The unique transfer identifier matching the original request.
        /// </summary>
        public required string TransferId { get; init; }

        /// <summary>
        /// Indicates whether the transfer completed successfully.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Total number of bytes transferred.
        /// </summary>
        public long BytesTransferred { get; init; }

        /// <summary>
        /// Wall-clock duration of the transfer operation.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Error message if the transfer failed. Null on success.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// SHA-256 content hash of the transferred data for integrity verification.
        /// </summary>
        public string? ContentHash { get; init; }

        /// <summary>
        /// The strategy ID that was used to execute the transfer.
        /// </summary>
        public string? StrategyUsed { get; init; }

        /// <summary>
        /// Additional metadata from the transfer operation.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Reports progress of an ongoing data transfer.
    /// </summary>
    public sealed record TransitProgress
    {
        /// <summary>
        /// The unique transfer identifier.
        /// </summary>
        public string? TransferId { get; init; }

        /// <summary>
        /// Number of bytes transferred so far.
        /// </summary>
        public long BytesTransferred { get; init; }

        /// <summary>
        /// Total number of bytes to transfer. Zero if unknown.
        /// </summary>
        public long TotalBytes { get; init; }

        /// <summary>
        /// Transfer completion percentage (0.0 to 100.0).
        /// </summary>
        public double PercentComplete { get; init; }

        /// <summary>
        /// Current transfer speed in bytes per second.
        /// </summary>
        public double BytesPerSecond { get; init; }

        /// <summary>
        /// Estimated time remaining until completion. Null if unknown.
        /// </summary>
        public TimeSpan? EstimatedRemaining { get; init; }

        /// <summary>
        /// Description of the current transfer phase (e.g., "Connecting", "Transferring", "Verifying").
        /// </summary>
        public string? CurrentPhase { get; init; }
    }

    /// <summary>
    /// Reports the health status of a transit strategy.
    /// </summary>
    public sealed record TransitHealthStatus
    {
        /// <summary>
        /// The strategy identifier this health status applies to.
        /// </summary>
        public string? StrategyId { get; init; }

        /// <summary>
        /// Indicates whether the strategy is healthy and able to accept transfers.
        /// </summary>
        public bool IsHealthy { get; init; }

        /// <summary>
        /// Timestamp of the last health check.
        /// </summary>
        public DateTime LastCheckTime { get; init; }

        /// <summary>
        /// Error message if the strategy is unhealthy. Null when healthy.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Number of endpoints currently reachable by this strategy.
        /// </summary>
        public int AvailableEndpoints { get; init; }

        /// <summary>
        /// Number of transfers currently in progress on this strategy.
        /// </summary>
        public int ActiveTransfers { get; init; }
    }

    /// <summary>
    /// Quality of Service policy for transit operations.
    /// Governs bandwidth allocation, priority, and cost constraints.
    /// </summary>
    public sealed record TransitQoSPolicy
    {
        /// <summary>
        /// Maximum bandwidth in bytes per second. Zero for unlimited.
        /// </summary>
        public long MaxBandwidthBytesPerSecond { get; init; }

        /// <summary>
        /// Transfer priority level.
        /// </summary>
        public TransitPriority Priority { get; init; } = TransitPriority.Normal;

        /// <summary>
        /// Minimum guaranteed bandwidth in bytes per second. Zero for no guarantee.
        /// </summary>
        public long MinBandwidthGuarantee { get; init; }

        /// <summary>
        /// Maximum cost allowed for this transfer. Zero for no limit.
        /// </summary>
        public decimal CostLimit { get; init; }
    }

    /// <summary>
    /// Configuration for compression and encryption layers applied during transit.
    /// </summary>
    public sealed record TransitLayerConfig
    {
        /// <summary>
        /// Whether to enable compression during transit.
        /// </summary>
        public bool EnableCompression { get; init; }

        /// <summary>
        /// Compression algorithm to use (e.g., "gzip", "zstd", "lz4").
        /// </summary>
        public string? CompressionAlgorithm { get; init; }

        /// <summary>
        /// Whether to enable encryption during transit.
        /// </summary>
        public bool EnableEncryption { get; init; }

        /// <summary>
        /// Encryption algorithm to use (e.g., "aes-256-gcm", "chacha20-poly1305").
        /// </summary>
        public string? EncryptionAlgorithm { get; init; }
    }

    /// <summary>
    /// Cost profile for a transit strategy, used for cost-aware routing.
    /// </summary>
    public sealed record TransitCostProfile
    {
        /// <summary>
        /// Cost per gigabyte transferred.
        /// </summary>
        public decimal CostPerGB { get; init; }

        /// <summary>
        /// Fixed cost per transfer operation.
        /// </summary>
        public decimal FixedCostPerTransfer { get; init; }

        /// <summary>
        /// Cost tier classification for this strategy.
        /// </summary>
        public TransitCostTier Tier { get; init; } = TransitCostTier.Free;

        /// <summary>
        /// Whether the strategy uses metered billing.
        /// </summary>
        public bool IsMetered { get; init; }
    }

    /// <summary>
    /// Priority levels for data transit operations.
    /// </summary>
    public enum TransitPriority
    {
        /// <summary>
        /// Low priority transfer. May be throttled under contention.
        /// </summary>
        Low = 0,

        /// <summary>
        /// Normal priority transfer. Default for most operations.
        /// </summary>
        Normal = 1,

        /// <summary>
        /// High priority transfer. Preferred bandwidth allocation.
        /// </summary>
        High = 2,

        /// <summary>
        /// Critical priority transfer. Maximum bandwidth, preempts lower priority transfers.
        /// </summary>
        Critical = 3
    }

    /// <summary>
    /// Cost tier classification for transit strategies.
    /// </summary>
    public enum TransitCostTier
    {
        /// <summary>
        /// No cost associated with this strategy (e.g., local network, LAN transfers).
        /// </summary>
        Free = 0,

        /// <summary>
        /// Usage-based billing (e.g., cloud egress, metered bandwidth).
        /// </summary>
        Metered = 1,

        /// <summary>
        /// Premium tier with dedicated bandwidth or priority routing.
        /// </summary>
        Premium = 2
    }
}
