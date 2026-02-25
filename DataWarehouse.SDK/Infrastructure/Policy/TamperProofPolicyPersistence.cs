using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Tamper-proof policy persistence decorator that wraps another <see cref="IPolicyPersistence"/>
    /// implementation and appends an immutable SHA-256 hash-chained audit block on every mutating
    /// operation (save, delete, save-profile). The audit chain is append-only and can be verified
    /// at any time via <see cref="VerifyChainIntegrity"/> for compliance with HIPAA, SOC2, and PCI-DSS.
    /// <para>
    /// This class implements <see cref="IPolicyPersistence"/> directly (decorator pattern) rather than
    /// extending <see cref="PolicyPersistenceBase"/> to avoid double-serialization when delegating to
    /// the inner persistence. All storage operations are forwarded to the inner implementation; only
    /// the audit chain is maintained locally.
    /// </para>
    /// <para>
    /// The hash chain uses a genesis block with hash "GENESIS". Each subsequent block includes the
    /// previous block's hash, creating a linked chain where any tampering invalidates all subsequent
    /// blocks. Block hashes are computed as SHA-256 over the concatenation of sequence number,
    /// previous hash, operation, key, data hash, and timestamp.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: TamperProof policy persistence (PERS-05)")]
    public sealed class TamperProofPolicyPersistence : IPolicyPersistence
    {
        private readonly IPolicyPersistence _inner;
        private readonly ConcurrentQueue<AuditBlock> _auditChain = new();
        private string _lastHash = "GENESIS";
        private long _sequenceNumber;
        private readonly object _chainLock = new();

        /// <summary>
        /// Initializes a new instance of <see cref="TamperProofPolicyPersistence"/> wrapping the
        /// specified inner persistence implementation.
        /// </summary>
        /// <param name="innerPersistence">
        /// The inner <see cref="IPolicyPersistence"/> that handles actual storage.
        /// Must not be null.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="innerPersistence"/> is null.
        /// </exception>
        public TamperProofPolicyPersistence(IPolicyPersistence innerPersistence)
        {
            _inner = innerPersistence ?? throw new ArgumentNullException(nameof(innerPersistence));
        }

        /// <summary>
        /// Gets the current number of audit blocks in the chain.
        /// </summary>
        public int AuditBlockCount => (int)Interlocked.Read(ref _sequenceNumber);

        /// <summary>
        /// Loads all persisted policies from the inner persistence. Read operations do not
        /// generate audit blocks.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only list of all persisted policies.</returns>
        public Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllAsync(CancellationToken ct = default)
        {
            return _inner.LoadAllAsync(ct);
        }

        /// <summary>
        /// Saves a policy via the inner persistence and appends an audit block recording the operation.
        /// The audit block captures a SHA-256 hash of the serialized policy data for integrity verification.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature this policy governs.</param>
        /// <param name="level">The hierarchy level at which this policy applies.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="policy">The feature policy to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task SaveAsync(string featureId, PolicyLevel level, string path, FeaturePolicy policy, CancellationToken ct = default)
        {
            await _inner.SaveAsync(featureId, level, path, policy, ct).ConfigureAwait(false);

            var serialized = PolicySerializationHelper.SerializePolicy(policy);
            var dataHash = ComputeDataHash(serialized);
            var key = $"{featureId}:{(int)level}:{path}";

            AppendAuditBlock("Save", key, dataHash);
        }

        /// <summary>
        /// Deletes a policy via the inner persistence and appends an audit block recording the deletion.
        /// The data hash for delete operations is recorded as "DELETED".
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to delete the policy for.</param>
        /// <param name="level">The hierarchy level from which to delete the policy.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task DeleteAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default)
        {
            await _inner.DeleteAsync(featureId, level, path, ct).ConfigureAwait(false);

            var key = $"{featureId}:{(int)level}:{path}";
            AppendAuditBlock("Delete", key, "DELETED");
        }

        /// <summary>
        /// Saves an operational profile via the inner persistence and appends an audit block.
        /// The audit block captures a SHA-256 hash of the serialized profile bytes.
        /// </summary>
        /// <param name="profile">The operational profile to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task SaveProfileAsync(OperationalProfile profile, CancellationToken ct = default)
        {
            await _inner.SaveProfileAsync(profile, ct).ConfigureAwait(false);

            var serialized = PolicySerializationHelper.SerializeProfile(profile);
            var dataHash = ComputeDataHash(serialized);

            AppendAuditBlock("SaveProfile", "profile", dataHash);
        }

        /// <summary>
        /// Loads the operational profile from the inner persistence. Read operations do not
        /// generate audit blocks.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The persisted operational profile, or null if none exists.</returns>
        public Task<OperationalProfile?> LoadProfileAsync(CancellationToken ct = default)
        {
            return _inner.LoadProfileAsync(ct);
        }

        /// <summary>
        /// Flushes pending writes on the inner persistence. Does not generate an audit block.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public Task FlushAsync(CancellationToken ct = default)
        {
            return _inner.FlushAsync(ct);
        }

        /// <summary>
        /// Returns a snapshot of the complete audit chain as a read-only list.
        /// The chain is ordered by sequence number from genesis to the most recent block.
        /// </summary>
        /// <returns>A read-only list of all audit blocks in chain order.</returns>
        public IReadOnlyList<AuditBlock> GetAuditChain()
        {
            return _auditChain.ToArray();
        }

        /// <summary>
        /// Verifies the integrity of the entire audit chain by walking from the first block to the last,
        /// recomputing each block's hash and validating the previous-hash linkage. Detects any tampering
        /// with block contents, reordering, or hash chain breaks.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if all blocks have valid hashes and correct previous-hash links;
        /// <see langword="false"/> if any integrity violation is detected.
        /// </returns>
        public bool VerifyChainIntegrity()
        {
            var blocks = _auditChain.ToArray();
            if (blocks.Length == 0)
                return true;

            var expectedPreviousHash = "GENESIS";

            for (int i = 0; i < blocks.Length; i++)
            {
                var block = blocks[i];

                // Verify previous hash link
                if (!string.Equals(block.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
                    return false;

                // Recompute and verify block hash
                var recomputedHash = ComputeBlockHash(
                    block.SequenceNumber,
                    block.PreviousHash,
                    block.Operation,
                    block.Key,
                    block.DataHash,
                    block.Timestamp);

                if (!string.Equals(block.BlockHash, recomputedHash, StringComparison.Ordinal))
                    return false;

                expectedPreviousHash = block.BlockHash;
            }

            return true;
        }

        /// <summary>
        /// Appends a new audit block to the chain under lock to ensure sequential ordering
        /// and correct previous-hash linkage.
        /// </summary>
        /// <param name="operation">The operation type ("Save", "Delete", "SaveProfile").</param>
        /// <param name="key">The policy key or identifier.</param>
        /// <param name="dataHash">The SHA-256 hash of the data, or "DELETED" for deletions.</param>
        private void AppendAuditBlock(string operation, string key, string dataHash)
        {
            lock (_chainLock)
            {
                var seq = Interlocked.Increment(ref _sequenceNumber);
                var timestamp = DateTimeOffset.UtcNow;
                var blockHash = ComputeBlockHash(seq, _lastHash, operation, key, dataHash, timestamp);

                var block = new AuditBlock
                {
                    SequenceNumber = seq,
                    PreviousHash = _lastHash,
                    Operation = operation,
                    Key = key,
                    DataHash = dataHash,
                    Timestamp = timestamp,
                    BlockHash = blockHash
                };

                _auditChain.Enqueue(block);
                _lastHash = blockHash;
            }
        }

        /// <summary>
        /// Computes the SHA-256 hash of serialized data and returns it as a hexadecimal string.
        /// </summary>
        /// <param name="data">The data to hash.</param>
        /// <returns>Uppercase hexadecimal representation of the SHA-256 hash.</returns>
        private static string ComputeDataHash(byte[] data)
        {
            return Convert.ToHexString(SHA256.HashData(data));
        }

        /// <summary>
        /// Computes the SHA-256 block hash from all constituent fields. The hash is computed over
        /// the UTF-8 encoding of the concatenated string representation of all fields.
        /// </summary>
        /// <param name="seq">The block's sequence number.</param>
        /// <param name="prevHash">The previous block's hash.</param>
        /// <param name="op">The operation type.</param>
        /// <param name="key">The policy key.</param>
        /// <param name="dataHash">The hash of the operation's data.</param>
        /// <param name="ts">The block's timestamp.</param>
        /// <returns>Uppercase hexadecimal representation of the SHA-256 block hash.</returns>
        private static string ComputeBlockHash(long seq, string prevHash, string op, string key, string dataHash, DateTimeOffset ts)
        {
            var input = $"{seq}|{prevHash}|{op}|{key}|{dataHash}|{ts:O}";
            var bytes = Encoding.UTF8.GetBytes(input);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        /// <summary>
        /// Represents a single immutable audit record in the hash chain. Each block links to the
        /// previous block via <see cref="PreviousHash"/>, forming a blockchain-like structure
        /// where tampering with any block invalidates all subsequent blocks.
        /// </summary>
        public sealed record AuditBlock
        {
            /// <summary>
            /// Monotonically increasing sequence number starting at 1. Used for ordering and
            /// detecting missing or reordered blocks.
            /// </summary>
            public required long SequenceNumber { get; init; }

            /// <summary>
            /// SHA-256 hash of the previous block, or "GENESIS" for the first block.
            /// Forms the backward link in the hash chain.
            /// </summary>
            public required string PreviousHash { get; init; }

            /// <summary>
            /// The type of mutation that produced this audit record: "Save", "Delete", or "SaveProfile".
            /// </summary>
            public required string Operation { get; init; }

            /// <summary>
            /// The policy key affected by this operation, or "profile" for profile operations.
            /// </summary>
            public required string Key { get; init; }

            /// <summary>
            /// SHA-256 hash of the operation's data payload, or "DELETED" for delete operations.
            /// Enables integrity verification without storing the full data.
            /// </summary>
            public required string DataHash { get; init; }

            /// <summary>
            /// UTC timestamp when this audit block was created.
            /// </summary>
            public required DateTimeOffset Timestamp { get; init; }

            /// <summary>
            /// SHA-256 hash of this entire block computed from: SequenceNumber, PreviousHash,
            /// Operation, Key, DataHash, and Timestamp. Any modification to these fields
            /// will produce a different hash, enabling tamper detection.
            /// </summary>
            public required string BlockHash { get; init; }
        }
    }
}
