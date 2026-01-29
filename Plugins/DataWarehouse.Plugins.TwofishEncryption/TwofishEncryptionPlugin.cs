using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.TwofishEncryption;

/// <summary>
/// Production-ready Twofish-256-CTR-HMAC encryption plugin for DataWarehouse.
/// Implements the full Twofish block cipher per the official specification.
/// Extends EncryptionPluginBase for composable key management with Direct and Envelope modes.
///
/// Security Features:
/// - Full Twofish-256 block cipher implementation per official specification
/// - CTR (Counter) mode for encryption providing streaming capability
/// - HMAC-SHA256 authentication tag for integrity verification
/// - Q0/Q1 permutation tables from Twofish specification
/// - Reed-Solomon matrix for key schedule over GF(2^8)
/// - MDS matrix application for diffusion
/// - 256-bit key, 16-byte blocks, 16 rounds
/// - Composable key management: Direct mode (IKeyStore) or Envelope mode (IEnvelopeKeyStore)
/// - Secure memory clearing for sensitive data (CryptographicOperations.ZeroMemory)
///
/// Data Format (when using manifest-based metadata):
/// [IV:16][Tag:32][Ciphertext]
///
/// Legacy Format (backward compatibility for old encrypted files):
/// [KeyIdLength:4][KeyId:variable][Nonce:16][Tag:32][Ciphertext]
///
/// Thread Safety: All operations are thread-safe.
///
/// Message Commands:
/// - twofish.encryption.configure: Configure encryption settings
/// - twofish.encryption.stats: Get encryption statistics
/// - twofish.encryption.setKeyStore: Set the key store
/// </summary>
public sealed class TwofishEncryptionPlugin : EncryptionPluginBase
{
    private readonly TwofishEncryptionConfig _config;

    /// <summary>
    /// Block size for Twofish (128 bits = 16 bytes).
    /// </summary>
    private const int BlockSizeBytes = 16;

    /// <summary>
    /// Maximum key ID length for legacy format.
    /// </summary>
    private const int MaxKeyIdLength = 64;

    /// <summary>
    /// Number of Twofish rounds.
    /// </summary>
    private const int NumRounds = 16;

    #region Abstract Property Overrides

    /// <inheritdoc/>
    protected override int KeySizeBytes => 32; // 256 bits

    /// <inheritdoc/>
    protected override int IvSizeBytes => 16; // 128 bits for CTR mode

    /// <inheritdoc/>
    protected override int TagSizeBytes => 32; // 256-bit HMAC-SHA256

    /// <inheritdoc/>
    protected override string AlgorithmId => "Twofish-256-CTR-HMAC";

    #endregion

    #region Twofish Specification Tables

