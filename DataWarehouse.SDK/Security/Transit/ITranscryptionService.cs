namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>
    /// Interface for transcryption (re-encryption) operations during data transit.
    /// Enables secure key rotation, security level upgrades, and cross-domain data transfer
    /// without exposing plaintext to intermediate systems.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transcryption is the process of converting ciphertext encrypted with one key (Key A)
    /// to ciphertext encrypted with a different key (Key B) without decrypting to plaintext
    /// in the intermediate system's memory.
    /// </para>
    /// <para>
    /// Use cases for transcryption:
    /// - Key rotation: Migrate encrypted data to new keys during key lifecycle management
    /// - Security level upgrade: Re-encrypt Standard-level data to High or QuantumSafe level
    /// - Cross-domain transfer: Re-encrypt data from one security domain to another
    /// - Multi-tenant isolation: Re-encrypt data when transferring between tenants
    /// - Compliance migration: Upgrade encryption to meet new regulatory requirements
    /// </para>
    /// <para>
    /// Security considerations:
    /// - Transcryption should minimize plaintext exposure in memory (use secure buffers)
    /// - Both source and target keys must be accessed through IKeyStore with proper ACL checks
    /// - Integrity verification is critical to prevent key oracle attacks
    /// - Audit events should be published for compliance tracking
    /// </para>
    /// <para>
    /// Performance note: Transcryption requires full decrypt + re-encrypt, so it has similar
    /// performance to two separate operations. For large datasets, consider streaming transcryption.
    /// </para>
    /// </remarks>
    public interface ITranscryptionService
    {
        /// <summary>
        /// Transcrypts data from one encryption key to another.
        /// Decrypts with Key A and immediately re-encrypts with Key B.
        /// </summary>
        /// <param name="sourceCiphertext">The ciphertext encrypted with the source key (Key A).</param>
        /// <param name="sourceMetadata">Encryption metadata for the source ciphertext (preset ID, IV, etc.).</param>
        /// <param name="options">Transcryption options specifying source and target keys.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Transcryption result containing the re-encrypted ciphertext and new metadata.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method performs the following steps:
        /// 1. Validate source metadata and integrity (if VerifyIntegrity option is true)
        /// 2. Retrieve source key (Key A) from IKeyStore using SourceKeyId and SourceSecurityContext
        /// 3. Decrypt source ciphertext to plaintext
        /// 4. Retrieve target key (Key B) from IKeyStore using TargetKeyId and TargetSecurityContext
        /// 5. Re-encrypt plaintext with target key
        /// 6. Securely wipe plaintext from memory
        /// 7. Publish audit events for both decryption and re-encryption
        /// 8. Return new ciphertext and metadata
        /// </para>
        /// <para>
        /// Security best practices:
        /// - Always enable VerifyIntegrity (default: true) to prevent key oracle attacks
        /// - Use separate security contexts if source and target keys have different ACLs
        /// - Monitor audit logs for unauthorized transcryption attempts
        /// </para>
        /// <para>
        /// If the source and target keys are the same, this method still performs full transcryption
        /// (useful for generating a new IV/nonce for forward secrecy).
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If sourceCiphertext, sourceMetadata, or options is null.</exception>
        /// <exception cref="System.Security.Cryptography.CryptographicException">If integrity verification fails or decryption fails.</exception>
        /// <exception cref="InvalidOperationException">If source or target key cannot be retrieved.</exception>
        /// <exception cref="System.Security.SecurityException">If security context lacks required permissions.</exception>
        Task<TranscryptionResult> TranscryptAsync(
            byte[] sourceCiphertext,
            Dictionary<string, object> sourceMetadata,
            TranscryptionOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Transcrypts a stream from one encryption key to another.
        /// Optimized for large payloads that don't fit in memory.
        /// </summary>
        /// <param name="sourceCiphertextStream">The encrypted stream with source key (Key A).</param>
        /// <param name="targetCiphertextStream">The stream to write re-encrypted data to (Key B).</param>
        /// <param name="options">Transcryption options specifying source and target keys.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Transcryption result containing metadata for the new encryption (ciphertext is written to stream).
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method uses streaming transcryption to process data in chunks, minimizing memory usage.
        /// It reads the encryption header from the source stream to determine source cipher and parameters.
        /// </para>
        /// <para>
        /// The transcryption is performed in streaming mode:
        /// 1. Read encrypted chunk from source stream
        /// 2. Decrypt chunk with source key
        /// 3. Re-encrypt chunk with target key
        /// 4. Write re-encrypted chunk to target stream
        /// 5. Securely wipe intermediate buffers
        /// 6. Repeat until entire stream is processed
        /// 7. Verify and write authentication tags
        /// </para>
        /// <para>
        /// Both streams must support reading/writing. The source stream must be positioned at the
        /// beginning of the encrypted data (including the encryption header).
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If any stream or options is null.</exception>
        /// <exception cref="System.Security.Cryptography.CryptographicException">If integrity verification fails.</exception>
        Task<TranscryptionResult> TranscryptStreamAsync(
            Stream sourceCiphertextStream,
            Stream targetCiphertextStream,
            TranscryptionOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Performs batch transcryption of multiple ciphertexts with the same source and target keys.
        /// More efficient than calling TranscryptAsync multiple times due to key caching.
        /// </summary>
        /// <param name="items">List of ciphertext/metadata pairs to transcrypt.</param>
        /// <param name="options">Transcryption options specifying source and target keys (applies to all items).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// List of transcryption results, one for each input item, in the same order.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method is optimized for batch operations:
        /// - Source and target keys are retrieved once and cached for all items
        /// - Audit events are batched for improved performance
        /// - Parallelization may be used for independent items (implementation-specific)
        /// </para>
        /// <para>
        /// Use this method when performing key rotation across many objects or migrating
        /// entire datasets to a new security level.
        /// </para>
        /// <para>
        /// If any item fails transcryption, the method continues processing remaining items
        /// and includes error information in the corresponding result entry.
        /// Check TranscryptionResult.Success for each item.
        /// </para>
        /// </remarks>
        Task<IReadOnlyList<TranscryptionResult>> TranscryptBatchAsync(
            IReadOnlyList<(byte[] Ciphertext, Dictionary<string, object> Metadata)> items,
            TranscryptionOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Upgrades the security level of encrypted data without changing the key.
        /// Re-encrypts with a higher security preset (e.g., Standard to High, High to QuantumSafe).
        /// </summary>
        /// <param name="sourceCiphertext">The ciphertext to upgrade.</param>
        /// <param name="sourceMetadata">Encryption metadata for the source ciphertext.</param>
        /// <param name="targetSecurityLevel">The target security level to upgrade to.</param>
        /// <param name="context">Security context for key access.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Transcryption result containing re-encrypted ciphertext at the higher security level.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method simplifies security level upgrades by:
        /// - Automatically selecting the appropriate target preset for the security level
        /// - Using the same key ID for source and target (only cipher changes)
        /// - Setting SecurityLevelUpgraded flag in the result
        /// </para>
        /// <para>
        /// Common upgrade paths:
        /// - Standard -> High: Add HMAC-SHA512 for dual integrity protection
        /// - High -> Military: Upgrade to Suite B algorithms with FIPS 140-2 Level 3+ modules
        /// - Military -> QuantumSafe: Add post-quantum cryptography (Kyber + Dilithium)
        /// </para>
        /// <para>
        /// If the source is already at or above the target security level, this method
        /// still performs transcryption (generates new IV/nonce) but sets SecurityLevelUpgraded = false.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If sourceCiphertext, sourceMetadata, or context is null.</exception>
        /// <exception cref="InvalidOperationException">If target security level preset cannot be found.</exception>
        Task<TranscryptionResult> UpgradeSecurityLevelAsync(
            byte[] sourceCiphertext,
            Dictionary<string, object> sourceMetadata,
            TransitSecurityLevel targetSecurityLevel,
            ISecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that transcryption is possible for the given options.
        /// Checks key availability, security context permissions, and preset compatibility.
        /// </summary>
        /// <param name="options">Transcryption options to validate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Validation result with success flag and error message if validation failed.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method performs pre-flight validation without decrypting any data:
        /// 1. Verify source key exists and is accessible to SourceSecurityContext
        /// 2. Verify target key exists and is accessible to TargetSecurityContext
        /// 3. Validate source preset exists (if specified in options)
        /// 4. Validate target preset exists (if specified in options)
        /// 5. Check that security contexts have required permissions
        /// </para>
        /// <para>
        /// Use this method before batch transcryption to fail fast if configuration is invalid.
        /// </para>
        /// </remarks>
        Task<(bool IsValid, string? ErrorMessage)> ValidateTranscryptionOptionsAsync(
            TranscryptionOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets statistics about transcryption operations.
        /// Useful for monitoring key rotation progress and compliance reporting.
        /// </summary>
        /// <returns>
        /// Statistics object with counts, success rates, and performance metrics.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Statistics may include:
        /// - Total transcryptions performed
        /// - Success/failure counts
        /// - Average transcryption time
        /// - Security level upgrade counts
        /// - Most common source/target key pairs
        /// </para>
        /// <para>
        /// Statistics are typically maintained in-memory and reset on service restart.
        /// For persistent metrics, integrate with observability systems (Prometheus, etc.).
        /// </para>
        /// </remarks>
        Task<TranscryptionStatistics> GetStatisticsAsync();
    }

    /// <summary>
    /// Statistics about transcryption operations.
    /// </summary>
    public record TranscryptionStatistics
    {
        /// <summary>
        /// Total number of transcryption operations performed.
        /// </summary>
        public long TotalOperations { get; init; }

        /// <summary>
        /// Number of successful transcryption operations.
        /// </summary>
        public long SuccessfulOperations { get; init; }

        /// <summary>
        /// Number of failed transcryption operations.
        /// </summary>
        public long FailedOperations { get; init; }

        /// <summary>
        /// Number of security level upgrade operations.
        /// </summary>
        public long SecurityUpgradeOperations { get; init; }

        /// <summary>
        /// Average transcryption time in milliseconds.
        /// </summary>
        public double AverageTranscryptionTimeMs { get; init; }

        /// <summary>
        /// Total bytes transcrypted.
        /// </summary>
        public long TotalBytesTranscrypted { get; init; }

        /// <summary>
        /// Most common source key IDs (key ID, count).
        /// </summary>
        public IReadOnlyDictionary<string, long> TopSourceKeys { get; init; } = new Dictionary<string, long>();

        /// <summary>
        /// Most common target key IDs (key ID, count).
        /// </summary>
        public IReadOnlyDictionary<string, long> TopTargetKeys { get; init; } = new Dictionary<string, long>();

        /// <summary>
        /// Timestamp when statistics collection started (UTC).
        /// </summary>
        public DateTime StatisticsStartedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Additional statistics metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }
}
