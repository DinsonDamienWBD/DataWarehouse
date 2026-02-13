using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Hardware;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for hardware accelerator plugins.
    /// Provides common implementation for lifecycle management and statistics tracking.
    /// </summary>
    public abstract class HardwareAcceleratorPluginBase : ComputePluginBase, IHardwareAccelerator, IIntelligenceAware
    {
        /// <inheritdoc/>
        public override string RuntimeType => "HardwareAccelerator";

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-hardware-accelerator" });

        private bool _initialized;
        private long _operationsCompleted;
        private long _totalBytesProcessed;
        private DateTime _startTime;
        private TimeSpan _totalProcessingTime;

        #region Intelligence Socket

        /// <summary>
        /// Gets whether Universal Intelligence (T90) is available for AI-assisted optimization.
        /// </summary>
        public new bool IsIntelligenceAvailable { get; protected set; }

        /// <summary>
        /// Gets the available Intelligence capabilities.
        /// </summary>
        public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        /// <summary>
        /// Discovers Intelligence availability.
        /// </summary>
        public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        /// <summary>
        /// Declared capabilities for hardware acceleration.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.acceleration",
                DisplayName = $"{Name} - Hardware Acceleration",
                Description = $"Hardware-accelerated operations via {Type}",
                Category = CapabilityCategory.Infrastructure,
                SubCategory = "Acceleration",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "acceleration", "hardware", Type.ToString().ToLowerInvariant() },
                SemanticDescription = "Use for hardware-accelerated compression, encryption, or compute"
            }
        };

        /// <summary>
        /// Gets static knowledge about hardware acceleration capabilities.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.accelerator.capability",
                    Topic = "hardware.acceleration",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"Hardware accelerator: {Type}, Available: {IsAvailable}",
                    Payload = new Dictionary<string, object>
                    {
                        ["acceleratorType"] = Type.ToString(),
                        ["isAvailable"] = IsAvailable,
                        ["isInitialized"] = IsInitialized
                    },
                    Tags = new[] { "hardware", "acceleration" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted accelerator optimization recommendations.
        /// </summary>
        protected virtual async Task<AcceleratorOptimizationHint?> RequestOptimizationAsync(AcceleratorStatistics stats, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <summary>
        /// Gets the category of this plugin. Always returns OrchestrationProvider for hardware accelerators.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <summary>
        /// Gets the type of hardware accelerator.
        /// </summary>
        public abstract AcceleratorType Type { get; }

        /// <summary>
        /// Gets whether this accelerator is available on the current system.
        /// Implementation should check for hardware presence and driver availability.
        /// </summary>
        public abstract bool IsAvailable { get; }

        /// <summary>
        /// Gets whether the accelerator has been initialized.
        /// </summary>
        protected bool IsInitialized => _initialized;

        /// <summary>
        /// Initializes the hardware accelerator.
        /// Only initializes if available and not already initialized.
        /// </summary>
        /// <returns>A task representing the initialization operation.</returns>
        public async Task InitializeAsync()
        {
            if (!_initialized && IsAvailable)
            {
                await InitializeHardwareAsync();
                _initialized = true;
                _startTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Processes data using the hardware accelerator.
        /// Automatically initializes if not yet initialized.
        /// </summary>
        /// <param name="data">Input data to process.</param>
        /// <param name="operation">The operation to perform.</param>
        /// <returns>The processed data.</returns>
        public async Task<byte[]> ProcessAsync(byte[] data, AcceleratorOperation operation)
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            var startTime = DateTime.UtcNow;
            var result = await PerformOperationAsync(data, operation);
            var duration = DateTime.UtcNow - startTime;

            // Update statistics
            Interlocked.Increment(ref _operationsCompleted);
            Interlocked.Add(ref _totalBytesProcessed, data.Length);
            _totalProcessingTime += duration;

            return result;
        }

        /// <summary>
        /// Gets current statistics for this accelerator.
        /// </summary>
        /// <returns>Accelerator statistics.</returns>
        public Task<AcceleratorStatistics> GetStatisticsAsync() => CollectStatisticsAsync();

        /// <summary>
        /// Starts the accelerator plugin.
        /// Override to perform custom startup operations.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the start operation.</returns>
        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Stops the accelerator plugin.
        /// Override to perform custom shutdown operations.
        /// </summary>
        /// <returns>A task representing the stop operation.</returns>
        public override Task StopAsync() => Task.CompletedTask;

        /// <summary>
        /// Initializes the hardware accelerator.
        /// Implement hardware-specific initialization (driver loading, device enumeration, etc.).
        /// </summary>
        /// <returns>A task representing the initialization operation.</returns>
        protected abstract Task InitializeHardwareAsync();

        /// <summary>
        /// Performs the actual hardware-accelerated operation.
        /// Implement the core acceleration logic for the specified operation.
        /// </summary>
        /// <param name="data">Input data.</param>
        /// <param name="op">Operation to perform.</param>
        /// <returns>Processed data.</returns>
        protected abstract Task<byte[]> PerformOperationAsync(byte[] data, AcceleratorOperation op);

        /// <summary>
        /// Collects current statistics for this accelerator.
        /// Default implementation provides basic statistics; override for custom metrics.
        /// </summary>
        /// <returns>Current accelerator statistics.</returns>
        protected virtual Task<AcceleratorStatistics> CollectStatisticsAsync()
        {
            var operations = Interlocked.Read(ref _operationsCompleted);
            var totalBytes = Interlocked.Read(ref _totalBytesProcessed);
            var uptime = _initialized ? DateTime.UtcNow - _startTime : TimeSpan.Zero;
            var throughputMBps = uptime.TotalSeconds > 0 ? (totalBytes / (1024.0 * 1024.0)) / uptime.TotalSeconds : 0;
            var utilization = _totalProcessingTime.TotalSeconds > 0 && uptime.TotalSeconds > 0
                ? _totalProcessingTime.TotalSeconds / uptime.TotalSeconds
                : 0.0;

            return Task.FromResult(new AcceleratorStatistics(
                Type,
                operations,
                throughputMBps,
                Math.Min(1.0, utilization),
                _totalProcessingTime
            ));
        }

        /// <summary>
        /// Gets metadata for this plugin.
        /// Includes hardware-specific information.
        /// </summary>
        /// <returns>Plugin metadata dictionary.</returns>
        protected override System.Collections.Generic.Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "HardwareAcceleration";
            metadata["AcceleratorType"] = Type.ToString();
            metadata["IsAvailable"] = IsAvailable;
            metadata["IsInitialized"] = IsInitialized;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for Intel QAT accelerator plugins.
    /// Provides compression and encryption acceleration using Intel QuickAssist Technology.
    /// </summary>
    public abstract class QatAcceleratorPluginBase : HardwareAcceleratorPluginBase, IQatAccelerator
    {
        /// <summary>
        /// Gets the accelerator type. Always returns IntelQAT.
        /// </summary>
        public override AcceleratorType Type => AcceleratorType.IntelQAT;

        /// <summary>
        /// Extended capabilities for QAT acceleration.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.qat.compression",
                DisplayName = $"{Name} - QAT Compression",
                Description = "Intel QAT hardware-accelerated compression/decompression",
                Category = CapabilityCategory.Infrastructure,
                SubCategory = "Compression",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "qat", "compression", "hardware" },
                SemanticDescription = "Use for high-throughput compression with Intel QAT"
            },
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.qat.encryption",
                DisplayName = $"{Name} - QAT Encryption",
                Description = "Intel QAT hardware-accelerated encryption/decryption",
                Category = CapabilityCategory.Security,
                SubCategory = "Encryption",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "qat", "encryption", "hardware" },
                SemanticDescription = "Use for hardware-accelerated AES encryption with Intel QAT"
            }
        };

        /// <summary>
        /// Requests AI-assisted QAT workload optimization.
        /// </summary>
        protected virtual async Task<QatWorkloadHint?> RequestQatOptimizationAsync(long dataSize, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        /// <summary>
        /// Compresses data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Data to compress.</param>
        /// <param name="level">Compression level.</param>
        /// <returns>Compressed data.</returns>
        public Task<byte[]> CompressQatAsync(byte[] data, QatCompressionLevel level)
            => QatCompressAsync(data, (int)level);

        /// <summary>
        /// Decompresses data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Compressed data.</param>
        /// <returns>Decompressed data.</returns>
        public Task<byte[]> DecompressQatAsync(byte[] data) => QatDecompressAsync(data);

        /// <summary>
        /// Encrypts data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Data to encrypt.</param>
        /// <param name="key">Encryption key.</param>
        /// <returns>Encrypted data.</returns>
        public Task<byte[]> EncryptQatAsync(byte[] data, byte[] key) => QatEncryptAsync(data, key);

        /// <summary>
        /// Decrypts data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Encrypted data.</param>
        /// <param name="key">Decryption key.</param>
        /// <returns>Decrypted data.</returns>
        public Task<byte[]> DecryptQatAsync(byte[] data, byte[] key) => QatDecryptAsync(data, key);

        /// <summary>
        /// Implements QAT compression.
        /// </summary>
        /// <param name="data">Data to compress.</param>
        /// <param name="level">Compression level (0=Fast, 1=Balanced, 2=Best).</param>
        /// <returns>Compressed data.</returns>
        protected abstract Task<byte[]> QatCompressAsync(byte[] data, int level);

        /// <summary>
        /// Implements QAT decompression.
        /// </summary>
        /// <param name="data">Compressed data.</param>
        /// <returns>Decompressed data.</returns>
        protected abstract Task<byte[]> QatDecompressAsync(byte[] data);

        /// <summary>
        /// Implements QAT encryption.
        /// </summary>
        /// <param name="data">Data to encrypt.</param>
        /// <param name="key">Encryption key.</param>
        /// <returns>Encrypted data.</returns>
        protected abstract Task<byte[]> QatEncryptAsync(byte[] data, byte[] key);

        /// <summary>
        /// Implements QAT decryption.
        /// </summary>
        /// <param name="data">Encrypted data.</param>
        /// <param name="key">Decryption key.</param>
        /// <returns>Decrypted data.</returns>
        protected abstract Task<byte[]> QatDecryptAsync(byte[] data, byte[] key);

        /// <summary>
        /// Performs operations by routing to appropriate QAT methods.
        /// </summary>
        /// <param name="data">Input data.</param>
        /// <param name="op">Operation to perform.</param>
        /// <returns>Processed data.</returns>
        protected override Task<byte[]> PerformOperationAsync(byte[] data, AcceleratorOperation op)
        {
            return op switch
            {
                AcceleratorOperation.Compress => QatCompressAsync(data, (int)QatCompressionLevel.Balanced),
                AcceleratorOperation.Decompress => QatDecompressAsync(data),
                AcceleratorOperation.Encrypt => throw new InvalidOperationException("Encrypt requires key parameter. Use EncryptQatAsync instead."),
                AcceleratorOperation.Decrypt => throw new InvalidOperationException("Decrypt requires key parameter. Use DecryptQatAsync instead."),
                _ => throw new NotSupportedException($"Operation {op} is not supported by QAT accelerator.")
            };
        }

        /// <summary>
        /// Gets metadata for this plugin.
        /// </summary>
        /// <returns>Plugin metadata dictionary.</returns>
        protected override System.Collections.Generic.Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsCompression"] = true;
            metadata["SupportsEncryption"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for GPU accelerator plugins.
    /// Supports CUDA, ROCm, and other GPU compute frameworks.
    /// </summary>
    public abstract class GpuAcceleratorPluginBase : HardwareAcceleratorPluginBase, IGpuAccelerator
    {
        /// <summary>
        /// Gets the accelerator type. Returns NvidiaGpu or AmdGpu based on runtime.
        /// </summary>
        public override AcceleratorType Type => Runtime switch
        {
            GpuRuntime.Cuda => AcceleratorType.NvidiaGpu,
            GpuRuntime.RoCm => AcceleratorType.AmdGpu,
            _ => AcceleratorType.NvidiaGpu | AcceleratorType.AmdGpu
        };

        /// <summary>
        /// Gets the GPU runtime in use (CUDA, ROCm, etc.).
        /// </summary>
        public abstract GpuRuntime Runtime { get; }

        /// <summary>
        /// Gets the number of available GPU devices.
        /// </summary>
        public abstract int DeviceCount { get; }

        /// <summary>
        /// Extended capabilities for GPU acceleration.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.gpu.compute",
                DisplayName = $"{Name} - GPU Compute",
                Description = $"GPU-accelerated compute via {Runtime}",
                Category = CapabilityCategory.AI,
                SubCategory = "Compute",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "gpu", "compute", Runtime.ToString().ToLowerInvariant() },
                SemanticDescription = "Use for GPU-accelerated matrix operations and embeddings"
            },
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.gpu.embeddings",
                DisplayName = $"{Name} - GPU Embeddings",
                Description = "GPU-accelerated embedding computation",
                Category = CapabilityCategory.AI,
                SubCategory = "Embeddings",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "gpu", "embeddings", "ai" },
                SemanticDescription = "Use for computing vector embeddings on GPU"
            }
        };

        /// <summary>
        /// Gets static knowledge about GPU capabilities.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.gpu.capability",
                    Topic = "hardware.gpu",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"GPU accelerator: {Runtime}, Devices: {DeviceCount}",
                    Payload = new Dictionary<string, object>
                    {
                        ["runtime"] = Runtime.ToString(),
                        ["deviceCount"] = DeviceCount,
                        ["supportsVectorOps"] = true,
                        ["supportsEmbeddings"] = true
                    },
                    Tags = new[] { "gpu", "compute" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted GPU workload distribution.
        /// </summary>
        protected virtual async Task<GpuWorkloadHint?> RequestGpuOptimizationAsync(int batchSize, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        /// <summary>
        /// Performs element-wise vector multiplication on GPU.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Result vector (a[i] * b[i]).</returns>
        public Task<float[]> VectorMultiplyAsync(float[] a, float[] b) => GpuVectorMulAsync(a, b);

        /// <summary>
        /// Performs matrix multiplication on GPU.
        /// </summary>
        /// <param name="a">First matrix.</param>
        /// <param name="b">Second matrix.</param>
        /// <returns>Result matrix (a × b).</returns>
        public Task<float[]> MatrixMultiplyAsync(float[,] a, float[,] b) => GpuMatrixMulAsync(a, b);

        /// <summary>
        /// Computes embeddings using GPU acceleration.
        /// </summary>
        /// <param name="input">Input vector.</param>
        /// <param name="weights">Weight matrix.</param>
        /// <returns>Embedding vector.</returns>
        public Task<float[]> ComputeEmbeddingsAsync(float[] input, float[,] weights) => GpuEmbeddingsAsync(input, weights);

        /// <summary>
        /// Implements GPU vector multiplication.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Result vector.</returns>
        protected abstract Task<float[]> GpuVectorMulAsync(float[] a, float[] b);

        /// <summary>
        /// Implements GPU matrix multiplication.
        /// </summary>
        /// <param name="a">First matrix.</param>
        /// <param name="b">Second matrix.</param>
        /// <returns>Result matrix.</returns>
        protected abstract Task<float[]> GpuMatrixMulAsync(float[,] a, float[,] b);

        /// <summary>
        /// Implements GPU embedding computation.
        /// </summary>
        /// <param name="input">Input vector.</param>
        /// <param name="weights">Weight matrix.</param>
        /// <returns>Embedding vector.</returns>
        protected abstract Task<float[]> GpuEmbeddingsAsync(float[] input, float[,] weights);

        /// <summary>
        /// GPU accelerators primarily work with float arrays, so this method is not commonly used.
        /// Override if byte-level GPU operations are needed.
        /// </summary>
        /// <param name="data">Input data.</param>
        /// <param name="op">Operation to perform.</param>
        /// <returns>Processed data.</returns>
        protected override Task<byte[]> PerformOperationAsync(byte[] data, AcceleratorOperation op)
        {
            return op switch
            {
                AcceleratorOperation.VectorMul => throw new InvalidOperationException("VectorMul requires float array parameters. Use VectorMultiplyAsync instead."),
                _ => throw new NotSupportedException($"Operation {op} is not supported by GPU accelerator. Use typed methods instead.")
            };
        }

        /// <summary>
        /// Gets metadata for this plugin.
        /// </summary>
        /// <returns>Plugin metadata dictionary.</returns>
        protected override System.Collections.Generic.Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Runtime"] = Runtime.ToString();
            metadata["DeviceCount"] = DeviceCount;
            metadata["SupportsVectorOps"] = true;
            metadata["SupportsMatrixOps"] = true;
            metadata["SupportsEmbeddings"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for TPM 2.0 provider plugins.
    /// Provides secure key storage and cryptographic operations using Trusted Platform Module.
    /// </summary>
    public abstract class Tpm2ProviderPluginBase : SecurityPluginBase, ITpm2Provider, IIntelligenceAware
    {
        /// <inheritdoc/>
        public override string SecurityDomain => "TPM2";

        /// <summary>
        /// Gets the category of this plugin. Always returns SecurityProvider for TPM plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <summary>
        /// Gets whether TPM 2.0 is available on this system.
        /// Implementation should check for TPM presence and version.
        /// </summary>
        public abstract bool IsAvailable { get; }

        #region Intelligence Socket

        public new bool IsIntelligenceAvailable { get; protected set; }
        public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.tpm.keys",
                DisplayName = $"{Name} - TPM Key Storage",
                Description = "Hardware-protected key storage via TPM 2.0",
                Category = CapabilityCategory.Security,
                SubCategory = "KeyManagement",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "tpm", "security", "keys" },
                SemanticDescription = "Use for hardware-protected cryptographic key operations"
            },
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.tpm.attestation",
                DisplayName = $"{Name} - TPM Attestation",
                Description = "Remote attestation via TPM 2.0",
                Category = CapabilityCategory.Security,
                SubCategory = "Attestation",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "tpm", "attestation", "security" },
                SemanticDescription = "Use for hardware-backed system attestation"
            }
        };

        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.tpm.capability",
                    Topic = "security.tpm",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"TPM 2.0 provider, Available: {IsAvailable}",
                    Payload = new Dictionary<string, object>
                    {
                        ["isAvailable"] = IsAvailable,
                        ["supportsKeyStorage"] = true,
                        ["supportsSigning"] = true,
                        ["supportsEncryption"] = true
                    },
                    Tags = new[] { "tpm", "security" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted key rotation recommendation.
        /// </summary>
        protected virtual async Task<KeyRotationHint?> RequestKeyRotationAdviceAsync(string keyId, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <summary>
        /// Creates a new key in the TPM.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key.</param>
        /// <param name="type">Key type to create.</param>
        /// <returns>Public key data.</returns>
        public Task<byte[]> CreateKeyAsync(string keyId, TpmKeyType type) => CreateTpmKeyAsync(keyId, type);

        /// <summary>
        /// Signs data using a TPM-stored key.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Data to sign.</param>
        /// <returns>Signature.</returns>
        public Task<byte[]> SignAsync(string keyId, byte[] data) => TpmSignAsync(keyId, data);

        /// <summary>
        /// Encrypts data using a TPM-stored key.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        public Task<byte[]> EncryptAsync(string keyId, byte[] data) => TpmEncryptAsync(keyId, data);

        /// <summary>
        /// Decrypts data using a TPM-stored key.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        public Task<byte[]> DecryptAsync(string keyId, byte[] data) => TpmDecryptAsync(keyId, data);

        /// <summary>
        /// Gets cryptographically secure random bytes from the TPM.
        /// </summary>
        /// <param name="length">Number of random bytes to generate.</param>
        /// <returns>Random bytes.</returns>
        public Task<byte[]> GetRandomAsync(int length) => TpmGetRandomAsync(length);

        /// <summary>
        /// Starts the TPM provider plugin.
        /// Override to perform custom startup operations.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the start operation.</returns>
        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Stops the TPM provider plugin.
        /// Override to perform custom shutdown operations.
        /// </summary>
        /// <returns>A task representing the stop operation.</returns>
        public override Task StopAsync() => Task.CompletedTask;

        /// <summary>
        /// Implements TPM key creation.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="type">Key type.</param>
        /// <returns>Public key data.</returns>
        protected abstract Task<byte[]> CreateTpmKeyAsync(string keyId, TpmKeyType type);

        /// <summary>
        /// Implements TPM signing operation.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Data to sign.</param>
        /// <returns>Signature.</returns>
        protected abstract Task<byte[]> TpmSignAsync(string keyId, byte[] data);

        /// <summary>
        /// Implements TPM encryption operation.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        protected abstract Task<byte[]> TpmEncryptAsync(string keyId, byte[] data);

        /// <summary>
        /// Implements TPM decryption operation.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        protected abstract Task<byte[]> TpmDecryptAsync(string keyId, byte[] data);

        /// <summary>
        /// Implements TPM random number generation.
        /// </summary>
        /// <param name="length">Number of bytes.</param>
        /// <returns>Random bytes.</returns>
        protected abstract Task<byte[]> TpmGetRandomAsync(int length);

        /// <summary>
        /// Gets metadata for this plugin.
        /// </summary>
        /// <returns>Plugin metadata dictionary.</returns>
        protected override System.Collections.Generic.Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "TPM2";
            metadata["IsAvailable"] = IsAvailable;
            metadata["SupportsKeyStorage"] = true;
            metadata["SupportsSigning"] = true;
            metadata["SupportsEncryption"] = true;
            metadata["SupportsRng"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for HSM provider plugins.
    /// Provides PKCS#11-based access to Hardware Security Modules.
    /// </summary>
    public abstract class HsmProviderPluginBase : SecurityPluginBase, IHsmProvider, IIntelligenceAware
    {
        /// <inheritdoc/>
        public override string SecurityDomain => "HSM";

        /// <summary>
        /// Gets the category of this plugin. Always returns SecurityProvider for HSM plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <summary>
        /// Gets the currently connected slot ID, or null if not connected.
        /// </summary>
        protected string? ConnectedSlot { get; private set; }

        /// <summary>
        /// Gets whether the HSM is currently connected.
        /// </summary>
        public bool IsConnected => ConnectedSlot != null;

        #region Intelligence Socket

        public new bool IsIntelligenceAvailable { get; protected set; }
        public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.hsm.keys",
                DisplayName = $"{Name} - HSM Key Management",
                Description = "PKCS#11-based HSM key storage and operations",
                Category = CapabilityCategory.Security,
                SubCategory = "KeyManagement",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "hsm", "pkcs11", "keys" },
                SemanticDescription = "Use for enterprise-grade hardware security module operations"
            },
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.hsm.crypto",
                DisplayName = $"{Name} - HSM Cryptography",
                Description = "Hardware-backed cryptographic operations via HSM",
                Category = CapabilityCategory.Security,
                SubCategory = "Cryptography",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "hsm", "encryption", "signing" },
                SemanticDescription = "Use for HSM-backed signing and encryption"
            }
        };

        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.hsm.capability",
                    Topic = "security.hsm",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"HSM provider, Connected: {IsConnected}, Slot: {ConnectedSlot ?? "None"}",
                    Payload = new Dictionary<string, object>
                    {
                        ["isConnected"] = IsConnected,
                        ["connectedSlot"] = ConnectedSlot ?? "None",
                        ["supportsPKCS11"] = true
                    },
                    Tags = new[] { "hsm", "security" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted HSM key policy recommendation.
        /// </summary>
        protected virtual async Task<HsmKeyPolicyHint?> RequestKeyPolicyAdviceAsync(string keyLabel, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <summary>
        /// Connects to the HSM with the specified slot and PIN.
        /// </summary>
        /// <param name="slotId">HSM slot identifier.</param>
        /// <param name="pin">PIN for authentication.</param>
        /// <returns>A task representing the connection operation.</returns>
        public async Task ConnectAsync(string slotId, string pin)
        {
            await OpenSessionAsync(slotId, pin);
            ConnectedSlot = slotId;
        }

        /// <summary>
        /// Disconnects from the HSM.
        /// </summary>
        /// <returns>A task representing the disconnection operation.</returns>
        public async Task DisconnectAsync()
        {
            await CloseSessionAsync();
            ConnectedSlot = null;
        }

        /// <summary>
        /// Lists all keys stored in the HSM.
        /// </summary>
        /// <returns>Array of key labels.</returns>
        public Task<string[]> ListKeysAsync() => EnumerateKeysAsync();

        /// <summary>
        /// Generates a new key in the HSM.
        /// </summary>
        /// <param name="label">Key label.</param>
        /// <param name="spec">Key specification.</param>
        /// <returns>Key handle or identifier.</returns>
        public Task<byte[]> GenerateKeyAsync(string label, HsmKeySpec spec) => GenerateHsmKeyAsync(label, spec);

        /// <summary>
        /// Signs data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to sign.</param>
        /// <param name="algorithm">Signature algorithm.</param>
        /// <returns>Signature.</returns>
        public Task<byte[]> SignAsync(string keyLabel, byte[] data, HsmSignatureAlgorithm algorithm)
            => HsmSignAsync(keyLabel, data, algorithm);

        /// <summary>
        /// Encrypts data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        public Task<byte[]> EncryptAsync(string keyLabel, byte[] data) => HsmEncryptAsync(keyLabel, data);

        /// <summary>
        /// Decrypts data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        public Task<byte[]> DecryptAsync(string keyLabel, byte[] data) => HsmDecryptAsync(keyLabel, data);

        /// <summary>
        /// Starts the HSM provider plugin.
        /// Override to perform custom startup operations.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the start operation.</returns>
        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Stops the HSM provider plugin.
        /// Automatically disconnects if connected.
        /// </summary>
        /// <returns>A task representing the stop operation.</returns>
        public override async Task StopAsync()
        {
            if (IsConnected)
            {
                await DisconnectAsync();
            }
        }

        /// <summary>
        /// Opens a session with the HSM.
        /// Implement PKCS#11 session initialization.
        /// </summary>
        /// <param name="slotId">Slot identifier.</param>
        /// <param name="pin">PIN for authentication.</param>
        /// <returns>A task representing the operation.</returns>
        protected abstract Task OpenSessionAsync(string slotId, string pin);

        /// <summary>
        /// Closes the HSM session.
        /// Implement PKCS#11 session cleanup.
        /// </summary>
        /// <returns>A task representing the operation.</returns>
        protected abstract Task CloseSessionAsync();

        /// <summary>
        /// Enumerates keys in the HSM.
        /// Implement PKCS#11 object enumeration.
        /// </summary>
        /// <returns>Array of key labels.</returns>
        protected abstract Task<string[]> EnumerateKeysAsync();

        /// <summary>
        /// Generates a key in the HSM.
        /// Implement PKCS#11 key generation.
        /// </summary>
        /// <param name="label">Key label.</param>
        /// <param name="spec">Key specification.</param>
        /// <returns>Key handle.</returns>
        protected abstract Task<byte[]> GenerateHsmKeyAsync(string label, HsmKeySpec spec);

        /// <summary>
        /// Signs data using HSM.
        /// Implement PKCS#11 signing operation.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to sign.</param>
        /// <param name="alg">Signature algorithm.</param>
        /// <returns>Signature.</returns>
        protected abstract Task<byte[]> HsmSignAsync(string keyLabel, byte[] data, HsmSignatureAlgorithm alg);

        /// <summary>
        /// Encrypts data using HSM.
        /// Implement PKCS#11 encryption operation.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        protected abstract Task<byte[]> HsmEncryptAsync(string keyLabel, byte[] data);

        /// <summary>
        /// Decrypts data using HSM.
        /// Implement PKCS#11 decryption operation.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        protected abstract Task<byte[]> HsmDecryptAsync(string keyLabel, byte[] data);

        /// <summary>
        /// Gets metadata for this plugin.
        /// </summary>
        /// <returns>Plugin metadata dictionary.</returns>
        protected override System.Collections.Generic.Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "HSM";
            metadata["IsConnected"] = IsConnected;
            metadata["ConnectedSlot"] = ConnectedSlot ?? "None";
            metadata["SupportsPKCS11"] = true;
            metadata["SupportsKeyGeneration"] = true;
            metadata["SupportsSigning"] = true;
            metadata["SupportsEncryption"] = true;
            return metadata;
        }
    }

    #region Intelligence Stub Types

    /// <summary>
    /// AI optimization hint for hardware accelerators.
    /// </summary>
    public record AcceleratorOptimizationHint
    {
        /// <summary>Recommended batch size for optimal throughput.</summary>
        public int RecommendedBatchSize { get; init; }

        /// <summary>Recommended concurrency level.</summary>
        public int RecommendedConcurrency { get; init; }

        /// <summary>Expected throughput improvement ratio.</summary>
        public double ExpectedImprovementRatio { get; init; }

        /// <summary>Reason for the recommendation.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// AI workload hint for Intel QAT accelerator.
    /// </summary>
    public record QatWorkloadHint
    {
        /// <summary>Recommended compression level.</summary>
        public int RecommendedCompressionLevel { get; init; }

        /// <summary>Whether to use QAT for this workload.</summary>
        public bool UseQat { get; init; }

        /// <summary>Expected speedup ratio vs software.</summary>
        public double ExpectedSpeedupRatio { get; init; }

        /// <summary>Reason for the recommendation.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// AI workload hint for GPU accelerator.
    /// </summary>
    public record GpuWorkloadHint
    {
        /// <summary>Recommended GPU device index.</summary>
        public int RecommendedDeviceIndex { get; init; }

        /// <summary>Recommended batch size.</summary>
        public int RecommendedBatchSize { get; init; }

        /// <summary>Whether to use GPU for this workload.</summary>
        public bool UseGpu { get; init; }

        /// <summary>Expected speedup ratio vs CPU.</summary>
        public double ExpectedSpeedupRatio { get; init; }
    }

    /// <summary>
    /// AI hint for key rotation in TPM.
    /// </summary>
    public record KeyRotationHint
    {
        /// <summary>Whether rotation is recommended.</summary>
        public bool RotationRecommended { get; init; }

        /// <summary>Recommended rotation interval.</summary>
        public TimeSpan RecommendedInterval { get; init; }

        /// <summary>Risk level if not rotated (0.0-1.0).</summary>
        public double RiskLevel { get; init; }

        /// <summary>Reason for the recommendation.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// AI hint for HSM key policy.
    /// </summary>
    public record HsmKeyPolicyHint
    {
        /// <summary>Recommended key algorithm.</summary>
        public string RecommendedAlgorithm { get; init; } = "RSA-4096";

        /// <summary>Recommended key usage constraints.</summary>
        public string[] RecommendedUsage { get; init; } = Array.Empty<string>();

        /// <summary>Whether hardware enforcement is recommended.</summary>
        public bool RequireHardwareEnforcement { get; init; }

        /// <summary>Reason for the recommendation.</summary>
        public string? Reason { get; init; }
    }

    #endregion
}
