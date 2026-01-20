using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AuditLogging
{
    /// <summary>
    /// Immutable audit logging plugin with hash chain verification for tamper-proof audit trails.
    /// Extends FeaturePluginBase for lifecycle management.
    /// <para>
    /// This plugin implements cryptographic hash chain verification to ensure audit log integrity,
    /// providing tamper-evident logging suitable for regulated environments.
    /// </para>
    /// <para>
    /// <b>Compliance frameworks supported:</b>
    /// <list type="bullet">
    ///   <item><description>HIPAA (Health Insurance Portability and Accountability Act)</description></item>
    ///   <item><description>SOX (Sarbanes-Oxley)</description></item>
    ///   <item><description>GDPR (General Data Protection Regulation)</description></item>
    ///   <item><description>PCI-DSS (Payment Card Industry Data Security Standard)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Hash Chain Features:</b>
    /// <list type="bullet">
    ///   <item><description>SHA-256 hash linking of consecutive entries</description></item>
    ///   <item><description>Genesis hash initialization for chain anchor</description></item>
    ///   <item><description>Chain metadata persistence in .chain files</description></item>
    ///   <item><description>Monotonically increasing sequence numbers</description></item>
    ///   <item><description>Tamper detection with specific error reporting</description></item>
    ///   <item><description>Partial and full chain verification</description></item>
    ///   <item><description>Chain recovery for minor corruption</description></item>
    ///   <item><description>Cryptographic proof export for auditors</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Message Commands:</b>
    /// <list type="bullet">
    ///   <item><description>audit.log: Log an audit event</description></item>
    ///   <item><description>audit.query: Query audit logs</description></item>
    ///   <item><description>audit.export: Export audit logs</description></item>
    ///   <item><description>audit.verify: Verify audit log integrity</description></item>
    ///   <item><description>audit.configure: Configure audit settings</description></item>
    ///   <item><description>audit.chain.status: Get chain health status</description></item>
    ///   <item><description>audit.chain.repair: Attempt chain recovery</description></item>
    ///   <item><description>audit.chain.export-proof: Export cryptographic proof</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hash chain provides cryptographic guarantees that audit entries have not been modified,
    /// deleted, or reordered after creation. Each entry contains:
    /// </para>
    /// <list type="number">
    ///   <item><description>A SHA-256 hash of its content combined with the previous entry's hash</description></item>
    ///   <item><description>A reference to the previous entry's hash (PreviousHash)</description></item>
    ///   <item><description>A monotonically increasing sequence number</description></item>
    /// </list>
    /// <para>
    /// Chain metadata (entry count, last hash, sequence number) is persisted in a separate .chain
    /// file to enable efficient verification and recovery operations.
    /// </para>
    /// </remarks>
    public sealed class AuditLoggingPlugin : FeaturePluginBase
    {
        #region Constants

        /// <summary>
        /// The genesis hash used as the PreviousHash for the first entry in the chain.
        /// This known constant provides a verifiable starting point for chain verification.
        /// </summary>
        /// <remarks>
        /// Using a constant genesis hash ensures that chain verification can always start
        /// from a known state. This is critical for compliance audits where the entire
        /// chain must be verifiable from origin.
        /// </remarks>
        public const string GenesisHashString = "DATAWAREHOUSE_AUDIT_GENESIS_V1";

        /// <summary>
        /// The file extension used for chain metadata files.
        /// </summary>
        private const string ChainFileExtension = ".chain";

        /// <summary>
        /// Maximum number of entries to verify in a single batch for partial verification.
        /// </summary>
        private const int PartialVerificationBatchSize = 10000;

        #endregion

        #region Private Fields

        private readonly AuditConfig _config;
        private readonly ConcurrentQueue<AuditEntry> _buffer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _chainMetadataLock = new(1, 1);
        private readonly CancellationTokenSource _shutdownCts = new();

        private StreamWriter? _currentWriter;
        private string? _currentLogPath;
        private byte[] _previousHash;
        private long _sequenceNumber;
        private Task? _flushTask;

        // Chain metadata tracking
        private ChainMetadata _chainMetadata;
        private string? _chainMetadataPath;

        // High-stakes tier components
        private readonly AuditMerkleTree _merkleTree = new();
        private AuditSignatureProvider? _signatureProvider;
        private readonly List<AuditCheckpoint> _checkpoints = new();
        private long _lastCheckpointSequence;

        #endregion

        #region Plugin Properties

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.audit";

        /// <inheritdoc/>
        public override string Name => "Audit Logging";

        /// <inheritdoc/>
        public override string Version => "2.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.GovernanceProvider;

        /// <summary>
        /// Gets the computed SHA-256 genesis hash used as the anchor for the hash chain.
        /// </summary>
        /// <value>
        /// A byte array containing the SHA-256 hash of <see cref="GenesisHashString"/>.
        /// </value>
        public static byte[] GenesisHash => SHA256.HashData(Encoding.UTF8.GetBytes(GenesisHashString));

        /// <summary>
        /// Gets the genesis hash as a hexadecimal string.
        /// </summary>
        /// <value>
        /// The uppercase hexadecimal representation of the genesis hash.
        /// </value>
        public static string GenesisHashHex => Convert.ToHexString(GenesisHash);

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="AuditLoggingPlugin"/> class.
        /// </summary>
        /// <param name="config">
        /// Optional configuration for the audit logging plugin. If null, default configuration is used.
        /// </param>
        /// <remarks>
        /// The plugin initializes with the genesis hash as the initial previous hash,
        /// ensuring that the first entry in any chain links back to the known genesis.
        /// </remarks>
        public AuditLoggingPlugin(AuditConfig? config = null)
        {
            _config = config ?? new AuditConfig();
            _buffer = new ConcurrentQueue<AuditEntry>();
            _previousHash = GenesisHash;
            _chainMetadata = new ChainMetadata
            {
                GenesisHash = GenesisHashHex,
                CreatedAt = DateTime.UtcNow
            };
        }

        #endregion

        #region Plugin Lifecycle

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "audit.log", DisplayName = "Log Event", Description = "Log an audit event" },
                new() { Name = "audit.query", DisplayName = "Query", Description = "Query audit logs" },
                new() { Name = "audit.export", DisplayName = "Export", Description = "Export audit logs in compliance format" },
                new() { Name = "audit.verify", DisplayName = "Verify", Description = "Verify audit log integrity" },
                new() { Name = "audit.configure", DisplayName = "Configure", Description = "Configure audit settings" },
                new() { Name = "audit.rotate", DisplayName = "Rotate", Description = "Force log rotation" },
                new() { Name = "audit.chain.status", DisplayName = "Chain Status", Description = "Get hash chain health status" },
                new() { Name = "audit.chain.verify", DisplayName = "Verify Chain", Description = "Verify entire chain integrity" },
                new() { Name = "audit.chain.verify-entry", DisplayName = "Verify Entry", Description = "Verify single entry and its chain link" },
                new() { Name = "audit.chain.repair", DisplayName = "Repair Chain", Description = "Attempt to recover from chain corruption" },
                new() { Name = "audit.chain.export-proof", DisplayName = "Export Proof", Description = "Export cryptographic proof for auditors" }
            ];
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "AuditLogging";
            metadata["SupportsImmutability"] = true;
            metadata["SupportsHashChain"] = true;
            metadata["HashChainVersion"] = "2.0";
            metadata["HashAlgorithm"] = "SHA-256";
            metadata["GenesisHash"] = GenesisHashHex;
            metadata["ComplianceFormats"] = new[] { "HIPAA", "SOX", "GDPR", "PCI-DSS" };
            metadata["ExportFormats"] = new[] { "JSON", "CSV", "SIEM" };
            metadata["RetentionDays"] = _config.RetentionDays;
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_config.LogDirectory);
            await LoadOrCreateChainMetadataAsync();
            await InitializeCurrentLogAsync();

            // Initialize high-stakes tier components
            if (_config.EnableDigitalSignatures)
            {
                _signatureProvider = new AuditSignatureProvider(_config.SigningKeyPath);
            }

            _flushTask = FlushLoopAsync(_shutdownCts.Token);
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            _shutdownCts.Cancel();

            if (_flushTask != null)
            {
                try { await _flushTask; } catch { }
            }

            await FlushBufferAsync();
            await SaveChainMetadataAsync();

            if (_currentWriter != null)
            {
                await _currentWriter.DisposeAsync();
            }
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "audit.log":
                    await HandleLogAsync(message);
                    break;
                case "audit.query":
                    await HandleQueryAsync(message);
                    break;
                case "audit.export":
                    await HandleExportAsync(message);
                    break;
                case "audit.verify":
                    await HandleVerifyAsync(message);
                    break;
                case "audit.configure":
                    HandleConfigure(message);
                    break;
                case "audit.rotate":
                    await RotateLogAsync();
                    break;
                case "audit.chain.status":
                    await HandleChainStatusAsync(message);
                    break;
                case "audit.chain.verify":
                    await HandleChainVerifyAsync(message);
                    break;
                case "audit.chain.verify-entry":
                    await HandleVerifyEntryAsync(message);
                    break;
                case "audit.chain.repair":
                    await HandleChainRepairAsync(message);
                    break;
                case "audit.chain.export-proof":
                    await HandleExportProofAsync(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        #endregion

        #region Core Logging Methods

        /// <summary>
        /// Logs an audit event with cryptographic hash chain linking.
        /// </summary>
        /// <param name="evt">The audit event to log.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// <para>
        /// Each logged entry is linked to the previous entry via SHA-256 hash,
        /// creating a tamper-evident chain. The entry includes:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>PreviousHash: Hash of the preceding entry (or genesis hash for first entry)</description></item>
        ///   <item><description>EntryHash: SHA-256(content + PreviousHash)</description></item>
        ///   <item><description>SequenceNumber: Monotonically increasing counter</description></item>
        /// </list>
        /// <para>
        /// This method is thread-safe and uses proper locking for concurrent access.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when evt is null.</exception>
        public async Task LogAsync(AuditEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);

            await _writeLock.WaitAsync();
            try
            {
                var entry = new AuditEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = DateTime.UtcNow,
                    SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
                    Action = evt.Action,
                    ResourceType = evt.ResourceType,
                    ResourceId = evt.ResourceId,
                    UserId = evt.UserId,
                    TenantId = evt.TenantId,
                    IpAddress = evt.IpAddress,
                    UserAgent = evt.UserAgent,
                    Outcome = evt.Outcome,
                    Details = evt.Details,
                    Severity = evt.Severity,
                    ComplianceTags = evt.ComplianceTags ?? []
                };

                // Link to previous entry in chain
                entry.PreviousHash = Convert.ToHexString(_previousHash);
                entry.EntryHash = ComputeEntryHash(entry);

                // Update chain state
                _previousHash = Convert.FromHexString(entry.EntryHash);

                // Update chain metadata
                await UpdateChainMetadataAsync(entry);

                // Add to Merkle tree for high-stakes tier
                if (_config.EnableMerkleTree)
                {
                    _merkleTree.AddLeaf(entry.EntryHash);

                    // Create checkpoint if block size reached
                    if (_merkleTree.LeafCount >= _config.MerkleTreeBlockSize)
                    {
                        await CreateCheckpointAsync();
                    }
                }

                _buffer.Enqueue(entry);

                if (_buffer.Count >= _config.FlushThreshold)
                {
                    await FlushBufferInternalAsync();
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        #endregion

        #region Hash Chain Verification Methods

        /// <summary>
        /// Verifies the integrity of the entire hash chain from genesis.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for long-running verification.</param>
        /// <returns>
        /// A <see cref="ChainVerificationResult"/> containing detailed verification results,
        /// including any detected tampering or corruption.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method performs comprehensive verification including:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>Genesis hash anchor validation</description></item>
        ///   <item><description>Sequential hash chain link verification</description></item>
        ///   <item><description>Sequence number continuity check</description></item>
        ///   <item><description>Entry hash recomputation and comparison</description></item>
        /// </list>
        /// <para>
        /// For HIPAA/SOX/PCI-DSS compliance, this provides cryptographic proof that
        /// audit logs have not been modified since creation.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var plugin = new AuditLoggingPlugin();
        /// await plugin.StartAsync(CancellationToken.None);
        ///
        /// var result = await plugin.VerifyChainIntegrityAsync();
        /// if (!result.IsValid)
        /// {
        ///     foreach (var error in result.TamperDetails)
        ///     {
        ///         Console.WriteLine($"Tampering detected: {error.Description}");
        ///     }
        /// }
        /// </code>
        /// </example>
        public async Task<ChainVerificationResult> VerifyChainIntegrityAsync(CancellationToken cancellationToken = default)
        {
            var result = new ChainVerificationResult
            {
                VerificationStartTime = DateTime.UtcNow,
                VerificationType = ChainVerificationType.Full
            };

            try
            {
                // Load all entries ordered by sequence number
                var entries = await QueryLogsAsync(DateTime.MinValue, DateTime.UtcNow, null, null, int.MaxValue);
                var orderedEntries = entries.OrderBy(e => e.SequenceNumber).ToList();

                result.TotalEntriesExamined = orderedEntries.Count;

                if (orderedEntries.Count == 0)
                {
                    result.IsValid = true;
                    result.ChainStatus = ChainHealthStatus.Healthy;
                    result.VerificationEndTime = DateTime.UtcNow;
                    return result;
                }

                // Verify genesis link for first entry
                var firstEntry = orderedEntries[0];
                if (firstEntry.PreviousHash != GenesisHashHex)
                {
                    result.IsValid = false;
                    result.ChainStatus = ChainHealthStatus.Corrupted;
                    result.TamperDetails.Add(new TamperDetail
                    {
                        EntryId = firstEntry.Id,
                        SequenceNumber = firstEntry.SequenceNumber,
                        TamperType = TamperType.GenesisLinkBroken,
                        Description = $"First entry does not link to genesis hash. Expected: {GenesisHashHex}, Found: {firstEntry.PreviousHash}",
                        DetectedAt = DateTime.UtcNow,
                        Severity = TamperSeverity.Critical
                    });
                    await LogTamperingAttemptAsync(firstEntry, TamperType.GenesisLinkBroken);
                }

                // Verify each entry's hash and chain link
                string expectedPreviousHash = GenesisHashHex;

                for (int i = 0; i < orderedEntries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = orderedEntries[i];
                    var validationResult = ValidateEntry(entry, expectedPreviousHash, i > 0 ? orderedEntries[i - 1] : null);

                    if (!validationResult.IsValid)
                    {
                        result.TamperDetails.AddRange(validationResult.TamperDetails);
                        await LogTamperingAttemptAsync(entry, validationResult.TamperDetails.FirstOrDefault()?.TamperType ?? TamperType.Unknown);
                    }
                    else
                    {
                        result.EntriesVerified++;
                    }

                    expectedPreviousHash = entry.EntryHash;
                }

                // Verify Merkle trees via checkpoints if enabled
                if (_config.EnableMerkleTree)
                {
                    var merkleResult = await VerifyMerkleCheckpointsAsync();
                    result.MerkleTreeValid = merkleResult;
                    if (!merkleResult)
                    {
                        result.TamperDetails.Add(new TamperDetail
                        {
                            TamperType = TamperType.MerkleTreeCorrupted,
                            Description = "Merkle tree checkpoint verification failed",
                            DetectedAt = DateTime.UtcNow,
                            Severity = TamperSeverity.High
                        });
                    }
                }
                else
                {
                    result.MerkleTreeValid = true;
                }

                // Verify signatures if enabled
                if (_config.EnableDigitalSignatures && _signatureProvider != null)
                {
                    result.SignaturesValid = VerifyCheckpointSignatures();
                    if (!result.SignaturesValid)
                    {
                        result.TamperDetails.Add(new TamperDetail
                        {
                            TamperType = TamperType.SignatureInvalid,
                            Description = "One or more checkpoint signatures are invalid",
                            DetectedAt = DateTime.UtcNow,
                            Severity = TamperSeverity.Critical
                        });
                    }
                }
                else
                {
                    result.SignaturesValid = true;
                }

                // Determine overall validity
                result.IsValid = result.TamperDetails.Count == 0 && result.MerkleTreeValid && result.SignaturesValid;
                result.ChainStatus = result.IsValid ? ChainHealthStatus.Healthy :
                    result.TamperDetails.Any(t => t.Severity == TamperSeverity.Critical) ? ChainHealthStatus.Corrupted :
                    ChainHealthStatus.Degraded;
            }
            catch (OperationCanceledException)
            {
                result.IsValid = false;
                result.ChainStatus = ChainHealthStatus.Unknown;
                result.TamperDetails.Add(new TamperDetail
                {
                    TamperType = TamperType.Unknown,
                    Description = "Verification was cancelled",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.Low
                });
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ChainStatus = ChainHealthStatus.Unknown;
                result.TamperDetails.Add(new TamperDetail
                {
                    TamperType = TamperType.Unknown,
                    Description = $"Verification error: {ex.Message}",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.High
                });
            }

            result.VerificationEndTime = DateTime.UtcNow;
            return result;
        }

        /// <summary>
        /// Verifies a single audit entry and its chain link to the previous entry.
        /// </summary>
        /// <param name="entryId">The unique identifier of the entry to verify.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>
        /// A <see cref="ChainVerificationResult"/> containing the verification result for the specific entry.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method verifies:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>The entry's hash matches recomputed hash from content</description></item>
        ///   <item><description>The entry's PreviousHash matches the preceding entry's EntryHash</description></item>
        ///   <item><description>Sequence number continuity with adjacent entries</description></item>
        /// </list>
        /// <para>
        /// Use this method for targeted verification when investigating specific entries,
        /// rather than verifying the entire chain.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown when entryId is null or empty.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the entry is not found.</exception>
        public async Task<ChainVerificationResult> VerifyEntryAsync(string entryId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(entryId))
            {
                throw new ArgumentException("Entry ID cannot be null or empty.", nameof(entryId));
            }

            var result = new ChainVerificationResult
            {
                VerificationStartTime = DateTime.UtcNow,
                VerificationType = ChainVerificationType.SingleEntry
            };

            try
            {
                // Find the specific entry
                var allEntries = await QueryLogsAsync(DateTime.MinValue, DateTime.UtcNow, null, null, int.MaxValue);
                var orderedEntries = allEntries.OrderBy(e => e.SequenceNumber).ToList();

                var entryIndex = orderedEntries.FindIndex(e => e.Id == entryId);
                if (entryIndex == -1)
                {
                    throw new KeyNotFoundException($"Audit entry with ID '{entryId}' not found.");
                }

                var entry = orderedEntries[entryIndex];
                var previousEntry = entryIndex > 0 ? orderedEntries[entryIndex - 1] : null;
                var expectedPreviousHash = previousEntry?.EntryHash ?? GenesisHashHex;

                result.TotalEntriesExamined = 1;

                var validationResult = ValidateEntry(entry, expectedPreviousHash, previousEntry);

                if (validationResult.IsValid)
                {
                    result.EntriesVerified = 1;
                    result.IsValid = true;
                    result.ChainStatus = ChainHealthStatus.Healthy;
                }
                else
                {
                    result.TamperDetails.AddRange(validationResult.TamperDetails);
                    result.IsValid = false;
                    result.ChainStatus = ChainHealthStatus.Corrupted;
                    await LogTamperingAttemptAsync(entry, validationResult.TamperDetails.FirstOrDefault()?.TamperType ?? TamperType.Unknown);
                }
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ChainStatus = ChainHealthStatus.Unknown;
                result.TamperDetails.Add(new TamperDetail
                {
                    EntryId = entryId,
                    TamperType = TamperType.Unknown,
                    Description = $"Verification error: {ex.Message}",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.High
                });
            }

            result.VerificationEndTime = DateTime.UtcNow;
            return result;
        }

        /// <summary>
        /// Gets the current health status of the hash chain.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>
        /// A <see cref="ChainStatusSummary"/> containing chain health information including
        /// entry count, last hash, and any detected issues.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method provides a quick health check without performing full verification.
        /// It returns:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>Total entry count</description></item>
        ///   <item><description>Current sequence number</description></item>
        ///   <item><description>Last entry hash</description></item>
        ///   <item><description>Chain creation timestamp</description></item>
        ///   <item><description>Last verification timestamp and result</description></item>
        /// </list>
        /// <para>
        /// For full integrity verification, use <see cref="VerifyChainIntegrityAsync"/>.
        /// </para>
        /// </remarks>
        public async Task<ChainStatusSummary> GetChainStatusAsync(CancellationToken cancellationToken = default)
        {
            await _chainMetadataLock.WaitAsync(cancellationToken);
            try
            {
                var summary = new ChainStatusSummary
                {
                    TotalEntries = _chainMetadata.EntryCount,
                    CurrentSequenceNumber = _chainMetadata.LastSequenceNumber,
                    LastEntryHash = _chainMetadata.LastEntryHash ?? GenesisHashHex,
                    GenesisHash = GenesisHashHex,
                    ChainCreatedAt = _chainMetadata.CreatedAt,
                    LastEntryTimestamp = _chainMetadata.LastEntryTimestamp,
                    LastVerificationTime = _chainMetadata.LastVerificationTime,
                    LastVerificationResult = _chainMetadata.LastVerificationPassed,
                    ChainMetadataPath = _chainMetadataPath ?? "Not initialized"
                };

                // Determine chain health based on available information
                if (_chainMetadata.EntryCount == 0)
                {
                    summary.HealthStatus = ChainHealthStatus.Healthy;
                    summary.HealthMessage = "Chain is empty but healthy - ready for entries.";
                }
                else if (_chainMetadata.LastVerificationTime.HasValue)
                {
                    summary.HealthStatus = _chainMetadata.LastVerificationPassed == true
                        ? ChainHealthStatus.Healthy
                        : ChainHealthStatus.Degraded;
                    summary.HealthMessage = _chainMetadata.LastVerificationPassed == true
                        ? $"Chain verified successfully at {_chainMetadata.LastVerificationTime.Value:O}"
                        : "Last verification detected issues - run full verification for details.";
                }
                else
                {
                    summary.HealthStatus = ChainHealthStatus.Unknown;
                    summary.HealthMessage = "Chain has not been verified. Run VerifyChainIntegrityAsync() to check health.";
                }

                // Count log files
                var logFiles = Directory.GetFiles(_config.LogDirectory, "audit-*.jsonl");
                summary.LogFileCount = logFiles.Length;

                // Count checkpoints
                var checkpointFiles = Directory.GetFiles(_config.LogDirectory, "checkpoint-*.json");
                summary.CheckpointCount = checkpointFiles.Length;

                return summary;
            }
            finally
            {
                _chainMetadataLock.Release();
            }
        }

        /// <summary>
        /// Performs partial chain verification for improved performance on large chains.
        /// </summary>
        /// <param name="startSequence">The starting sequence number for verification.</param>
        /// <param name="endSequence">The ending sequence number for verification (inclusive).</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>
        /// A <see cref="ChainVerificationResult"/> for the specified range of entries.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Use this method when full chain verification is too time-consuming.
        /// Partial verification still validates hash chain links within the specified range,
        /// but does not verify the connection to entries outside the range.
        /// </para>
        /// <para>
        /// For compliance purposes, periodic full verification should still be performed,
        /// but partial verification can be used for continuous monitoring.
        /// </para>
        /// </remarks>
        public async Task<ChainVerificationResult> VerifyPartialChainAsync(
            long startSequence,
            long endSequence,
            CancellationToken cancellationToken = default)
        {
            var result = new ChainVerificationResult
            {
                VerificationStartTime = DateTime.UtcNow,
                VerificationType = ChainVerificationType.Partial
            };

            try
            {
                var allEntries = await QueryLogsAsync(DateTime.MinValue, DateTime.UtcNow, null, null, int.MaxValue);
                var rangeEntries = allEntries
                    .Where(e => e.SequenceNumber >= startSequence && e.SequenceNumber <= endSequence)
                    .OrderBy(e => e.SequenceNumber)
                    .ToList();

                result.TotalEntriesExamined = rangeEntries.Count;

                if (rangeEntries.Count == 0)
                {
                    result.IsValid = true;
                    result.ChainStatus = ChainHealthStatus.Healthy;
                    result.VerificationEndTime = DateTime.UtcNow;
                    return result;
                }

                // Get the entry immediately before our range for previous hash validation
                var precedingEntry = allEntries
                    .Where(e => e.SequenceNumber < startSequence)
                    .OrderByDescending(e => e.SequenceNumber)
                    .FirstOrDefault();

                string expectedPreviousHash = precedingEntry?.EntryHash ?? GenesisHashHex;

                for (int i = 0; i < rangeEntries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = rangeEntries[i];
                    var previousEntry = i > 0 ? rangeEntries[i - 1] : precedingEntry;
                    var validationResult = ValidateEntry(entry, expectedPreviousHash, previousEntry);

                    if (!validationResult.IsValid)
                    {
                        result.TamperDetails.AddRange(validationResult.TamperDetails);
                    }
                    else
                    {
                        result.EntriesVerified++;
                    }

                    expectedPreviousHash = entry.EntryHash;
                }

                result.IsValid = result.TamperDetails.Count == 0;
                result.ChainStatus = result.IsValid ? ChainHealthStatus.Healthy : ChainHealthStatus.Corrupted;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ChainStatus = ChainHealthStatus.Unknown;
                result.TamperDetails.Add(new TamperDetail
                {
                    TamperType = TamperType.Unknown,
                    Description = $"Partial verification error: {ex.Message}",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.High
                });
            }

            result.VerificationEndTime = DateTime.UtcNow;
            return result;
        }

        #endregion

        #region Chain Recovery Methods

        /// <summary>
        /// Attempts to repair the hash chain after detecting corruption.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>
        /// A <see cref="ChainRepairResult"/> indicating what repairs were attempted and their success.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>WARNING:</b> Chain repair may not be possible in all cases. This method can only
        /// repair certain types of corruption:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>Chain metadata out of sync with actual entries</description></item>
        ///   <item><description>Missing or corrupted .chain metadata file</description></item>
        ///   <item><description>Sequence number gaps (by recomputing chain state)</description></item>
        /// </list>
        /// <para>
        /// This method CANNOT repair:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>Modified entry content (hashes will not match)</description></item>
        ///   <item><description>Deleted entries (chain links are permanently broken)</description></item>
        ///   <item><description>Reordered entries</description></item>
        /// </list>
        /// <para>
        /// For compliance purposes, any chain that cannot be verified should be flagged
        /// and investigated. Successful repair should be documented in the audit trail.
        /// </para>
        /// </remarks>
        public async Task<ChainRepairResult> RepairChainAsync(CancellationToken cancellationToken = default)
        {
            var result = new ChainRepairResult
            {
                RepairStartTime = DateTime.UtcNow
            };

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                // First, perform verification to identify issues
                var verificationResult = await VerifyChainIntegrityAsync(cancellationToken);

                if (verificationResult.IsValid)
                {
                    result.Success = true;
                    result.Message = "No repairs needed - chain is valid.";
                    result.RepairEndTime = DateTime.UtcNow;
                    return result;
                }

                result.IssuesFound = verificationResult.TamperDetails.Count;

                // Categorize issues
                var metadataIssues = verificationResult.TamperDetails
                    .Where(t => t.TamperType == TamperType.MetadataMismatch)
                    .ToList();

                var hashMismatchIssues = verificationResult.TamperDetails
                    .Where(t => t.TamperType == TamperType.HashMismatch)
                    .ToList();

                var linkBrokenIssues = verificationResult.TamperDetails
                    .Where(t => t.TamperType == TamperType.ChainLinkBroken ||
                                t.TamperType == TamperType.GenesisLinkBroken)
                    .ToList();

                // Attempt to repair metadata issues
                if (metadataIssues.Count > 0)
                {
                    try
                    {
                        await RebuildChainMetadataAsync(cancellationToken);
                        result.IssuesRepaired += metadataIssues.Count;
                        result.RepairActions.Add("Rebuilt chain metadata from log entries.");
                    }
                    catch (Exception ex)
                    {
                        result.RepairActions.Add($"Failed to rebuild metadata: {ex.Message}");
                    }
                }

                // Hash mismatches and broken links cannot be automatically repaired
                if (hashMismatchIssues.Count > 0)
                {
                    result.RepairActions.Add($"Found {hashMismatchIssues.Count} hash mismatch(es) - these indicate content modification and cannot be automatically repaired.");
                    result.UnrepairableIssues.AddRange(hashMismatchIssues.Select(i =>
                        $"Entry {i.EntryId} (Seq: {i.SequenceNumber}): {i.Description}"));
                }

                if (linkBrokenIssues.Count > 0)
                {
                    result.RepairActions.Add($"Found {linkBrokenIssues.Count} broken chain link(s) - these indicate entry deletion or reordering and cannot be automatically repaired.");
                    result.UnrepairableIssues.AddRange(linkBrokenIssues.Select(i =>
                        $"Entry {i.EntryId} (Seq: {i.SequenceNumber}): {i.Description}"));
                }

                // Log repair attempt
                await LogChainRepairAttemptAsync(result);

                // Final verification
                var finalVerification = await VerifyChainIntegrityAsync(cancellationToken);
                result.Success = finalVerification.IsValid;
                result.Message = finalVerification.IsValid
                    ? "Chain repaired successfully."
                    : $"Partial repair completed. {result.UnrepairableIssues.Count} issue(s) could not be repaired.";
            }
            finally
            {
                _writeLock.Release();
            }

            result.RepairEndTime = DateTime.UtcNow;
            return result;
        }

        /// <summary>
        /// Exports cryptographic proof of the hash chain for external auditors.
        /// </summary>
        /// <param name="outputPath">Optional path to write the proof file. If null, proof is returned in the result.</param>
        /// <param name="startDate">Optional start date to limit the proof scope.</param>
        /// <param name="endDate">Optional end date to limit the proof scope.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>
        /// A <see cref="ChainProofExport"/> containing the cryptographic proof of chain integrity.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The exported proof includes:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>Chain metadata (genesis hash, entry count, date range)</description></item>
        ///   <item><description>Hash chain anchors at regular intervals</description></item>
        ///   <item><description>Merkle tree roots for each checkpoint (if enabled)</description></item>
        ///   <item><description>Digital signatures (if enabled)</description></item>
        ///   <item><description>Verification instructions for auditors</description></item>
        /// </list>
        /// <para>
        /// This proof can be provided to auditors to demonstrate chain integrity without
        /// giving them access to the actual audit log content.
        /// </para>
        /// </remarks>
        public async Task<ChainProofExport> ExportChainProofAsync(
            string? outputPath = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            CancellationToken cancellationToken = default)
        {
            var proof = new ChainProofExport
            {
                ExportedAt = DateTime.UtcNow,
                GenesisHash = GenesisHashHex,
                HashAlgorithm = "SHA-256",
                ProofVersion = "2.0"
            };

            try
            {
                // Load entries for the specified range
                var entries = await QueryLogsAsync(
                    startDate ?? DateTime.MinValue,
                    endDate ?? DateTime.UtcNow,
                    null, null, int.MaxValue);

                var orderedEntries = entries.OrderBy(e => e.SequenceNumber).ToList();

                proof.TotalEntries = orderedEntries.Count;
                proof.StartSequenceNumber = orderedEntries.FirstOrDefault()?.SequenceNumber ?? 0;
                proof.EndSequenceNumber = orderedEntries.LastOrDefault()?.SequenceNumber ?? 0;
                proof.StartTimestamp = orderedEntries.FirstOrDefault()?.Timestamp ?? DateTime.UtcNow;
                proof.EndTimestamp = orderedEntries.LastOrDefault()?.Timestamp ?? DateTime.UtcNow;

                // Generate hash anchors at regular intervals
                int anchorInterval = Math.Max(1, orderedEntries.Count / 100); // ~100 anchors
                for (int i = 0; i < orderedEntries.Count; i += anchorInterval)
                {
                    var entry = orderedEntries[i];
                    proof.HashAnchors.Add(new HashAnchor
                    {
                        SequenceNumber = entry.SequenceNumber,
                        Timestamp = entry.Timestamp,
                        EntryHash = entry.EntryHash,
                        PreviousHash = entry.PreviousHash
                    });
                }

                // Always include first and last entries as anchors
                if (orderedEntries.Count > 0)
                {
                    var first = orderedEntries.First();
                    if (!proof.HashAnchors.Any(a => a.SequenceNumber == first.SequenceNumber))
                    {
                        proof.HashAnchors.Insert(0, new HashAnchor
                        {
                            SequenceNumber = first.SequenceNumber,
                            Timestamp = first.Timestamp,
                            EntryHash = first.EntryHash,
                            PreviousHash = first.PreviousHash
                        });
                    }

                    var last = orderedEntries.Last();
                    if (!proof.HashAnchors.Any(a => a.SequenceNumber == last.SequenceNumber))
                    {
                        proof.HashAnchors.Add(new HashAnchor
                        {
                            SequenceNumber = last.SequenceNumber,
                            Timestamp = last.Timestamp,
                            EntryHash = last.EntryHash,
                            PreviousHash = last.PreviousHash
                        });
                    }
                }

                // Include checkpoint Merkle roots
                var checkpointFiles = Directory.GetFiles(_config.LogDirectory, "checkpoint-*.json");
                foreach (var file in checkpointFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var checkpoint = JsonSerializer.Deserialize<AuditCheckpoint>(json);
                        if (checkpoint != null)
                        {
                            proof.MerkleRoots.Add(new MerkleRootAnchor
                            {
                                Timestamp = checkpoint.Timestamp,
                                StartSequence = checkpoint.StartSequence,
                                EndSequence = checkpoint.EndSequence,
                                MerkleRoot = checkpoint.MerkleRoot,
                                EntryCount = checkpoint.EntryCount,
                                Signature = checkpoint.Signature
                            });
                        }
                    }
                    catch
                    {
                        // Skip corrupted checkpoint files
                    }
                }

                // Perform and record verification
                var verificationResult = await VerifyChainIntegrityAsync(cancellationToken);
                proof.VerificationResult = new ProofVerificationSummary
                {
                    IsValid = verificationResult.IsValid,
                    EntriesVerified = verificationResult.EntriesVerified,
                    VerificationTime = verificationResult.VerificationEndTime ?? DateTime.UtcNow,
                    HashChainValid = verificationResult.TamperDetails.All(t => t.TamperType != TamperType.HashMismatch && t.TamperType != TamperType.ChainLinkBroken),
                    MerkleTreeValid = verificationResult.MerkleTreeValid,
                    SignaturesValid = verificationResult.SignaturesValid
                };

                // Add verification instructions
                proof.VerificationInstructions = GenerateVerificationInstructions();

                // Write to file if path specified
                if (!string.IsNullOrEmpty(outputPath))
                {
                    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                    var proofJson = JsonSerializer.Serialize(proof, jsonOptions);
                    await File.WriteAllTextAsync(outputPath, proofJson, cancellationToken);
                    proof.OutputFilePath = outputPath;
                }
            }
            catch (Exception ex)
            {
                proof.VerificationResult = new ProofVerificationSummary
                {
                    IsValid = false,
                    ErrorMessage = $"Export failed: {ex.Message}"
                };
            }

            return proof;
        }

        #endregion

        #region Message Handlers

        private async Task HandleLogAsync(PluginMessage message)
        {
            var evt = new AuditEvent
            {
                Action = GetString(message.Payload, "action") ?? "unknown",
                ResourceType = GetString(message.Payload, "resourceType") ?? "unknown",
                ResourceId = GetString(message.Payload, "resourceId"),
                UserId = GetString(message.Payload, "userId") ?? "anonymous",
                TenantId = GetString(message.Payload, "tenantId"),
                IpAddress = GetString(message.Payload, "ipAddress"),
                UserAgent = GetString(message.Payload, "userAgent"),
                Outcome = GetString(message.Payload, "outcome") ?? "success",
                Severity = GetString(message.Payload, "severity") ?? "info",
                Details = message.Payload.TryGetValue("details", out var d) && d is Dictionary<string, object> details
                    ? details
                    : new Dictionary<string, object>(),
                ComplianceTags = message.Payload.TryGetValue("complianceTags", out var t) && t is string[] tags
                    ? tags
                    : []
            };

            await LogAsync(evt);
        }

        private async Task HandleQueryAsync(PluginMessage message)
        {
            var startDate = message.Payload.TryGetValue("startDate", out var sd) && sd is DateTime start
                ? start
                : DateTime.UtcNow.AddDays(-7);

            var endDate = message.Payload.TryGetValue("endDate", out var ed) && ed is DateTime end
                ? end
                : DateTime.UtcNow;

            var userId = GetString(message.Payload, "userId");
            var action = GetString(message.Payload, "action");
            var limit = message.Payload.TryGetValue("limit", out var l) && l is int lim ? lim : 1000;

            var results = await QueryLogsAsync(startDate, endDate, userId, action, limit);
        }

        private async Task HandleExportAsync(PluginMessage message)
        {
            var format = GetString(message.Payload, "format") ?? "JSON";
            var compliance = GetString(message.Payload, "compliance") ?? "GENERIC";
            var startDate = message.Payload.TryGetValue("startDate", out var sd) && sd is DateTime start
                ? start
                : DateTime.UtcNow.AddDays(-30);
            var endDate = message.Payload.TryGetValue("endDate", out var ed) && ed is DateTime end
                ? end
                : DateTime.UtcNow;
            var outputPath = GetString(message.Payload, "outputPath");

            var entries = await QueryLogsAsync(startDate, endDate, null, null, int.MaxValue);

            var exporter = GetExporter(format, compliance);
            var exported = exporter.Export(entries);

            if (!string.IsNullOrEmpty(outputPath))
            {
                await File.WriteAllTextAsync(outputPath, exported);
            }
        }

        private async Task HandleVerifyAsync(PluginMessage message)
        {
            var result = await VerifyChainIntegrityAsync();

            // Update chain metadata with verification result
            await _chainMetadataLock.WaitAsync();
            try
            {
                _chainMetadata.LastVerificationTime = DateTime.UtcNow;
                _chainMetadata.LastVerificationPassed = result.IsValid;
                await SaveChainMetadataAsync();
            }
            finally
            {
                _chainMetadataLock.Release();
            }
        }

        private void HandleConfigure(PluginMessage message)
        {
            if (message.Payload.TryGetValue("retentionDays", out var rd) && rd is int days)
                _config.RetentionDays = days;
            if (message.Payload.TryGetValue("flushThreshold", out var ft) && ft is int threshold)
                _config.FlushThreshold = threshold;
        }

        private async Task HandleChainStatusAsync(PluginMessage message)
        {
            var status = await GetChainStatusAsync();
            // Status would be returned via message response mechanism
        }

        private async Task HandleChainVerifyAsync(PluginMessage message)
        {
            var result = await VerifyChainIntegrityAsync();
            // Result would be returned via message response mechanism
        }

        private async Task HandleVerifyEntryAsync(PluginMessage message)
        {
            var entryId = GetString(message.Payload, "entryId");
            if (!string.IsNullOrEmpty(entryId))
            {
                var result = await VerifyEntryAsync(entryId);
                // Result would be returned via message response mechanism
            }
        }

        private async Task HandleChainRepairAsync(PluginMessage message)
        {
            var result = await RepairChainAsync();
            // Result would be returned via message response mechanism
        }

        private async Task HandleExportProofAsync(PluginMessage message)
        {
            var outputPath = GetString(message.Payload, "outputPath");
            var startDate = message.Payload.TryGetValue("startDate", out var sd) && sd is DateTime start
                ? start
                : (DateTime?)null;
            var endDate = message.Payload.TryGetValue("endDate", out var ed) && ed is DateTime end
                ? end
                : (DateTime?)null;

            var proof = await ExportChainProofAsync(outputPath, startDate, endDate);
            // Proof would be returned via message response mechanism
        }

        #endregion

        #region Chain Metadata Management

        /// <summary>
        /// Loads existing chain metadata or creates new metadata file.
        /// </summary>
        private async Task LoadOrCreateChainMetadataAsync()
        {
            _chainMetadataPath = Path.Combine(_config.LogDirectory, $"audit{ChainFileExtension}");

            if (File.Exists(_chainMetadataPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_chainMetadataPath);
                    _chainMetadata = JsonSerializer.Deserialize<ChainMetadata>(json) ?? new ChainMetadata();

                    // Restore chain state from metadata
                    if (!string.IsNullOrEmpty(_chainMetadata.LastEntryHash))
                    {
                        _previousHash = Convert.FromHexString(_chainMetadata.LastEntryHash);
                    }
                    _sequenceNumber = _chainMetadata.LastSequenceNumber;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AUDIT] Failed to load chain metadata: {ex.Message}. Rebuilding from logs.");
                    await RebuildChainMetadataAsync();
                }
            }
            else
            {
                // Initialize new chain metadata
                _chainMetadata = new ChainMetadata
                {
                    GenesisHash = GenesisHashHex,
                    CreatedAt = DateTime.UtcNow,
                    EntryCount = 0,
                    LastSequenceNumber = 0,
                    LastEntryHash = null
                };

                await SaveChainMetadataAsync();
            }
        }

        /// <summary>
        /// Updates chain metadata after adding a new entry.
        /// </summary>
        private async Task UpdateChainMetadataAsync(AuditEntry entry)
        {
            await _chainMetadataLock.WaitAsync();
            try
            {
                _chainMetadata.EntryCount++;
                _chainMetadata.LastSequenceNumber = entry.SequenceNumber;
                _chainMetadata.LastEntryHash = entry.EntryHash;
                _chainMetadata.LastEntryTimestamp = entry.Timestamp;
                _chainMetadata.LastModifiedAt = DateTime.UtcNow;

                // Save metadata periodically (every 100 entries or on flush)
                if (_chainMetadata.EntryCount % 100 == 0)
                {
                    await SaveChainMetadataInternalAsync();
                }
            }
            finally
            {
                _chainMetadataLock.Release();
            }
        }

        /// <summary>
        /// Saves chain metadata to the .chain file.
        /// </summary>
        private async Task SaveChainMetadataAsync()
        {
            await _chainMetadataLock.WaitAsync();
            try
            {
                await SaveChainMetadataInternalAsync();
            }
            finally
            {
                _chainMetadataLock.Release();
            }
        }

        /// <summary>
        /// Internal method to save chain metadata (called when lock is already held).
        /// </summary>
        private async Task SaveChainMetadataInternalAsync()
        {
            if (string.IsNullOrEmpty(_chainMetadataPath)) return;

            var json = JsonSerializer.Serialize(_chainMetadata, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _chainMetadataPath + ".tmp";

            // Write to temp file first, then atomic rename for crash safety
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _chainMetadataPath, overwrite: true);
        }

        /// <summary>
        /// Rebuilds chain metadata by scanning all log files.
        /// </summary>
        private async Task RebuildChainMetadataAsync(CancellationToken cancellationToken = default)
        {
            var entries = await QueryLogsAsync(DateTime.MinValue, DateTime.UtcNow, null, null, int.MaxValue);
            var orderedEntries = entries.OrderBy(e => e.SequenceNumber).ToList();

            _chainMetadata = new ChainMetadata
            {
                GenesisHash = GenesisHashHex,
                CreatedAt = orderedEntries.FirstOrDefault()?.Timestamp ?? DateTime.UtcNow,
                EntryCount = orderedEntries.Count,
                LastSequenceNumber = orderedEntries.LastOrDefault()?.SequenceNumber ?? 0,
                LastEntryHash = orderedEntries.LastOrDefault()?.EntryHash,
                LastEntryTimestamp = orderedEntries.LastOrDefault()?.Timestamp,
                LastModifiedAt = DateTime.UtcNow
            };

            // Update in-memory state
            if (!string.IsNullOrEmpty(_chainMetadata.LastEntryHash))
            {
                _previousHash = Convert.FromHexString(_chainMetadata.LastEntryHash);
            }
            else
            {
                _previousHash = GenesisHash;
            }
            _sequenceNumber = _chainMetadata.LastSequenceNumber;

            await SaveChainMetadataInternalAsync();
        }

        #endregion

        #region Validation Helpers

        /// <summary>
        /// Validates a single entry against its expected previous hash and the actual previous entry.
        /// </summary>
        private EntryValidationResult ValidateEntry(AuditEntry entry, string expectedPreviousHash, AuditEntry? previousEntry)
        {
            var result = new EntryValidationResult { IsValid = true };

            // Verify entry hash
            var recomputedHash = ComputeEntryHash(entry);
            if (entry.EntryHash != recomputedHash)
            {
                result.IsValid = false;
                result.TamperDetails.Add(new TamperDetail
                {
                    EntryId = entry.Id,
                    SequenceNumber = entry.SequenceNumber,
                    TamperType = TamperType.HashMismatch,
                    Description = $"Entry hash mismatch. Stored: {entry.EntryHash}, Computed: {recomputedHash}. Entry content may have been modified.",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.Critical,
                    ExpectedValue = recomputedHash,
                    ActualValue = entry.EntryHash
                });
            }

            // Verify chain link
            if (entry.PreviousHash != expectedPreviousHash)
            {
                result.IsValid = false;
                result.TamperDetails.Add(new TamperDetail
                {
                    EntryId = entry.Id,
                    SequenceNumber = entry.SequenceNumber,
                    TamperType = TamperType.ChainLinkBroken,
                    Description = $"Chain link broken. Entry's PreviousHash ({entry.PreviousHash}) does not match previous entry's hash ({expectedPreviousHash}). Entries may have been deleted or reordered.",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.Critical,
                    ExpectedValue = expectedPreviousHash,
                    ActualValue = entry.PreviousHash
                });
            }

            // Verify sequence number continuity
            if (previousEntry != null && entry.SequenceNumber != previousEntry.SequenceNumber + 1)
            {
                result.IsValid = false;
                result.TamperDetails.Add(new TamperDetail
                {
                    EntryId = entry.Id,
                    SequenceNumber = entry.SequenceNumber,
                    TamperType = TamperType.SequenceGap,
                    Description = $"Sequence number gap detected. Expected: {previousEntry.SequenceNumber + 1}, Found: {entry.SequenceNumber}. Entries may have been deleted.",
                    DetectedAt = DateTime.UtcNow,
                    Severity = TamperSeverity.High,
                    ExpectedValue = (previousEntry.SequenceNumber + 1).ToString(),
                    ActualValue = entry.SequenceNumber.ToString()
                });
            }

            return result;
        }

        /// <summary>
        /// Logs a tampering attempt as a critical security event.
        /// </summary>
        private async Task LogTamperingAttemptAsync(AuditEntry entry, TamperType tamperType)
        {
            // Log to stderr for immediate visibility
            Console.Error.WriteLine($"[AUDIT SECURITY CRITICAL] Tampering detected! Type: {tamperType}, Entry ID: {entry.Id}, Sequence: {entry.SequenceNumber}");

            // Create a security audit event for the tampering detection
            // Note: We don't use LogAsync here to avoid infinite recursion
            var securityEvent = new
            {
                EventType = "SECURITY_TAMPERING_DETECTED",
                TamperType = tamperType.ToString(),
                AffectedEntryId = entry.Id,
                AffectedSequenceNumber = entry.SequenceNumber,
                DetectedAt = DateTime.UtcNow,
                Severity = "CRITICAL",
                ComplianceTags = new[] { "HIPAA", "SOX", "PCI-DSS", "SECURITY_INCIDENT" }
            };

            // Write directly to a security events file
            var securityLogPath = Path.Combine(_config.LogDirectory, "security-events.jsonl");
            var json = JsonSerializer.Serialize(securityEvent);

            await _writeLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(securityLogPath, json + Environment.NewLine);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Logs a chain repair attempt.
        /// </summary>
        private async Task LogChainRepairAttemptAsync(ChainRepairResult result)
        {
            var repairEvent = new
            {
                EventType = "CHAIN_REPAIR_ATTEMPTED",
                Success = result.Success,
                IssuesFound = result.IssuesFound,
                IssuesRepaired = result.IssuesRepaired,
                RepairActions = result.RepairActions,
                UnrepairableIssues = result.UnrepairableIssues,
                Timestamp = DateTime.UtcNow,
                ComplianceTags = new[] { "HIPAA", "SOX", "PCI-DSS", "MAINTENANCE" }
            };

            var securityLogPath = Path.Combine(_config.LogDirectory, "security-events.jsonl");
            var json = JsonSerializer.Serialize(repairEvent);

            await File.AppendAllTextAsync(securityLogPath, json + Environment.NewLine);
        }

        /// <summary>
        /// Generates verification instructions for auditors.
        /// </summary>
        private static string GenerateVerificationInstructions()
        {
            return @"
HASH CHAIN VERIFICATION INSTRUCTIONS
====================================

This cryptographic proof demonstrates the integrity of the audit log chain.
Follow these steps to verify the chain independently:

1. VERIFY GENESIS HASH
   - The chain starts with the genesis hash: SHA256('DATAWAREHOUSE_AUDIT_GENESIS_V1')
   - Expected value: " + GenesisHashHex + @"
   - First entry's PreviousHash must equal this value.

2. VERIFY HASH CHAIN LINKS
   - For each entry, compute: SHA256(SequenceNumber|Timestamp|Action|ResourceType|ResourceId|UserId|Outcome|PreviousHash)
   - The result must match the entry's EntryHash
   - Each entry's PreviousHash must equal the previous entry's EntryHash

3. VERIFY SEQUENCE CONTINUITY
   - Sequence numbers must be monotonically increasing with no gaps
   - Gaps indicate potential entry deletion

4. VERIFY MERKLE ROOTS (if available)
   - Each checkpoint's MerkleRoot is computed from the entry hashes in that block
   - Use binary Merkle tree construction with SHA256

5. VERIFY SIGNATURES (if available)
   - Checkpoint signatures are RSA-SHA256 signatures
   - Verify using the public key provided separately

For detailed verification, use the VerifyChainIntegrityAsync() method in the SDK.
";
        }

        #endregion

        #region Core Operations

        private async Task<IEnumerable<AuditEntry>> QueryLogsAsync(
            DateTime startDate, DateTime endDate, string? userId, string? action, int limit)
        {
            var results = new List<AuditEntry>();
            var logFiles = Directory.GetFiles(_config.LogDirectory, "audit-*.jsonl")
                .OrderByDescending(f => f);

            foreach (var logFile in logFiles)
            {
                var fileName = Path.GetFileName(logFile);
                if (!TryParseLogDate(fileName, out var logDate))
                    continue;

                if (logDate.Date < startDate.Date.AddDays(-1))
                    break;

                var lines = await File.ReadAllLinesAsync(logFile);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                        if (entry == null) continue;

                        if (entry.Timestamp < startDate || entry.Timestamp > endDate)
                            continue;
                        if (!string.IsNullOrEmpty(userId) && entry.UserId != userId)
                            continue;
                        if (!string.IsNullOrEmpty(action) && entry.Action != action)
                            continue;

                        results.Add(entry);

                        if (results.Count >= limit)
                            return results;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AUDIT] Failed to process audit event: {ex.Message}");
                    }
                }
            }

            return results;
        }

        private string ComputeEntryHash(AuditEntry entry)
        {
            var data = $"{entry.SequenceNumber}|{entry.Timestamp:O}|{entry.Action}|{entry.ResourceType}|" +
                       $"{entry.ResourceId}|{entry.UserId}|{entry.Outcome}|{entry.PreviousHash}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }

        private async Task InitializeCurrentLogAsync()
        {
            var date = DateTime.UtcNow;
            _currentLogPath = Path.Combine(_config.LogDirectory, $"audit-{date:yyyyMMdd}.jsonl");

            _currentWriter = new StreamWriter(
                new FileStream(_currentLogPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                Encoding.UTF8)
            {
                AutoFlush = false
            };

            if (File.Exists(_currentLogPath))
            {
                var lines = await File.ReadAllLinesAsync(_currentLogPath);
                if (lines.Length > 0)
                {
                    var lastLine = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    if (lastLine != null)
                    {
                        try
                        {
                            var lastEntry = JsonSerializer.Deserialize<AuditEntry>(lastLine);
                            if (lastEntry != null)
                            {
                                _sequenceNumber = Math.Max(_sequenceNumber, lastEntry.SequenceNumber);
                                _previousHash = Convert.FromHexString(lastEntry.EntryHash);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[AUDIT] Failed to process audit event: {ex.Message}");
                        }
                    }
                }
            }
        }

        private async Task RotateLogAsync()
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_currentWriter != null)
                {
                    await _currentWriter.FlushAsync();
                    await _currentWriter.DisposeAsync();
                }

                await SaveChainMetadataInternalAsync();
                await InitializeCurrentLogAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task FlushBufferAsync()
        {
            if (_buffer.IsEmpty) return;

            await _writeLock.WaitAsync();
            try
            {
                await FlushBufferInternalAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Internal flush method (called when write lock is already held).
        /// </summary>
        private async Task FlushBufferInternalAsync()
        {
            var today = DateTime.UtcNow.Date;
            if (_currentLogPath != null)
            {
                var currentDate = TryParseLogDate(Path.GetFileName(_currentLogPath), out var d) ? d : today;
                if (currentDate.Date != today)
                {
                    if (_currentWriter != null)
                    {
                        await _currentWriter.FlushAsync();
                        await _currentWriter.DisposeAsync();
                    }
                    await InitializeCurrentLogAsync();
                }
            }

            while (_buffer.TryDequeue(out var entry))
            {
                var json = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEntry);
                if (_currentWriter is { } writer)
                {
                    await writer.WriteLineAsync(json);
                }
                else
                {
                    Console.Error.WriteLine($"[AUDIT] Writer not initialized - audit entry lost");
                }
            }

            if (_currentWriter is { } flushWriter)
            {
                await flushWriter.FlushAsync();
            }

            // Save chain metadata on flush
            await _chainMetadataLock.WaitAsync();
            try
            {
                await SaveChainMetadataInternalAsync();
            }
            finally
            {
                _chainMetadataLock.Release();
            }
        }

        private async Task FlushLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.FlushInterval, ct);
                    await FlushBufferAsync();
                    await CleanupOldLogsAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AUDIT] Failed to process audit event: {ex.Message}");
                }
            }
        }

        private async Task CleanupOldLogsAsync()
        {
            var cutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);
            var logFiles = Directory.GetFiles(_config.LogDirectory, "audit-*.jsonl");

            foreach (var file in logFiles)
            {
                if (TryParseLogDate(Path.GetFileName(file), out var date) && date < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        private static bool TryParseLogDate(string fileName, out DateTime date)
        {
            date = default;
            if (fileName.Length < 14) return false;

            var dateStr = fileName.Substring(6, 8);
            return DateTime.TryParseExact(dateStr, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out date);
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private IAuditExporter GetExporter(string format, string compliance)
        {
            return format.ToUpperInvariant() switch
            {
                "CSV" => new CsvExporter(compliance),
                "SIEM" => new SiemExporter(compliance),
                _ => new JsonExporter(compliance)
            };
        }

        #endregion

        #region Checkpoint and Merkle Tree Operations

        /// <summary>
        /// Creates a signed checkpoint of the current Merkle tree state.
        /// </summary>
        private async Task CreateCheckpointAsync()
        {
            var checkpoint = new AuditCheckpoint
            {
                Timestamp = DateTime.UtcNow,
                StartSequence = _lastCheckpointSequence + 1,
                EndSequence = _sequenceNumber,
                MerkleRoot = _merkleTree.ComputeRoot(),
                EntryCount = _merkleTree.LeafCount
            };

            checkpoint.CheckpointHash = checkpoint.ComputeCheckpointHash();

            if (_signatureProvider != null)
            {
                checkpoint.Signature = _signatureProvider.Sign(checkpoint.CheckpointHash);
            }

            _checkpoints.Add(checkpoint);
            _lastCheckpointSequence = _sequenceNumber;

            var checkpointPath = Path.Combine(_config.LogDirectory, $"checkpoint-{checkpoint.Timestamp:yyyyMMdd-HHmmss}.json");
            var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(checkpointPath, json);

            _merkleTree.Clear();
        }

        /// <summary>
        /// Performs comprehensive integrity verification for high-stakes tier.
        /// </summary>
        public async Task<AuditVerificationResult> VerifyIntegrityAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            var result = new AuditVerificationResult();
            var entries = await QueryLogsAsync(
                startDate ?? DateTime.MinValue,
                endDate ?? DateTime.UtcNow,
                null, null, int.MaxValue);

            var entryList = entries.ToList();
            result.EntriesVerified = entryList.Count;

            result.HashChainValid = VerifyHashChain(entryList);
            if (!result.HashChainValid)
            {
                result.Errors.Add("Hash chain integrity check failed - possible tampering detected");
            }

            if (_config.EnableMerkleTree)
            {
                result.MerkleTreeValid = await VerifyMerkleCheckpointsAsync();
                if (!result.MerkleTreeValid)
                {
                    result.Errors.Add("Merkle tree checkpoint verification failed");
                }
            }
            else
            {
                result.MerkleTreeValid = true;
            }

            if (_config.EnableDigitalSignatures && _signatureProvider != null)
            {
                result.SignaturesValid = VerifyCheckpointSignatures();
                if (!result.SignaturesValid)
                {
                    result.Errors.Add("One or more checkpoint signatures are invalid");
                }
            }
            else
            {
                result.SignaturesValid = true;
            }

            result.IsValid = result.HashChainValid && result.MerkleTreeValid && result.SignaturesValid;
            return result;
        }

        private async Task<bool> VerifyMerkleCheckpointsAsync()
        {
            var checkpointFiles = Directory.GetFiles(_config.LogDirectory, "checkpoint-*.json");

            foreach (var file in checkpointFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var checkpoint = JsonSerializer.Deserialize<AuditCheckpoint>(json);
                    if (checkpoint == null) continue;

                    var computedHash = checkpoint.ComputeCheckpointHash();
                    if (computedHash != checkpoint.CheckpointHash)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private bool VerifyCheckpointSignatures()
        {
            if (_signatureProvider == null) return true;

            foreach (var checkpoint in _checkpoints)
            {
                if (!string.IsNullOrEmpty(checkpoint.Signature))
                {
                    if (!_signatureProvider.Verify(checkpoint.CheckpointHash, checkpoint.Signature))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool VerifyHashChain(List<AuditEntry> entries)
        {
            if (entries.Count == 0) return true;

            entries = entries.OrderBy(e => e.SequenceNumber).ToList();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var computedHash = ComputeEntryHash(entry);

                if (entry.EntryHash != computedHash)
                    return false;

                if (i == 0 && entry.PreviousHash != GenesisHashHex)
                    return false;

                if (i > 0 && entry.PreviousHash != entries[i - 1].EntryHash)
                    return false;
            }

            return true;
        }

        #endregion
    }

    #region Data Transfer Objects

    /// <summary>
    /// Represents a single audit log entry with hash chain linking.
    /// </summary>
    /// <remarks>
    /// Each entry contains cryptographic hash information that links it to the previous entry
    /// in the chain, enabling tamper detection for compliance with HIPAA, SOX, and PCI-DSS.
    /// </remarks>
    public class AuditEntry
    {
        /// <summary>
        /// Gets or sets the unique identifier for this audit entry.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the UTC timestamp when the event occurred.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the monotonically increasing sequence number.
        /// </summary>
        /// <remarks>
        /// Sequence numbers must be contiguous with no gaps. Gaps indicate potential tampering
        /// through entry deletion.
        /// </remarks>
        public long SequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the action that was performed.
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the type of resource affected.
        /// </summary>
        public string ResourceType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identifier of the affected resource.
        /// </summary>
        public string? ResourceId { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the user who performed the action.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the tenant identifier for multi-tenant environments.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the IP address of the client.
        /// </summary>
        public string? IpAddress { get; set; }

        /// <summary>
        /// Gets or sets the user agent string of the client.
        /// </summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// Gets or sets the outcome of the action (e.g., "success", "failure").
        /// </summary>
        public string Outcome { get; set; } = "success";

        /// <summary>
        /// Gets or sets the severity level of the event.
        /// </summary>
        public string Severity { get; set; } = "info";

        /// <summary>
        /// Gets or sets additional details about the event.
        /// </summary>
        public Dictionary<string, object> Details { get; set; } = new();

        /// <summary>
        /// Gets or sets compliance framework tags for this entry.
        /// </summary>
        public string[] ComplianceTags { get; set; } = [];

        /// <summary>
        /// Gets or sets the SHA-256 hash of this entry's content combined with the previous hash.
        /// </summary>
        /// <remarks>
        /// This hash is computed as: SHA256(SequenceNumber|Timestamp|Action|ResourceType|ResourceId|UserId|Outcome|PreviousHash)
        /// </remarks>
        public string EntryHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the hash of the previous entry in the chain.
        /// </summary>
        /// <remarks>
        /// For the first entry in the chain, this contains the genesis hash.
        /// </remarks>
        public string PreviousHash { get; set; } = string.Empty;

        // Backward compatibility alias
        [JsonIgnore]
        internal string Hash
        {
            get => EntryHash;
            set => EntryHash = value;
        }
    }

    /// <summary>
    /// Represents an audit event to be logged.
    /// </summary>
    public class AuditEvent
    {
        /// <summary>
        /// Gets or sets the action that was performed.
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the type of resource affected.
        /// </summary>
        public string ResourceType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the identifier of the affected resource.
        /// </summary>
        public string? ResourceId { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the user who performed the action.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the tenant identifier for multi-tenant environments.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the IP address of the client.
        /// </summary>
        public string? IpAddress { get; set; }

        /// <summary>
        /// Gets or sets the user agent string of the client.
        /// </summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// Gets or sets the outcome of the action.
        /// </summary>
        public string Outcome { get; set; } = "success";

        /// <summary>
        /// Gets or sets the severity level of the event.
        /// </summary>
        public string Severity { get; set; } = "info";

        /// <summary>
        /// Gets or sets additional details about the event.
        /// </summary>
        public Dictionary<string, object> Details { get; set; } = new();

        /// <summary>
        /// Gets or sets compliance framework tags for this event.
        /// </summary>
        public string[]? ComplianceTags { get; set; }
    }

    /// <summary>
    /// Configuration options for the audit logging plugin.
    /// </summary>
    public class AuditConfig
    {
        /// <summary>
        /// Gets or sets the directory where audit logs are stored.
        /// </summary>
        public string LogDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "audit");

        /// <summary>
        /// Gets or sets the retention period for audit logs in days.
        /// </summary>
        /// <remarks>
        /// Default is 2555 days (~7 years) to meet compliance requirements.
        /// </remarks>
        public int RetentionDays { get; set; } = 2555;

        /// <summary>
        /// Gets or sets the number of entries to buffer before flushing to disk.
        /// </summary>
        public int FlushThreshold { get; set; } = 100;

        /// <summary>
        /// Gets or sets the interval between automatic flushes.
        /// </summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets whether digital signatures are enabled for checkpoints.
        /// </summary>
        public bool EnableDigitalSignatures { get; set; } = false;

        /// <summary>
        /// Gets or sets whether Merkle tree checkpointing is enabled.
        /// </summary>
        public bool EnableMerkleTree { get; set; } = true;

        /// <summary>
        /// Gets or sets whether secure external timestamping is enabled.
        /// </summary>
        public bool EnableSecureTimestamping { get; set; } = false;

        /// <summary>
        /// Gets or sets the path to the signing key for digital signatures.
        /// </summary>
        public string? SigningKeyPath { get; set; }

        /// <summary>
        /// Gets or sets the URL of the timestamp server for secure timestamping.
        /// </summary>
        public string? TimestampServerUrl { get; set; }

        /// <summary>
        /// Gets or sets the number of entries per Merkle tree block.
        /// </summary>
        public int MerkleTreeBlockSize { get; set; } = 1000;
    }

    /// <summary>
    /// Metadata about the hash chain, stored in a separate .chain file.
    /// </summary>
    public class ChainMetadata
    {
        /// <summary>
        /// Gets or sets the genesis hash used as the chain anchor.
        /// </summary>
        public string GenesisHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the total number of entries in the chain.
        /// </summary>
        public long EntryCount { get; set; }

        /// <summary>
        /// Gets or sets the sequence number of the last entry.
        /// </summary>
        public long LastSequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the hash of the last entry in the chain.
        /// </summary>
        public string? LastEntryHash { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of the last entry.
        /// </summary>
        public DateTime? LastEntryTimestamp { get; set; }

        /// <summary>
        /// Gets or sets when the chain was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets when the chain metadata was last modified.
        /// </summary>
        public DateTime LastModifiedAt { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of the last verification.
        /// </summary>
        public DateTime? LastVerificationTime { get; set; }

        /// <summary>
        /// Gets or sets whether the last verification passed.
        /// </summary>
        public bool? LastVerificationPassed { get; set; }
    }

    /// <summary>
    /// Result of hash chain verification.
    /// </summary>
    public class ChainVerificationResult
    {
        /// <summary>
        /// Gets or sets whether the chain is valid.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Gets or sets the type of verification performed.
        /// </summary>
        public ChainVerificationType VerificationType { get; set; }

        /// <summary>
        /// Gets or sets the health status of the chain.
        /// </summary>
        public ChainHealthStatus ChainStatus { get; set; }

        /// <summary>
        /// Gets or sets the total number of entries examined.
        /// </summary>
        public long TotalEntriesExamined { get; set; }

        /// <summary>
        /// Gets or sets the number of entries successfully verified.
        /// </summary>
        public long EntriesVerified { get; set; }

        /// <summary>
        /// Gets or sets whether the Merkle tree verification passed.
        /// </summary>
        public bool MerkleTreeValid { get; set; }

        /// <summary>
        /// Gets or sets whether signature verification passed.
        /// </summary>
        public bool SignaturesValid { get; set; }

        /// <summary>
        /// Gets or sets details about detected tampering.
        /// </summary>
        public List<TamperDetail> TamperDetails { get; set; } = new();

        /// <summary>
        /// Gets or sets when verification started.
        /// </summary>
        public DateTime VerificationStartTime { get; set; }

        /// <summary>
        /// Gets or sets when verification ended.
        /// </summary>
        public DateTime? VerificationEndTime { get; set; }
    }

    /// <summary>
    /// Details about a detected tampering incident.
    /// </summary>
    public class TamperDetail
    {
        /// <summary>
        /// Gets or sets the ID of the affected entry.
        /// </summary>
        public string? EntryId { get; set; }

        /// <summary>
        /// Gets or sets the sequence number of the affected entry.
        /// </summary>
        public long SequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the type of tampering detected.
        /// </summary>
        public TamperType TamperType { get; set; }

        /// <summary>
        /// Gets or sets a human-readable description of the tampering.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the tampering was detected.
        /// </summary>
        public DateTime DetectedAt { get; set; }

        /// <summary>
        /// Gets or sets the severity of the tampering.
        /// </summary>
        public TamperSeverity Severity { get; set; }

        /// <summary>
        /// Gets or sets the expected value (for comparison).
        /// </summary>
        public string? ExpectedValue { get; set; }

        /// <summary>
        /// Gets or sets the actual value found.
        /// </summary>
        public string? ActualValue { get; set; }
    }

    /// <summary>
    /// Types of tampering that can be detected.
    /// </summary>
    public enum TamperType
    {
        /// <summary>
        /// Unknown tampering type.
        /// </summary>
        Unknown,

        /// <summary>
        /// Entry's computed hash does not match stored hash.
        /// </summary>
        HashMismatch,

        /// <summary>
        /// Entry's previous hash does not match previous entry's hash.
        /// </summary>
        ChainLinkBroken,

        /// <summary>
        /// First entry does not link to genesis hash.
        /// </summary>
        GenesisLinkBroken,

        /// <summary>
        /// Gap detected in sequence numbers.
        /// </summary>
        SequenceGap,

        /// <summary>
        /// Merkle tree integrity check failed.
        /// </summary>
        MerkleTreeCorrupted,

        /// <summary>
        /// Digital signature verification failed.
        /// </summary>
        SignatureInvalid,

        /// <summary>
        /// Chain metadata does not match actual chain state.
        /// </summary>
        MetadataMismatch
    }

    /// <summary>
    /// Severity levels for tampering incidents.
    /// </summary>
    public enum TamperSeverity
    {
        /// <summary>
        /// Low severity - informational.
        /// </summary>
        Low,

        /// <summary>
        /// Medium severity - requires investigation.
        /// </summary>
        Medium,

        /// <summary>
        /// High severity - potential security incident.
        /// </summary>
        High,

        /// <summary>
        /// Critical severity - confirmed tampering.
        /// </summary>
        Critical
    }

    /// <summary>
    /// Types of chain verification.
    /// </summary>
    public enum ChainVerificationType
    {
        /// <summary>
        /// Full chain verification from genesis.
        /// </summary>
        Full,

        /// <summary>
        /// Partial chain verification for a range of entries.
        /// </summary>
        Partial,

        /// <summary>
        /// Single entry verification.
        /// </summary>
        SingleEntry
    }

    /// <summary>
    /// Health status of the hash chain.
    /// </summary>
    public enum ChainHealthStatus
    {
        /// <summary>
        /// Chain status is unknown (not yet verified).
        /// </summary>
        Unknown,

        /// <summary>
        /// Chain is healthy and verified.
        /// </summary>
        Healthy,

        /// <summary>
        /// Chain has minor issues but is functional.
        /// </summary>
        Degraded,

        /// <summary>
        /// Chain is corrupted and requires investigation.
        /// </summary>
        Corrupted
    }

    /// <summary>
    /// Summary of chain health status.
    /// </summary>
    public class ChainStatusSummary
    {
        /// <summary>
        /// Gets or sets the total number of entries in the chain.
        /// </summary>
        public long TotalEntries { get; set; }

        /// <summary>
        /// Gets or sets the current sequence number.
        /// </summary>
        public long CurrentSequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the hash of the last entry.
        /// </summary>
        public string LastEntryHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the genesis hash.
        /// </summary>
        public string GenesisHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the chain was created.
        /// </summary>
        public DateTime ChainCreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of the last entry.
        /// </summary>
        public DateTime? LastEntryTimestamp { get; set; }

        /// <summary>
        /// Gets or sets the health status of the chain.
        /// </summary>
        public ChainHealthStatus HealthStatus { get; set; }

        /// <summary>
        /// Gets or sets a human-readable health message.
        /// </summary>
        public string HealthMessage { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the chain was last verified.
        /// </summary>
        public DateTime? LastVerificationTime { get; set; }

        /// <summary>
        /// Gets or sets the result of the last verification.
        /// </summary>
        public bool? LastVerificationResult { get; set; }

        /// <summary>
        /// Gets or sets the number of log files.
        /// </summary>
        public int LogFileCount { get; set; }

        /// <summary>
        /// Gets or sets the number of checkpoints.
        /// </summary>
        public int CheckpointCount { get; set; }

        /// <summary>
        /// Gets or sets the path to the chain metadata file.
        /// </summary>
        public string ChainMetadataPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of chain repair operation.
    /// </summary>
    public class ChainRepairResult
    {
        /// <summary>
        /// Gets or sets whether the repair was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets a message describing the repair result.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of issues found.
        /// </summary>
        public int IssuesFound { get; set; }

        /// <summary>
        /// Gets or sets the number of issues repaired.
        /// </summary>
        public int IssuesRepaired { get; set; }

        /// <summary>
        /// Gets or sets the repair actions taken.
        /// </summary>
        public List<string> RepairActions { get; set; } = new();

        /// <summary>
        /// Gets or sets issues that could not be repaired.
        /// </summary>
        public List<string> UnrepairableIssues { get; set; } = new();

        /// <summary>
        /// Gets or sets when the repair started.
        /// </summary>
        public DateTime RepairStartTime { get; set; }

        /// <summary>
        /// Gets or sets when the repair ended.
        /// </summary>
        public DateTime RepairEndTime { get; set; }
    }

    /// <summary>
    /// Cryptographic proof export for auditors.
    /// </summary>
    public class ChainProofExport
    {
        /// <summary>
        /// Gets or sets when the proof was exported.
        /// </summary>
        public DateTime ExportedAt { get; set; }

        /// <summary>
        /// Gets or sets the proof format version.
        /// </summary>
        public string ProofVersion { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the hash algorithm used.
        /// </summary>
        public string HashAlgorithm { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the genesis hash.
        /// </summary>
        public string GenesisHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the total number of entries covered.
        /// </summary>
        public long TotalEntries { get; set; }

        /// <summary>
        /// Gets or sets the starting sequence number.
        /// </summary>
        public long StartSequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the ending sequence number.
        /// </summary>
        public long EndSequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the start timestamp.
        /// </summary>
        public DateTime StartTimestamp { get; set; }

        /// <summary>
        /// Gets or sets the end timestamp.
        /// </summary>
        public DateTime EndTimestamp { get; set; }

        /// <summary>
        /// Gets or sets the hash anchors at regular intervals.
        /// </summary>
        public List<HashAnchor> HashAnchors { get; set; } = new();

        /// <summary>
        /// Gets or sets the Merkle root anchors from checkpoints.
        /// </summary>
        public List<MerkleRootAnchor> MerkleRoots { get; set; } = new();

        /// <summary>
        /// Gets or sets the verification result summary.
        /// </summary>
        public ProofVerificationSummary? VerificationResult { get; set; }

        /// <summary>
        /// Gets or sets verification instructions for auditors.
        /// </summary>
        public string VerificationInstructions { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the output file path if written to disk.
        /// </summary>
        public string? OutputFilePath { get; set; }
    }

    /// <summary>
    /// A hash anchor point in the chain proof.
    /// </summary>
    public class HashAnchor
    {
        /// <summary>
        /// Gets or sets the sequence number.
        /// </summary>
        public long SequenceNumber { get; set; }

        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the entry hash.
        /// </summary>
        public string EntryHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the previous hash.
        /// </summary>
        public string PreviousHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// A Merkle root anchor from a checkpoint.
    /// </summary>
    public class MerkleRootAnchor
    {
        /// <summary>
        /// Gets or sets the checkpoint timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the start sequence number.
        /// </summary>
        public long StartSequence { get; set; }

        /// <summary>
        /// Gets or sets the end sequence number.
        /// </summary>
        public long EndSequence { get; set; }

        /// <summary>
        /// Gets or sets the Merkle root.
        /// </summary>
        public string MerkleRoot { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of entries in this block.
        /// </summary>
        public int EntryCount { get; set; }

        /// <summary>
        /// Gets or sets the digital signature.
        /// </summary>
        public string? Signature { get; set; }
    }

    /// <summary>
    /// Verification summary for proof export.
    /// </summary>
    public class ProofVerificationSummary
    {
        /// <summary>
        /// Gets or sets whether the chain is valid.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Gets or sets the number of entries verified.
        /// </summary>
        public long EntriesVerified { get; set; }

        /// <summary>
        /// Gets or sets when verification occurred.
        /// </summary>
        public DateTime VerificationTime { get; set; }

        /// <summary>
        /// Gets or sets whether the hash chain is valid.
        /// </summary>
        public bool HashChainValid { get; set; }

        /// <summary>
        /// Gets or sets whether the Merkle tree is valid.
        /// </summary>
        public bool MerkleTreeValid { get; set; }

        /// <summary>
        /// Gets or sets whether signatures are valid.
        /// </summary>
        public bool SignaturesValid { get; set; }

        /// <summary>
        /// Gets or sets any error message.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Internal result of single entry validation.
    /// </summary>
    internal class EntryValidationResult
    {
        public bool IsValid { get; set; }
        public List<TamperDetail> TamperDetails { get; set; } = new();
    }

    #endregion

    #region Merkle Tree Implementation

    /// <summary>
    /// Merkle Tree for efficient tamper detection.
    /// Used by high-stakes tier for cryptographic proof of audit integrity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Merkle tree provides O(log n) verification of individual entries
    /// and O(1) verification of the entire block via the root hash.
    /// </para>
    /// <para>
    /// Checkpoints are created periodically containing the Merkle root,
    /// allowing efficient verification without processing all entries.
    /// </para>
    /// </remarks>
    public class AuditMerkleTree
    {
        private readonly List<string> _leaves = new();
        private readonly object _lock = new();

        /// <summary>
        /// Adds an entry hash as a leaf to the Merkle tree.
        /// </summary>
        /// <param name="hash">The hash to add as a leaf.</param>
        public void AddLeaf(string hash)
        {
            lock (_lock)
            {
                _leaves.Add(hash);
            }
        }

        /// <summary>
        /// Computes the Merkle root from all leaves.
        /// </summary>
        /// <returns>The Merkle root hash as a hexadecimal string.</returns>
        public string ComputeRoot()
        {
            lock (_lock)
            {
                if (_leaves.Count == 0)
                    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("EMPTY")));

                var currentLevel = _leaves.ToList();

                while (currentLevel.Count > 1)
                {
                    var nextLevel = new List<string>();

                    for (int i = 0; i < currentLevel.Count; i += 2)
                    {
                        string left = currentLevel[i];
                        string right = i + 1 < currentLevel.Count ? currentLevel[i + 1] : left;
                        var combined = SHA256.HashData(Encoding.UTF8.GetBytes(left + right));
                        nextLevel.Add(Convert.ToHexString(combined));
                    }

                    currentLevel = nextLevel;
                }

                return currentLevel[0];
            }
        }

        /// <summary>
        /// Gets proof path for a specific leaf index.
        /// </summary>
        /// <param name="leafIndex">The zero-based index of the leaf.</param>
        /// <returns>A list of proof nodes for verification.</returns>
        public List<MerkleProofNode> GetProof(int leafIndex)
        {
            lock (_lock)
            {
                if (leafIndex < 0 || leafIndex >= _leaves.Count)
                    return new List<MerkleProofNode>();

                var proof = new List<MerkleProofNode>();
                var currentLevel = _leaves.ToList();
                int index = leafIndex;

                while (currentLevel.Count > 1)
                {
                    var nextLevel = new List<string>();
                    int siblingIndex = index % 2 == 0 ? index + 1 : index - 1;

                    if (siblingIndex >= 0 && siblingIndex < currentLevel.Count)
                    {
                        proof.Add(new MerkleProofNode
                        {
                            Hash = currentLevel[siblingIndex],
                            IsLeft = index % 2 != 0
                        });
                    }

                    for (int i = 0; i < currentLevel.Count; i += 2)
                    {
                        string left = currentLevel[i];
                        string right = i + 1 < currentLevel.Count ? currentLevel[i + 1] : left;
                        var combined = SHA256.HashData(Encoding.UTF8.GetBytes(left + right));
                        nextLevel.Add(Convert.ToHexString(combined));
                    }

                    currentLevel = nextLevel;
                    index /= 2;
                }

                return proof;
            }
        }

        /// <summary>
        /// Verifies a leaf hash belongs to the tree with the given root.
        /// </summary>
        /// <param name="leafHash">The leaf hash to verify.</param>
        /// <param name="proof">The proof nodes.</param>
        /// <param name="root">The expected Merkle root.</param>
        /// <returns>True if the proof is valid; otherwise, false.</returns>
        public static bool VerifyProof(string leafHash, List<MerkleProofNode> proof, string root)
        {
            var current = leafHash;

            foreach (var node in proof)
            {
                var left = node.IsLeft ? node.Hash : current;
                var right = node.IsLeft ? current : node.Hash;
                current = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(left + right)));
            }

            return current == root;
        }

        /// <summary>
        /// Gets the number of leaves in the tree.
        /// </summary>
        public int LeafCount => _leaves.Count;

        /// <summary>
        /// Clears all leaves from the tree.
        /// </summary>
        public void Clear() { lock (_lock) { _leaves.Clear(); } }
    }

    /// <summary>
    /// A node in a Merkle proof path.
    /// </summary>
    public class MerkleProofNode
    {
        /// <summary>
        /// Gets or sets the hash value of the sibling node.
        /// </summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether this node is on the left side.
        /// </summary>
        public bool IsLeft { get; set; }
    }

    #endregion

    #region Signature Provider

    /// <summary>
    /// Digital signature provider for tamper-evident audit entries.
    /// </summary>
    /// <remarks>
    /// Uses RSA-SHA256 signatures for checkpoint authentication.
    /// Signatures provide non-repudiation for compliance requirements.
    /// </remarks>
    public class AuditSignatureProvider : IDisposable
    {
        private readonly RSA _rsa;
        private readonly bool _ownsKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="AuditSignatureProvider"/> class.
        /// </summary>
        /// <param name="keyPath">Optional path to an existing RSA key file.</param>
        public AuditSignatureProvider(string? keyPath = null)
        {
            if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
            {
                var keyData = File.ReadAllText(keyPath);
                _rsa = RSA.Create();
                _rsa.ImportFromPem(keyData);
                _ownsKey = true;
            }
            else
            {
                _rsa = RSA.Create(2048);
                _ownsKey = true;
            }
        }

        /// <summary>
        /// Signs the specified data.
        /// </summary>
        /// <param name="data">The data to sign.</param>
        /// <returns>The Base64-encoded signature.</returns>
        public string Sign(string data)
        {
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signature = _rsa.SignData(dataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }

        /// <summary>
        /// Verifies a signature against the specified data.
        /// </summary>
        /// <param name="data">The original data.</param>
        /// <param name="signature">The Base64-encoded signature to verify.</param>
        /// <returns>True if the signature is valid; otherwise, false.</returns>
        public bool Verify(string data, string signature)
        {
            try
            {
                var dataBytes = Encoding.UTF8.GetBytes(data);
                var signatureBytes = Convert.FromBase64String(signature);
                return _rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Exports the public key in PEM format.
        /// </summary>
        /// <returns>The public key in PEM format.</returns>
        public string ExportPublicKey()
        {
            return _rsa.ExportSubjectPublicKeyInfoPem();
        }

        /// <summary>
        /// Exports the key pair to files.
        /// </summary>
        /// <param name="privateKeyPath">Path for the private key file.</param>
        /// <param name="publicKeyPath">Path for the public key file.</param>
        public void ExportKeyPair(string privateKeyPath, string publicKeyPath)
        {
            File.WriteAllText(privateKeyPath, _rsa.ExportRSAPrivateKeyPem());
            File.WriteAllText(publicKeyPath, _rsa.ExportSubjectPublicKeyInfoPem());
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_ownsKey)
                _rsa.Dispose();
        }
    }

    #endregion

    #region Checkpoint

    /// <summary>
    /// Checkpoint record for Merkle tree integrity verification.
    /// Created periodically to allow efficient verification of large audit logs.
    /// </summary>
    public class AuditCheckpoint
    {
        /// <summary>
        /// Gets or sets the checkpoint timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the starting sequence number for this checkpoint.
        /// </summary>
        public long StartSequence { get; set; }

        /// <summary>
        /// Gets or sets the ending sequence number for this checkpoint.
        /// </summary>
        public long EndSequence { get; set; }

        /// <summary>
        /// Gets or sets the Merkle root hash.
        /// </summary>
        public string MerkleRoot { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the digital signature of the checkpoint.
        /// </summary>
        public string? Signature { get; set; }

        /// <summary>
        /// Gets or sets the number of entries in this checkpoint.
        /// </summary>
        public int EntryCount { get; set; }

        /// <summary>
        /// Gets or sets the hash of the checkpoint itself.
        /// </summary>
        public string CheckpointHash { get; set; } = string.Empty;

        /// <summary>
        /// Computes the hash of this checkpoint's content.
        /// </summary>
        /// <returns>The checkpoint hash as a hexadecimal string.</returns>
        public string ComputeCheckpointHash()
        {
            var data = $"{Timestamp:O}|{StartSequence}|{EndSequence}|{MerkleRoot}|{EntryCount}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }
    }

    #endregion

    #region Legacy Verification Result

    /// <summary>
    /// Result of audit integrity verification (legacy format).
    /// </summary>
    public class AuditVerificationResult
    {
        /// <summary>
        /// Gets or sets whether the verification passed.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Gets or sets the number of entries verified.
        /// </summary>
        public long EntriesVerified { get; set; }

        /// <summary>
        /// Gets or sets the number of entries that failed verification.
        /// </summary>
        public long EntriesFailed { get; set; }

        /// <summary>
        /// Gets or sets whether the hash chain is valid.
        /// </summary>
        public bool HashChainValid { get; set; }

        /// <summary>
        /// Gets or sets whether the Merkle tree is valid.
        /// </summary>
        public bool MerkleTreeValid { get; set; }

        /// <summary>
        /// Gets or sets whether signatures are valid.
        /// </summary>
        public bool SignaturesValid { get; set; }

        /// <summary>
        /// Gets or sets the list of errors found.
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// Gets or sets the verification timestamp.
        /// </summary>
        public DateTime VerificationTime { get; set; } = DateTime.UtcNow;
    }

    #endregion

    #region Exporters

    internal interface IAuditExporter
    {
        string Export(IEnumerable<AuditEntry> entries);
    }

    internal class JsonExporter : IAuditExporter
    {
        private readonly string _compliance;

        public JsonExporter(string compliance) => _compliance = compliance;

        public string Export(IEnumerable<AuditEntry> entries)
        {
            var export = new
            {
                ExportedAt = DateTime.UtcNow,
                ComplianceFramework = _compliance,
                Entries = entries.ToList()
            };
            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    internal class CsvExporter : IAuditExporter
    {
        private readonly string _compliance;

        public CsvExporter(string compliance) => _compliance = compliance;

        public string Export(IEnumerable<AuditEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,SequenceNumber,Action,ResourceType,ResourceId,UserId,Outcome,Severity,EntryHash,PreviousHash");

            foreach (var entry in entries)
            {
                sb.AppendLine($"\"{entry.Timestamp:O}\",{entry.SequenceNumber},\"{entry.Action}\",\"{entry.ResourceType}\"," +
                             $"\"{entry.ResourceId}\",\"{entry.UserId}\",\"{entry.Outcome}\",\"{entry.Severity}\"," +
                             $"\"{entry.EntryHash}\",\"{entry.PreviousHash}\"");
            }

            return sb.ToString();
        }
    }

    internal class SiemExporter : IAuditExporter
    {
        private readonly string _compliance;

        public SiemExporter(string compliance) => _compliance = compliance;

        public string Export(IEnumerable<AuditEntry> entries)
        {
            var sb = new StringBuilder();

            foreach (var entry in entries)
            {
                var cef = $"CEF:0|DataWarehouse|AuditLog|1.0|{entry.Action}|{entry.Action}|" +
                         $"{GetSeverityNumber(entry.Severity)}|" +
                         $"rt={entry.Timestamp:O} " +
                         $"src={entry.IpAddress ?? "unknown"} " +
                         $"suser={entry.UserId} " +
                         $"outcome={entry.Outcome} " +
                         $"cs1={entry.ResourceType} cs1Label=ResourceType " +
                         $"cs2={entry.ResourceId ?? ""} cs2Label=ResourceId " +
                         $"cs3={_compliance} cs3Label=ComplianceFramework " +
                         $"cs4={entry.EntryHash} cs4Label=EntryHash " +
                         $"cs5={entry.PreviousHash} cs5Label=PreviousHash";
                sb.AppendLine(cef);
            }

            return sb.ToString();
        }

        private static int GetSeverityNumber(string severity)
        {
            return severity.ToLowerInvariant() switch
            {
                "critical" => 10,
                "high" => 7,
                "medium" => 5,
                "low" => 3,
                "info" => 1,
                _ => 1
            };
        }
    }

    [JsonSerializable(typeof(AuditEntry))]
    internal partial class AuditJsonContext : JsonSerializerContext { }

    #endregion
}
