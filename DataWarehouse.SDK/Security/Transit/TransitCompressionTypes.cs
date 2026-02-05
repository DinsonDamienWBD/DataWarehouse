namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>When to apply transit compression relative to encryption.</summary>
    public enum TransitCompressionMode
    {
        /// <summary>No transit compression.</summary>
        None,
        /// <summary>Compress before encryption (recommended — compressed data encrypts better).</summary>
        BeforeEncryption,
        /// <summary>Compress after encryption (unusual but supported).</summary>
        AfterEncryption,
        /// <summary>Always compress regardless of encryption state.</summary>
        Always,
        /// <summary>Auto-detect: compress if data is compressible (entropy < 7.0).</summary>
        Auto
    }

    /// <summary>Transit compression optimization priority.</summary>
    public enum TransitCompressionPriority
    {
        /// <summary>Minimize latency — use fastest algorithm.</summary>
        Speed,
        /// <summary>Balance speed and ratio.</summary>
        Balanced,
        /// <summary>Maximize compression ratio — use best algorithm.</summary>
        Ratio,
        /// <summary>Minimize bandwidth cost — highest ratio for metered connections.</summary>
        BandwidthSaving
    }

    /// <summary>Options for transit compression.</summary>
    public class TransitCompressionOptions
    {
        /// <summary>When to apply compression relative to encryption.</summary>
        public TransitCompressionMode Mode { get; init; } = TransitCompressionMode.BeforeEncryption;

        /// <summary>Preferred compression algorithm. Null = auto-select based on priority.</summary>
        public string? PreferredAlgorithm { get; init; }

        /// <summary>Fallback algorithms in order of preference.</summary>
        public string[] FallbackAlgorithms { get; init; } = [];

        /// <summary>Optimization priority.</summary>
        public TransitCompressionPriority Priority { get; init; } = TransitCompressionPriority.Balanced;

        /// <summary>Compression level (1-22, algorithm-dependent). Null = use algorithm default.</summary>
        public int? CompressionLevel { get; init; }

        /// <summary>Minimum data size in bytes to compress. Below this, skip compression.</summary>
        public int MinimumSizeBytes { get; init; } = 256;

        /// <summary>Maximum entropy (0-8) to attempt compression. Above this, skip (data is incompressible).</summary>
        public double MaximumEntropy { get; init; } = 7.5;

        /// <summary>Enable dictionary-based compression for repeated transfers of similar data.</summary>
        public bool UseDictionary { get; init; }

        /// <summary>Shared dictionary ID for dictionary compression.</summary>
        public string? DictionaryId { get; init; }

        /// <summary>Streaming buffer size in bytes.</summary>
        public int StreamBufferSize { get; init; } = 65536;

        /// <summary>Remote endpoint capabilities (for negotiation).</summary>
        public TransitCompressionCapabilities? RemoteCapabilities { get; init; }

        /// <summary>Additional algorithm-specific parameters.</summary>
        public Dictionary<string, object> ExtendedOptions { get; init; } = new();
    }

    /// <summary>Result of transit compression.</summary>
    public class TransitCompressionResult
    {
        /// <summary>Compressed data.</summary>
        public required byte[] CompressedData { get; init; }

        /// <summary>Metadata needed for decompression.</summary>
        public required TransitCompressionMetadata Metadata { get; init; }

        /// <summary>Original uncompressed size.</summary>
        public long OriginalSize { get; init; }

        /// <summary>Compressed size.</summary>
        public long CompressedSize { get; init; }

        /// <summary>Compression ratio (original/compressed).</summary>
        public double CompressionRatio => CompressedSize > 0 ? (double)OriginalSize / CompressedSize : 1.0;

        /// <summary>Whether compression was actually applied (may be skipped if data is incompressible).</summary>
        public bool WasCompressed { get; init; }

        /// <summary>Time taken to compress.</summary>
        public TimeSpan CompressionTime { get; init; }
    }

    /// <summary>Metadata attached to compressed transit data for decompression.</summary>
    public class TransitCompressionMetadata
    {
        /// <summary>Algorithm used for compression.</summary>
        public required string AlgorithmName { get; init; }

        /// <summary>Whether the data was actually compressed.</summary>
        public bool IsCompressed { get; init; } = true;

        /// <summary>Original uncompressed size for buffer pre-allocation.</summary>
        public long OriginalSize { get; init; }

        /// <summary>Dictionary ID if dictionary compression was used.</summary>
        public string? DictionaryId { get; init; }

        /// <summary>Algorithm-specific decompression parameters.</summary>
        public Dictionary<string, object> Parameters { get; init; } = new();
    }

    /// <summary>Result of transit decompression.</summary>
    public class TransitDecompressionResult
    {
        /// <summary>Decompressed data.</summary>
        public required byte[] Data { get; init; }

        /// <summary>Whether data was actually decompressed (false if it wasn't compressed).</summary>
        public bool WasDecompressed { get; init; }

        /// <summary>Time taken to decompress.</summary>
        public TimeSpan DecompressionTime { get; init; }
    }

    /// <summary>Describes a transit endpoint's compression capabilities.</summary>
    public class TransitCompressionCapabilities
    {
        /// <summary>Supported compression algorithms in order of preference.</summary>
        public string[] SupportedAlgorithms { get; init; } = [];

        /// <summary>Whether streaming compression is supported.</summary>
        public bool SupportsStreaming { get; init; } = true;

        /// <summary>Whether dictionary compression is supported.</summary>
        public bool SupportsDictionary { get; init; }

        /// <summary>Available shared dictionary IDs.</summary>
        public string[] AvailableDictionaries { get; init; } = [];

        /// <summary>Estimated compression throughput in MB/s.</summary>
        public double EstimatedThroughputMBps { get; init; }

        /// <summary>Maximum compression level supported.</summary>
        public int MaxCompressionLevel { get; init; } = 22;

        /// <summary>Whether hardware acceleration is available (e.g., QAT, FPGA).</summary>
        public bool HasHardwareAcceleration { get; init; }
    }

    /// <summary>Result of compression algorithm negotiation between endpoints.</summary>
    public class CompressionNegotiationResult
    {
        /// <summary>Whether negotiation succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Agreed-upon algorithm.</summary>
        public string? AgreedAlgorithm { get; init; }

        /// <summary>Agreed compression level.</summary>
        public int AgreedLevel { get; init; }

        /// <summary>Whether to use dictionary compression.</summary>
        public bool UseDictionary { get; init; }

        /// <summary>Agreed dictionary ID (if any).</summary>
        public string? DictionaryId { get; init; }

        /// <summary>Reason for failure (if !Success).</summary>
        public string? FailureReason { get; init; }

        /// <summary>All mutually supported algorithms.</summary>
        public string[] MutuallySupportedAlgorithms { get; init; } = [];
    }

    // --- Transit Compression Policies (like TransitSecurityPolicy for encryption) ---

    /// <summary>Pre-defined transit compression policy.</summary>
    public class TransitCompressionPolicy
    {
        public string PolicyName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public TransitCompressionMode Mode { get; init; }
        public TransitCompressionPriority Priority { get; init; }
        public string PreferredAlgorithm { get; init; } = string.Empty;
        public string[] AllowedAlgorithms { get; init; } = [];
        public int? DefaultLevel { get; init; }
        public int MinimumSizeBytes { get; init; } = 256;
        public bool AllowNegotiation { get; init; } = true;
        public bool AllowFallbackToUncompressed { get; init; } = true;
    }
}
