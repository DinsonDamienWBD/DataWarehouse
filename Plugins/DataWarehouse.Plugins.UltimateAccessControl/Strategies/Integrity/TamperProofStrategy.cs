using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Integrity
{
    /// <summary>
    /// Tamper-evident storage using sealed hash chains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides tamper-evident storage with cryptographic hash chains:
    /// - Hash chain linking: Each entry contains hash of previous entry
    /// - Sealed records: Cannot be modified without breaking the chain
    /// - Tamper detection: Any modification invalidates subsequent hashes
    /// - Chain verification: Validate entire chain integrity
    /// - Genesis block: Starting point for hash chain
    /// - Append-only semantics: Records can only be added, never modified
    /// </para>
    /// <para>
    /// <b>PRODUCTION-READY:</b> Real cryptographic hash chains using HMAC-SHA256.
    /// Provides strong tamper-evidence guarantees.
    /// </para>
    /// </remarks>
    public sealed class TamperProofStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, TamperProofChain> _chains = new();
        private readonly byte[] _hmacKey;
        private bool _requireChainVerificationOnAccess = true;

        public TamperProofStrategy()
        {
            // Generate random HMAC key for this instance
            _hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_hmacKey);
            }
        }

        /// <inheritdoc/>
        public override string StrategyId => "integrity-tamperproof";

        /// <inheritdoc/>
        public override string StrategyName => "Tamper-Proof Storage (Hash Chains)";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("RequireChainVerificationOnAccess", out var rcv) && rcv is bool rcvBool)
                _requireChainVerificationOnAccess = rcvBool;

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Creates a new tamper-proof chain for a resource.
        /// </summary>
        public TamperProofChain CreateChain(string chainId, string description)
        {
            if (string.IsNullOrWhiteSpace(chainId))
                throw new ArgumentException("Chain ID cannot be empty", nameof(chainId));

            var genesisBlock = new TamperProofBlock
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"Genesis block for {chainId}",
                PreviousHash = new byte[32], // All zeros for genesis
                Hash = Array.Empty<byte>()
            };

            genesisBlock = genesisBlock with { Hash = ComputeBlockHash(genesisBlock) };

            var chain = new TamperProofChain
            {
                ChainId = chainId,
                Description = description,
                CreatedAt = DateTime.UtcNow,
                Blocks = new List<TamperProofBlock> { genesisBlock },
                IsValid = true
            };

            _chains[chainId] = chain;
            return chain;
        }

        /// <summary>
        /// Appends a new block to the chain.
        /// </summary>
        public TamperProofBlock AppendBlock(string chainId, string data)
        {
            if (!_chains.TryGetValue(chainId, out var chain))
                throw new InvalidOperationException($"Chain {chainId} does not exist");

            var lastBlock = chain.Blocks[^1];
            var newBlock = new TamperProofBlock
            {
                Index = lastBlock.Index + 1,
                Timestamp = DateTime.UtcNow,
                Data = data,
                PreviousHash = lastBlock.Hash,
                Hash = Array.Empty<byte>()
            };

            newBlock = newBlock with { Hash = ComputeBlockHash(newBlock) };

            // Create new chain with appended block
            var updatedChain = chain with
            {
                Blocks = chain.Blocks.Concat(new[] { newBlock }).ToList()
            };

            _chains[chainId] = updatedChain;
            return newBlock;
        }

        /// <summary>
        /// Verifies the integrity of the entire chain.
        /// </summary>
        public ChainVerificationResult VerifyChain(string chainId)
        {
            if (!_chains.TryGetValue(chainId, out var chain))
            {
                return new ChainVerificationResult
                {
                    IsValid = false,
                    Reason = "Chain not found",
                    ChainId = chainId,
                    Timestamp = DateTime.UtcNow
                };
            }

            // Verify genesis block
            if (chain.Blocks.Count == 0)
            {
                return new ChainVerificationResult
                {
                    IsValid = false,
                    Reason = "Empty chain",
                    ChainId = chainId,
                    Timestamp = DateTime.UtcNow
                };
            }

            var genesisBlock = chain.Blocks[0];
            if (genesisBlock.Index != 0)
            {
                return new ChainVerificationResult
                {
                    IsValid = false,
                    Reason = "Invalid genesis block index",
                    ChainId = chainId,
                    Timestamp = DateTime.UtcNow,
                    InvalidBlockIndex = 0
                };
            }

            // Verify each block's hash and linkage
            for (int i = 0; i < chain.Blocks.Count; i++)
            {
                var block = chain.Blocks[i];

                // Verify block hash
                var expectedHash = ComputeBlockHash(block with { Hash = Array.Empty<byte>() });
                if (!ConstantTimeCompare(expectedHash, block.Hash))
                {
                    return new ChainVerificationResult
                    {
                        IsValid = false,
                        Reason = $"Block {i} hash mismatch - tampering detected",
                        ChainId = chainId,
                        Timestamp = DateTime.UtcNow,
                        InvalidBlockIndex = i
                    };
                }

                // Verify linkage to previous block
                if (i > 0)
                {
                    var prevBlock = chain.Blocks[i - 1];
                    if (!ConstantTimeCompare(block.PreviousHash, prevBlock.Hash))
                    {
                        return new ChainVerificationResult
                        {
                            IsValid = false,
                            Reason = $"Block {i} linkage broken - chain integrity compromised",
                            ChainId = chainId,
                            Timestamp = DateTime.UtcNow,
                            InvalidBlockIndex = i
                        };
                    }
                }
            }

            return new ChainVerificationResult
            {
                IsValid = true,
                Reason = "Chain verification passed - no tampering detected",
                ChainId = chainId,
                Timestamp = DateTime.UtcNow,
                BlockCount = chain.Blocks.Count
            };
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (!_requireChainVerificationOnAccess)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Chain verification not enforced on access"
                };
            }

            // Check if resource has a tamper-proof chain
            if (!_chains.TryGetValue(context.ResourceId, out var chain))
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No tamper-proof chain registered for resource",
                    Metadata = new Dictionary<string, object>
                    {
                        ["TamperProofStatus"] = "NotRegistered"
                    }
                };
            }

            // Verify chain integrity
            var verification = VerifyChain(context.ResourceId);

            return new AccessDecision
            {
                IsGranted = verification.IsValid,
                Reason = verification.IsValid
                    ? "Access granted - chain integrity verified"
                    : $"Access denied - {verification.Reason}",
                Metadata = new Dictionary<string, object>
                {
                    ["TamperProofStatus"] = verification.IsValid ? "Valid" : "Invalid",
                    ["BlockCount"] = chain.Blocks.Count,
                    ["CreatedAt"] = chain.CreatedAt,
                    ["VerificationTimestamp"] = verification.Timestamp
                }
            };
        }

        private byte[] ComputeBlockHash(TamperProofBlock block)
        {
            using var hmac = new HMACSHA256(_hmacKey);
            var dataToHash = System.Text.Encoding.UTF8.GetBytes(
                $"{block.Index}|{block.Timestamp:O}|{block.Data}|{Convert.ToBase64String(block.PreviousHash)}"
            );
            return hmac.ComputeHash(dataToHash);
        }

        private static bool ConstantTimeCompare(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        /// <summary>
        /// Gets a tamper-proof chain.
        /// </summary>
        public TamperProofChain? GetChain(string chainId)
        {
            return _chains.TryGetValue(chainId, out var chain) ? chain : null;
        }

        /// <summary>
        /// Gets all chains.
        /// </summary>
        public IReadOnlyDictionary<string, TamperProofChain> GetAllChains()
        {
            return _chains.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    /// <summary>
    /// A tamper-proof chain of blocks.
    /// </summary>
    public sealed record TamperProofChain
    {
        public required string ChainId { get; init; }
        public required string Description { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required IReadOnlyList<TamperProofBlock> Blocks { get; init; }
        public required bool IsValid { get; init; }
    }

    /// <summary>
    /// A block in a tamper-proof chain.
    /// </summary>
    public sealed record TamperProofBlock
    {
        public required int Index { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string Data { get; init; }
        public required byte[] PreviousHash { get; init; }
        public required byte[] Hash { get; init; }
    }

    /// <summary>
    /// Result of chain verification.
    /// </summary>
    public sealed record ChainVerificationResult
    {
        public required bool IsValid { get; init; }
        public required string Reason { get; init; }
        public required string ChainId { get; init; }
        public required DateTime Timestamp { get; init; }
        public int? InvalidBlockIndex { get; init; }
        public int BlockCount { get; init; }
    }
}
