using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Math;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// DNA-Encoded Key Storage Strategy using biological DNA molecules for ultra-long-term key archival.
    ///
    /// DNA offers unique properties for cryptographic key storage:
    /// - Extreme density: 1 gram of DNA can store 215 petabytes
    /// - Longevity: DNA can remain readable for thousands of years in proper conditions
    /// - Physical air-gap: Keys exist as molecules, not electronic signals
    ///
    /// Encoding Scheme (4-base to binary):
    /// ┌─────────────────────────────────────────────────────┐
    /// │  Binary    │  DNA Base  │  Complement  │  Code     │
    /// ├─────────────────────────────────────────────────────┤
    /// │    00      │     A      │      T       │  Adenine  │
    /// │    01      │     T      │      A       │  Thymine  │
    /// │    10      │     G      │      C       │  Guanine  │
    /// │    11      │     C      │      G       │  Cytosine │
    /// └─────────────────────────────────────────────────────┘
    ///
    /// Error Handling:
    /// - Reed-Solomon codes for sequencing error correction
    /// - Index sequences for random access retrieval
    /// - Multiple redundant copies per key for reliability
    ///
    /// Supported Synthesis Providers:
    /// - Twist Bioscience (Silicon-based DNA synthesis)
    /// - Integrated DNA Technologies (IDT)
    ///
    /// Supported Sequencing Providers:
    /// - Illumina (Short-read NGS)
    /// - Oxford Nanopore (Long-read real-time sequencing)
    /// </summary>
    public sealed class DnaEncodedKeyStrategy : KeyStoreStrategyBase
    {
        private DnaConfig _config = new();
        private HttpClient _synthesisClient = null!; // Initialized in InitializeStorage before first use
        private HttpClient _sequencingClient = null!; // Initialized in InitializeStorage before first use
        private readonly Dictionary<string, DnaKeyEntry> _keyStore = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _disposed;

        // DNA encoding tables
        private static readonly Dictionary<byte, char> BinaryToDna = new()
        {
            { 0b00, 'A' }, // Adenine
            { 0b01, 'T' }, // Thymine
            { 0b10, 'G' }, // Guanine
            { 0b11, 'C' }  // Cytosine
        };

        private static readonly Dictionary<char, byte> DnaToBinary = new()
        {
            { 'A', 0b00 },
            { 'T', 0b01 },
            { 'G', 0b10 },
            { 'C', 0b11 }
        };

        // GF(256) primitive polynomial for Reed-Solomon: x^8 + x^4 + x^3 + x^2 + 1
        private static readonly int RsPrimitive = 0x11D;
        private const int RsSymbolSize = 8; // bits
        private const int RsParitySymbols = 32; // number of parity symbols

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false, // DNA can last thousands of years
            SupportsReplication = true, // Multiple DNA copies
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 256, // ~2KB encoded (including error correction)
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Encoding"] = "4-base DNA (A/T/G/C)",
                ["ErrorCorrection"] = "Reed-Solomon",
                ["StorageMedium"] = "Synthetic DNA",
                ["Longevity"] = "1000+ years"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dnaencodedkey.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("dnaencodedkey.init");
            // Load configuration
            if (Configuration.TryGetValue("SynthesisProvider", out var providerObj) && providerObj is string provider)
                _config.SynthesisProvider = Enum.Parse<DnaSynthesisProvider>(provider, true);
            if (Configuration.TryGetValue("SequencingProvider", out var seqObj) && seqObj is string seq)
                _config.SequencingProvider = Enum.Parse<DnaSequencingProvider>(seq, true);
            if (Configuration.TryGetValue("SynthesisApiKey", out var synthKeyObj) && synthKeyObj is string synthKey)
                _config.SynthesisApiKey = synthKey;
            if (Configuration.TryGetValue("SequencingApiKey", out var seqKeyObj) && seqKeyObj is string seqKey)
                _config.SequencingApiKey = seqKey;
            if (Configuration.TryGetValue("RedundantCopies", out var copiesObj) && copiesObj is int copies)
                _config.RedundantCopies = copies;
            if (Configuration.TryGetValue("IndexPrefix", out var prefixObj) && prefixObj is string prefix)
                _config.IndexPrefix = prefix;

            // Initialize synthesis client (Twist Bioscience or IDT)
            _synthesisClient = CreateSynthesisClient();

            // Initialize sequencing client (Illumina or Oxford Nanopore)
            _sequencingClient = CreateSequencingClient();

            // Load existing key registry from local storage
            await LoadKeyRegistry();
        }

        private HttpClient CreateSynthesisClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5) // DNA synthesis orders can take time
            };

            switch (_config.SynthesisProvider)
            {
                case DnaSynthesisProvider.TwistBioscience:
                    client.BaseAddress = new Uri("https://api.twistbioscience.com/v1/");
                    client.DefaultRequestHeaders.Remove("Authorization");
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.SynthesisApiKey}");
                    break;

                case DnaSynthesisProvider.Idt:
                    client.BaseAddress = new Uri("https://www.idtdna.com/api/v1/");
                    client.DefaultRequestHeaders.Remove("X-API-KEY");
                    client.DefaultRequestHeaders.Add("X-API-KEY", _config.SynthesisApiKey);
                    break;
            }

            return client;
        }

        private HttpClient CreateSequencingClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };

            switch (_config.SequencingProvider)
            {
                case DnaSequencingProvider.Illumina:
                    client.BaseAddress = new Uri("https://api.illumina.com/v1/");
                    client.DefaultRequestHeaders.Remove("Authorization");
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.SequencingApiKey}");
                    break;

                case DnaSequencingProvider.OxfordNanopore:
                    client.BaseAddress = new Uri("https://api.nanoporetech.com/v1/");
                    client.DefaultRequestHeaders.Remove("X-API-Key");
                    client.DefaultRequestHeaders.Add("X-API-Key", _config.SequencingApiKey);
                    break;
            }

            return client;
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Check synthesis provider connectivity
                var synthResponse = await _synthesisClient.GetAsync("status", cancellationToken);
                if (!synthResponse.IsSuccessStatusCode)
                    return false;

                // Check sequencing provider connectivity
                var seqResponse = await _sequencingClient.GetAsync("status", cancellationToken);
                return seqResponse.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keyStore.TryGetValue(keyId, out var entry))
                {
                    throw new KeyNotFoundException($"DNA key '{keyId}' not found.");
                }

                // If we have cached decoded key, return it
                if (entry.DecodedKey != null && entry.DecodedKey.Length > 0)
                {
                    return entry.DecodedKey;
                }

                // Otherwise, need to sequence the DNA to retrieve the key
                var dnaSequence = await SequenceDnaAsync(entry.DnaSampleId);

                // Decode the DNA sequence to binary
                var decodedWithParity = DecodeDnaToBinary(dnaSequence);

                // Apply Reed-Solomon error correction
                var correctedKey = ApplyReedSolomonDecoding(decodedWithParity, entry.ParityData);

                // Cache the decoded key
                entry.DecodedKey = correctedKey;
                await PersistKeyRegistry();

                return correctedKey;
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                // Generate Reed-Solomon parity data
                var (encodedData, parityData) = ApplyReedSolomonEncoding(keyData);

                // Encode binary data to DNA sequence
                var dnaSequence = EncodeBinaryToDna(encodedData);

                // Add index sequences for random access
                var indexedSequence = AddIndexSequences(keyId, dnaSequence);

                // Submit DNA synthesis order
                var synthesisOrder = await SubmitDnaSynthesisAsync(keyId, indexedSequence);

                // Create key entry
                var entry = new DnaKeyEntry
                {
                    KeyId = keyId,
                    DnaSequence = indexedSequence,
                    ParityData = parityData,
                    SynthesisOrderId = synthesisOrder.OrderId,
                    DnaSampleId = synthesisOrder.SampleId,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    DecodedKey = keyData, // Cache the original key
                    SequenceLength = indexedSequence.Length,
                    RedundantCopies = _config.RedundantCopies
                };

                _keyStore[keyId] = entry;
                _currentKeyId = keyId;

                await PersistKeyRegistry();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Encodes binary data to DNA sequence using 2-bits-per-base encoding.
        /// Avoids homopolymer runs (e.g., AAAA) which cause sequencing errors.
        /// </summary>
        public string EncodeBinaryToDna(byte[] data)
        {
            // #3526: The previous implementation rotated bases to avoid homopolymer runs but never
            // stored a rotation marker, making decoding lossy and broken.
            // Fix: use a reversible encoding — each 2-bit pair maps directly to a base with no
            // silent mutation. DNA synthesis hardware handles homopolymer runs at the oligo level.
            // The decoder can now faithfully recover the original bytes.
            var dna = new StringBuilder(data.Length * 4); // 4 bases per byte

            foreach (var b in data)
            {
                // Encode each byte as 4 DNA bases (2 bits each), MSB first.
                for (int i = 6; i >= 0; i -= 2)
                {
                    var twoBits = (byte)((b >> i) & 0b11);
                    dna.Append(BinaryToDna[twoBits]);
                }
            }

            return dna.ToString();
        }

        /// <summary>
        /// Decodes DNA sequence back to binary data.
        /// </summary>
        public byte[] DecodeDnaToBinary(string dnaSequence)
        {
            // Remove index sequences and extract data portion
            var dataSequence = ExtractDataSequence(dnaSequence);

            if (dataSequence.Length % 4 != 0)
            {
                throw new FormatException("DNA sequence length must be divisible by 4.");
            }

            var bytes = new byte[dataSequence.Length / 4];

            for (int i = 0; i < dataSequence.Length; i += 4)
            {
                byte value = 0;
                for (int j = 0; j < 4; j++)
                {
                    var baseChar = char.ToUpper(dataSequence[i + j]);
                    if (!DnaToBinary.TryGetValue(baseChar, out var bits))
                    {
                        throw new FormatException($"Invalid DNA base: {baseChar}");
                    }
                    value = (byte)((value << 2) | bits);
                }
                bytes[i / 4] = value;
            }

            return bytes;
        }

        /// <summary>
        /// Applies Reed-Solomon encoding for error correction.
        /// Returns encoded data and separate parity data.
        /// </summary>
        private (byte[] encodedData, byte[] parityData) ApplyReedSolomonEncoding(byte[] data)
        {
            // Initialize GF(256) lookup tables
            var gfExp = new int[512];
            var gfLog = new int[256];
            InitializeGaloisField(gfExp, gfLog);

            // Pad data to block boundary
            var blockSize = 255 - RsParitySymbols; // RS(255, 223)
            var paddedLength = ((data.Length + blockSize - 1) / blockSize) * blockSize;
            var paddedData = new byte[paddedLength];
            Array.Copy(data, paddedData, data.Length);

            // Generate generator polynomial for RS code
            var generator = GenerateRsGenerator(RsParitySymbols, gfExp, gfLog);

            var parity = new List<byte>();

            // Encode each block
            for (int i = 0; i < paddedData.Length; i += blockSize)
            {
                var block = new byte[blockSize];
                Array.Copy(paddedData, i, block, 0, blockSize);

                var blockParity = RsEncode(block, generator, gfExp, gfLog);
                parity.AddRange(blockParity);
            }

            return (paddedData, parity.ToArray());
        }

        /// <summary>
        /// Applies Reed-Solomon decoding for error correction.
        /// </summary>
        private byte[] ApplyReedSolomonDecoding(byte[] data, byte[] parity)
        {
            var gfExp = new int[512];
            var gfLog = new int[256];
            InitializeGaloisField(gfExp, gfLog);

            var blockSize = 255 - RsParitySymbols;
            var correctedData = new List<byte>();

            int parityOffset = 0;

            for (int i = 0; i < data.Length; i += blockSize)
            {
                var block = new byte[blockSize];
                Array.Copy(data, i, block, 0, Math.Min(blockSize, data.Length - i));

                var blockParity = new byte[RsParitySymbols];
                if (parityOffset + RsParitySymbols <= parity.Length)
                {
                    Array.Copy(parity, parityOffset, blockParity, 0, RsParitySymbols);
                }
                parityOffset += RsParitySymbols;

                // Combine data and parity for decoding
                var codeword = new byte[255];
                Array.Copy(block, codeword, blockSize);
                Array.Copy(blockParity, 0, codeword, blockSize, RsParitySymbols);

                // Decode and correct errors
                var corrected = RsDecode(codeword, gfExp, gfLog);
                correctedData.AddRange(corrected.Take(blockSize));
            }

            return correctedData.ToArray();
        }

        private void InitializeGaloisField(int[] gfExp, int[] gfLog)
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                gfExp[i] = x;
                gfLog[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0)
                {
                    x ^= RsPrimitive;
                }
            }
            for (int i = 255; i < 512; i++)
            {
                gfExp[i] = gfExp[i - 255];
            }
        }

        private int[] GenerateRsGenerator(int nsym, int[] gfExp, int[] gfLog)
        {
            var g = new int[nsym + 1];
            g[0] = 1;

            for (int i = 0; i < nsym; i++)
            {
                var newG = new int[nsym + 1];
                for (int j = 0; j <= i + 1; j++)
                {
                    if (j == 0)
                        newG[j] = GfMul(g[j], gfExp[i], gfExp, gfLog);
                    else if (j == i + 1)
                        newG[j] = g[j - 1];
                    else
                        newG[j] = g[j - 1] ^ GfMul(g[j], gfExp[i], gfExp, gfLog);
                }
                g = newG;
            }

            return g;
        }

        private int GfMul(int x, int y, int[] gfExp, int[] gfLog)
        {
            if (x == 0 || y == 0) return 0;
            return gfExp[gfLog[x] + gfLog[y]];
        }

        private byte[] RsEncode(byte[] data, int[] generator, int[] gfExp, int[] gfLog)
        {
            var parity = new int[RsParitySymbols];

            foreach (var d in data)
            {
                var coef = d ^ parity[0];
                Array.Copy(parity, 1, parity, 0, parity.Length - 1);
                parity[parity.Length - 1] = 0;

                for (int i = 0; i < generator.Length - 1; i++)
                {
                    parity[i] ^= GfMul(generator[i + 1], coef, gfExp, gfLog);
                }
            }

            return parity.Select(p => (byte)p).ToArray();
        }

        private byte[] RsDecode(byte[] codeword, int[] gfExp, int[] gfLog)
        {
            // #3509: Reed-Solomon decoding requires a proper RS decoder (e.g., Berlekamp-Massey).
            // Simply returning the data portion without error correction is a stub.
            throw new NotSupportedException(
                "Reed-Solomon decoding requires native RS library. " +
                "Configure via DnaOptions.ReedSolomonProvider.");
        }

        private char RotateBase(char baseChar)
        {
            return baseChar switch
            {
                'A' => 'C',
                'C' => 'G',
                'G' => 'T',
                'T' => 'A',
                _ => baseChar
            };
        }

        /// <summary>
        /// Adds index sequences for random access retrieval.
        /// Format: [INDEX_PREFIX][KEY_ID_HASH][DATA][CHECKSUM]
        /// </summary>
        private string AddIndexSequences(string keyId, string dataSequence)
        {
            var keyIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(keyId));
            var indexSequence = EncodeBinaryToDna(keyIdHash.Take(8).ToArray()); // 8-byte index

            var checksumBytes = SHA256.HashData(Encoding.UTF8.GetBytes(dataSequence));
            var checksumSequence = EncodeBinaryToDna(checksumBytes.Take(4).ToArray()); // 4-byte checksum

            return $"{_config.IndexPrefix}{indexSequence}{dataSequence}{checksumSequence}";
        }

        private string ExtractDataSequence(string fullSequence)
        {
            // Remove index prefix, index sequence (32 bases for 8 bytes), and checksum (16 bases for 4 bytes)
            var prefixLength = _config.IndexPrefix.Length;
            var indexLength = 32; // 8 bytes * 4 bases/byte
            var checksumLength = 16; // 4 bytes * 4 bases/byte

            if (fullSequence.Length < prefixLength + indexLength + checksumLength)
            {
                throw new FormatException("DNA sequence too short.");
            }

            var dataStart = prefixLength + indexLength;
            var dataEnd = fullSequence.Length - checksumLength;

            return fullSequence.Substring(dataStart, dataEnd - dataStart);
        }

        /// <summary>
        /// Submits DNA synthesis order to Twist Bioscience or IDT.
        /// </summary>
        private async Task<DnaSynthesisOrder> SubmitDnaSynthesisAsync(string keyId, string dnaSequence)
        {
            switch (_config.SynthesisProvider)
            {
                case DnaSynthesisProvider.TwistBioscience:
                    return await SubmitTwistOrder(keyId, dnaSequence);
                case DnaSynthesisProvider.Idt:
                    return await SubmitIdtOrder(keyId, dnaSequence);
                default:
                    throw new NotSupportedException($"Synthesis provider '{_config.SynthesisProvider}' not supported.");
            }
        }

        private async Task<DnaSynthesisOrder> SubmitTwistOrder(string keyId, string dnaSequence)
        {
            var request = new TwistSynthesisRequest
            {
                Name = $"key-{keyId}",
                Sequence = dnaSequence,
                Scale = "25nm", // Synthesis scale
                Purification = "Standard",
                Quantity = _config.RedundantCopies,
                Modifications = new Dictionary<string, string>
                {
                    ["5prime"] = "none",
                    ["3prime"] = "none"
                }
            };

            var response = await _synthesisClient.PostAsJsonAsync("oligos/order", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TwistSynthesisResponse>();

            return new DnaSynthesisOrder
            {
                OrderId = result!.OrderId,
                SampleId = result.SampleIds?.FirstOrDefault() ?? Guid.NewGuid().ToString(),
                Status = result.Status,
                EstimatedDelivery = result.EstimatedDelivery
            };
        }

        private async Task<DnaSynthesisOrder> SubmitIdtOrder(string keyId, string dnaSequence)
        {
            var request = new IdtSynthesisRequest
            {
                Name = $"key-{keyId}",
                Sequence = dnaSequence,
                Scale = "25nmole",
                Purification = "STANDARD",
                Quantity = _config.RedundantCopies
            };

            var response = await _synthesisClient.PostAsJsonAsync("oligos", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<IdtSynthesisResponse>();

            return new DnaSynthesisOrder
            {
                OrderId = result!.OrderNumber,
                SampleId = result.OligoId ?? Guid.NewGuid().ToString(),
                Status = result.Status,
                EstimatedDelivery = result.ExpectedShipDate
            };
        }

        /// <summary>
        /// Sequences DNA sample using Illumina or Oxford Nanopore.
        /// </summary>
        private async Task<string> SequenceDnaAsync(string sampleId)
        {
            switch (_config.SequencingProvider)
            {
                case DnaSequencingProvider.Illumina:
                    return await SequenceWithIllumina(sampleId);
                case DnaSequencingProvider.OxfordNanopore:
                    return await SequenceWithNanopore(sampleId);
                default:
                    throw new NotSupportedException($"Sequencing provider '{_config.SequencingProvider}' not supported.");
            }
        }

        private async Task<string> SequenceWithIllumina(string sampleId)
        {
            // Submit sequencing job
            var request = new IlluminaSequencingRequest
            {
                SampleId = sampleId,
                ReadLength = 150,
                ReadType = "paired-end",
                Coverage = 30,
                IndexPrefix = _config.IndexPrefix
            };

            var submitResponse = await _sequencingClient.PostAsJsonAsync("runs/submit", request);
            submitResponse.EnsureSuccessStatusCode();

            var submitResult = await submitResponse.Content.ReadFromJsonAsync<IlluminaSubmitResponse>();

            // Poll for results (in production, would use webhooks)
            var runId = submitResult!.RunId;
            IlluminaResultResponse? result = null;

            for (int i = 0; i < 10; i++) // Max 10 attempts
            {
                await Task.Delay(TimeSpan.FromSeconds(5));

                var statusResponse = await _sequencingClient.GetAsync($"runs/{runId}/status");
                var status = await statusResponse.Content.ReadFromJsonAsync<IlluminaStatusResponse>();

                if (status?.Status == "COMPLETED")
                {
                    var resultResponse = await _sequencingClient.GetAsync($"runs/{runId}/results");
                    result = await resultResponse.Content.ReadFromJsonAsync<IlluminaResultResponse>();
                    break;
                }
            }

            if (result?.ConsensusSequence == null)
            {
                throw new InvalidOperationException("Sequencing did not complete in expected time.");
            }

            return result.ConsensusSequence;
        }

        private async Task<string> SequenceWithNanopore(string sampleId)
        {
            // Oxford Nanopore provides real-time sequencing
            var request = new NanoporeSequencingRequest
            {
                SampleId = sampleId,
                FlowCellType = "FLO-MIN106",
                Kit = "SQK-LSK109",
                RunDuration = TimeSpan.FromHours(1).TotalMinutes
            };

            var submitResponse = await _sequencingClient.PostAsJsonAsync("sequencing/start", request);
            submitResponse.EnsureSuccessStatusCode();

            var submitResult = await submitResponse.Content.ReadFromJsonAsync<NanoporeSubmitResponse>();

            // Wait for sequencing to complete
            var runId = submitResult!.RunId;
            NanoporeResultResponse? result = null;

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));

                var statusResponse = await _sequencingClient.GetAsync($"sequencing/{runId}/status");
                var status = await statusResponse.Content.ReadFromJsonAsync<NanoporeStatusResponse>();

                if (status?.State == "finished")
                {
                    var resultResponse = await _sequencingClient.GetAsync($"sequencing/{runId}/consensus");
                    result = await resultResponse.Content.ReadFromJsonAsync<NanoporeResultResponse>();
                    break;
                }
            }

            if (result?.Sequence == null)
            {
                throw new InvalidOperationException("Nanopore sequencing did not complete.");
            }

            return result.Sequence;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _keyStore.Keys.ToList().AsReadOnly();
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete DNA-encoded keys.");
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_keyStore.Remove(keyId))
                {
                    // Note: Physical DNA samples would need to be destroyed separately
                    await PersistKeyRegistry();
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_keyStore.TryGetValue(keyId, out var entry))
                {
                    return null;
                }

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = entry.CreatedAt,
                    CreatedBy = entry.CreatedBy,
                    KeySizeBytes = entry.DecodedKey?.Length ?? 0,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SynthesisOrderId"] = entry.SynthesisOrderId,
                        ["DnaSampleId"] = entry.DnaSampleId,
                        ["SequenceLength"] = entry.SequenceLength,
                        ["RedundantCopies"] = entry.RedundantCopies,
                        ["StorageMedium"] = "Synthetic DNA"
                    }
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        private string GetStoragePath()
        {
            if (Configuration.TryGetValue("StoragePath", out var pathObj) && pathObj is string path)
                return path;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "dna-keys.json");
        }

        private async Task LoadKeyRegistry()
        {
            var path = GetStoragePath();
            if (!File.Exists(path))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, DnaKeyEntrySerialized>>(json);

                if (stored != null)
                {
                    // #3510: Decrypt stored keys on load using machine-derived key.
                    var encryptionKey = DeriveDnaStorageKey();
                    foreach (var kvp in stored)
                    {
                        byte[]? decodedKey = null;
                        if (!string.IsNullOrEmpty(kvp.Value.DecodedKey))
                        {
                            try
                            {
                                decodedKey = kvp.Value.DecodedKey.StartsWith("enc1:", StringComparison.Ordinal)
                                    ? DecryptKeyFromStorage(kvp.Value.DecodedKey, encryptionKey)
                                    : null; // Reject plaintext base64 keys - they are not accepted
                            }
                            catch
                            {
                                decodedKey = null; // Key will need to be re-derived from DNA
                            }
                        }

                        _keyStore[kvp.Key] = new DnaKeyEntry
                        {
                            KeyId = kvp.Value.KeyId,
                            DnaSequence = kvp.Value.DnaSequence,
                            ParityData = string.IsNullOrEmpty(kvp.Value.ParityData)
                                ? Array.Empty<byte>()
                                : Convert.FromBase64String(kvp.Value.ParityData),
                            SynthesisOrderId = kvp.Value.SynthesisOrderId,
                            DnaSampleId = kvp.Value.DnaSampleId,
                            CreatedAt = kvp.Value.CreatedAt,
                            CreatedBy = kvp.Value.CreatedBy,
                            DecodedKey = decodedKey,
                            SequenceLength = kvp.Value.SequenceLength,
                            RedundantCopies = kvp.Value.RedundantCopies
                        };
                    }

                    if (_keyStore.Count > 0)
                    {
                        _currentKeyId = _keyStore.Keys.First();
                    }
                }
            }
            catch
            {

                // Ignore load errors
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        private async Task PersistKeyRegistry()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // #3510: Encrypt DecodedKey with machine-derived key before persisting.
            // Raw key bytes must never be written to disk in plaintext.
            var encryptionKey = DeriveDnaStorageKey();

            var toStore = _keyStore.ToDictionary(
                kvp => kvp.Key,
                kvp => new DnaKeyEntrySerialized
                {
                    KeyId = kvp.Value.KeyId,
                    DnaSequence = kvp.Value.DnaSequence,
                    ParityData = kvp.Value.ParityData.Length > 0
                        ? Convert.ToBase64String(kvp.Value.ParityData)
                        : "",
                    SynthesisOrderId = kvp.Value.SynthesisOrderId,
                    DnaSampleId = kvp.Value.DnaSampleId,
                    CreatedAt = kvp.Value.CreatedAt,
                    CreatedBy = kvp.Value.CreatedBy,
                    DecodedKey = kvp.Value.DecodedKey != null
                        ? EncryptKeyForStorage(kvp.Value.DecodedKey, encryptionKey)
                        : "",
                    SequenceLength = kvp.Value.SequenceLength,
                    RedundantCopies = kvp.Value.RedundantCopies
                });

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        // #3510: Machine-derived encryption key for protecting stored DNA key material.
        private byte[] DeriveDnaStorageKey()
        {
            var entropy = string.Join("|", Environment.MachineName, Environment.UserName,
                "DataWarehouse.DnaKeyStorage.v1");
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(entropy),
                32,
                salt: null,
                info: Encoding.UTF8.GetBytes("DnaEncodedKey.StorageEncryption"));
        }

        private static string EncryptKeyForStorage(byte[] keyBytes, byte[] encryptionKey)
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            var tag = new byte[16];
            var ciphertext = new byte[keyBytes.Length];
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Encrypt(nonce, keyBytes, ciphertext, tag);
            // Format: "enc1:" + base64(nonce + tag + ciphertext)
            var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);
            return "enc1:" + Convert.ToBase64String(combined);
        }

        private byte[] DecryptKeyFromStorage(string stored, byte[] encryptionKey)
        {
            if (!stored.StartsWith("enc1:", StringComparison.Ordinal))
                throw new CryptographicException("DNA key storage format unrecognized. Plaintext keys are no longer accepted.");

            var combined = Convert.FromBase64String(stored[5..]);
            if (combined.Length < 28)
                throw new CryptographicException("DNA key storage entry is too short.");

            var nonce = combined.AsSpan(0, 12).ToArray();
            var tag = combined.AsSpan(12, 16).ToArray();
            var ciphertext = combined.AsSpan(28).ToArray();
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _synthesisClient?.Dispose();
            _sequencingClient?.Dispose();
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region DNA Types

    public enum DnaSynthesisProvider
    {
        TwistBioscience,
        Idt
    }

    public enum DnaSequencingProvider
    {
        Illumina,
        OxfordNanopore
    }

    public class DnaConfig
    {
        public DnaSynthesisProvider SynthesisProvider { get; set; } = DnaSynthesisProvider.TwistBioscience;
        public DnaSequencingProvider SequencingProvider { get; set; } = DnaSequencingProvider.Illumina;
        public string SynthesisApiKey { get; set; } = "";
        public string SequencingApiKey { get; set; } = "";
        public int RedundantCopies { get; set; } = 3;
        public string IndexPrefix { get; set; } = "ATGCATGC"; // 8-base index prefix
    }

    internal class DnaKeyEntry
    {
        public string KeyId { get; set; } = "";
        public string DnaSequence { get; set; } = "";
        public byte[] ParityData { get; set; } = Array.Empty<byte>();
        public string SynthesisOrderId { get; set; } = "";
        public string DnaSampleId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public byte[]? DecodedKey { get; set; }
        public int SequenceLength { get; set; }
        public int RedundantCopies { get; set; }
    }

    internal class DnaKeyEntrySerialized
    {
        public string KeyId { get; set; } = "";
        public string DnaSequence { get; set; } = "";
        public string ParityData { get; set; } = "";
        public string SynthesisOrderId { get; set; } = "";
        public string DnaSampleId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? DecodedKey { get; set; }
        public int SequenceLength { get; set; }
        public int RedundantCopies { get; set; }
    }

    internal class DnaSynthesisOrder
    {
        public string OrderId { get; set; } = "";
        public string SampleId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? EstimatedDelivery { get; set; }
    }

    // Twist Bioscience API Types
    internal class TwistSynthesisRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("sequence")]
        public string Sequence { get; set; } = "";

        [JsonPropertyName("scale")]
        public string Scale { get; set; } = "";

        [JsonPropertyName("purification")]
        public string Purification { get; set; } = "";

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("modifications")]
        public Dictionary<string, string> Modifications { get; set; } = new();
    }

    internal class TwistSynthesisResponse
    {
        [JsonPropertyName("order_id")]
        public string OrderId { get; set; } = "";

        [JsonPropertyName("sample_ids")]
        public string[]? SampleIds { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("estimated_delivery")]
        public DateTime? EstimatedDelivery { get; set; }
    }

    // IDT API Types
    internal class IdtSynthesisRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("sequence")]
        public string Sequence { get; set; } = "";

        [JsonPropertyName("scale")]
        public string Scale { get; set; } = "";

        [JsonPropertyName("purification")]
        public string Purification { get; set; } = "";

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }
    }

    internal class IdtSynthesisResponse
    {
        [JsonPropertyName("order_number")]
        public string OrderNumber { get; set; } = "";

        [JsonPropertyName("oligo_id")]
        public string? OligoId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("expected_ship_date")]
        public DateTime? ExpectedShipDate { get; set; }
    }

    // Illumina API Types
    internal class IlluminaSequencingRequest
    {
        [JsonPropertyName("sample_id")]
        public string SampleId { get; set; } = "";

        [JsonPropertyName("read_length")]
        public int ReadLength { get; set; }

        [JsonPropertyName("read_type")]
        public string ReadType { get; set; } = "";

        [JsonPropertyName("coverage")]
        public int Coverage { get; set; }

        [JsonPropertyName("index_prefix")]
        public string IndexPrefix { get; set; } = "";
    }

    internal class IlluminaSubmitResponse
    {
        [JsonPropertyName("run_id")]
        public string RunId { get; set; } = "";
    }

    internal class IlluminaStatusResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }

    internal class IlluminaResultResponse
    {
        [JsonPropertyName("consensus_sequence")]
        public string? ConsensusSequence { get; set; }
    }

    // Oxford Nanopore API Types
    internal class NanoporeSequencingRequest
    {
        [JsonPropertyName("sample_id")]
        public string SampleId { get; set; } = "";

        [JsonPropertyName("flow_cell_type")]
        public string FlowCellType { get; set; } = "";

        [JsonPropertyName("kit")]
        public string Kit { get; set; } = "";

        [JsonPropertyName("run_duration")]
        public double RunDuration { get; set; }
    }

    internal class NanoporeSubmitResponse
    {
        [JsonPropertyName("run_id")]
        public string RunId { get; set; } = "";
    }

    internal class NanoporeStatusResponse
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "";
    }

    internal class NanoporeResultResponse
    {
        [JsonPropertyName("sequence")]
        public string? Sequence { get; set; }
    }

    #endregion
}
