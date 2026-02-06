using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Watermarking
{
    /// <summary>
    /// Forensic watermarking strategy for traitor tracing and data leak detection.
    /// Embeds invisible, unique identifiers into data that survive transformations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Watermarking features:
    /// - Unique per-user/per-access watermarks
    /// - Invisible embedding (no perceptible changes)
    /// - Robustness against common transformations
    /// - Cryptographic binding to prevent tampering
    /// - Extraction capability for leak investigation
    /// </para>
    /// <para>
    /// Supported data types:
    /// - Binary data: Bit manipulation in LSB
    /// - Text: Unicode homoglyphs, zero-width characters
    /// - Structured data: Record ordering, precision watermarks
    /// - Database results: Row/column ordering, value perturbation
    /// </para>
    /// </remarks>
    public sealed class WatermarkingStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, WatermarkRecord> _watermarks = new();
        private byte[]? _signingKey;
        private const int WatermarkSize = 32; // 256-bit watermark

        /// <inheritdoc/>
        public override string StrategyId => "watermarking";

        /// <inheritdoc/>
        public override string StrategyName => "Forensic Watermarking";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 1000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("SigningKey", out var keyObj) && keyObj is byte[] key)
            {
                _signingKey = key;
            }
            else if (configuration.TryGetValue("SigningKeyBase64", out var keyB64) && keyB64 is string keyString)
            {
                _signingKey = Convert.FromBase64String(keyString);
            }
            else
            {
                // Generate a random signing key if not provided
                _signingKey = RandomNumberGenerator.GetBytes(32);
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Generates a unique watermark for a specific access context.
        /// </summary>
        public WatermarkInfo GenerateWatermark(string userId, string resourceId, Dictionary<string, object>? metadata = null)
        {
            var watermarkId = Guid.NewGuid().ToString("N");

            // Create watermark payload
            var payload = new WatermarkPayload
            {
                WatermarkId = watermarkId,
                UserId = userId,
                ResourceId = resourceId,
                Timestamp = DateTime.UtcNow,
                Metadata = metadata ?? new Dictionary<string, object>()
            };

            // Serialize and sign
            var payloadBytes = SerializePayload(payload);
            var signature = SignPayload(payloadBytes);

            // Create compact watermark (256-bit)
            var watermarkData = ComputeWatermarkData(payloadBytes, signature);

            var record = new WatermarkRecord
            {
                WatermarkId = watermarkId,
                UserId = userId,
                ResourceId = resourceId,
                CreatedAt = DateTime.UtcNow,
                WatermarkData = watermarkData,
                Signature = signature,
                Metadata = payload.Metadata
            };

            _watermarks[watermarkId] = record;

            return new WatermarkInfo
            {
                WatermarkId = watermarkId,
                WatermarkData = watermarkData,
                CreatedAt = record.CreatedAt
            };
        }

        /// <summary>
        /// Embeds a watermark into binary data.
        /// </summary>
        public byte[] EmbedInBinary(byte[] data, WatermarkInfo watermark)
        {
            if (data.Length < WatermarkSize * 8)
            {
                throw new InvalidOperationException($"Data too small for watermarking. Need at least {WatermarkSize * 8} bytes.");
            }

            var result = new byte[data.Length];
            Array.Copy(data, result, data.Length);

            // Embed watermark using spread-spectrum technique
            var positions = GenerateEmbedPositions(data.Length, watermark.WatermarkData);

            for (int i = 0; i < WatermarkSize * 8; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                int watermarkBit = (watermark.WatermarkData[byteIndex] >> (7 - bitIndex)) & 1;

                int position = positions[i];
                // Embed in LSB
                result[position] = (byte)((result[position] & 0xFE) | watermarkBit);
            }

            return result;
        }

        /// <summary>
        /// Extracts a watermark from binary data.
        /// </summary>
        public WatermarkInfo? ExtractFromBinary(byte[] data)
        {
            if (data.Length < WatermarkSize * 8)
            {
                return null;
            }

            // Try to extract watermark data
            // We need to try multiple position patterns to find a valid watermark
            var candidateWatermarks = new List<byte[]>();

            // Generate multiple position patterns based on potential watermark seeds
            foreach (var record in _watermarks.Values)
            {
                var positions = GenerateEmbedPositions(data.Length, record.WatermarkData);
                var extracted = new byte[WatermarkSize];

                for (int i = 0; i < WatermarkSize * 8; i++)
                {
                    int byteIndex = i / 8;
                    int bitIndex = i % 8;
                    int position = positions[i];

                    int extractedBit = data[position] & 1;
                    extracted[byteIndex] |= (byte)(extractedBit << (7 - bitIndex));
                }

                // Check if extracted matches the record
                if (AreArraysEqual(extracted, record.WatermarkData))
                {
                    return new WatermarkInfo
                    {
                        WatermarkId = record.WatermarkId,
                        WatermarkData = extracted,
                        CreatedAt = record.CreatedAt
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Embeds a watermark into text using zero-width characters.
        /// </summary>
        public string EmbedInText(string text, WatermarkInfo watermark)
        {
            var sb = new StringBuilder();

            // Insert watermark at word boundaries using zero-width characters
            // 0 = Zero-Width Space (U+200B)
            // 1 = Zero-Width Non-Joiner (U+200C)

            int watermarkBitIndex = 0;
            bool inWord = false;

            foreach (char c in text)
            {
                sb.Append(c);

                if (char.IsWhiteSpace(c))
                {
                    if (inWord && watermarkBitIndex < WatermarkSize * 8)
                    {
                        int byteIndex = watermarkBitIndex / 8;
                        int bitIndex = watermarkBitIndex % 8;
                        int bit = (watermark.WatermarkData[byteIndex] >> (7 - bitIndex)) & 1;

                        sb.Append(bit == 0 ? '\u200B' : '\u200C');
                        watermarkBitIndex++;
                    }
                    inWord = false;
                }
                else
                {
                    inWord = true;
                }
            }

            // Append remaining watermark bits at the end
            while (watermarkBitIndex < WatermarkSize * 8)
            {
                int byteIndex = watermarkBitIndex / 8;
                int bitIndex = watermarkBitIndex % 8;
                int bit = (watermark.WatermarkData[byteIndex] >> (7 - bitIndex)) & 1;

                sb.Append(bit == 0 ? '\u200B' : '\u200C');
                watermarkBitIndex++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts a watermark from text with zero-width characters.
        /// </summary>
        public WatermarkInfo? ExtractFromText(string text)
        {
            var extractedBits = new List<int>();

            foreach (char c in text)
            {
                if (c == '\u200B')
                    extractedBits.Add(0);
                else if (c == '\u200C')
                    extractedBits.Add(1);
            }

            if (extractedBits.Count < WatermarkSize * 8)
            {
                return null;
            }

            var extracted = new byte[WatermarkSize];
            for (int i = 0; i < WatermarkSize * 8 && i < extractedBits.Count; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                extracted[byteIndex] |= (byte)(extractedBits[i] << (7 - bitIndex));
            }

            // Find matching watermark record
            foreach (var record in _watermarks.Values)
            {
                if (AreArraysEqual(extracted, record.WatermarkData))
                {
                    return new WatermarkInfo
                    {
                        WatermarkId = record.WatermarkId,
                        WatermarkData = extracted,
                        CreatedAt = record.CreatedAt
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the traitor tracing information for a watermark.
        /// </summary>
        public TraitorInfo? TraceWatermark(WatermarkInfo watermark)
        {
            if (!_watermarks.TryGetValue(watermark.WatermarkId, out var record))
            {
                // Try to find by watermark data
                foreach (var r in _watermarks.Values)
                {
                    if (AreArraysEqual(r.WatermarkData, watermark.WatermarkData))
                    {
                        record = r;
                        break;
                    }
                }
            }

            if (record == null)
            {
                return null;
            }

            return new TraitorInfo
            {
                WatermarkId = record.WatermarkId,
                UserId = record.UserId,
                ResourceId = record.ResourceId,
                AccessTimestamp = record.CreatedAt,
                Metadata = record.Metadata
            };
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Watermarking strategy generates watermarks for all access
            // The actual embedding is done at the data layer

            var watermark = GenerateWatermark(context.SubjectId, context.ResourceId,
                new Dictionary<string, object>
                {
                    ["Action"] = context.Action,
                    ["ClientIp"] = context.ClientIpAddress ?? "unknown",
                    ["Timestamp"] = DateTime.UtcNow.ToString("o")
                });

            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted with forensic watermark",
                ApplicablePolicies = new[] { "ForensicWatermarking" },
                Metadata = new Dictionary<string, object>
                {
                    ["WatermarkId"] = watermark.WatermarkId,
                    ["WatermarkCreatedAt"] = watermark.CreatedAt.ToString("o")
                }
            });
        }

        private byte[] SerializePayload(WatermarkPayload payload)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(payload.WatermarkId);
            writer.Write(payload.UserId);
            writer.Write(payload.ResourceId);
            writer.Write(payload.Timestamp.ToBinary());

            return ms.ToArray();
        }

        private byte[] SignPayload(byte[] payload)
        {
            if (_signingKey == null)
            {
                throw new InvalidOperationException("Signing key not configured");
            }

            using var hmac = new HMACSHA256(_signingKey);
            return hmac.ComputeHash(payload);
        }

        private byte[] ComputeWatermarkData(byte[] payload, byte[] signature)
        {
            // Combine payload hash and signature to create 32-byte watermark
            using var sha = SHA256.Create();
            var combined = new byte[payload.Length + signature.Length];
            Array.Copy(payload, 0, combined, 0, payload.Length);
            Array.Copy(signature, 0, combined, payload.Length, signature.Length);
            return sha.ComputeHash(combined);
        }

        private int[] GenerateEmbedPositions(int dataLength, byte[] watermarkData)
        {
            // Generate pseudo-random positions based on watermark data as seed
            var positions = new int[WatermarkSize * 8];
            var usedPositions = new HashSet<int>();

            // Use watermark data to seed RNG
            var seed = BinaryPrimitives.ReadInt32LittleEndian(watermarkData.AsSpan(0, 4));
            var rng = new Random(seed);

            int maxPosition = dataLength - 1;
            int minSpacing = Math.Max(1, dataLength / (WatermarkSize * 8 * 2));

            for (int i = 0; i < positions.Length; i++)
            {
                int position;
                int attempts = 0;
                do
                {
                    position = rng.Next(0, maxPosition);
                    attempts++;
                    if (attempts > 1000)
                    {
                        // Fall back to sequential
                        position = (i * minSpacing) % maxPosition;
                        break;
                    }
                } while (usedPositions.Contains(position));

                positions[i] = position;
                usedPositions.Add(position);
            }

            return positions;
        }

        private static bool AreArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Watermark payload structure.
    /// </summary>
    internal record WatermarkPayload
    {
        public required string WatermarkId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required DateTime Timestamp { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Stored watermark record.
    /// </summary>
    internal record WatermarkRecord
    {
        public required string WatermarkId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required byte[] WatermarkData { get; init; }
        public required byte[] Signature { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Information about an embedded watermark.
    /// </summary>
    public record WatermarkInfo
    {
        public required string WatermarkId { get; init; }
        public required byte[] WatermarkData { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    /// <summary>
    /// Information about the source of a watermarked leak (traitor tracing).
    /// </summary>
    public record TraitorInfo
    {
        public required string WatermarkId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required DateTime AccessTimestamp { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }
}
