namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>
    /// Pipeline stage interface for transit encryption in data processing pipelines.
    /// Enables pluggable encryption/decryption as a stage in ETL, streaming, or request/response pipelines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface allows transit encryption to be integrated into pipeline architectures:
    /// - ETL pipelines: Encrypt data extracted from source before loading to target
    /// - Streaming pipelines: Encrypt/decrypt messages in event streams (Kafka, RabbitMQ)
    /// - API gateways: Encrypt outbound requests, decrypt inbound responses
    /// - Data replication: Encrypt data during cross-region or cross-cloud replication
    /// </para>
    /// <para>
    /// Pipeline stages support:
    /// - Cipher negotiation with downstream/upstream endpoints
    /// - Automatic retry with fallback ciphers on negotiation failure
    /// - Metrics and telemetry integration
    /// - Circuit breaker patterns for fault tolerance
    /// </para>
    /// <para>
    /// Integration with pipeline orchestrators:
    /// - Implement this interface in a pipeline stage plugin
    /// - Register with IPipelineOrchestrator
    /// - Configure via pipeline definition (YAML, JSON, code)
    /// </para>
    /// </remarks>
    public interface ITransitEncryptionStage
    {
        /// <summary>
        /// Processes outbound data (encrypt for transit to downstream system).
        /// </summary>
        /// <param name="data">The plaintext data to encrypt.</param>
        /// <param name="context">Pipeline context containing metadata, security context, and downstream capabilities.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Processing result containing encrypted data and updated context.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Typical outbound processing flow:
        /// 1. Extract downstream endpoint capabilities from context (if available)
        /// 2. Negotiate cipher with downstream endpoint
        /// 3. Retrieve encryption key from IKeyStore
        /// 4. Encrypt data with negotiated cipher
        /// 5. Add encryption metadata to context for downstream consumption
        /// 6. Emit metrics (encryption time, cipher used, data size)
        /// 7. Return encrypted data
        /// </para>
        /// <para>
        /// If cipher negotiation fails and Mode is Opportunistic, the stage may return
        /// plaintext data with a warning in the context. If Mode is AlwaysEncrypt,
        /// negotiation failure throws InvalidOperationException.
        /// </para>
        /// <para>
        /// The context is updated with encryption metadata that must be transmitted
        /// to the downstream system for decryption.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If data or context is null.</exception>
        /// <exception cref="InvalidOperationException">If encryption is required but fails (AlwaysEncrypt mode).</exception>
        Task<PipelineStageResult> ProcessOutboundAsync(
            byte[] data,
            TransitPipelineContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Processes inbound data (decrypt from transit from upstream system).
        /// </summary>
        /// <param name="data">The encrypted data received from upstream.</param>
        /// <param name="context">Pipeline context containing encryption metadata and security context.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Processing result containing decrypted data and updated context.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Typical inbound processing flow:
        /// 1. Extract encryption metadata from context
        /// 2. Validate metadata completeness and authenticity
        /// 3. Retrieve cipher preset by ID
        /// 4. Retrieve decryption key from IKeyStore
        /// 5. Decrypt data with specified cipher
        /// 6. Verify authentication tag (AEAD algorithms)
        /// 7. Emit metrics (decryption time, cipher used, data size)
        /// 8. Return plaintext data
        /// </para>
        /// <para>
        /// If the data is not encrypted (opportunistic mode used upstream), the stage
        /// returns the data unchanged with a warning in the context.
        /// </para>
        /// <para>
        /// Authentication failures (tampered data) throw CryptographicException.
        /// The pipeline should handle this by logging, alerting, and rejecting the data.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If data or context is null.</exception>
        /// <exception cref="System.Security.Cryptography.CryptographicException">If authentication fails or decryption fails.</exception>
        Task<PipelineStageResult> ProcessInboundAsync(
            byte[] data,
            TransitPipelineContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Processes outbound stream (encrypt for transit).
        /// Optimized for large payloads in streaming pipelines.
        /// </summary>
        /// <param name="inputStream">The plaintext stream to encrypt.</param>
        /// <param name="outputStream">The stream to write encrypted data to.</param>
        /// <param name="context">Pipeline context containing metadata and security context.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Processing result containing encryption metadata (ciphertext is written to stream).
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method uses streaming encryption for memory efficiency.
        /// Both streams must support reading/writing.
        /// </para>
        /// <para>
        /// The output stream will contain the encrypted data with an embedded header
        /// containing encryption metadata. The downstream system reads this header
        /// for decryption parameters.
        /// </para>
        /// </remarks>
        Task<PipelineStageResult> ProcessOutboundStreamAsync(
            Stream inputStream,
            Stream outputStream,
            TransitPipelineContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Processes inbound stream (decrypt from transit).
        /// Optimized for large payloads in streaming pipelines.
        /// </summary>
        /// <param name="inputStream">The encrypted stream received from upstream.</param>
        /// <param name="outputStream">The stream to write decrypted data to.</param>
        /// <param name="context">Pipeline context containing security context.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Processing result containing decryption metadata (plaintext is written to stream).
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method reads the encryption header from the input stream to determine
        /// decryption parameters. The input stream must be positioned at the beginning
        /// of the encrypted data (including header).
        /// </para>
        /// </remarks>
        Task<PipelineStageResult> ProcessInboundStreamAsync(
            Stream inputStream,
            Stream outputStream,
            TransitPipelineContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Negotiates cipher with a downstream/upstream endpoint.
        /// Used during pipeline initialization or connection establishment.
        /// </summary>
        /// <param name="remoteEndpoint">Remote endpoint identifier or URL.</param>
        /// <param name="direction">Direction of communication (Outbound or Inbound).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Negotiation result containing the selected cipher or error information.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method performs out-of-band cipher negotiation with a remote endpoint:
        /// 1. Retrieve local capabilities from ITransitEncryption
        /// 2. Query remote endpoint for its capabilities (HTTP, gRPC, message bus)
        /// 3. Negotiate best mutual cipher
        /// 4. Cache negotiation result for subsequent data transfers
        /// 5. Return negotiation result
        /// </para>
        /// <para>
        /// Negotiated ciphers are cached in the pipeline context or configuration store
        /// to avoid repeated negotiation overhead for every message.
        /// </para>
        /// <para>
        /// If the remote endpoint is unreachable or doesn't support negotiation,
        /// the result will indicate failure and the pipeline can use fallback behavior.
        /// </para>
        /// </remarks>
        Task<CipherNegotiationResult> NegotiateCipherWithEndpointAsync(
            string remoteEndpoint,
            PipelineDirection direction,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates the configuration of this pipeline stage.
        /// Called during pipeline initialization to ensure the stage is properly configured.
        /// </summary>
        /// <param name="stageConfiguration">Configuration dictionary for this stage.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Validation result with success flag and error messages if invalid.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Validation checks typically include:
        /// - Required configuration keys present (key store ID, preset ID, etc.)
        /// - Key store plugin is registered and accessible
        /// - Cipher preset IDs are valid
        /// - Security level constraints are satisfied
        /// - Endpoint URLs are valid (if specified)
        /// </para>
        /// <para>
        /// Pipelines should call this during initialization to fail fast on misconfiguration.
        /// </para>
        /// </remarks>
        Task<StageValidationResult> ValidateConfigurationAsync(
            Dictionary<string, object> stageConfiguration,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets metrics for this pipeline stage.
        /// Used for monitoring, alerting, and performance optimization.
        /// </summary>
        /// <returns>
        /// Metrics object with counts, latencies, and error rates.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Metrics may include:
        /// - Total messages processed (inbound/outbound)
        /// - Encryption/decryption success/failure counts
        /// - Average processing time
        /// - Cipher negotiation success rate
        /// - Authentication failure count (security alerts)
        /// - Bytes processed
        /// </para>
        /// <para>
        /// Integrate with observability systems (Prometheus, OpenTelemetry, etc.)
        /// for persistent metrics collection and alerting.
        /// </para>
        /// </remarks>
        Task<PipelineStageMetrics> GetMetricsAsync();
    }

    /// <summary>
    /// Direction of data flow in a pipeline.
    /// </summary>
    public enum PipelineDirection
    {
        /// <summary>
        /// Outbound: Data flowing from this system to a downstream system (encrypt).
        /// </summary>
        Outbound,

        /// <summary>
        /// Inbound: Data flowing from an upstream system to this system (decrypt).
        /// </summary>
        Inbound
    }

    /// <summary>
    /// Context for transit encryption pipeline stages.
    /// Contains metadata, security context, and endpoint information.
    /// </summary>
    public class TransitPipelineContext
    {
        /// <summary>
        /// Security context for key access and audit trail.
        /// </summary>
        public required ISecurityContext SecurityContext { get; init; }

        /// <summary>
        /// Encryption metadata for inbound processing or to be populated for outbound processing.
        /// </summary>
        public Dictionary<string, object> EncryptionMetadata { get; init; } = new();

        /// <summary>
        /// Remote endpoint capabilities (for outbound cipher negotiation).
        /// </summary>
        public EndpointCapabilities? RemoteCapabilities { get; init; }

        /// <summary>
        /// Pipeline-level metadata (request ID, correlation ID, timestamps, etc.).
        /// </summary>
        public Dictionary<string, object> PipelineMetadata { get; init; } = new();

        /// <summary>
        /// Transit encryption options to use for this operation.
        /// If null, defaults from pipeline configuration are used.
        /// </summary>
        public TransitEncryptionOptions? EncryptionOptions { get; init; }

        /// <summary>
        /// Remote endpoint identifier or URL.
        /// </summary>
        public string? RemoteEndpoint { get; init; }

        /// <summary>
        /// Warnings accumulated during pipeline processing.
        /// </summary>
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Result of a pipeline stage operation.
    /// </summary>
    public record PipelineStageResult
    {
        /// <summary>
        /// The processed data (encrypted or decrypted).
        /// Null for streaming operations (data is written to stream).
        /// </summary>
        public byte[]? ProcessedData { get; init; }

        /// <summary>
        /// Whether the operation was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Updated context after processing (may contain new metadata).
        /// </summary>
        public required TransitPipelineContext Context { get; init; }

        /// <summary>
        /// Error message if Success is false.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// The cipher preset ID that was used for encryption/decryption.
        /// </summary>
        public string? UsedPresetId { get; init; }

        /// <summary>
        /// Processing time in milliseconds.
        /// </summary>
        public double ProcessingTimeMs { get; init; }

        /// <summary>
        /// Whether the data was actually encrypted/decrypted or passed through (opportunistic mode).
        /// </summary>
        public bool WasEncrypted { get; init; }
    }

    /// <summary>
    /// Result of stage configuration validation.
    /// </summary>
    public record StageValidationResult
    {
        /// <summary>
        /// Whether the configuration is valid.
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// List of validation errors (empty if valid).
        /// </summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        /// <summary>
        /// List of validation warnings (non-fatal issues).
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Additional validation metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Metrics for a pipeline stage.
    /// </summary>
    public record PipelineStageMetrics
    {
        /// <summary>
        /// Total outbound messages processed.
        /// </summary>
        public long TotalOutboundMessages { get; init; }

        /// <summary>
        /// Total inbound messages processed.
        /// </summary>
        public long TotalInboundMessages { get; init; }

        /// <summary>
        /// Successful encryption operations.
        /// </summary>
        public long SuccessfulEncryptions { get; init; }

        /// <summary>
        /// Failed encryption operations.
        /// </summary>
        public long FailedEncryptions { get; init; }

        /// <summary>
        /// Successful decryption operations.
        /// </summary>
        public long SuccessfulDecryptions { get; init; }

        /// <summary>
        /// Failed decryption operations.
        /// </summary>
        public long FailedDecryptions { get; init; }

        /// <summary>
        /// Authentication failures (tampered data).
        /// </summary>
        public long AuthenticationFailures { get; init; }

        /// <summary>
        /// Average encryption time in milliseconds.
        /// </summary>
        public double AverageEncryptionTimeMs { get; init; }

        /// <summary>
        /// Average decryption time in milliseconds.
        /// </summary>
        public double AverageDecryptionTimeMs { get; init; }

        /// <summary>
        /// Total bytes encrypted.
        /// </summary>
        public long TotalBytesEncrypted { get; init; }

        /// <summary>
        /// Total bytes decrypted.
        /// </summary>
        public long TotalBytesDecrypted { get; init; }

        /// <summary>
        /// Cipher negotiation success rate (0.0 to 1.0).
        /// </summary>
        public double NegotiationSuccessRate { get; init; }

        /// <summary>
        /// Timestamp when metrics collection started (UTC).
        /// </summary>
        public DateTime MetricsStartedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Additional metrics metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }
}
