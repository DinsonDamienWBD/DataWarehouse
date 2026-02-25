namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>
    /// Transit compression interface for compressing/decompressing data in transit.
    /// Provides configurable algorithm selection, negotiation with remote endpoints,
    /// streaming support, and integration with the transit encryption pipeline.
    /// </summary>
    public interface ITransitCompression
    {
        /// <summary>Compress data for transit.</summary>
        Task<TransitCompressionResult> CompressForTransitAsync(
            byte[] data,
            TransitCompressionOptions options,
            CancellationToken ct = default);

        /// <summary>Decompress data received from transit.</summary>
        Task<TransitDecompressionResult> DecompressFromTransitAsync(
            byte[] compressedData,
            TransitCompressionMetadata metadata,
            CancellationToken ct = default);

        /// <summary>Compress a stream for transit (large payloads).</summary>
        Task CompressStreamForTransitAsync(
            Stream input, Stream output,
            TransitCompressionOptions options,
            CancellationToken ct = default);

        /// <summary>Decompress a stream received from transit.</summary>
        Task DecompressStreamFromTransitAsync(
            Stream input, Stream output,
            TransitCompressionMetadata metadata,
            CancellationToken ct = default);

        /// <summary>Negotiate compression algorithm with a remote endpoint.</summary>
        Task<CompressionNegotiationResult> NegotiateCompressionAsync(
            TransitCompressionCapabilities localCapabilities,
            TransitCompressionCapabilities remoteCapabilities,
            CancellationToken ct = default);

        /// <summary>Get local endpoint compression capabilities.</summary>
        TransitCompressionCapabilities GetLocalCapabilities();

        /// <summary>Check if a specific algorithm is supported.</summary>
        bool SupportsAlgorithm(string algorithmName);

        /// <summary>Estimate compressed size for bandwidth planning.</summary>
        long EstimateCompressedSize(long inputSize, string algorithmName);
    }
}