    /// <summary>
    /// Q0 permutation table from Twofish specification.
    /// </summary>
    private static readonly byte[] Q0 = new byte[]
    {
        0xA9, 0x67, 0xB3, 0xE8, 0x04, 0xFD, 0xA3, 0x76, 0x9A, 0x92, 0x80, 0x78, 0xE4, 0xDD, 0xD1, 0x38,
        0x0D, 0xC6, 0x35, 0x98, 0x18, 0xF7, 0xEC, 0x6C, 0x43, 0x75, 0x37, 0x26, 0xFA, 0x13, 0x94, 0x48,
        0xF2, 0xD0, 0x8B, 0x30, 0x84, 0x54, 0xDF, 0x23, 0x19, 0x5B, 0x3D, 0x59, 0xF3, 0xAE, 0xA2, 0x82,
        0x63, 0x01, 0x83, 0x2E, 0xD9, 0x51, 0x9B, 0x7C, 0xA6, 0xEB, 0xA5, 0xBE, 0x16, 0x0C, 0xE3, 0x61,
        0xC0, 0x8C, 0x3A, 0xF5, 0x73, 0x2C, 0x25, 0x0B, 0xBB, 0x4E, 0x89, 0x6B, 0x53, 0x6A, 0xB4, 0xF1,
        0xE1, 0xE6, 0xBD, 0x45, 0xE2, 0xF4, 0xB6, 0x66, 0xCC, 0x95, 0x03, 0x56, 0xD4, 0x1C, 0x1E, 0xD7,
        0xFB, 0xC3, 0x8E, 0xB5, 0xE9, 0xCF, 0xBF, 0xBA, 0xEA, 0x77, 0x39, 0xAF, 0x33, 0xC9, 0x62, 0x71,
        0x81, 0x79, 0x09, 0xAD, 0x24, 0xCD, 0xF9, 0xD8, 0xE5, 0xC5, 0xB9, 0x4D, 0x44, 0x08, 0x86, 0xE7,
        0xA1, 0x1D, 0xAA, 0xED, 0x06, 0x70, 0xB2, 0xD2, 0x41, 0x7B, 0xA0, 0x11, 0x31, 0xC2, 0x27, 0x90,
        0x20, 0xF6, 0x60, 0xFF, 0x96, 0x5C, 0xB1, 0xAB, 0x9E, 0x9C, 0x52, 0x1B, 0x5F, 0x93, 0x0A, 0xEF,
        0x91, 0x85, 0x49, 0xEE, 0x2D, 0x4F, 0x8F, 0x3B, 0x47, 0x87, 0x6D, 0x46, 0xD6, 0x3E, 0x69, 0x64,
        0x2A, 0xCE, 0xCB, 0x2F, 0xFC, 0x97, 0x05, 0x7A, 0xAC, 0x7F, 0xD5, 0x1A, 0x4B, 0x0E, 0xA7, 0x5A,
        0x28, 0x14, 0x3F, 0x29, 0x88, 0x3C, 0x4C, 0x02, 0xB8, 0xDA, 0xB0, 0x17, 0x55, 0x1F, 0x8A, 0x7D,
        0x57, 0xC7, 0x8D, 0x74, 0xB7, 0xC4, 0x9F, 0x72, 0x7E, 0x15, 0x22, 0x12, 0x58, 0x07, 0x99, 0x34,
        0x6E, 0x50, 0xDE, 0x68, 0x65, 0xBC, 0xDB, 0xF8, 0xC8, 0xA8, 0x2B, 0x40, 0xDC, 0xFE, 0x32, 0xA4,
        0xCA, 0x10, 0x21, 0xF0, 0xD3, 0x5D, 0x0F, 0x00, 0x6F, 0x9D, 0x36, 0x42, 0x4A, 0x5E, 0xC1, 0xE0
    };

    /// <summary>
    /// Q1 permutation table from Twofish specification.
    /// </summary>
    private static readonly byte[] Q1 = new byte[]
    {
        0x75, 0xF3, 0xC6, 0xF4, 0xDB, 0x7B, 0xFB, 0xC8, 0x4A, 0xD3, 0xE6, 0x6B, 0x45, 0x7D, 0xE8, 0x4B,
        0xD6, 0x32, 0xD8, 0xFD, 0x37, 0x71, 0xF1, 0xE1, 0x30, 0x0F, 0xF8, 0x1B, 0x87, 0xFA, 0x06, 0x3F,
        0x5E, 0xBA, 0xAE, 0x5B, 0x8A, 0x00, 0xBC, 0x9D, 0x6D, 0xC1, 0xB1, 0x0E, 0x80, 0x5D, 0xD2, 0xD5,
        0xA0, 0x84, 0x07, 0x14, 0xB5, 0x90, 0x2C, 0xA3, 0xB2, 0x73, 0x4C, 0x54, 0x92, 0x74, 0x36, 0x51,
        0x38, 0xB0, 0xBD, 0x5A, 0xFC, 0x60, 0x62, 0x96, 0x6C, 0x42, 0xF7, 0x10, 0x7C, 0x28, 0x27, 0x8C,
        0x13, 0x95, 0x9C, 0xC7, 0x24, 0x46, 0x3B, 0x70, 0xCA, 0xE3, 0x85, 0xCB, 0x11, 0xD0, 0x93, 0xB8,
        0xA6, 0x83, 0x20, 0xFF, 0x9F, 0x77, 0xC3, 0xCC, 0x03, 0x6F, 0x08, 0xBF, 0x40, 0xE7, 0x2B, 0xE2,
        0x79, 0x0C, 0xAA, 0x82, 0x41, 0x3A, 0xEA, 0xB9, 0xE4, 0x9A, 0xA4, 0x97, 0x7E, 0xDA, 0x7A, 0x17,
        0x66, 0x94, 0xA1, 0x1D, 0x3D, 0xF0, 0xDE, 0xB3, 0x0B, 0x72, 0xA7, 0x1C, 0xEF, 0xD1, 0x53, 0x3E,
        0x8F, 0x33, 0x26, 0x5F, 0xEC, 0x76, 0x2A, 0x49, 0x81, 0x88, 0xEE, 0x21, 0xC4, 0x1A, 0xEB, 0xD9,
        0xC5, 0x39, 0x99, 0xCD, 0xAD, 0x31, 0x8B, 0x01, 0x18, 0x23, 0xDD, 0x1F, 0x4E, 0x2D, 0xF9, 0x48,
        0x4F, 0xF2, 0x65, 0x8E, 0x78, 0x5C, 0x58, 0x19, 0x8D, 0xE5, 0x98, 0x57, 0x67, 0x7F, 0x05, 0x64,
        0xAF, 0x63, 0xB6, 0xFE, 0xF5, 0xB7, 0x3C, 0xA5, 0xCE, 0xE9, 0x68, 0x44, 0xE0, 0x4D, 0x43, 0x69,
        0x29, 0x2E, 0xAC, 0x15, 0x59, 0xA8, 0x0A, 0x9E, 0x6E, 0x47, 0xDF, 0x34, 0x35, 0x6A, 0xCF, 0xDC,
        0x22, 0xC9, 0xC0, 0x9B, 0x89, 0xD4, 0xED, 0xAB, 0x12, 0xA2, 0x0D, 0x52, 0xBB, 0x02, 0x2F, 0xA9,
        0xD7, 0x61, 0x1E, 0xB4, 0x50, 0x04, 0xF6, 0xC2, 0x16, 0x25, 0x86, 0x56, 0x55, 0x09, 0xBE, 0x91
    };

