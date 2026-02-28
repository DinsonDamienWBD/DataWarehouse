using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Contracts.Composition
{
    /// <summary>
    /// Represents a single transformation step in a data provenance chain.
    /// Part of Data DNA Provenance Certificate system.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record CertificateEntry
    {
        /// <summary>
        /// Unique identifier for this transformation step.
        /// </summary>
        public required string TransformId { get; init; }

        /// <summary>
        /// Description of the operation performed (e.g., "encrypt", "compress", "write").
        /// </summary>
        public required string Operation { get; init; }

        /// <summary>
        /// Identifier of the upstream object this transformation derived from.
        /// Null for root/origin objects.
        /// </summary>
        public string? SourceObjectId { get; init; }

        /// <summary>
        /// Timestamp when this transformation occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// SHA256 hash of the data before this transformation.
        /// Null for creation operations.
        /// </summary>
        public string? BeforeHash { get; init; }

        /// <summary>
        /// SHA256 hash of the data after this transformation.
        /// </summary>
        public string? AfterHash { get; init; }

        /// <summary>
        /// Optional metadata providing additional context for this transformation.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Information about blockchain anchoring for tamper-proof verification.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record BlockchainAnchorInfo
    {
        /// <summary>
        /// Unique identifier for this blockchain anchor.
        /// </summary>
        public required string AnchorId { get; init; }

        /// <summary>
        /// Block number where the hash was anchored.
        /// </summary>
        public required long BlockNumber { get; init; }

        /// <summary>
        /// Timestamp when the anchor was created.
        /// </summary>
        public required DateTimeOffset AnchoredAt { get; init; }

        /// <summary>
        /// The Merkle root or direct hash that was anchored to the blockchain.
        /// </summary>
        public required string RootHash { get; init; }

        /// <summary>
        /// Number of confirmations for the blockchain transaction.
        /// </summary>
        public int Confirmations { get; init; }

        /// <summary>
        /// Whether the blockchain anchor is valid.
        /// </summary>
        public required bool IsValid { get; init; }

        /// <summary>
        /// Blockchain transaction identifier.
        /// Null if not yet confirmed.
        /// </summary>
        public string? TransactionId { get; init; }
    }

    /// <summary>
    /// A cryptographically verifiable provenance certificate showing the complete
    /// transformation history of a data object.
    /// Composes SelfTrackingData hash chains with TamperProof blockchain anchoring.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record ProvenanceCertificate
    {
        /// <summary>
        /// Unique identifier for this certificate (GUID).
        /// </summary>
        public required string CertificateId { get; init; }

        /// <summary>
        /// The object this certificate describes.
        /// </summary>
        public required string ObjectId { get; init; }

        /// <summary>
        /// Timestamp when this certificate was issued.
        /// </summary>
        public required DateTimeOffset IssuedAt { get; init; }

        /// <summary>
        /// Ordered transformation history (oldest first).
        /// Each entry represents one step in the data's lifecycle.
        /// </summary>
        public required IReadOnlyList<CertificateEntry> Chain { get; init; }

        /// <summary>
        /// Blockchain anchor information for tamper-proof verification.
        /// Null if blockchain anchoring is not configured.
        /// </summary>
        public BlockchainAnchorInfo? BlockchainAnchor { get; init; }

        /// <summary>
        /// SHA256 hash of the entire certificate chain for tamper detection.
        /// </summary>
        public required string CertificateHash { get; init; }

        /// <summary>
        /// Verifies the integrity of this certificate by validating the hash chain.
        /// This method is self-contained and does not require external services.
        /// </summary>
        /// <returns>Verification result with validation details.</returns>
        public CertificateVerificationResult Verify()
        {
            var chainLength = Chain.Count;

            // Empty chain is valid by definition
            if (chainLength == 0)
                return CertificateVerificationResult.CreateValid(0, BlockchainAnchor != null);

            // Verify that adjacent entries link correctly (AfterHash[i] matches BeforeHash[i+1])
            for (int i = 0; i < chainLength - 1; i++)
            {
                var current = Chain[i];
                var next = Chain[i + 1];

                // Check hash chain continuity — both hashes must be present; a null AfterHash
                // is not a valid link and must be treated as a broken chain to prevent bypass.
                if (current.AfterHash == null || next.BeforeHash == null)
                {
                    return CertificateVerificationResult.CreateInvalid(
                        chainLength,
                        i,
                        $"Hash chain broken between entry {i} and {i + 1}: AfterHash or BeforeHash is null");
                }

                if (current.AfterHash != next.BeforeHash)
                {
                    return CertificateVerificationResult.CreateInvalid(
                        chainLength,
                        i,
                        $"Hash chain broken between entry {i} and {i + 1}: AfterHash does not match BeforeHash");
                }
            }

            // Compute certificate hash and compare to stored value
            var computedHash = ComputeCertificateHash(Chain);
            if (computedHash != CertificateHash)
            {
                return CertificateVerificationResult.CreateInvalid(
                    chainLength,
                    -1,
                    "Certificate hash mismatch: certificate has been tampered with");
            }

            return CertificateVerificationResult.CreateValid(chainLength, BlockchainAnchor != null);
        }

        /// <summary>
        /// Computes SHA256 hash of the certificate chain for tamper detection.
        /// Uses canonical JSON serialization for consistency.
        /// Note: property order is stable because CertificateEntry is a record with fixed
        /// declared property order; dictionary fields within entries are sorted explicitly.
        /// </summary>
        private static string ComputeCertificateHash(IReadOnlyList<CertificateEntry> chain)
        {
            // Serialize to canonical JSON (no whitespace, camelCase).
            // CertificateEntry is a record — System.Text.Json emits properties in declaration
            // order, which is deterministic. Any Dictionary<string,*> fields in entries must
            // be sorted before serialization to ensure canonical form.
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // Produce a canonical representation by converting to sorted JsonNode tree
            var rawJson = JsonSerializer.Serialize(chain, options);
            var node = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
            var canonicalJson = SortJsonNode(node)?.ToJsonString() ?? rawJson;
            var bytes = Encoding.UTF8.GetBytes(canonicalJson);

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(bytes);

            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// Recursively sorts all object keys alphabetically to produce canonical JSON.
        /// </summary>
        private static System.Text.Json.Nodes.JsonNode? SortJsonNode(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                var sorted = new System.Text.Json.Nodes.JsonObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    sorted[key] = SortJsonNode(obj[key]?.DeepClone());
                }
                return sorted;
            }

            if (node is System.Text.Json.Nodes.JsonArray arr)
            {
                var sortedArr = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in arr)
                {
                    sortedArr.Add(SortJsonNode(item?.DeepClone()));
                }
                return sortedArr;
            }

            return node?.DeepClone();
        }
    }

    /// <summary>
    /// Result of verifying a provenance certificate.
    /// </summary>
    [SdkCompatibility("3.0.0")]
    public record CertificateVerificationResult
    {
        /// <summary>
        /// Whether the certificate passed validation.
        /// </summary>
        public required bool IsValid { get; init; }

        /// <summary>
        /// Number of entries in the certificate chain.
        /// </summary>
        public required int ChainLength { get; init; }

        /// <summary>
        /// Index of the first broken link in the chain.
        /// Null if the certificate is valid.
        /// </summary>
        public int? BrokenLinkIndex { get; init; }

        /// <summary>
        /// Error message describing why verification failed.
        /// Null if the certificate is valid.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Whether this certificate includes blockchain anchoring.
        /// </summary>
        public bool BlockchainAnchored { get; init; }

        /// <summary>
        /// Creates a successful verification result.
        /// </summary>
        /// <param name="chainLength">Number of entries in the chain.</param>
        /// <param name="anchored">Whether blockchain anchoring is present.</param>
        /// <returns>Valid verification result.</returns>
        public static CertificateVerificationResult CreateValid(int chainLength, bool anchored)
        {
            return new CertificateVerificationResult
            {
                IsValid = true,
                ChainLength = chainLength,
                BlockchainAnchored = anchored
            };
        }

        /// <summary>
        /// Creates a failed verification result.
        /// </summary>
        /// <param name="chainLength">Number of entries in the chain.</param>
        /// <param name="brokenLinkIndex">Index where the chain broke.</param>
        /// <param name="error">Error description.</param>
        /// <returns>Invalid verification result.</returns>
        public static CertificateVerificationResult CreateInvalid(int chainLength, int brokenLinkIndex, string error)
        {
            return new CertificateVerificationResult
            {
                IsValid = false,
                ChainLength = chainLength,
                BrokenLinkIndex = brokenLinkIndex >= 0 ? brokenLinkIndex : null,
                ErrorMessage = error,
                BlockchainAnchored = false
            };
        }
    }
}
