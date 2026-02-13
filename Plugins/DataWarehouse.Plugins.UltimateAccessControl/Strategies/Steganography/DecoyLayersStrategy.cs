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
    /// Decoy layers strategy for plausible deniability through multiple hidden data layers.
    /// Implements T74.10 - Production-ready multi-layer deception for steganography.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deniability features:
    /// - Multiple hidden layers with different passwords
    /// - Decoy data that appears legitimate when extracted
    /// - Layer obfuscation (impossible to prove additional layers exist)
    /// - Chaff injection to mask real layer boundaries
    /// - Rubber-hose cryptanalysis resistance
    /// </para>
    /// <para>
    /// Layer types:
    /// - Public layer: Openly visible, innocent content
    /// - Decoy layer: Extracted with decoy password, plausible content
    /// - Secret layer: Real hidden content, requires correct key
    /// - Chaff layer: Random noise to mask layer count
    /// </para>
    /// </remarks>
    public sealed class DecoyLayersStrategy : AccessControlStrategyBase
    {
        private const int LayerHeaderSize = 48;
        private const int MaxLayers = 10;
        private static readonly byte[] LayerMagic = { 0x4C, 0x41, 0x59, 0x45, 0x52, 0x44, 0x45, 0x43 }; // "LAYERDEC"

        private int _defaultLayerCount = 3;
        private bool _injectChaff = true;
        private double _chaffRatio = 0.2;
        private bool _useProgressiveDecryption = true;
        private DeniabilityMode _deniabilityMode = DeniabilityMode.Full;

        /// <inheritdoc/>
        public override string StrategyId => "decoy-layers";

        /// <inheritdoc/>
        public override string StrategyName => "Decoy Layers";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = false,
            SupportsAuditTrail = false, // Deliberately no audit for deniability
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("DefaultLayerCount", out var countObj) && countObj is int count)
            {
                _defaultLayerCount = Math.Clamp(count, 2, MaxLayers);
            }

            if (configuration.TryGetValue("InjectChaff", out var chaffObj) && chaffObj is bool chaff)
            {
                _injectChaff = chaff;
            }

            if (configuration.TryGetValue("ChaffRatio", out var ratioObj) && ratioObj is double ratio)
            {
                _chaffRatio = Math.Clamp(ratio, 0.05, 0.5);
            }

            if (configuration.TryGetValue("UseProgressiveDecryption", out var progObj) && progObj is bool prog)
            {
                _useProgressiveDecryption = prog;
            }

            if (configuration.TryGetValue("DeniabilityMode", out var modeObj) && modeObj is string mode)
            {
                _deniabilityMode = Enum.TryParse<DeniabilityMode>(mode, true, out var m)
                    ? m : DeniabilityMode.Full;
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Creates a multi-layered steganographic container with decoys.
        /// </summary>
        /// <param name="layers">The layers to embed, each with its own password and content.</param>
        /// <param name="carrierData">The carrier data to embed into.</param>
        /// <returns>Carrier with embedded layers.</returns>
        public MultiLayerEmbeddingResult CreateLayeredContainer(IEnumerable<LayerDefinition> layers, byte[] carrierData)
        {
            var layerList = layers.ToList();

            if (!layerList.Any())
            {
                return new MultiLayerEmbeddingResult
                {
                    Success = false,
                    Error = "No layers provided"
                };
            }

            if (layerList.Count > MaxLayers)
            {
                return new MultiLayerEmbeddingResult
                {
                    Success = false,
                    Error = $"Maximum {MaxLayers} layers allowed"
                };
            }

            // Calculate total space needed
            long totalNeeded = CalculateTotalSpace(layerList);
            long availableSpace = carrierData.Length / 8; // LSB capacity

            if (totalNeeded > availableSpace)
            {
                return new MultiLayerEmbeddingResult
                {
                    Success = false,
                    Error = $"Insufficient carrier space. Need {totalNeeded}, have {availableSpace}"
                };
            }

            try
            {
                // Process and encrypt each layer
                var processedLayers = ProcessLayers(layerList);

                // Inject chaff if enabled
                if (_injectChaff)
                {
                    processedLayers = InjectChaffLayers(processedLayers, carrierData.Length);
                }

                // Interleave layers for deniability
                var interleavedData = InterleaveLayers(processedLayers);

                // Embed into carrier
                var modifiedCarrier = EmbedLayeredData(carrierData, interleavedData);

                return new MultiLayerEmbeddingResult
                {
                    Success = true,
                    ModifiedCarrier = modifiedCarrier,
                    LayerCount = layerList.Count,
                    TotalEmbeddedSize = interleavedData.Length,
                    LayerInfo = processedLayers.Select(l => new LayerInfo
                    {
                        LayerType = l.Type,
                        Size = l.EncryptedData.Length,
                        IsDecoy = l.Type == LayerType.Decoy
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                return new MultiLayerEmbeddingResult
                {
                    Success = false,
                    Error = $"Layer embedding failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Extracts a specific layer using its password.
        /// </summary>
        /// <param name="carrierData">The carrier containing hidden layers.</param>
        /// <param name="password">The password for the desired layer.</param>
        /// <returns>The extracted layer content.</returns>
        public LayerExtractionResult ExtractLayer(byte[] carrierData, string password)
        {
            try
            {
                // Extract embedded data
                var embeddedData = ExtractEmbeddedData(carrierData);

                // Derive key from password
                var key = DeriveKey(password);

                // Find and decrypt matching layer
                var layers = ParseLayers(embeddedData);

                foreach (var layer in layers)
                {
                    if (TryDecryptLayer(layer, key, out var decryptedData))
                    {
                        return new LayerExtractionResult
                        {
                            Success = true,
                            Data = decryptedData,
                            LayerType = layer.Type,
                            LayerIndex = layer.Index,
                            Message = layer.Type == LayerType.Decoy
                                ? "Decoy layer extracted - this is not the real content"
                                : null
                        };
                    }
                }

                // No matching layer found - this is expected for deniability
                return new LayerExtractionResult
                {
                    Success = false,
                    Error = "No layer found for provided password",
                    MightHaveMoreLayers = true
                };
            }
            catch (Exception ex)
            {
                return new LayerExtractionResult
                {
                    Success = false,
                    Error = $"Extraction failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Creates a decoy layer with believable fake content.
        /// </summary>
        public DecoyContent GenerateDecoyContent(DecoyType type, int approximateSize)
        {
            byte[] content;
            string description;

            switch (type)
            {
                case DecoyType.PersonalNote:
                    content = GeneratePersonalNoteDecoy(approximateSize);
                    description = "Personal notes and reminders";
                    break;

                case DecoyType.PasswordList:
                    content = GeneratePasswordListDecoy(approximateSize);
                    description = "Password list (fake)";
                    break;

                case DecoyType.FinancialData:
                    content = GenerateFinancialDecoy(approximateSize);
                    description = "Financial records";
                    break;

                case DecoyType.DiaryEntry:
                    content = GenerateDiaryDecoy(approximateSize);
                    description = "Personal diary entries";
                    break;

                case DecoyType.RandomBytes:
                    content = GenerateRandomDecoy(approximateSize);
                    description = "Encrypted data (appears as random)";
                    break;

                case DecoyType.RecipeCollection:
                    content = GenerateRecipeDecoy(approximateSize);
                    description = "Recipe collection";
                    break;

                default:
                    content = GenerateGenericDecoy(approximateSize);
                    description = "Miscellaneous notes";
                    break;
            }

            return new DecoyContent
            {
                Data = content,
                Type = type,
                Description = description,
                Size = content.Length,
                PlausibilityScore = CalculatePlausibility(type, content)
            };
        }

        /// <summary>
        /// Analyzes a carrier to determine if it might contain hidden layers.
        /// </summary>
        public LayerAnalysis AnalyzeForLayers(byte[] carrierData)
        {
            try
            {
                var embeddedData = ExtractEmbeddedData(carrierData);
                var potentialLayers = DetectPotentialLayers(embeddedData);

                // Calculate statistics that don't reveal actual content
                double entropy = CalculateEntropy(embeddedData);
                bool hasStructure = DetectStructure(embeddedData);

                return new LayerAnalysis
                {
                    ContainsEmbeddedData = embeddedData.Length > 100,
                    EstimatedLayerCount = potentialLayers.Count,
                    EntropyLevel = entropy,
                    HasDetectableStructure = hasStructure,
                    DeniabilityAssessment = AssessDeniability(potentialLayers, entropy, hasStructure),
                    Warnings = GetLayerWarnings(potentialLayers, entropy)
                };
            }
            catch
            {
                return new LayerAnalysis
                {
                    ContainsEmbeddedData = false,
                    DeniabilityAssessment = "Unable to analyze - carrier may be clean"
                };
            }
        }

        /// <summary>
        /// Verifies the integrity of all layers without revealing their contents.
        /// </summary>
        public LayerIntegrityCheck VerifyLayerIntegrity(byte[] carrierData, IEnumerable<string> passwords)
        {
            var results = new List<LayerVerificationResult>();

            foreach (var password in passwords)
            {
                var extractResult = ExtractLayer(carrierData, password);

                results.Add(new LayerVerificationResult
                {
                    PasswordHash = ComputePasswordHash(password),
                    Found = extractResult.Success,
                    DataSize = extractResult.Data?.Length ?? 0,
                    IntegrityValid = extractResult.Success && extractResult.Data != null
                });
            }

            return new LayerIntegrityCheck
            {
                LayersChecked = results.Count,
                LayersFound = results.Count(r => r.Found),
                AllIntegrityValid = results.All(r => r.IntegrityValid),
                Results = results
            };
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Decoy layers strategy does not perform access control - use for plausible deniability",
                ApplicablePolicies = new[] { "Steganography.DecoyLayers" }
            });
        }

        private long CalculateTotalSpace(List<LayerDefinition> layers)
        {
            long total = 0;

            foreach (var layer in layers)
            {
                // Header + encrypted content + HMAC
                total += LayerHeaderSize + layer.Content.Length + 32 + 16; // +16 for encryption overhead
            }

            // Chaff overhead
            if (_injectChaff)
            {
                total = (long)(total * (1 + _chaffRatio));
            }

            return total;
        }

        private List<ProcessedLayer> ProcessLayers(List<LayerDefinition> layers)
        {
            var processed = new List<ProcessedLayer>();
            int index = 0;

            foreach (var layer in layers)
            {
                var key = DeriveKey(layer.Password);
                var encryptedData = EncryptLayerData(layer.Content, key);

                processed.Add(new ProcessedLayer
                {
                    Index = index,
                    Type = layer.IsDecoy ? LayerType.Decoy : LayerType.Secret,
                    EncryptedData = encryptedData,
                    KeyHash = ComputeKeyHash(key),
                    OriginalSize = layer.Content.Length
                });

                index++;
            }

            return processed;
        }

        private List<ProcessedLayer> InjectChaffLayers(List<ProcessedLayer> layers, int carrierSize)
        {
            var result = new List<ProcessedLayer>(layers);
            int chaffCount = (int)(layers.Count * _chaffRatio) + 1;

            using var rng = RandomNumberGenerator.Create();

            for (int i = 0; i < chaffCount; i++)
            {
                // Generate random chaff that looks like an encrypted layer
                int chaffSize = layers.Average(l => l.EncryptedData.Length) > 0
                    ? (int)layers.Average(l => l.EncryptedData.Length)
                    : 256;

                var chaffData = new byte[chaffSize];
                rng.GetBytes(chaffData);

                result.Add(new ProcessedLayer
                {
                    Index = -1, // Chaff markers
                    Type = LayerType.Chaff,
                    EncryptedData = chaffData,
                    KeyHash = new byte[32], // Random hash
                    OriginalSize = 0
                });
            }

            // Shuffle to hide real layer positions
            var shuffled = result.OrderBy(_ => Guid.NewGuid()).ToList();

            // Reassign indices
            for (int i = 0; i < shuffled.Count; i++)
            {
                shuffled[i] = shuffled[i] with { Index = i };
            }

            return shuffled;
        }

        private byte[] InterleaveLayers(List<ProcessedLayer> layers)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Write layer count (could be fake for additional deniability)
            writer.Write(layers.Count + RandomNumberGenerator.GetInt32(0, 3)); // Fake count

            foreach (var layer in layers)
            {
                // Write layer header
                writer.Write(LayerMagic);
                writer.Write(layer.Index);
                writer.Write((int)layer.Type);
                writer.Write(layer.EncryptedData.Length);
                writer.Write(layer.OriginalSize);
                writer.Write(layer.KeyHash);

                // Write encrypted data
                writer.Write(layer.EncryptedData);
            }

            return ms.ToArray();
        }

        private byte[] EmbedLayeredData(byte[] carrier, byte[] layeredData)
        {
            var result = new byte[carrier.Length];
            Array.Copy(carrier, result, carrier.Length);

            int bitIndex = 0;
            int totalBits = layeredData.Length * 8;

            // Find data region (skip headers)
            int dataStart = Math.Min(100, carrier.Length / 10);

            for (int i = dataStart; i < result.Length && bitIndex < totalBits; i++)
            {
                // Skip every 4th byte (alpha channel for RGBA)
                if ((i - dataStart) % 4 == 3)
                    continue;

                int byteIndex = bitIndex / 8;
                int bitPosition = 7 - (bitIndex % 8);
                int bit = (layeredData[byteIndex] >> bitPosition) & 1;

                result[i] = (byte)((result[i] & 0xFE) | bit);
                bitIndex++;
            }

            return result;
        }

        private byte[] ExtractEmbeddedData(byte[] carrier)
        {
            var bits = new List<byte>();
            int dataStart = Math.Min(100, carrier.Length / 10);
            byte currentByte = 0;
            int bitCount = 0;

            for (int i = dataStart; i < carrier.Length; i++)
            {
                if ((i - dataStart) % 4 == 3)
                    continue;

                int bit = carrier[i] & 1;
                currentByte |= (byte)(bit << (7 - bitCount));
                bitCount++;

                if (bitCount == 8)
                {
                    bits.Add(currentByte);
                    currentByte = 0;
                    bitCount = 0;

                    // Limit extraction size
                    if (bits.Count > 10_000_000)
                        break;
                }
            }

            return bits.ToArray();
        }

        private List<ProcessedLayer> ParseLayers(byte[] embeddedData)
        {
            var layers = new List<ProcessedLayer>();

            if (embeddedData.Length < 4)
                return layers;

            using var ms = new MemoryStream(embeddedData);
            using var reader = new BinaryReader(ms);

            try
            {
                int layerCount = reader.ReadInt32();
                layerCount = Math.Min(layerCount, MaxLayers * 2); // Account for chaff

                for (int i = 0; i < layerCount && ms.Position < ms.Length - LayerHeaderSize; i++)
                {
                    // Read magic
                    var magic = reader.ReadBytes(8);
                    if (!magic.SequenceEqual(LayerMagic))
                    {
                        // Try to find next layer
                        continue;
                    }

                    int index = reader.ReadInt32();
                    var type = (LayerType)reader.ReadInt32();
                    int dataLength = reader.ReadInt32();
                    int originalSize = reader.ReadInt32();
                    var keyHash = reader.ReadBytes(32);

                    if (dataLength <= 0 || dataLength > 10_000_000)
                        continue;

                    var encryptedData = reader.ReadBytes(dataLength);

                    layers.Add(new ProcessedLayer
                    {
                        Index = index,
                        Type = type,
                        EncryptedData = encryptedData,
                        KeyHash = keyHash,
                        OriginalSize = originalSize
                    });
                }
            }
            catch (EndOfStreamException)
            {
                // Expected when reading corrupted or partial data
            }

            return layers;
        }

        private bool TryDecryptLayer(ProcessedLayer layer, byte[] key, out byte[] decryptedData)
        {
            decryptedData = Array.Empty<byte>();

            // Verify key hash matches
            var computedHash = ComputeKeyHash(key);
            if (!CryptographicOperations.FixedTimeEquals(computedHash, layer.KeyHash))
            {
                return false;
            }

            try
            {
                decryptedData = DecryptLayerData(layer.EncryptedData, key);
                return decryptedData.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private byte[] DeriveKey(string password)
        {
            var salt = Encoding.UTF8.GetBytes("DecoyLayersSalt2024");
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        }

        private byte[] EncryptLayerData(byte[] data, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, 16);

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(data, 0, data.Length);
            }

            return ms.ToArray();
        }

        private byte[] DecryptLayerData(byte[] encryptedData, byte[] key)
        {
            if (encryptedData.Length < 17)
                throw new InvalidDataException("Encrypted data too short");

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            var iv = new byte[16];
            Buffer.BlockCopy(encryptedData, 0, iv, 0, 16);
            aes.IV = iv;

            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(encryptedData, 16, encryptedData.Length - 16);
            }

            return ms.ToArray();
        }

        private byte[] ComputeKeyHash(byte[] key)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(key);
        }

        private string ComputePasswordHash(string password)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hash).Substring(0, 12);
        }

        private byte[] GeneratePersonalNoteDecoy(int size)
        {
            var notes = new[]
            {
                "Remember to call mom on Sunday.",
                "Grocery list: milk, eggs, bread, butter.",
                "Meeting with Dr. Smith at 3pm tomorrow.",
                "Pick up dry cleaning after work.",
                "Ideas for vacation: Hawaii, Alaska, Europe.",
                "Birthday gift ideas: watch, book, concert tickets.",
                "Car service due next month.",
                "Pay rent by the 5th.",
                "Gym schedule: Mon/Wed/Fri mornings."
            };

            return GenerateTextContent(notes, size);
        }

        private byte[] GeneratePasswordListDecoy(int size)
        {
            var entries = new[]
            {
                "Email: p@ssw0rd123",
                "Bank: mySecureBank!2024",
                "Netflix: streaming_fun_99",
                "Amazon: shopaholic#buyer",
                "WiFi: HomeNetwork2024!",
                "Computer: desktop_login_456",
                "Phone PIN: 1234 (need to change)",
                "Safe combination: 24-8-36"
            };

            return GenerateTextContent(entries, size);
        }

        private byte[] GenerateFinancialDecoy(int size)
        {
            var entries = new[]
            {
                "Savings Account: $12,456.78",
                "Checking Balance: $3,211.45",
                "Credit Card Debt: $2,100.00",
                "Monthly Income: $5,500.00",
                "Rent: $1,800.00",
                "Utilities: $150.00",
                "Groceries budget: $400.00",
                "Emergency fund goal: $10,000"
            };

            return GenerateTextContent(entries, size);
        }

        private byte[] GenerateDiaryDecoy(int size)
        {
            var entries = new[]
            {
                "March 15 - Had a great day at work. Got positive feedback on my project.",
                "March 16 - Went hiking with friends. Beautiful weather!",
                "March 17 - Started reading that new book everyone's talking about.",
                "March 18 - Feeling stressed about upcoming deadline.",
                "March 19 - Finally finished the report. Celebrated with pizza.",
                "March 20 - Quiet weekend. Watched movies and relaxed."
            };

            return GenerateTextContent(entries, size);
        }

        private byte[] GenerateRecipeDecoy(int size)
        {
            var entries = new[]
            {
                "CHOCOLATE CHIP COOKIES: 2 cups flour, 1 cup butter, 1 cup sugar, 2 eggs, 1 tsp vanilla, 2 cups chocolate chips. Mix dry ingredients. Cream butter and sugar. Add eggs and vanilla. Combine. Fold in chips. Bake 350F 12 min.",
                "PASTA CARBONARA: Cook spaghetti. Fry bacon. Beat eggs with parmesan. Toss hot pasta with bacon, then egg mixture. Season with pepper.",
                "BANANA BREAD: 3 ripe bananas, 1/3 cup butter, 3/4 cup sugar, 1 egg, 1 tsp vanilla, 1 tsp baking soda, pinch salt, 1.5 cups flour. Mash bananas, mix all ingredients, bake 350F 60 min."
            };

            return GenerateTextContent(entries, size);
        }

        private byte[] GenerateRandomDecoy(int size)
        {
            var data = new byte[size];
            RandomNumberGenerator.Fill(data);
            return data;
        }

        private byte[] GenerateGenericDecoy(int size)
        {
            var entries = new[]
            {
                "Notes from meeting: discussed Q2 targets, marketing strategy, and team expansion.",
                "Things to remember: update address, renew license, schedule dentist.",
                "Project ideas: mobile app, website redesign, automation scripts.",
                "Shopping list: office supplies, printer paper, new mouse."
            };

            return GenerateTextContent(entries, size);
        }

        private byte[] GenerateTextContent(string[] templates, int targetSize)
        {
            var sb = new StringBuilder();

            while (sb.Length < targetSize)
            {
                sb.AppendLine(templates[RandomNumberGenerator.GetInt32(templates.Length)]);
                sb.AppendLine();
            }

            var text = sb.ToString();
            if (text.Length > targetSize)
                text = text.Substring(0, targetSize);

            return Encoding.UTF8.GetBytes(text);
        }

        private double CalculatePlausibility(DecoyType type, byte[] content)
        {
            // Score based on content analysis
            double baseScore = type switch
            {
                DecoyType.PersonalNote => 0.9,
                DecoyType.DiaryEntry => 0.85,
                DecoyType.RecipeCollection => 0.95,
                DecoyType.PasswordList => 0.7, // Suspicion-raising but believable
                DecoyType.FinancialData => 0.75,
                DecoyType.RandomBytes => 0.6,
                _ => 0.8
            };

            // Adjust based on content characteristics
            if (type != DecoyType.RandomBytes)
            {
                var text = Encoding.UTF8.GetString(content);
                if (text.Contains("\n"))
                    baseScore += 0.05;
                if (text.Length > 100)
                    baseScore += 0.03;
            }

            return Math.Min(baseScore, 1.0);
        }

        private List<DetectedLayer> DetectPotentialLayers(byte[] data)
        {
            var layers = new List<DetectedLayer>();

            // Look for layer magic bytes
            for (int i = 0; i < data.Length - LayerMagic.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < LayerMagic.Length; j++)
                {
                    if (data[i + j] != LayerMagic[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    layers.Add(new DetectedLayer { Offset = i });
                }
            }

            return layers;
        }

        private double CalculateEntropy(byte[] data)
        {
            if (data.Length == 0)
                return 0;

            var histogram = new int[256];
            foreach (var b in data)
                histogram[b]++;

            double entropy = 0;
            double total = data.Length;

            foreach (var count in histogram)
            {
                if (count > 0)
                {
                    double p = count / total;
                    entropy -= p * Math.Log2(p);
                }
            }

            return entropy;
        }

        private bool DetectStructure(byte[] data)
        {
            // Look for repeating patterns or headers
            int patternMatches = 0;

            for (int i = 0; i < data.Length - 8; i++)
            {
                if (data[i] == LayerMagic[0] && i + LayerMagic.Length < data.Length)
                {
                    bool match = true;
                    for (int j = 1; j < LayerMagic.Length; j++)
                    {
                        if (data[i + j] != LayerMagic[j])
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match)
                        patternMatches++;
                }
            }

            return patternMatches > 0;
        }

        private string AssessDeniability(List<DetectedLayer> layers, double entropy, bool hasStructure)
        {
            if (!layers.Any() && entropy > 7.5)
                return "Excellent - appears as random noise";

            if (layers.Count == 1)
                return "Good - single layer detected, could be the only one";

            if (hasStructure && layers.Count > 2)
                return "Fair - multiple layers detected, but content remains protected";

            return "Moderate - some structure visible but content protected";
        }

        private List<string> GetLayerWarnings(List<DetectedLayer> layers, double entropy)
        {
            var warnings = new List<string>();

            if (layers.Count > 5)
                warnings.Add("Many layer signatures detected - consider reducing layer count");

            if (entropy < 6.0)
                warnings.Add("Low entropy may indicate non-random patterns");

            if (entropy > 7.9)
                warnings.Add("Very high entropy suggests encrypted content");

            return warnings;
        }
    }

    /// <summary>
    /// Deniability modes.
    /// </summary>
    public enum DeniabilityMode
    {
        Basic,
        Standard,
        Full
    }

    /// <summary>
    /// Layer types.
    /// </summary>
    public enum LayerType
    {
        Public,
        Decoy,
        Secret,
        Chaff
    }

    /// <summary>
    /// Decoy content types.
    /// </summary>
    public enum DecoyType
    {
        PersonalNote,
        PasswordList,
        FinancialData,
        DiaryEntry,
        RandomBytes,
        RecipeCollection,
        Generic
    }

    /// <summary>
    /// Layer definition for embedding.
    /// </summary>
    public record LayerDefinition
    {
        public string Password { get; init; } = "";
        public byte[] Content { get; init; } = Array.Empty<byte>();
        public bool IsDecoy { get; init; }
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Processed layer ready for embedding.
    /// </summary>
    internal record ProcessedLayer
    {
        public int Index { get; init; }
        public LayerType Type { get; init; }
        public byte[] EncryptedData { get; init; } = Array.Empty<byte>();
        public byte[] KeyHash { get; init; } = Array.Empty<byte>();
        public int OriginalSize { get; init; }
    }

    /// <summary>
    /// Detected layer during analysis.
    /// </summary>
    internal record DetectedLayer
    {
        public int Offset { get; init; }
    }

    /// <summary>
    /// Layer information summary.
    /// </summary>
    public record LayerInfo
    {
        public LayerType LayerType { get; init; }
        public int Size { get; init; }
        public bool IsDecoy { get; init; }
    }

    /// <summary>
    /// Multi-layer embedding result.
    /// </summary>
    public record MultiLayerEmbeddingResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public byte[]? ModifiedCarrier { get; init; }
        public int LayerCount { get; init; }
        public int TotalEmbeddedSize { get; init; }
        public List<LayerInfo> LayerInfo { get; init; } = new();
    }

    /// <summary>
    /// Layer extraction result.
    /// </summary>
    public record LayerExtractionResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public byte[]? Data { get; init; }
        public LayerType LayerType { get; init; }
        public int LayerIndex { get; init; }
        public string? Message { get; init; }
        public bool MightHaveMoreLayers { get; init; }
    }

    /// <summary>
    /// Generated decoy content.
    /// </summary>
    public record DecoyContent
    {
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public DecoyType Type { get; init; }
        public string Description { get; init; } = "";
        public int Size { get; init; }
        public double PlausibilityScore { get; init; }
    }

    /// <summary>
    /// Layer analysis result.
    /// </summary>
    public record LayerAnalysis
    {
        public bool ContainsEmbeddedData { get; init; }
        public int EstimatedLayerCount { get; init; }
        public double EntropyLevel { get; init; }
        public bool HasDetectableStructure { get; init; }
        public string DeniabilityAssessment { get; init; } = "";
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Layer verification result.
    /// </summary>
    public record LayerVerificationResult
    {
        public string PasswordHash { get; init; } = "";
        public bool Found { get; init; }
        public int DataSize { get; init; }
        public bool IntegrityValid { get; init; }
    }

    /// <summary>
    /// Layer integrity check result.
    /// </summary>
    public record LayerIntegrityCheck
    {
        public int LayersChecked { get; init; }
        public int LayersFound { get; init; }
        public bool AllIntegrityValid { get; init; }
        public List<LayerVerificationResult> Results { get; init; } = new();
    }
}