    /// <summary>
    /// Reed-Solomon matrix for key schedule over GF(2^8) with polynomial 0x14D.
    /// Used to compute S-box keys from the input key material.
    /// </summary>
    private static readonly byte[,] RS = new byte[,]
    {
        { 0x01, 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E },
        { 0xA4, 0x56, 0x82, 0xF3, 0x1E, 0xC6, 0x68, 0xE5 },
        { 0x02, 0xA1, 0xFC, 0xC1, 0x47, 0xAE, 0x3D, 0x19 },
        { 0xA4, 0x55, 0x87, 0x5A, 0x58, 0xDB, 0x9E, 0x03 }
    };

    #endregion

    #region Twofish Key Schedule Context

    /// <summary>
    /// Twofish key schedule context containing expanded subkeys and S-box keys.
    /// </summary>
    private sealed class TwofishContext : IDisposable
    {
        /// <summary>
        /// 40 subkeys for whitening and round keys.
        /// </summary>
        public uint[] SubKeys = new uint[40];

        /// <summary>
        /// S-box keys derived from Reed-Solomon computation.
        /// </summary>
        public uint[] SBoxKeys = new uint[4];

        /// <summary>
        /// Key length in 64-bit words (4 for 256-bit keys).
        /// </summary>
        public int KeyLength;

        /// <summary>
        /// Securely clears the key schedule from memory.
        /// </summary>
        public void Dispose()
        {
            if (SubKeys != null)
            {
                Array.Clear(SubKeys, 0, SubKeys.Length);
            }
            if (SBoxKeys != null)
            {
                Array.Clear(SBoxKeys, 0, SBoxKeys.Length);
            }
        }
    }

    #endregion

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.encryption.twofish256";

    /// <inheritdoc/>
    public override string Name => "Twofish-256-CTR-HMAC Encryption";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SubCategory => "Encryption";

    /// <inheritdoc/>
    public override int QualityLevel => 90;

    /// <inheritdoc/>
    public override int DefaultOrder => 90;

    /// <inheritdoc/>
    public override bool AllowBypass => false;

    /// <inheritdoc/>
    public override string[] RequiredPrecedingStages => new[] { "Compression" };

    /// <inheritdoc/>
    public override string[] IncompatibleStages => new[] { "encryption.fips", "encryption.aes256", "encryption.chacha20" };

