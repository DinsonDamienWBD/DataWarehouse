using System;
using System.Threading.Tasks;

// FUTURE: Hardware accelerator contracts -- interfaces preserved for pluggable hardware
// acceleration (TPM2, HSM, QAT, GPU) per AD-06. These types have zero current implementations
// but define contracts for future hardware-acceleration-capable plugins. Do NOT delete during dead code cleanup.

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Hardware acceleration types supported by the DataWarehouse system.
    /// These flags can be combined to indicate support for multiple accelerator types.
    /// </summary>
    [Flags]
    public enum AcceleratorType
    {
        /// <summary>No hardware acceleration.</summary>
        None = 0,

        /// <summary>Intel QuickAssist Technology (QAT) - Compression and Encryption acceleration.</summary>
        IntelQAT = 1,

        /// <summary>NVIDIA GPU (CUDA) - Vector and matrix operations.</summary>
        NvidiaGpu = 2,

        /// <summary>AMD GPU (ROCm) - Vector and matrix operations.</summary>
        AmdGpu = 4,

        /// <summary>Intel AES-NI - AES encryption/decryption acceleration.</summary>
        IntelAesNi = 8,

        /// <summary>Intel AVX-512 - SIMD operations for vector processing.</summary>
        IntelAvx512 = 16,

        /// <summary>ARM SVE (Scalable Vector Extension) - ARM SIMD operations.</summary>
        ArmSve = 32,

        /// <summary>SmartNIC - Network protocol offload (TLS, IPsec, compression).</summary>
        SmartNic = 64,

        /// <summary>FPGA - Programmable hardware acceleration for custom operations.</summary>
        Fpga = 128,

        /// <summary>TPM 2.0 - Trusted Platform Module for secure key storage.</summary>
        Tpm2 = 256,

        /// <summary>HSM PCIe - Hardware Security Module over PCIe for cryptographic operations.</summary>
        HsmPcie = 512,

        /// <summary>OpenCL - Cross-vendor GPU/CPU/FPGA compute acceleration.</summary>
        OpenCL = 1024,

        /// <summary>SYCL - Intel oneAPI heterogeneous compute (CPU+GPU+FPGA).</summary>
        Sycl = 2048,

        /// <summary>Triton - GPU kernel compilation for ML workloads.</summary>
        Triton = 4096,

        /// <summary>CANN - Huawei Ascend NPU acceleration.</summary>
        Cann = 8192
    }

    /// <summary>
    /// Operations supported by hardware accelerators.
    /// </summary>
    public enum AcceleratorOperation
    {
        /// <summary>Compress data.</summary>
        Compress,

        /// <summary>Decompress data.</summary>
        Decompress,

        /// <summary>Encrypt data.</summary>
        Encrypt,

        /// <summary>Decrypt data.</summary>
        Decrypt,

        /// <summary>Calculate hash/digest.</summary>
        Hash,

        /// <summary>Vector multiplication.</summary>
        VectorMul,

        /// <summary>Custom operation defined by accelerator.</summary>
        Custom
    }

    /// <summary>
    /// Statistics for a hardware accelerator.
    /// </summary>
    /// <param name="Type">The accelerator type.</param>
    /// <param name="OperationsCompleted">Total number of operations completed.</param>
    /// <param name="AverageThroughputMBps">Average throughput in MB/s.</param>
    /// <param name="CurrentUtilization">Current utilization percentage (0.0-1.0).</param>
    /// <param name="TotalProcessingTime">Total time spent processing operations.</param>
    public record AcceleratorStatistics(
        AcceleratorType Type,
        long OperationsCompleted,
        double AverageThroughputMBps,
        double CurrentUtilization,
        TimeSpan TotalProcessingTime
    );

    /// <summary>
    /// Base interface for all hardware accelerators.
    /// Provides common operations for initializing and using hardware acceleration.
    /// </summary>
    public interface IHardwareAccelerator
    {
        /// <summary>
        /// Gets the type of hardware accelerator.
        /// </summary>
        AcceleratorType Type { get; }

        /// <summary>
        /// Gets whether this accelerator is available on the current system.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Gets whether this accelerator is using a CPU-based fallback implementation
        /// rather than actual hardware acceleration.
        /// </summary>
        bool IsCpuFallback { get; }

        /// <summary>
        /// Initializes the hardware accelerator.
        /// </summary>
        /// <returns>A task representing the initialization operation.</returns>
        Task InitializeAsync();

        /// <summary>
        /// Processes data using the hardware accelerator.
        /// </summary>
        /// <param name="data">Input data to process.</param>
        /// <param name="operation">The operation to perform.</param>
        /// <returns>The processed data.</returns>
        Task<byte[]> ProcessAsync(byte[] data, AcceleratorOperation operation);

        /// <summary>
        /// Gets statistics for this accelerator.
        /// </summary>
        /// <returns>Current accelerator statistics.</returns>
        Task<AcceleratorStatistics> GetStatisticsAsync();
    }

    /// <summary>
    /// Compression levels for Intel QAT accelerator.
    /// </summary>
    public enum QatCompressionLevel
    {
        /// <summary>Fast compression with lower ratio.</summary>
        Fast,

        /// <summary>Balanced compression speed and ratio.</summary>
        Balanced,

        /// <summary>Best compression ratio, slower speed.</summary>
        Best
    }

    /// <summary>
    /// Intel QuickAssist Technology (QAT) accelerator interface.
    /// Supports hardware-accelerated compression and encryption.
    /// </summary>
    public interface IQatAccelerator : IHardwareAccelerator
    {
        /// <summary>
        /// Compresses data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Data to compress.</param>
        /// <param name="level">Compression level.</param>
        /// <returns>Compressed data.</returns>
        Task<byte[]> CompressQatAsync(byte[] data, QatCompressionLevel level);

        /// <summary>
        /// Decompresses data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Compressed data.</param>
        /// <returns>Decompressed data.</returns>
        Task<byte[]> DecompressQatAsync(byte[] data);

        /// <summary>
        /// Encrypts data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Data to encrypt.</param>
        /// <param name="key">Encryption key.</param>
        /// <returns>Encrypted data.</returns>
        Task<byte[]> EncryptQatAsync(byte[] data, byte[] key);

        /// <summary>
        /// Decrypts data using Intel QAT hardware.
        /// </summary>
        /// <param name="data">Encrypted data.</param>
        /// <param name="key">Decryption key.</param>
        /// <returns>Decrypted data.</returns>
        Task<byte[]> DecryptQatAsync(byte[] data, byte[] key);
    }

    /// <summary>
    /// GPU runtime environments.
    /// </summary>
    public enum GpuRuntime
    {
        /// <summary>No GPU runtime available.</summary>
        None,

        /// <summary>NVIDIA CUDA runtime.</summary>
        Cuda,

        /// <summary>AMD ROCm runtime.</summary>
        RoCm,

        /// <summary>OpenCL runtime (cross-vendor).</summary>
        OpenCL,

        /// <summary>SYCL runtime (Intel oneAPI DPC++).</summary>
        Sycl,

        /// <summary>Triton GPU kernel compilation runtime.</summary>
        Triton,

        /// <summary>CANN runtime (Huawei Ascend NPU).</summary>
        Cann,

        /// <summary>Apple Metal runtime.</summary>
        Metal
    }

    /// <summary>
    /// GPU accelerator interface for CUDA/ROCm operations.
    /// Supports vector and matrix operations for ML/AI workloads.
    /// </summary>
    public interface IGpuAccelerator : IHardwareAccelerator
    {
        /// <summary>
        /// Gets the GPU runtime in use (CUDA, ROCm, etc.).
        /// </summary>
        GpuRuntime Runtime { get; }

        /// <summary>
        /// Gets the number of available GPU devices.
        /// </summary>
        int DeviceCount { get; }

        /// <summary>
        /// Performs element-wise vector multiplication on GPU.
        /// </summary>
        /// <param name="a">First vector.</param>
        /// <param name="b">Second vector.</param>
        /// <returns>Result vector (a[i] * b[i]).</returns>
        Task<float[]> VectorMultiplyAsync(float[] a, float[] b);

        /// <summary>
        /// Performs matrix multiplication on GPU.
        /// </summary>
        /// <param name="a">First matrix.</param>
        /// <param name="b">Second matrix.</param>
        /// <returns>Result matrix (a × b).</returns>
        Task<float[]> MatrixMultiplyAsync(float[,] a, float[,] b);

        /// <summary>
        /// Computes embeddings using GPU acceleration.
        /// </summary>
        /// <param name="input">Input vector.</param>
        /// <param name="weights">Weight matrix.</param>
        /// <returns>Embedding vector.</returns>
        Task<float[]> ComputeEmbeddingsAsync(float[] input, float[,] weights);
    }

    /// <summary>
    /// TPM 2.0 key types.
    /// </summary>
    public enum TpmKeyType
    {
        /// <summary>RSA 2048-bit key.</summary>
        Rsa2048,

        /// <summary>RSA 4096-bit key.</summary>
        Rsa4096,

        /// <summary>ECC P-256 key.</summary>
        EccP256,

        /// <summary>ECC P-384 key.</summary>
        EccP384
    }

    /// <summary>
    /// TPM 2.0 provider for secure key storage and cryptographic operations.
    /// Uses the Trusted Platform Module for hardware-backed security.
    /// </summary>
    public interface ITpm2Provider
    {
        /// <summary>
        /// Gets whether TPM 2.0 is available on this system.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Creates a new key in the TPM.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key.</param>
        /// <param name="type">Key type to create.</param>
        /// <returns>Public key data.</returns>
        Task<byte[]> CreateKeyAsync(string keyId, TpmKeyType type);

        /// <summary>
        /// Signs data using a TPM-stored key.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Data to sign.</param>
        /// <returns>Signature.</returns>
        Task<byte[]> SignAsync(string keyId, byte[] data);

        /// <summary>
        /// Encrypts data using a TPM-stored key.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        Task<byte[]> EncryptAsync(string keyId, byte[] data);

        /// <summary>
        /// Decrypts data using a TPM-stored key.
        /// </summary>
        /// <param name="keyId">Key identifier.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        Task<byte[]> DecryptAsync(string keyId, byte[] data);

        /// <summary>
        /// Gets cryptographically secure random bytes from the TPM.
        /// </summary>
        /// <param name="length">Number of random bytes to generate.</param>
        /// <returns>Random bytes.</returns>
        Task<byte[]> GetRandomAsync(int length);
    }

    /// <summary>
    /// HSM key specification for key generation.
    /// </summary>
    /// <param name="Algorithm">Algorithm name (e.g., "RSA", "ECC").</param>
    /// <param name="KeySizeBits">Key size in bits.</param>
    /// <param name="Exportable">Whether the key can be exported.</param>
    public record HsmKeySpec(string Algorithm, int KeySizeBits, bool Exportable);

    /// <summary>
    /// HSM signature algorithms.
    /// </summary>
    public enum HsmSignatureAlgorithm
    {
        /// <summary>RSA PKCS#1 v1.5 with SHA-256.</summary>
        RsaPkcs1Sha256,

        /// <summary>RSA-PSS with SHA-256.</summary>
        RsaPssSha256,

        /// <summary>ECDSA P-256 with SHA-256.</summary>
        EcdsaP256Sha256,

        /// <summary>ECDSA P-384 with SHA-384.</summary>
        EcdsaP384Sha384
    }

    /// <summary>
    /// Hardware Security Module (HSM) provider interface.
    /// Provides PKCS#11-based access to HSM devices for cryptographic operations.
    /// </summary>
    public interface IHsmProvider
    {
        /// <summary>
        /// Gets whether the HSM is currently connected.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connects to the HSM with the specified slot and PIN.
        /// </summary>
        /// <param name="slotId">HSM slot identifier.</param>
        /// <param name="pin">PIN for authentication.</param>
        /// <returns>A task representing the connection operation.</returns>
        Task ConnectAsync(string slotId, string pin);

        /// <summary>
        /// Disconnects from the HSM.
        /// </summary>
        /// <returns>A task representing the disconnection operation.</returns>
        Task DisconnectAsync();

        /// <summary>
        /// Lists all keys stored in the HSM.
        /// </summary>
        /// <returns>Array of key labels.</returns>
        Task<string[]> ListKeysAsync();

        /// <summary>
        /// Generates a new key in the HSM.
        /// </summary>
        /// <param name="label">Key label.</param>
        /// <param name="spec">Key specification.</param>
        /// <returns>Key handle or identifier.</returns>
        Task<byte[]> GenerateKeyAsync(string label, HsmKeySpec spec);

        /// <summary>
        /// Signs data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to sign.</param>
        /// <param name="algorithm">Signature algorithm.</param>
        /// <returns>Signature.</returns>
        Task<byte[]> SignAsync(string keyLabel, byte[] data, HsmSignatureAlgorithm algorithm);

        /// <summary>
        /// Encrypts data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        Task<byte[]> EncryptAsync(string keyLabel, byte[] data);

        /// <summary>
        /// Decrypts data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        Task<byte[]> DecryptAsync(string keyLabel, byte[] data);
    }

    /// <summary>
    /// SmartNIC offload interface.
    /// Provides hardware acceleration for network protocols and compression.
    /// </summary>
    public interface ISmartNicOffload
    {
        /// <summary>
        /// Gets whether SmartNIC offload is available.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Offloads TLS encryption/decryption to SmartNIC.
        /// </summary>
        /// <param name="socketFd">Socket file descriptor.</param>
        /// <returns>True if offload was successful.</returns>
        Task<bool> OffloadTlsAsync(int socketFd);

        /// <summary>
        /// Offloads IPsec encryption/decryption to SmartNIC.
        /// </summary>
        /// <param name="socketFd">Socket file descriptor.</param>
        /// <param name="key">IPsec key.</param>
        /// <returns>True if offload was successful.</returns>
        Task<bool> OffloadIpsecAsync(int socketFd, byte[] key);

        /// <summary>
        /// Offloads data compression/decompression to SmartNIC.
        /// </summary>
        /// <param name="socketFd">Socket file descriptor.</param>
        /// <returns>True if offload was successful.</returns>
        Task<bool> OffloadCompressionAsync(int socketFd);
    }
}
