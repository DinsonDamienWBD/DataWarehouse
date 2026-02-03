namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>
    /// Interface for transit-specific encryption operations.
    /// Provides encryption/decryption with cipher negotiation, endpoint-adaptive security,
    /// and additional authenticated data support for data in transit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transit encryption differs from at-rest encryption in several ways:
    /// - Cipher negotiation: Endpoints may support different algorithms, requiring runtime negotiation
    /// - Performance sensitivity: Transit often requires lower latency than storage
    /// - Additional authenticated data: Useful for including metadata (request IDs, timestamps, routing info)
    /// - Ephemeral keys: May use session keys rather than long-lived storage keys
    /// - Compression: Often beneficial for network transfer
    /// </para>
    /// <para>
    /// This interface supports multiple encryption modes:
    /// - AlwaysEncrypt: Defense-in-depth even over TLS (recommended for sensitive data)
    /// - Opportunistic: Encrypt if endpoint supports it, fall back to TLS only
    /// - None: Rely on transport-level security only (TLS/HTTPS)
    /// </para>
    /// <para>
    /// Implementations should integrate with:
    /// - IKeyStore for key retrieval
    /// - ICommonCipherPresets for cipher selection
    /// - Message bus for audit events
    /// </para>
    /// </remarks>
    public interface ITransitEncryption
    {
        /// <summary>
        /// Encrypts data for transit to a remote endpoint.
        /// </summary>
        /// <param name="plaintext">The plaintext data to encrypt.</param>
        /// <param name="options">Encryption options including cipher selection and endpoint capabilities.</param>
        /// <param name="context">Security context for key access and audit trail.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Encryption result containing ciphertext and metadata needed for decryption.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method performs the following steps:
        /// 1. Cipher negotiation (if RemoteCapabilities provided in options)
        /// 2. Key retrieval from IKeyStore
        /// 3. Optional compression (if CompressBeforeEncryption is true)
        /// 4. Encryption with selected cipher preset
        /// 5. Audit event publication (if message bus configured)
        /// </para>
        /// <para>
        /// The result includes EncryptionMetadata which must be transmitted to the remote endpoint
        /// for decryption. This metadata typically includes: IV/nonce, preset ID, compression flag.
        /// </para>
        /// <para>
        /// If cipher negotiation fails (no mutually supported ciphers), this method throws
        /// InvalidOperationException with details about the negotiation failure.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If plaintext, options, or context is null.</exception>
        /// <exception cref="InvalidOperationException">If cipher negotiation fails or key cannot be retrieved.</exception>
        /// <exception cref="System.Security.SecurityException">If security context lacks required permissions.</exception>
        Task<TransitEncryptionResult> EncryptForTransitAsync(
            byte[] plaintext,
            TransitEncryptionOptions options,
            ISecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Encrypts a stream for transit to a remote endpoint.
        /// </summary>
        /// <param name="plaintextStream">The plaintext stream to encrypt.</param>
        /// <param name="ciphertextStream">The stream to write encrypted data to.</param>
        /// <param name="options">Encryption options including cipher selection and endpoint capabilities.</param>
        /// <param name="context">Security context for key access and audit trail.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Encryption result containing metadata needed for decryption (ciphertext is written to stream).
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method is optimized for large payloads that don't fit in memory.
        /// It uses streaming encryption to process data in chunks.
        /// </para>
        /// <para>
        /// The ciphertext stream will contain:
        /// 1. Encryption header (magic bytes, version, metadata length)
        /// 2. Encryption metadata (JSON or binary serialized)
        /// 3. Encrypted payload (streamed in chunks)
        /// 4. Authentication tag (AEAD algorithms)
        /// </para>
        /// <para>
        /// Both streams must support seeking if compression is enabled.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If any stream, options, or context is null.</exception>
        /// <exception cref="InvalidOperationException">If cipher negotiation fails or streams don't support required operations.</exception>
        Task<TransitEncryptionResult> EncryptStreamForTransitAsync(
            Stream plaintextStream,
            Stream ciphertextStream,
            TransitEncryptionOptions options,
            ISecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Decrypts data received from a remote endpoint.
        /// </summary>
        /// <param name="ciphertext">The encrypted data received from transit.</param>
        /// <param name="encryptionMetadata">Metadata from the encryption operation (preset ID, IV, nonce, etc.).</param>
        /// <param name="context">Security context for key access and audit trail.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Decryption result containing plaintext and information about the encryption that was used.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method performs the following steps:
        /// 1. Validate encryption metadata
        /// 2. Retrieve cipher preset by ID
        /// 3. Retrieve decryption key from IKeyStore
        /// 4. Decrypt with selected cipher
        /// 5. Optional decompression (if metadata indicates compression was used)
        /// 6. Verify authentication tag (AEAD algorithms)
        /// 7. Audit event publication
        /// </para>
        /// <para>
        /// If the ciphertext has been tampered with or the authentication tag is invalid,
        /// this method throws System.Security.Cryptography.CryptographicException.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If ciphertext, metadata, or context is null.</exception>
        /// <exception cref="System.Security.Cryptography.CryptographicException">If authentication fails or decryption fails.</exception>
        /// <exception cref="InvalidOperationException">If preset ID is unknown or key cannot be retrieved.</exception>
        Task<TransitDecryptionResult> DecryptFromTransitAsync(
            byte[] ciphertext,
            Dictionary<string, object> encryptionMetadata,
            ISecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Decrypts a stream received from a remote endpoint.
        /// </summary>
        /// <param name="ciphertextStream">The encrypted stream received from transit.</param>
        /// <param name="plaintextStream">The stream to write decrypted data to.</param>
        /// <param name="context">Security context for key access and audit trail.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Decryption result containing information about the decryption operation (plaintext is written to stream).
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method is optimized for large payloads that don't fit in memory.
        /// It reads the encryption header from the stream to determine cipher and parameters.
        /// </para>
        /// <para>
        /// The ciphertext stream must be positioned at the beginning of the encrypted data
        /// (including the encryption header).
        /// </para>
        /// <para>
        /// Authentication is verified after the entire stream has been decrypted.
        /// If authentication fails, the plaintext stream content should be discarded.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If any stream or context is null.</exception>
        /// <exception cref="System.Security.Cryptography.CryptographicException">If authentication fails or decryption fails.</exception>
        Task<TransitDecryptionResult> DecryptStreamFromTransitAsync(
            Stream ciphertextStream,
            Stream plaintextStream,
            ISecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Negotiates the best cipher preset with a remote endpoint based on mutual capabilities.
        /// </summary>
        /// <param name="localPreferences">Local endpoint's preferred cipher presets (in priority order).</param>
        /// <param name="remoteCapabilities">Remote endpoint's supported cipher presets and security levels.</param>
        /// <param name="minimumSecurityLevel">Minimum acceptable security level (negotiation fails if not met).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Negotiation result containing the selected cipher preset or error information.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Cipher negotiation algorithm:
        /// 1. Filter remote capabilities by minimum security level
        /// 2. Find intersection of local preferences and remote supported presets
        /// 3. Select the highest-priority mutual preset
        /// 4. If no mutual presets, return failure with error message
        /// </para>
        /// <para>
        /// Use this method during handshake/connection establishment to agree on encryption parameters
        /// before transmitting sensitive data.
        /// </para>
        /// <para>
        /// The negotiation is deterministic: given the same inputs, it always produces the same result.
        /// This ensures both endpoints arrive at the same cipher selection.
        /// </para>
        /// </remarks>
        Task<CipherNegotiationResult> NegotiateCipherAsync(
            IReadOnlyList<string> localPreferences,
            EndpointCapabilities remoteCapabilities,
            TransitSecurityLevel minimumSecurityLevel = TransitSecurityLevel.Standard,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the capabilities of this transit encryption implementation.
        /// Used for advertising to remote endpoints during handshake.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Endpoint capabilities object describing supported cipher presets and security levels.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The returned capabilities should include:
        /// - All cipher preset IDs supported by this implementation
        /// - All individual algorithm names supported
        /// - Maximum security level this endpoint can handle
        /// - Whether transcryption is supported
        /// - Protocol version and extensions
        /// </para>
        /// <para>
        /// Transmit this object to remote endpoints during connection establishment
        /// for cipher negotiation.
        /// </para>
        /// </remarks>
        Task<EndpointCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    }
}