    /// <summary>
    /// Initializes a new instance of the Twofish encryption plugin.
    /// </summary>
    /// <param name="config">Optional configuration. If null, defaults are used.</param>
    public TwofishEncryptionPlugin(TwofishEncryptionConfig? config = null)
    {
        _config = config ?? new TwofishEncryptionConfig();

        // Set defaults from config (backward compatibility)
        if (_config.KeyStore != null)
        {
            DefaultKeyStore = _config.KeyStore;
        }
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "twofish.encryption.configure", DisplayName = "Configure", Description = "Configure Twofish encryption settings" },
            new() { Name = "twofish.encryption.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
            new() { Name = "twofish.encryption.setKeyStore", DisplayName = "Set Key Store", Description = "Configure the key store" },
            new() { Name = "twofish.encryption.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using Twofish-256-CTR-HMAC" },
            new() { Name = "twofish.encryption.decrypt", DisplayName = "Decrypt", Description = "Decrypt Twofish-256-CTR-HMAC encrypted data" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Algorithm"] = AlgorithmId;
        metadata["BlockCipher"] = "Twofish";
        metadata["Mode"] = "CTR";
        metadata["Authentication"] = "HMAC-SHA256";
        metadata["KeySize"] = KeySizeBytes * 8;
        metadata["BlockSize"] = BlockSizeBytes * 8;
        metadata["IVSize"] = IvSizeBytes * 8;
        metadata["TagSize"] = TagSizeBytes * 8;
        metadata["Rounds"] = NumRounds;
        metadata["SupportsKeyRotation"] = true;
        metadata["SupportsStreaming"] = true;
        metadata["RequiresKeyStore"] = true;
        metadata["SupportedModes"] = new[] { "Direct", "Envelope" };
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "twofish.encryption.configure" => HandleConfigureAsync(message),
            "twofish.encryption.stats" => HandleStatsAsync(message),
            "twofish.encryption.setKeyStore" => HandleSetKeyStoreAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Core Encryption/Decryption (Algorithm-Specific)

    /// <summary>
    /// Performs Twofish-256-CTR-HMAC encryption on the input stream.
    /// Base class handles key resolution, config resolution, and statistics.
    /// </summary>
    /// <param name="input">The plaintext input stream.</param>
    /// <param name="key">The encryption key (provided by base class).</param>
    /// <param name="iv">The initialization vector (nonce for CTR mode, provided by base class).</param>
    /// <param name="context">The kernel context for logging.</param>
    /// <returns>A stream containing [IV:16][Tag:32][Ciphertext].</returns>
    /// <exception cref="CryptographicException">Thrown on encryption failure.</exception>
    protected override async Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context)
    {
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? macKey = null;
        TwofishContext? twofishCtx = null;

        try
        {
            // Read all input data
            using var inputMs = new MemoryStream();
            await input.CopyToAsync(inputMs);
            plaintext = inputMs.ToArray();

            // Derive separate MAC key from encryption key using HKDF-like derivation
            macKey = SHA256.HashData(key);

            // Initialize Twofish context
            twofishCtx = InitializeContext(key);

            // Encrypt using CTR mode
            ciphertext = new byte[plaintext.Length];
            var counter = new byte[BlockSizeBytes];
            Array.Copy(iv, counter, IvSizeBytes);

            for (int i = 0; i < plaintext.Length; i += BlockSizeBytes)
            {
                var block = TwofishEncryptBlock(counter, twofishCtx);
                var blockLen = Math.Min(BlockSizeBytes, plaintext.Length - i);

                for (int j = 0; j < blockLen; j++)
                {
                    ciphertext[i + j] = (byte)(plaintext[i + j] ^ block[j]);
                }

                CryptographicOperations.ZeroMemory(block);
                IncrementCounter(counter);
            }

            CryptographicOperations.ZeroMemory(counter);

            // Compute HMAC-SHA256 tag over IV and ciphertext
            var dataToMac = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, dataToMac, iv.Length);
            Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
            tag = HMACSHA256.HashData(macKey, dataToMac);
            CryptographicOperations.ZeroMemory(dataToMac);

            // Build output: [IV:16][Tag:32][Ciphertext]
            // Note: Key info is now stored in EncryptionMetadata by base class
            var outputLength = IvSizeBytes + TagSizeBytes + ciphertext.Length;
            var output = new byte[outputLength];
            var pos = 0;

            // Write IV (16 bytes)
            iv.CopyTo(output, pos);
            pos += IvSizeBytes;

            // Write authentication tag (32 bytes)
            tag.CopyTo(output, pos);
            pos += TagSizeBytes;

            // Write ciphertext
            ciphertext.CopyTo(output, pos);

            context.LogDebug($"Twofish-256-CTR-HMAC encrypted {plaintext.Length} bytes");

            return new MemoryStream(output);
        }
        finally
        {
            // Security: Clear sensitive data from memory
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
            if (macKey != null) CryptographicOperations.ZeroMemory(macKey);
            twofishCtx?.Dispose();
        }
    }

