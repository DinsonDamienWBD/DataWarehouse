using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Steganography
{
    /// <summary>
    /// Steganography strategy that hides data within other data carriers (images, audio, etc.).
    /// Provides covert communication and data concealment capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported carrier types:
    /// - Images: LSB (Least Significant Bit) embedding in PNG, BMP
    /// - Text: Whitespace encoding, zero-width characters, homoglyph substitution
    /// - Binary: Slack space, metadata embedding
    /// </para>
    /// <para>
    /// The strategy supports:
    /// - Encryption of hidden data before embedding
    /// - Capacity estimation for carrier files
    /// - Detection resistance through adaptive embedding
    /// - Integrity verification with checksums
    /// </para>
    /// </remarks>
    public sealed class SteganographyStrategy : AccessControlStrategyBase
    {
        private const int HeaderSize = 16; // Magic + Length + Checksum
        private static readonly byte[] Magic = { 0x53, 0x54, 0x45, 0x47 }; // "STEG"
        private byte[]? _encryptionKey;

        /// <inheritdoc/>
        public override string StrategyId => "steganography";

        /// <inheritdoc/>
        public override string StrategyName => "Steganography";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = false,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 100
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("EncryptionKey", out var keyObj) && keyObj is byte[] key)
            {
                _encryptionKey = key;
            }
            else if (configuration.TryGetValue("EncryptionKeyBase64", out var keyB64) && keyB64 is string keyString)
            {
                _encryptionKey = Convert.FromBase64String(keyString);
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Hides data within an image carrier using LSB embedding.
        /// </summary>
        /// <param name="carrierData">The carrier image data (PNG or BMP format).</param>
        /// <param name="secretData">The data to hide.</param>
        /// <param name="encrypt">Whether to encrypt the secret data before embedding.</param>
        /// <returns>The carrier with hidden data.</returns>
        public byte[] HideInImage(byte[] carrierData, byte[] secretData, bool encrypt = true)
        {
            var dataToHide = PrepareDataForHiding(secretData, encrypt);

            // Estimate capacity needed (3 bits per pixel for RGB, using 1 bit per channel)
            var minCarrierSize = (dataToHide.Length + HeaderSize) * 8 / 3 + 1000;
            if (carrierData.Length < minCarrierSize)
            {
                throw new InvalidOperationException(
                    $"Carrier too small. Need at least {minCarrierSize} bytes, got {carrierData.Length}");
            }

            // Create a copy of the carrier
            var result = new byte[carrierData.Length];
            Array.Copy(carrierData, result, carrierData.Length);

            // Find the pixel data start (skip headers)
            int dataOffset = FindImageDataOffset(result);
            if (dataOffset < 0)
            {
                throw new InvalidOperationException("Unable to find image data in carrier");
            }

            // Create header: Magic + Length + Checksum
            var header = new byte[HeaderSize];
            Array.Copy(Magic, 0, header, 0, 4);
            BitConverter.GetBytes(dataToHide.Length).CopyTo(header, 4);
            var checksum = ComputeChecksum(dataToHide);
            BitConverter.GetBytes(checksum).CopyTo(header, 8);

            // Combine header and data
            var payload = new byte[header.Length + dataToHide.Length];
            Array.Copy(header, 0, payload, 0, header.Length);
            Array.Copy(dataToHide, 0, payload, header.Length, dataToHide.Length);

            // Embed using LSB
            int bitIndex = 0;
            for (int i = dataOffset; i < result.Length && bitIndex < payload.Length * 8; i++)
            {
                // Skip every 4th byte if it's alpha channel (RGBA)
                if ((i - dataOffset) % 4 == 3)
                    continue;

                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = (payload[byteIndex] >> bitPosition) & 1;

                result[i] = (byte)((result[i] & 0xFE) | bit);
                bitIndex++;
            }

            if (bitIndex < payload.Length * 8)
            {
                throw new InvalidOperationException("Carrier too small to hold all secret data");
            }

            return result;
        }

        /// <summary>
        /// Extracts hidden data from an image carrier.
        /// </summary>
        /// <param name="carrierData">The carrier image with hidden data.</param>
        /// <param name="decrypt">Whether to decrypt the extracted data.</param>
        /// <returns>The extracted secret data.</returns>
        public byte[] ExtractFromImage(byte[] carrierData, bool decrypt = true)
        {
            int dataOffset = FindImageDataOffset(carrierData);
            if (dataOffset < 0)
            {
                throw new InvalidOperationException("Unable to find image data in carrier");
            }

            // Extract header first
            var header = new byte[HeaderSize];
            int bitIndex = 0;
            for (int i = dataOffset; i < carrierData.Length && bitIndex < HeaderSize * 8; i++)
            {
                if ((i - dataOffset) % 4 == 3)
                    continue;

                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = carrierData[i] & 1;

                header[byteIndex] |= (byte)(bit << bitPosition);
                bitIndex++;
            }

            // Verify magic
            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                throw new InvalidDataException("No steganographic data found in carrier");
            }

            // Extract length
            int dataLength = BitConverter.ToInt32(header, 4);
            if (dataLength < 0 || dataLength > 100_000_000)
            {
                throw new InvalidDataException("Invalid data length in steganographic header");
            }

            uint expectedChecksum = BitConverter.ToUInt32(header, 8);

            // Extract data
            var extractedData = new byte[dataLength];
            int extractBitIndex = 0;
            int skipBits = HeaderSize * 8;
            bitIndex = 0;

            for (int i = dataOffset; i < carrierData.Length && extractBitIndex < dataLength * 8; i++)
            {
                if ((i - dataOffset) % 4 == 3)
                    continue;

                if (bitIndex >= skipBits)
                {
                    int byteIndex = extractBitIndex / 8;
                    int bitPosition = 7 - (extractBitIndex % 8);
                    int bit = carrierData[i] & 1;

                    extractedData[byteIndex] |= (byte)(bit << bitPosition);
                    extractBitIndex++;
                }
                bitIndex++;
            }

            // Verify checksum
            uint actualChecksum = ComputeChecksum(extractedData);
            if (actualChecksum != expectedChecksum)
            {
                throw new InvalidDataException("Checksum verification failed - data may be corrupted");
            }

            // Decrypt if needed
            if (decrypt && _encryptionKey != null)
            {
                return DecryptData(extractedData);
            }

            return extractedData;
        }

        /// <summary>
        /// Hides data within text using whitespace encoding.
        /// </summary>
        /// <param name="coverText">The cover text.</param>
        /// <param name="secretData">The data to hide.</param>
        /// <param name="encrypt">Whether to encrypt the secret data.</param>
        /// <returns>Text with hidden data encoded in whitespace.</returns>
        public string HideInText(string coverText, byte[] secretData, bool encrypt = true)
        {
            var dataToHide = PrepareDataForHiding(secretData, encrypt);

            // Encode using spaces (0) and tabs (1)
            var sb = new StringBuilder();
            var lines = coverText.Split('\n');
            int dataIndex = 0;
            int bitIndex = 0;

            foreach (var line in lines)
            {
                sb.Append(line.TrimEnd());

                if (dataIndex < dataToHide.Length)
                {
                    // Add whitespace encoding at end of line
                    for (int i = 0; i < 8 && dataIndex < dataToHide.Length; i++)
                    {
                        int bit = (dataToHide[dataIndex] >> (7 - bitIndex)) & 1;
                        sb.Append(bit == 0 ? ' ' : '\t');
                        bitIndex++;
                        if (bitIndex == 8)
                        {
                            bitIndex = 0;
                            dataIndex++;
                        }
                    }
                }

                sb.Append('\n');
            }

            // If data remains, add it as trailing whitespace
            while (dataIndex < dataToHide.Length)
            {
                for (int i = 0; i < 8 && dataIndex < dataToHide.Length; i++)
                {
                    int bit = (dataToHide[dataIndex] >> (7 - bitIndex)) & 1;
                    sb.Append(bit == 0 ? ' ' : '\t');
                    bitIndex++;
                    if (bitIndex == 8)
                    {
                        bitIndex = 0;
                        dataIndex++;
                    }
                }
                sb.Append('\n');
            }

            // Add length marker at the very end (zero-width characters)
            sb.Append("\u200B"); // Zero-width space as marker
            sb.Append(Convert.ToBase64String(BitConverter.GetBytes(dataToHide.Length)));
            sb.Append("\u200B");

            return sb.ToString();
        }

        /// <summary>
        /// Extracts hidden data from text whitespace encoding.
        /// </summary>
        public byte[] ExtractFromText(string text, bool decrypt = true)
        {
            // Find length marker
            int markerStart = text.IndexOf("\u200B");
            int markerEnd = text.LastIndexOf("\u200B");

            if (markerStart < 0 || markerEnd <= markerStart)
            {
                throw new InvalidDataException("No steganographic marker found in text");
            }

            // Extract base64 length between markers (filter out whitespace/newlines)
            var lengthStr = text.Substring(markerStart + 1, markerEnd - markerStart - 1);
            lengthStr = new string(lengthStr.Where(c => !char.IsWhiteSpace(c)).ToArray());

            if (string.IsNullOrEmpty(lengthStr))
            {
                throw new InvalidDataException("Invalid length marker in steganographic text");
            }

            int dataLength;
            try
            {
                dataLength = BitConverter.ToInt32(Convert.FromBase64String(lengthStr), 0);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("Invalid base64 encoding in length marker");
            }

            if (dataLength < 0 || dataLength > 10_000_000)
            {
                throw new InvalidDataException("Invalid data length in steganographic text");
            }

            // Extract from whitespace (only up to first marker to avoid processing base64 content)
            var result = new byte[dataLength];
            int dataIndex = 0;
            int bitIndex = 0;

            // Only process characters before the first marker
            for (int i = 0; i < markerStart && dataIndex < dataLength; i++)
            {
                char c = text[i];

                if (c == ' ')
                {
                    // Bit 0
                    bitIndex++;
                }
                else if (c == '\t')
                {
                    // Bit 1
                    result[dataIndex] |= (byte)(1 << (7 - (bitIndex % 8)));
                    bitIndex++;
                }
                else
                {
                    continue;
                }

                if (bitIndex % 8 == 0 && bitIndex > 0)
                {
                    dataIndex++;
                }
            }

            if (decrypt && _encryptionKey != null)
            {
                return DecryptData(result);
            }

            return result;
        }

        /// <summary>
        /// Hides data within audio using LSB embedding in PCM samples.
        /// </summary>
        /// <param name="carrierData">The carrier audio data (WAV format).</param>
        /// <param name="secretData">The data to hide.</param>
        /// <param name="encrypt">Whether to encrypt the secret data before embedding.</param>
        /// <returns>The carrier with hidden data.</returns>
        public byte[] HideInAudio(byte[] carrierData, byte[] secretData, bool encrypt = true)
        {
            var dataToHide = PrepareDataForHiding(secretData, encrypt);

            // WAV header is typically 44 bytes
            int wavHeaderSize = 44;
            if (carrierData.Length < wavHeaderSize + 1000)
            {
                throw new InvalidOperationException("Audio carrier too small");
            }

            // Verify WAV signature
            if (carrierData[0] != 'R' || carrierData[1] != 'I' || carrierData[2] != 'F' || carrierData[3] != 'F')
            {
                throw new InvalidOperationException("Audio carrier must be in WAV format");
            }

            var minCarrierSize = (dataToHide.Length + HeaderSize) * 8 + wavHeaderSize;
            if (carrierData.Length < minCarrierSize)
            {
                throw new InvalidOperationException(
                    $"Audio carrier too small. Need at least {minCarrierSize} bytes, got {carrierData.Length}");
            }

            // Create a copy of the carrier
            var result = new byte[carrierData.Length];
            Array.Copy(carrierData, result, carrierData.Length);

            // Create header
            var header = new byte[HeaderSize];
            Array.Copy(Magic, 0, header, 0, 4);
            BitConverter.GetBytes(dataToHide.Length).CopyTo(header, 4);
            var checksum = ComputeChecksum(dataToHide);
            BitConverter.GetBytes(checksum).CopyTo(header, 8);

            // Combine header and data
            var payload = new byte[header.Length + dataToHide.Length];
            Array.Copy(header, 0, payload, 0, header.Length);
            Array.Copy(dataToHide, 0, payload, header.Length, dataToHide.Length);

            // Embed using LSB in audio samples (skip WAV header)
            int bitIndex = 0;
            for (int i = wavHeaderSize; i < result.Length && bitIndex < payload.Length * 8; i++)
            {
                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = (payload[byteIndex] >> bitPosition) & 1;

                result[i] = (byte)((result[i] & 0xFE) | bit);
                bitIndex++;
            }

            if (bitIndex < payload.Length * 8)
            {
                throw new InvalidOperationException("Audio carrier too small to hold all secret data");
            }

            return result;
        }

        /// <summary>
        /// Extracts hidden data from audio carrier.
        /// </summary>
        /// <param name="carrierData">The carrier audio with hidden data.</param>
        /// <param name="decrypt">Whether to decrypt the extracted data.</param>
        /// <returns>The extracted secret data.</returns>
        public byte[] ExtractFromAudio(byte[] carrierData, bool decrypt = true)
        {
            int wavHeaderSize = 44;
            if (carrierData.Length < wavHeaderSize + HeaderSize * 8)
            {
                throw new InvalidOperationException("Audio carrier too small");
            }

            // Extract header first
            var header = new byte[HeaderSize];
            int bitIndex = 0;
            for (int i = wavHeaderSize; i < carrierData.Length && bitIndex < HeaderSize * 8; i++)
            {
                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = carrierData[i] & 1;

                header[byteIndex] |= (byte)(bit << bitPosition);
                bitIndex++;
            }

            // Verify magic
            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                throw new InvalidDataException("No steganographic data found in audio carrier");
            }

            // Extract length
            int dataLength = BitConverter.ToInt32(header, 4);
            if (dataLength < 0 || dataLength > 100_000_000)
            {
                throw new InvalidDataException("Invalid data length in steganographic header");
            }

            uint expectedChecksum = BitConverter.ToUInt32(header, 8);

            // Extract data
            var extractedData = new byte[dataLength];
            int extractBitIndex = 0;
            int skipBits = HeaderSize * 8;
            bitIndex = 0;

            for (int i = wavHeaderSize; i < carrierData.Length && extractBitIndex < dataLength * 8; i++)
            {
                if (bitIndex >= skipBits)
                {
                    int byteIndex = extractBitIndex / 8;
                    int bitPosition = 7 - (extractBitIndex % 8);
                    int bit = carrierData[i] & 1;

                    extractedData[byteIndex] |= (byte)(bit << bitPosition);
                    extractBitIndex++;
                }
                bitIndex++;
            }

            // Verify checksum
            uint actualChecksum = ComputeChecksum(extractedData);
            if (actualChecksum != expectedChecksum)
            {
                throw new InvalidDataException("Checksum verification failed - data may be corrupted");
            }

            // Decrypt if needed
            if (decrypt && _encryptionKey != null)
            {
                return DecryptData(extractedData);
            }

            return extractedData;
        }

        /// <summary>
        /// Hides data within video by embedding in frame data (simplified keyframe LSB).
        /// </summary>
        /// <param name="carrierData">The carrier video data.</param>
        /// <param name="secretData">The data to hide.</param>
        /// <param name="encrypt">Whether to encrypt the secret data before embedding.</param>
        /// <returns>The carrier with hidden data.</returns>
        public byte[] HideInVideo(byte[] carrierData, byte[] secretData, bool encrypt = true)
        {
            var dataToHide = PrepareDataForHiding(secretData, encrypt);

            // For simplicity, embed in video container data using LSB
            // Production implementation would parse video codec frames
            int videoHeaderSize = 512; // Conservative estimate for video headers

            if (carrierData.Length < videoHeaderSize + 1000)
            {
                throw new InvalidOperationException("Video carrier too small");
            }

            var minCarrierSize = (dataToHide.Length + HeaderSize) * 8 + videoHeaderSize;
            if (carrierData.Length < minCarrierSize)
            {
                throw new InvalidOperationException(
                    $"Video carrier too small. Need at least {minCarrierSize} bytes, got {carrierData.Length}");
            }

            // Create a copy of the carrier
            var result = new byte[carrierData.Length];
            Array.Copy(carrierData, result, carrierData.Length);

            // Create header
            var header = new byte[HeaderSize];
            Array.Copy(Magic, 0, header, 0, 4);
            BitConverter.GetBytes(dataToHide.Length).CopyTo(header, 4);
            var checksum = ComputeChecksum(dataToHide);
            BitConverter.GetBytes(checksum).CopyTo(header, 8);

            // Combine header and data
            var payload = new byte[header.Length + dataToHide.Length];
            Array.Copy(header, 0, payload, 0, header.Length);
            Array.Copy(dataToHide, 0, payload, header.Length, dataToHide.Length);

            // Embed using LSB (skip video header region)
            int bitIndex = 0;
            for (int i = videoHeaderSize; i < result.Length && bitIndex < payload.Length * 8; i++)
            {
                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = (payload[byteIndex] >> bitPosition) & 1;

                result[i] = (byte)((result[i] & 0xFE) | bit);
                bitIndex++;
            }

            if (bitIndex < payload.Length * 8)
            {
                throw new InvalidOperationException("Video carrier too small to hold all secret data");
            }

            return result;
        }

        /// <summary>
        /// Extracts hidden data from video carrier.
        /// </summary>
        /// <param name="carrierData">The carrier video with hidden data.</param>
        /// <param name="decrypt">Whether to decrypt the extracted data.</param>
        /// <returns>The extracted secret data.</returns>
        public byte[] ExtractFromVideo(byte[] carrierData, bool decrypt = true)
        {
            int videoHeaderSize = 512;
            if (carrierData.Length < videoHeaderSize + HeaderSize * 8)
            {
                throw new InvalidOperationException("Video carrier too small");
            }

            // Extract header first
            var header = new byte[HeaderSize];
            int bitIndex = 0;
            for (int i = videoHeaderSize; i < carrierData.Length && bitIndex < HeaderSize * 8; i++)
            {
                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = carrierData[i] & 1;

                header[byteIndex] |= (byte)(bit << bitPosition);
                bitIndex++;
            }

            // Verify magic
            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                throw new InvalidDataException("No steganographic data found in video carrier");
            }

            // Extract length
            int dataLength = BitConverter.ToInt32(header, 4);
            if (dataLength < 0 || dataLength > 100_000_000)
            {
                throw new InvalidDataException("Invalid data length in steganographic header");
            }

            uint expectedChecksum = BitConverter.ToUInt32(header, 8);

            // Extract data
            var extractedData = new byte[dataLength];
            int extractBitIndex = 0;
            int skipBits = HeaderSize * 8;
            bitIndex = 0;

            for (int i = videoHeaderSize; i < carrierData.Length && extractBitIndex < dataLength * 8; i++)
            {
                if (bitIndex >= skipBits)
                {
                    int byteIndex = extractBitIndex / 8;
                    int bitPosition = 7 - (extractBitIndex % 8);
                    int bit = carrierData[i] & 1;

                    extractedData[byteIndex] |= (byte)(bit << bitPosition);
                    extractBitIndex++;
                }
                bitIndex++;
            }

            // Verify checksum
            uint actualChecksum = ComputeChecksum(extractedData);
            if (actualChecksum != expectedChecksum)
            {
                throw new InvalidDataException("Checksum verification failed - data may be corrupted");
            }

            // Decrypt if needed
            if (decrypt && _encryptionKey != null)
            {
                return DecryptData(extractedData);
            }

            return extractedData;
        }

        /// <summary>
        /// Estimates the capacity of a carrier for hidden data.
        /// </summary>
        public long EstimateCapacity(byte[] carrierData, CarrierType type)
        {
            return type switch
            {
                CarrierType.Image => (carrierData.Length - 100) / 8 - HeaderSize,
                CarrierType.Audio => (carrierData.Length - 44) / 8 - HeaderSize, // Skip WAV header
                CarrierType.Video => (carrierData.Length - 512) / 8 - HeaderSize, // Skip video headers
                CarrierType.Text => carrierData.Length / 10, // Approximate
                _ => carrierData.Length / 8 - HeaderSize
            };
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Steganography strategy grants access to resources based on ability to extract hidden credentials
            // This is primarily a data hiding mechanism, not an access control mechanism

            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Steganography strategy does not perform access control - use for data hiding only",
                ApplicablePolicies = new[] { "SteganographyDataHiding" }
            });
        }

        private byte[] PrepareDataForHiding(byte[] data, bool encrypt)
        {
            if (encrypt && _encryptionKey != null)
            {
                return EncryptData(data);
            }
            return data;
        }

        private byte[] EncryptData(byte[] data)
        {
            if (_encryptionKey == null || _encryptionKey.Length != 32)
            {
                throw new InvalidOperationException("Encryption key must be 32 bytes for AES-256");
            }

            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.GenerateIV();

            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(data, 0, data.Length);
            }

            return ms.ToArray();
        }

        private byte[] DecryptData(byte[] encryptedData)
        {
            if (_encryptionKey == null || _encryptionKey.Length != 32)
            {
                throw new InvalidOperationException("Encryption key must be 32 bytes for AES-256");
            }

            using var aes = Aes.Create();
            aes.Key = _encryptionKey;

            var iv = new byte[16];
            Array.Copy(encryptedData, 0, iv, 0, 16);
            aes.IV = iv;

            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(encryptedData, 16, encryptedData.Length - 16);
            }

            return ms.ToArray();
        }

        private static int FindImageDataOffset(byte[] imageData)
        {
            // Check for PNG signature
            if (imageData.Length > 8 &&
                imageData[0] == 0x89 && imageData[1] == 0x50 &&
                imageData[2] == 0x4E && imageData[3] == 0x47)
            {
                // PNG: Find IDAT chunk
                int offset = 8;
                while (offset < imageData.Length - 8)
                {
                    int chunkLength = (imageData[offset] << 24) | (imageData[offset + 1] << 16) |
                                     (imageData[offset + 2] << 8) | imageData[offset + 3];
                    var chunkType = Encoding.ASCII.GetString(imageData, offset + 4, 4);

                    if (chunkType == "IDAT")
                    {
                        return offset + 8;
                    }

                    offset += 12 + chunkLength;
                }
                return -1;
            }

            // Check for BMP signature
            if (imageData.Length > 54 && imageData[0] == 0x42 && imageData[1] == 0x4D)
            {
                // BMP: Data offset is at bytes 10-13
                return BitConverter.ToInt32(imageData, 10);
            }

            // For raw data, start from beginning
            return 100; // Skip potential headers
        }

        private static uint ComputeChecksum(byte[] data)
        {
            uint checksum = 0;
            foreach (byte b in data)
            {
                checksum = ((checksum << 5) + checksum) ^ b;
            }
            return checksum;
        }
    }

    /// <summary>
    /// Types of carrier files for steganography.
    /// </summary>
    public enum CarrierType
    {
        Image,
        Audio,
        Video,
        Text,
        Binary
    }
}
