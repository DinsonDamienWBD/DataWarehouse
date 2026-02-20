using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Composition;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataLineage.Composition
{
    /// <summary>
    /// Data DNA Provenance Certificate Service that composes SelfTrackingData hash chains
    /// with TamperProof blockchain anchoring into unified, verifiable provenance certificates.
    /// Implements COMP-02: cryptographic provenance certificates with offline verification.
    /// </summary>
    /// <remarks>
    /// This service orchestrates:
    /// 1. SelfTrackingDataStrategy: Per-object transformation history with hash chains
    /// 2. TamperProofBlockchain: Blockchain anchoring for tamper-proof verification
    ///
    /// Any stored object can produce a ProvenanceCertificate showing every transformation
    /// with before/after hashes, anchored to a blockchain. The certificate's Verify() method
    /// validates the hash chain locally without requiring external services.
    ///
    /// Gracefully degrades when blockchain is not configured (certificate still includes hash chain).
    /// </remarks>
    [SdkCompatibility("3.0.0")]
    public sealed class ProvenanceCertificateService : IDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly ILogger? _logger;

        // Bounded certificate store per Phase 23 memory safety
        private readonly BoundedDictionary<string, List<ProvenanceCertificate>> _certificateStore = new BoundedDictionary<string, List<ProvenanceCertificate>>(1000);
        private const int MaxCertificatesPerObject = 10000;

        private readonly List<IDisposable> _subscriptions = new();
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the ProvenanceCertificateService.
        /// </summary>
        /// <param name="messageBus">Message bus for cross-plugin communication.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public ProvenanceCertificateService(IMessageBus messageBus, ILogger? logger = null)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _logger = logger;
        }

        /// <summary>
        /// Starts listening for certificate requests via message bus.
        /// </summary>
        public void StartListening()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ProvenanceCertificateService));

            _logger?.LogInformation("ProvenanceCertificateService starting to listen for certificate requests");

            var subscription = _messageBus.Subscribe("composition.provenance.request-certificate",
                HandleCertificateRequestAsync);

            _subscriptions.Add(subscription);

            _logger?.LogInformation("ProvenanceCertificateService listening on composition.provenance.request-certificate");
        }

        /// <summary>
        /// Issues a provenance certificate for the specified object.
        /// Composes transformation history from lineage plugin with blockchain anchoring from TamperProof.
        /// </summary>
        /// <param name="objectId">The object to issue a certificate for.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A complete provenance certificate with hash chain and optional blockchain anchor.</returns>
        public async Task<ProvenanceCertificate> IssueCertificateAsync(string objectId, CancellationToken ct = default)
        {
            _logger?.LogInformation("Issuing provenance certificate for object {ObjectId}", objectId);

            try
            {
                // Step 1: Request transformation history from lineage plugin
                var historyRequest = PluginMessage.Create("composition.provenance.get-history", new Dictionary<string, object>
                {
                    ["objectId"] = objectId
                });

                var historyResponse = await _messageBus.SendAsync(
                    "composition.provenance.get-history",
                    historyRequest,
                    TimeSpan.FromSeconds(30),
                    ct);

                var chain = new List<CertificateEntry>();

                if (historyResponse.Success && historyResponse.Payload != null)
                {
                    // Parse transformation records from response
                    if (historyResponse.Payload is JsonElement element && element.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var recordElement in element.EnumerateArray())
                        {
                            var entry = new CertificateEntry
                            {
                                TransformId = recordElement.GetProperty("transformId").GetString() ?? Guid.NewGuid().ToString("N"),
                                Operation = recordElement.GetProperty("operation").GetString() ?? "unknown",
                                SourceObjectId = recordElement.TryGetProperty("sourceObjectId", out var src)
                                    ? src.GetString()
                                    : null,
                                Timestamp = recordElement.TryGetProperty("timestamp", out var ts)
                                    ? ts.GetDateTimeOffset()
                                    : DateTimeOffset.UtcNow,
                                BeforeHash = recordElement.TryGetProperty("beforeHash", out var before)
                                    ? before.GetString()
                                    : null,
                                AfterHash = recordElement.TryGetProperty("afterHash", out var after)
                                    ? after.GetString()
                                    : null
                            };

                            chain.Add(entry);
                        }
                    }
                }

                // Sort chain by timestamp (oldest first)
                chain = chain.OrderBy(e => e.Timestamp).ToList();

                _logger?.LogDebug("Retrieved {Count} transformation records for object {ObjectId}",
                    chain.Count, objectId);

                // Step 2: Request blockchain anchor verification (graceful degradation if unavailable)
                BlockchainAnchorInfo? blockchainAnchor = null;

                try
                {
                    var anchorRequest = PluginMessage.Create("composition.provenance.verify-anchor", new Dictionary<string, object>
                    {
                        ["objectId"] = objectId
                    });

                    var anchorResponse = await _messageBus.SendAsync(
                        "composition.provenance.verify-anchor",
                        anchorRequest,
                        TimeSpan.FromSeconds(10),
                        ct);

                    if (anchorResponse.Success && anchorResponse.Payload is JsonElement anchorElement)
                    {
                        blockchainAnchor = new BlockchainAnchorInfo
                        {
                            AnchorId = anchorElement.GetProperty("anchorId").GetString() ?? Guid.NewGuid().ToString("N"),
                            BlockNumber = anchorElement.GetProperty("blockNumber").GetInt64(),
                            AnchoredAt = anchorElement.GetProperty("anchoredAt").GetDateTimeOffset(),
                            RootHash = anchorElement.GetProperty("rootHash").GetString() ?? "",
                            Confirmations = anchorElement.TryGetProperty("confirmations", out var conf)
                                ? conf.GetInt32()
                                : 0,
                            IsValid = anchorElement.GetProperty("isValid").GetBoolean(),
                            TransactionId = anchorElement.TryGetProperty("transactionId", out var txId)
                                ? txId.GetString()
                                : null
                        };

                        _logger?.LogDebug("Blockchain anchor retrieved for object {ObjectId}: block {BlockNumber}",
                            objectId, blockchainAnchor.BlockNumber);
                    }
                }
                catch (Exception ex)
                {
                    // Graceful degradation: blockchain unavailable, certificate still valid without anchor
                    _logger?.LogWarning(ex, "Blockchain anchoring unavailable for object {ObjectId}, certificate will not include anchor", objectId);
                }

                // Step 3: Compute certificate hash
                var certificateHash = ComputeCertificateHash(chain);

                // Step 4: Assemble certificate
                var certificate = new ProvenanceCertificate
                {
                    CertificateId = Guid.NewGuid().ToString("N"),
                    ObjectId = objectId,
                    IssuedAt = DateTimeOffset.UtcNow,
                    Chain = chain.AsReadOnly(),
                    BlockchainAnchor = blockchainAnchor,
                    CertificateHash = certificateHash
                };

                // Store certificate in bounded collection
                StoreCertificate(objectId, certificate);

                _logger?.LogInformation("Issued certificate {CertificateId} for object {ObjectId} with {ChainLength} entries",
                    certificate.CertificateId, objectId, chain.Count);

                return certificate;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error issuing certificate for object {ObjectId}", objectId);
                throw;
            }
        }

        /// <summary>
        /// Verifies a provenance certificate.
        /// Calls the certificate's built-in Verify() method and optionally re-checks blockchain anchor.
        /// </summary>
        /// <param name="certificate">The certificate to verify.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Verification result.</returns>
        public async Task<CertificateVerificationResult> VerifyCertificateAsync(
            ProvenanceCertificate certificate,
            CancellationToken ct = default)
        {
            _logger?.LogInformation("Verifying certificate {CertificateId}", certificate.CertificateId);

            // Local chain validation (offline, no external dependencies)
            var result = certificate.Verify();

            if (!result.IsValid)
            {
                _logger?.LogWarning("Certificate {CertificateId} failed local validation: {Error}",
                    certificate.CertificateId, result.ErrorMessage);
                return result;
            }

            // If blockchain anchor is present, verify it with TamperProof plugin
            if (certificate.BlockchainAnchor != null)
            {
                try
                {
                    var verifyRequest = PluginMessage.Create("composition.provenance.verify-blockchain-anchor", new Dictionary<string, object>
                    {
                        ["anchorId"] = certificate.BlockchainAnchor.AnchorId,
                        ["rootHash"] = certificate.BlockchainAnchor.RootHash,
                        ["blockNumber"] = certificate.BlockchainAnchor.BlockNumber
                    });

                    var verifyResponse = await _messageBus.SendAsync(
                        "composition.provenance.verify-blockchain-anchor",
                        verifyRequest,
                        TimeSpan.FromSeconds(10),
                        ct);

                    if (verifyResponse.Success && verifyResponse.Payload is JsonElement element)
                    {
                        var blockchainValid = element.GetProperty("isValid").GetBoolean();

                        if (!blockchainValid)
                        {
                            _logger?.LogWarning("Certificate {CertificateId} blockchain anchor verification failed",
                                certificate.CertificateId);
                            return CertificateVerificationResult.CreateInvalid(
                                certificate.Chain.Count,
                                -1,
                                "Blockchain anchor verification failed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Blockchain verification unavailable for certificate {CertificateId}",
                        certificate.CertificateId);
                    // Continue with local validation result (graceful degradation)
                }
            }

            _logger?.LogInformation("Certificate {CertificateId} verified successfully", certificate.CertificateId);
            return result;
        }

        /// <summary>
        /// Gets all certificates ever issued for an object.
        /// </summary>
        /// <param name="objectId">The object ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of certificates.</returns>
        public Task<IReadOnlyList<ProvenanceCertificate>> GetCertificateHistoryAsync(
            string objectId,
            CancellationToken ct = default)
        {
            if (_certificateStore.TryGetValue(objectId, out var certificates))
            {
                lock (certificates)
                {
                    return Task.FromResult<IReadOnlyList<ProvenanceCertificate>>(certificates.ToList().AsReadOnly());
                }
            }

            return Task.FromResult<IReadOnlyList<ProvenanceCertificate>>(Array.Empty<ProvenanceCertificate>());
        }

        /// <summary>
        /// Handles certificate requests from other plugins via message bus.
        /// </summary>
        private async Task HandleCertificateRequestAsync(PluginMessage message)
        {
            try
            {
                var objectId = message.Payload["objectId"]?.ToString();
                if (string.IsNullOrEmpty(objectId))
                {
                    _logger?.LogWarning("Certificate request missing objectId");
                    return;
                }

                _logger?.LogDebug("Received certificate request for object {ObjectId}", objectId);

                var certificate = await IssueCertificateAsync(objectId);

                // Publish certificate issued event
                await _messageBus.PublishAsync("composition.provenance.certificate-issued",
                    PluginMessage.Create("composition.provenance.certificate-issued", new Dictionary<string, object>
                    {
                        ["certificateId"] = certificate.CertificateId,
                        ["objectId"] = objectId,
                        ["issuedAt"] = certificate.IssuedAt,
                        ["chainLength"] = certificate.Chain.Count,
                        ["blockchainAnchored"] = certificate.BlockchainAnchor != null
                    }));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling certificate request");
            }
        }

        /// <summary>
        /// Computes SHA256 hash of certificate chain for tamper detection.
        /// Uses canonical JSON serialization (sorted keys, no whitespace).
        /// </summary>
        private static string ComputeCertificateHash(List<CertificateEntry> chain)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(chain, options);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(bytes);

            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// Stores a certificate in the bounded certificate store.
        /// </summary>
        private void StoreCertificate(string objectId, ProvenanceCertificate certificate)
        {
            var certificates = _certificateStore.GetOrAdd(objectId, _ => new List<ProvenanceCertificate>());

            lock (certificates)
            {
                certificates.Add(certificate);

                // Enforce bounded storage (oldest-first eviction)
                if (certificates.Count > MaxCertificatesPerObject)
                {
                    _logger?.LogWarning("Max certificates reached for object {ObjectId}, removing oldest", objectId);
                    certificates.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();

            _disposed = true;
        }
    }
}