    /// <summary>
    /// Performs Twofish-256-CTR-HMAC decryption on the input stream.
    /// Base class handles key resolution, config resolution, and statistics.
    /// Supports both new format [IV:16][Tag:32][Ciphertext] and legacy format with key ID header.
    /// </summary>
    /// <param name="input">The encrypted input stream.</param>
    /// <param name="key">The decryption key (provided by base class).</param>
    /// <param name="iv">The initialization vector (null if embedded in ciphertext).</param>
    /// <param name="context">The kernel context for logging.</param>
    /// <returns>The decrypted stream and authentication tag.</returns>
    /// <exception cref="CryptographicException">
    /// Thrown on decryption failure or HMAC verification failure.
    /// </exception>
    protected override async Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv, IKernelContext context)
    {
        byte[]? encryptedData = null;
        byte[]? plaintext = null;
        byte[]? macKey = null;
        TwofishContext? twofishCtx = null;

        try
        {
            // Read all encrypted data
            using var inputMs = new MemoryStream();
            await input.CopyToAsync(inputMs);
            encryptedData = inputMs.ToArray();

            // Check if this is legacy format (has key ID header)
            var isLegacyFormat = IsLegacyFormat(encryptedData);

            var pos = 0;

            if (isLegacyFormat)
            {
                // Legacy format: [KeyIdLength:4][KeyId:variable][IV:16][Tag:32][Ciphertext]
                var legacyHeaderSize = 4 + MaxKeyIdLength + IvSizeBytes + TagSizeBytes;
                if (encryptedData.Length < legacyHeaderSize)
                    throw new CryptographicException($"Legacy encrypted data too short. Minimum size is {legacyHeaderSize} bytes.");

                // Read key ID length
                var keyIdLength = BitConverter.ToInt32(encryptedData, pos);
                pos += 4;

                // Skip key ID (already resolved by base class)
                pos += keyIdLength;
            }

            // Parse: [IV:16][Tag:32][Ciphertext]
            var remainingLength = encryptedData.Length - pos;
            if (remainingLength < IvSizeBytes + TagSizeBytes)
                throw new CryptographicException("Encrypted data too short");

            // If IV not provided by base class, read from data
            if (iv == null)
            {
                iv = new byte[IvSizeBytes];
                Array.Copy(encryptedData, pos, iv, 0, IvSizeBytes);
                pos += IvSizeBytes;
            }
            else
            {
                // IV provided by base class (from metadata), skip in data
                pos += IvSizeBytes;
            }

            // Read authentication tag (32 bytes)
            var storedTag = new byte[TagSizeBytes];
            Array.Copy(encryptedData, pos, storedTag, 0, TagSizeBytes);
            pos += TagSizeBytes;

            // Read ciphertext (remaining bytes)
            var ciphertextLength = encryptedData.Length - pos;
            var ciphertext = new byte[ciphertextLength];
            if (ciphertextLength > 0)
            {
                Array.Copy(encryptedData, pos, ciphertext, 0, ciphertextLength);
            }

            // Derive MAC key
            macKey = SHA256.HashData(key);

            // Verify HMAC-SHA256 tag before decryption (authenticate-then-decrypt)
            var dataToMac = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, dataToMac, iv.Length);
            Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
            var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
            CryptographicOperations.ZeroMemory(dataToMac);

            if (!CryptographicOperations.FixedTimeEquals(expectedTag, storedTag))
            {
                CryptographicOperations.ZeroMemory(expectedTag);
                throw new CryptographicException("HMAC verification failed. Data may be corrupted or tampered with.");
            }
            CryptographicOperations.ZeroMemory(expectedTag);

            // Initialize Twofish context
            twofishCtx = InitializeContext(key);

            // Decrypt using CTR mode
            plaintext = new byte[ciphertextLength];
            var counter = new byte[BlockSizeBytes];
            Array.Copy(iv, counter, IvSizeBytes);

            for (int i = 0; i < ciphertext.Length; i += BlockSizeBytes)
            {
                var block = TwofishEncryptBlock(counter, twofishCtx);
                var blockLen = Math.Min(BlockSizeBytes, ciphertext.Length - i);

                for (int j = 0; j < blockLen; j++)
                {
                    plaintext[i + j] = (byte)(ciphertext[i + j] ^ block[j]);
                }

                CryptographicOperations.ZeroMemory(block);
                IncrementCounter(counter);
            }

            CryptographicOperations.ZeroMemory(counter);
            CryptographicOperations.ZeroMemory(ciphertext);

            context.LogDebug($"Twofish-256-CTR-HMAC decrypted {ciphertextLength} bytes");

            // Return a copy since we'll zero the original
            var result = new byte[plaintext.Length];
            Array.Copy(plaintext, result, plaintext.Length);
            return (new MemoryStream(result), storedTag);
        }
        finally
        {
            // Security: Clear sensitive data from memory
            if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            if (macKey != null) CryptographicOperations.ZeroMemory(macKey);
            twofishCtx?.Dispose();
        }
    }

    /// <summary>
    /// Checks if the encrypted data uses the legacy format with key ID header.
    /// </summary>
    private bool IsLegacyFormat(byte[] data)
    {
        if (data.Length < 4 + MaxKeyIdLength)
            return false;

        // Read key ID length from header
        var keyIdLength = BitConverter.ToInt32(data, 0);

        // Legacy format has a valid key ID length (1 to MaxKeyIdLength)
        return keyIdLength > 0 && keyIdLength <= MaxKeyIdLength;
    }

    #endregion

    #region Twofish Implementation

    /// <summary>
    /// Initializes the Twofish key schedule context.
    /// </summary>
    /// <param name="key">The 256-bit encryption key.</param>
    /// <returns>The initialized Twofish context.</returns>
    private static TwofishContext InitializeContext(byte[] key)
    {
        var ctx = new TwofishContext();
        int keyLen = key.Length;
        ctx.KeyLength = keyLen / 8; // Number of 64-bit words (4 for 256-bit key)

        var Me = new uint[ctx.KeyLength];
        var Mo = new uint[ctx.KeyLength];

        // Split key into even and odd words
        for (int i = 0; i < ctx.KeyLength; i++)
        {
            Me[i] = BitConverter.ToUInt32(key, 8 * i);
            Mo[i] = BitConverter.ToUInt32(key, 8 * i + 4);
        }

        // Compute S-box keys using Reed-Solomon matrix
        for (int i = 0; i < ctx.KeyLength; i++)
        {
            ctx.SBoxKeys[ctx.KeyLength - 1 - i] = ComputeReedSolomon(key, 8 * i);
        }

        // Generate round subkeys using the h function
        uint rho = 0x01010101;
        for (int i = 0; i < 20; i++)
        {
            uint A = HFunction(2 * (uint)i * rho, Me, ctx.KeyLength);
            uint B = HFunction((2 * (uint)i + 1) * rho, Mo, ctx.KeyLength);
            B = RotateLeft(B, 8);

            ctx.SubKeys[2 * i] = A + B;
            ctx.SubKeys[2 * i + 1] = RotateLeft(A + 2 * B, 9);
        }

        // Clear temporary arrays
        Array.Clear(Me, 0, Me.Length);
        Array.Clear(Mo, 0, Mo.Length);

        return ctx;
    }

    /// <summary>
    /// Computes Reed-Solomon code word for key schedule.
    /// Uses GF(2^8) with polynomial 0x14D.
    /// </summary>
    private static uint ComputeReedSolomon(byte[] key, int offset)
    {
        var result = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = 0;
            for (int j = 0; j < 8; j++)
            {
                result[i] ^= GfMult(RS[i, j], key[offset + j], 0x14D);
            }
        }
        return BitConverter.ToUInt32(result, 0);
    }

    /// <summary>
    /// The h function used in key schedule for generating subkeys.
    /// </summary>
    private static uint HFunction(uint X, uint[] L, int k)
    {
        byte[] y = new byte[4];
        y[0] = (byte)X;
        y[1] = (byte)(X >> 8);
        y[2] = (byte)(X >> 16);
        y[3] = (byte)(X >> 24);

        if (k == 4)
        {
            y[0] = Q1[y[0] ^ (byte)L[3]];
            y[1] = Q0[y[1] ^ (byte)(L[3] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(L[3] >> 16)];
            y[3] = Q1[y[3] ^ (byte)(L[3] >> 24)];
        }

        if (k >= 3)
        {
            y[0] = Q1[y[0] ^ (byte)L[2]];
            y[1] = Q1[y[1] ^ (byte)(L[2] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(L[2] >> 16)];
            y[3] = Q0[y[3] ^ (byte)(L[2] >> 24)];
        }

        y[0] = Q1[Q0[Q0[y[0] ^ (byte)L[1]] ^ (byte)L[0]]];
        y[1] = Q0[Q0[Q1[y[1] ^ (byte)(L[1] >> 8)] ^ (byte)(L[0] >> 8)]];
        y[2] = Q1[Q1[Q0[y[2] ^ (byte)(L[1] >> 16)] ^ (byte)(L[0] >> 16)]];
        y[3] = Q0[Q1[Q1[y[3] ^ (byte)(L[1] >> 24)] ^ (byte)(L[0] >> 24)]];

        return ApplyMDS(y);
    }

    /// <summary>
    /// The g function for round transformation using key-dependent S-boxes.
    /// </summary>
    private static uint GFunction(uint X, uint[] sBoxKeys, int k)
    {
        byte[] y = new byte[4];
        y[0] = (byte)X;
        y[1] = (byte)(X >> 8);
        y[2] = (byte)(X >> 16);
        y[3] = (byte)(X >> 24);

        if (k == 4)
        {
            y[0] = Q1[y[0] ^ (byte)sBoxKeys[3]];
            y[1] = Q0[y[1] ^ (byte)(sBoxKeys[3] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(sBoxKeys[3] >> 16)];
            y[3] = Q1[y[3] ^ (byte)(sBoxKeys[3] >> 24)];
        }

        if (k >= 3)
        {
            y[0] = Q1[y[0] ^ (byte)sBoxKeys[2]];
            y[1] = Q1[y[1] ^ (byte)(sBoxKeys[2] >> 8)];
            y[2] = Q0[y[2] ^ (byte)(sBoxKeys[2] >> 16)];
            y[3] = Q0[y[3] ^ (byte)(sBoxKeys[2] >> 24)];
        }

        y[0] = Q1[Q0[Q0[y[0] ^ (byte)sBoxKeys[1]] ^ (byte)sBoxKeys[0]]];
        y[1] = Q0[Q0[Q1[y[1] ^ (byte)(sBoxKeys[1] >> 8)] ^ (byte)(sBoxKeys[0] >> 8)]];
        y[2] = Q1[Q1[Q0[y[2] ^ (byte)(sBoxKeys[1] >> 16)] ^ (byte)(sBoxKeys[0] >> 16)]];
        y[3] = Q0[Q1[Q1[y[3] ^ (byte)(sBoxKeys[1] >> 24)] ^ (byte)(sBoxKeys[0] >> 24)]];

        return ApplyMDS(y);
    }

    /// <summary>
    /// Applies the MDS matrix multiplication for diffusion.
    /// Uses GF(2^8) with polynomial 0x169.
    /// </summary>
    private static uint ApplyMDS(byte[] y)
    {
        byte[] result = new byte[4];

        result[0] = (byte)(GfMult(0x01, y[0], 0x169) ^ GfMult(0xEF, y[1], 0x169) ^
                          GfMult(0x5B, y[2], 0x169) ^ GfMult(0x5B, y[3], 0x169));
        result[1] = (byte)(GfMult(0x5B, y[0], 0x169) ^ GfMult(0xEF, y[1], 0x169) ^
                          GfMult(0xEF, y[2], 0x169) ^ GfMult(0x01, y[3], 0x169));
        result[2] = (byte)(GfMult(0xEF, y[0], 0x169) ^ GfMult(0x5B, y[1], 0x169) ^
                          GfMult(0x01, y[2], 0x169) ^ GfMult(0xEF, y[3], 0x169));
        result[3] = (byte)(GfMult(0xEF, y[0], 0x169) ^ GfMult(0x01, y[1], 0x169) ^
                          GfMult(0xEF, y[2], 0x169) ^ GfMult(0x5B, y[3], 0x169));

        return BitConverter.ToUInt32(result, 0);
    }

    /// <summary>
    /// Galois Field multiplication over GF(2^8).
    /// </summary>
    /// <param name="a">First operand.</param>
    /// <param name="b">Second operand.</param>
    /// <param name="poly">Irreducible polynomial for the field.</param>
    /// <returns>Product in GF(2^8).</returns>
    private static byte GfMult(byte a, byte b, int poly)
    {
        int result = 0;
        int aa = a;
        int bb = b;

        while (aa != 0)
        {
            if ((aa & 1) != 0)
                result ^= bb;

            bb <<= 1;
            if ((bb & 0x100) != 0)
                bb ^= poly;

            aa >>= 1;
        }

        return (byte)result;
    }

    /// <summary>
    /// The F function - core round function combining two g functions.
    /// </summary>
    private static void FFunction(uint R0, uint R1, uint[] sBoxKeys, int k,
                                  uint subKey0, uint subKey1, out uint F0, out uint F1)
    {
        uint T0 = GFunction(R0, sBoxKeys, k);
        uint T1 = GFunction(RotateLeft(R1, 8), sBoxKeys, k);

        F0 = T0 + T1 + subKey0;
        F1 = T0 + 2 * T1 + subKey1;
    }

    /// <summary>
    /// Encrypts a single 16-byte block using Twofish.
    /// </summary>
    /// <param name="block">The 16-byte input block.</param>
    /// <param name="ctx">The Twofish context with key schedule.</param>
    /// <returns>The 16-byte encrypted block.</returns>
    private static byte[] TwofishEncryptBlock(byte[] block, TwofishContext ctx)
    {
        // Input whitening
        uint R0 = BitConverter.ToUInt32(block, 0) ^ ctx.SubKeys[0];
        uint R1 = BitConverter.ToUInt32(block, 4) ^ ctx.SubKeys[1];
        uint R2 = BitConverter.ToUInt32(block, 8) ^ ctx.SubKeys[2];
        uint R3 = BitConverter.ToUInt32(block, 12) ^ ctx.SubKeys[3];

        // 16 rounds of Feistel network
        for (int round = 0; round < NumRounds; round++)
        {
            FFunction(R0, R1, ctx.SBoxKeys, ctx.KeyLength,
                     ctx.SubKeys[8 + 2 * round], ctx.SubKeys[9 + 2 * round],
                     out uint F0, out uint F1);

            R2 = RotateRight(R2 ^ F0, 1);
            R3 = RotateLeft(R3, 1) ^ F1;

            // Swap for next round (except last)
            if (round < NumRounds - 1)
            {
                (R0, R2) = (R2, R0);
                (R1, R3) = (R3, R1);
            }
        }

        // Undo last swap
        (R0, R2) = (R2, R0);
        (R1, R3) = (R3, R1);

        // Output whitening
        R0 ^= ctx.SubKeys[4];
        R1 ^= ctx.SubKeys[5];
        R2 ^= ctx.SubKeys[6];
        R3 ^= ctx.SubKeys[7];

        // Build output
        var output = new byte[BlockSizeBytes];
        BitConverter.GetBytes(R0).CopyTo(output, 0);
        BitConverter.GetBytes(R1).CopyTo(output, 4);
        BitConverter.GetBytes(R2).CopyTo(output, 8);
        BitConverter.GetBytes(R3).CopyTo(output, 12);

        return output;
    }

    /// <summary>
    /// Rotates a 32-bit value left by the specified number of bits.
    /// </summary>
    private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));

    /// <summary>
    /// Rotates a 32-bit value right by the specified number of bits.
    /// </summary>
    private static uint RotateRight(uint value, int count) => (value >> count) | (value << (32 - count));

    /// <summary>
    /// Increments the CTR mode counter in big-endian format.
    /// </summary>
    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    #endregion

    #region Message Handlers

    private Task HandleConfigureAsync(PluginMessage message)
    {
        // Use base class configuration methods
        if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
        {
            SetDefaultKeyStore(ks);
        }

        if (message.Payload.TryGetValue("envelopeKeyStore", out var eksObj) && eksObj is IEnvelopeKeyStore eks &&
            message.Payload.TryGetValue("kekKeyId", out var kekObj) && kekObj is string kek)
        {
            SetDefaultEnvelopeKeyStore(eks, kek);
        }

        if (message.Payload.TryGetValue("mode", out var modeObj))
        {
            if (modeObj is KeyManagementMode mode)
            {
                SetDefaultMode(mode);
            }
            else if (modeObj is string modeStr && Enum.TryParse<KeyManagementMode>(modeStr, true, out var parsedMode))
            {
                SetDefaultMode(parsedMode);
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        // Use base class statistics
        var stats = GetStatistics();

        message.Payload["EncryptionCount"] = stats.EncryptionCount;
        message.Payload["DecryptionCount"] = stats.DecryptionCount;
        message.Payload["TotalBytesEncrypted"] = stats.TotalBytesEncrypted;
        message.Payload["TotalBytesDecrypted"] = stats.TotalBytesDecrypted;
        message.Payload["UniqueKeysUsed"] = stats.UniqueKeysUsed;
        message.Payload["Algorithm"] = AlgorithmId;
        message.Payload["IVSizeBytes"] = IvSizeBytes;
        message.Payload["TagSizeBytes"] = TagSizeBytes;

        return Task.CompletedTask;
    }

    private Task HandleSetKeyStoreAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
        {
            SetDefaultKeyStore(ks);
        }
        return Task.CompletedTask;
    }

    #endregion
}

/// <summary>
/// Configuration for Twofish encryption plugin.
/// </summary>
public sealed class TwofishEncryptionConfig
{
    /// <summary>
    /// Gets or sets the key store to use for encryption keys.
    /// This is used as the default when not explicitly specified per operation.
    /// </summary>
    public IKeyStore? KeyStore { get; set; }
}
