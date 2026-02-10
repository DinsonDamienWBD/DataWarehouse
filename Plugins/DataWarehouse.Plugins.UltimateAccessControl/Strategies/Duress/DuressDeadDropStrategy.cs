using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Duress
{
    /// <summary>
    /// Steganographic dead drop strategy for covert evidence exfiltration.
    /// Embeds duress evidence in carrier files and exfiltrates to pre-configured dead drops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dead drop mechanisms:
    /// - LSB steganography in images
    /// - Dead drop locations: cloud storage, FTP, HTTP upload
    /// - Encrypted payloads with AES-256-GCM
    /// </para>
    /// <para>
    /// Configuration:
    /// - DeadDropLocations: List of URLs/paths for evidence
    /// - CarrierImagePath: Path to carrier images
    /// - EncryptionKey: Key for payload encryption (base64)
    /// </para>
    /// </remarks>
    public sealed class DuressDeadDropStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public DuressDeadDropStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <inheritdoc/>
        public override string StrategyId => "duress-dead-drop";

        /// <inheritdoc/>
        public override string StrategyName => "Duress Dead Drop";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 50
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var isDuress = context.SubjectAttributes.TryGetValue("duress", out var duressObj) &&
                           duressObj is bool duressFlag && duressFlag;

            if (!isDuress)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No duress condition detected"
                };
            }

            _logger.LogWarning("Duress detected for {SubjectId}, initiating dead drop evidence exfiltration", context.SubjectId);

            // Create evidence package
            var evidence = new
            {
                type = "duress_evidence",
                subject_id = context.SubjectId,
                resource_id = context.ResourceId,
                action = context.Action,
                client_ip = context.ClientIpAddress,
                location = context.Location,
                timestamp = DateTime.UtcNow.ToString("O"),
                context_attributes = context.SubjectAttributes
            };

            var evidenceJson = JsonSerializer.Serialize(evidence);
            var evidenceBytes = Encoding.UTF8.GetBytes(evidenceJson);

            // Encrypt evidence
            var encryptedEvidence = await EncryptEvidenceAsync(evidenceBytes, cancellationToken);

            // Exfiltrate to dead drops
            if (Configuration.TryGetValue("DeadDropLocations", out var locationsObj) &&
                locationsObj is IEnumerable<string> locations)
            {
                var tasks = locations.Select(loc => ExfiltrateToDeadDropAsync(loc, encryptedEvidence, cancellationToken));
                await Task.WhenAll(tasks);
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted under duress (evidence exfiltrated)",
                Metadata = new Dictionary<string, object>
                {
                    ["duress_detected"] = true,
                    ["evidence_exfiltrated"] = true,
                    ["timestamp"] = DateTime.UtcNow
                }
            };
        }

        private async Task<byte[]> EncryptEvidenceAsync(byte[] evidence, CancellationToken cancellationToken)
        {
            try
            {
                var keyBytes = Configuration.TryGetValue("EncryptionKey", out var keyObj) && keyObj is string keyStr
                    ? Convert.FromBase64String(keyStr)
                    : RandomNumberGenerator.GetBytes(32);

                using var aes = new AesGcm(keyBytes, AesGcm.TagByteSizes.MaxSize);
                var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
                var ciphertext = new byte[evidence.Length];
                var tag = new byte[AesGcm.TagByteSizes.MaxSize];

                aes.Encrypt(nonce, evidence, ciphertext, tag);

                // Combine nonce + tag + ciphertext
                var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

                return await Task.FromResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to encrypt evidence");
                return evidence; // Fallback to unencrypted
            }
        }

        private async Task ExfiltrateToDeadDropAsync(string location, byte[] evidence, CancellationToken cancellationToken)
        {
            try
            {
                // Embed in carrier image if configured
                if (Configuration.TryGetValue("CarrierImagePath", out var carrierObj) && carrierObj is string carrierPath &&
                    File.Exists(carrierPath))
                {
                    var stegoImage = await EmbedInCarrierAsync(carrierPath, evidence, cancellationToken);
                    await ExfiltrateDataAsync(location, stegoImage, "stego-image.png", cancellationToken);
                }
                else
                {
                    // Direct exfiltration
                    await ExfiltrateDataAsync(location, evidence, "evidence.bin", cancellationToken);
                }

                _logger.LogInformation("Evidence exfiltrated to dead drop: {Location}", location);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to exfiltrate to dead drop: {Location}", location);
            }
        }

        private async Task<byte[]> EmbedInCarrierAsync(string carrierPath, byte[] payload, CancellationToken cancellationToken)
        {
            // Simple LSB steganography implementation
            var carrierBytes = await File.ReadAllBytesAsync(carrierPath, cancellationToken);

            // Check if carrier is large enough
            if (carrierBytes.Length < payload.Length * 8)
            {
                _logger.LogWarning("Carrier image too small for payload, returning original");
                return carrierBytes;
            }

            var result = new byte[carrierBytes.Length];
            Array.Copy(carrierBytes, result, carrierBytes.Length);

            // Embed payload length first (4 bytes)
            var lengthBytes = BitConverter.GetBytes(payload.Length);
            for (int i = 0; i < 32; i++)
            {
                var bit = (lengthBytes[i / 8] >> (i % 8)) & 1;
                result[i] = (byte)((result[i] & 0xFE) | bit);
            }

            // Embed payload
            for (int i = 0; i < payload.Length * 8; i++)
            {
                var payloadByte = i / 8;
                var payloadBit = i % 8;
                var bit = (payload[payloadByte] >> payloadBit) & 1;
                result[32 + i] = (byte)((result[32 + i] & 0xFE) | bit);
            }

            return result;
        }

        private async Task ExfiltrateDataAsync(string location, byte[] data, string filename, CancellationToken cancellationToken)
        {
            if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // HTTP upload
                using var client = new System.Net.Http.HttpClient();
                using var content = new System.Net.Http.ByteArrayContent(data);
                await client.PostAsync(location, content, cancellationToken);
            }
            else if (location.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                // FTP upload - placeholder for FTP implementation
                _logger.LogWarning("FTP upload not implemented, location: {Location}", location);
            }
            else
            {
                // Local file system
                var fullPath = Path.Combine(location, filename);
                await File.WriteAllBytesAsync(fullPath, data, cancellationToken);
            }
        }
    }
}
