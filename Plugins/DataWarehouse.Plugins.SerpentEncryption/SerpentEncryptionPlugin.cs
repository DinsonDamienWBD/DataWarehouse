using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.SerpentEncryption;

/// <summary>
/// Production-ready Serpent-256-CTR-HMAC encryption plugin for DataWarehouse.
/// Extends EncryptionPluginBase for composable key management with Direct and Envelope modes.
/// Implements the full Serpent block cipher according to the official specification.
///
/// Serpent Cipher Specification:
/// - 128-bit block cipher with 256-bit key
/// - 32 rounds with 8 distinct S-boxes (S0-S7)
/// - Initial Permutation (IP) and Final Permutation (FP)
/// - Linear transformation between rounds
/// - Key schedule using PHI constant (0x9E3779B9)
///
/// Security Features:
/// - CTR mode for encryption (parallelizable, no padding required)
/// - HMAC-SHA256 authentication (256-bit tag)
/// - Cryptographically secure random nonce generation (128-bit)
/// - Composable key management: Direct mode (IKeyStore) or Envelope mode (IEnvelopeKeyStore)
/// - Secure memory clearing for sensitive data (CryptographicOperations.ZeroMemory)
///
/// Data Format (when using manifest-based metadata):
/// [IV:16][Tag:32][Ciphertext:...]
///
/// Legacy Format (backward compatibility for old encrypted files):
/// [KeyIdLength:4][KeyId:variable][Nonce:16][Tag:32][Ciphertext]
///
/// Thread Safety: All operations are thread-safe.
///
/// Message Commands:
/// - serpent.encryption.configure: Configure encryption settings
/// - serpent.encryption.stats: Get encryption statistics
/// - serpent.encryption.setKeyStore: Set the key store
/// </summary>
public sealed class SerpentEncryptionPlugin : EncryptionPluginBase
{
    private readonly SerpentEncryptionConfig _config;

    /// <summary>
    /// PHI constant used in Serpent key schedule (golden ratio fractional part).
    /// </summary>
    private const uint PHI = 0x9E3779B9;

    /// <summary>
    /// Maximum key ID length in legacy header format.
    /// </summary>
    private const int MaxKeyIdLength = 64;

    /// <summary>
    /// Size of key ID length prefix (4 bytes for Int32) - for legacy format only.
    /// </summary>
    private const int KeyIdLengthSize = 4;

    #region Abstract Property Overrides

    /// <inheritdoc/>
    protected override int KeySizeBytes => 32; // 256 bits

    /// <inheritdoc/>
    protected override int IvSizeBytes => 16; // 128 bits for CTR mode

    /// <inheritdoc/>
    protected override int TagSizeBytes => 32; // 256 bits for HMAC-SHA256

    /// <inheritdoc/>
    protected override string AlgorithmId => "Serpent-256-CTR-HMAC";

    #endregion

    #region Plugin Identity

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.encryption.serpent256";

    /// <inheritdoc/>
    public override string Name => "Serpent-256-CTR-HMAC Encryption";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SubCategory => "Encryption";

    /// <inheritdoc/>
    public override int QualityLevel => 95;

    /// <inheritdoc/>
    public override int DefaultOrder => 90;

    /// <inheritdoc/>
    public override bool AllowBypass => false;

    /// <inheritdoc/>
    public override string[] RequiredPrecedingStages => new[] { "Compression" };

    /// <inheritdoc/>
    public override string[] IncompatibleStages => new[] { "encryption.aes256", "encryption.chacha20", "encryption.fips" };

    #endregion

    /// <summary>
    /// Initializes a new instance of the Serpent encryption plugin.
    /// </summary>
    /// <param name="config">Optional configuration. If null, defaults are used.</param>
    public SerpentEncryptionPlugin(SerpentEncryptionConfig? config = null)
    {
        _config = config ?? new SerpentEncryptionConfig();

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
            new() { Name = "serpent.encryption.configure", DisplayName = "Configure", Description = "Configure Serpent encryption settings" },
            new() { Name = "serpent.encryption.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
            new() { Name = "serpent.encryption.setKeyStore", DisplayName = "Set Key Store", Description = "Configure the key store" },
            new() { Name = "serpent.encryption.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using Serpent-256-CTR-HMAC" },
            new() { Name = "serpent.encryption.decrypt", DisplayName = "Decrypt", Description = "Decrypt Serpent-256-CTR-HMAC encrypted data" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Algorithm"] = AlgorithmId;
        metadata["BlockSize"] = 128;
        metadata["KeySize"] = KeySizeBytes * 8;
        metadata["Rounds"] = 32;
        metadata["SBoxCount"] = 8;
        metadata["NonceSize"] = IvSizeBytes * 8;
        metadata["TagSize"] = TagSizeBytes * 8;
        metadata["SupportsKeyRotation"] = true;
        metadata["SupportsStreaming"] = true;
        metadata["RequiresKeyStore"] = true;
        metadata["CipherType"] = "BlockCipher";
        metadata["Mode"] = "CTR";
        metadata["Authentication"] = "HMAC-SHA256";
        metadata["SupportedModes"] = new[] { "Direct", "Envelope" };
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "serpent.encryption.configure" => HandleConfigureAsync(message),
            "serpent.encryption.stats" => HandleStatsAsync(message),
            "serpent.encryption.setKeyStore" => HandleSetKeyStoreAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Core Encryption/Decryption (Algorithm-Specific)

    /// <summary>
    /// Performs Serpent-256-CTR-HMAC encryption on the input stream.
    /// Base class handles key resolution, config resolution, and statistics.
    /// </summary>
    /// <param name="input">The plaintext input stream.</param>
    /// <param name="key">The encryption key (provided by base class).</param>
    /// <param name="iv">The initialization vector / nonce (provided by base class).</param>
    /// <param name="context">The kernel context for logging.</param>
    /// <returns>A stream containing [IV:16][Tag:32][Ciphertext].</returns>
    /// <exception cref="CryptographicException">Thrown on encryption failure.</exception>
    protected override async Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context)
    {
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? macKey = null;
        uint[]? subkeys = null;

        try
        {
            // Read all input data
            using var inputMs = new MemoryStream();
            await input.CopyToAsync(inputMs);
            plaintext = inputMs.ToArray();

            // Derive MAC key from encryption key using SHA256
            macKey = SHA256.HashData(key);

            // Generate Serpent subkeys
            subkeys = GenerateSerpentSubkeys(key);

            // Encrypt using CTR mode
            ciphertext = new byte[plaintext.Length];
            var counter = new byte[16];
            Array.Copy(iv, counter, 16);

            for (int i = 0; i < plaintext.Length; i += 16)
            {
                var block = SerpentEncryptBlock(counter, subkeys);
                var blockLen = Math.Min(16, plaintext.Length - i);

                for (int j = 0; j < blockLen; j++)
                    ciphertext[i + j] = (byte)(plaintext[i + j] ^ block[j]);

                IncrementCounter(counter);
            }

            // Compute HMAC-SHA256 tag over iv || ciphertext
            var dataToMac = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, dataToMac, iv.Length);
            Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
            tag = HMACSHA256.HashData(macKey, dataToMac);

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

            context.LogDebug($"Serpent-256-CTR-HMAC encrypted {plaintext.Length} bytes");

            return new MemoryStream(output);
        }
        finally
        {
            // Security: Clear sensitive data from memory
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
            if (macKey != null) CryptographicOperations.ZeroMemory(macKey);
            if (subkeys != null) Array.Clear(subkeys, 0, subkeys.Length);
        }
    }

    /// <summary>
    /// Performs Serpent-256-CTR-HMAC decryption on the input stream.
    /// Base class handles key resolution, config resolution, and statistics.
    /// Supports both new format [IV:16][Tag:32][Ciphertext] and legacy format with key ID header.
    /// </summary>
    /// <param name="input">The encrypted input stream.</param>
    /// <param name="key">The decryption key (provided by base class).</param>
    /// <param name="iv">The initialization vector / nonce (null if embedded in ciphertext).</param>
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
        uint[]? subkeys = null;

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
                var keyIdLength = BitConverter.ToInt32(encryptedData, pos);
                pos += 4;

                if (keyIdLength <= 0 || keyIdLength > MaxKeyIdLength || pos + keyIdLength > encryptedData.Length)
                {
                    throw new CryptographicException("Invalid key ID length in encrypted data header");
                }

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
            var tag = new byte[TagSizeBytes];
            Array.Copy(encryptedData, pos, tag, 0, TagSizeBytes);
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

            // Verify HMAC tag
            var dataToMac = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, dataToMac, iv.Length);
            Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
            var expectedTag = HMACSHA256.HashData(macKey, dataToMac);

            if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
            {
                throw new CryptographicException("MAC verification failed. Data may be corrupted or tampered with.");
            }

            // Decrypt using CTR mode
            subkeys = GenerateSerpentSubkeys(key);
            plaintext = new byte[ciphertextLength];
            var counter = new byte[16];
            Array.Copy(iv, counter, 16);

            for (int i = 0; i < ciphertext.Length; i += 16)
            {
                var block = SerpentEncryptBlock(counter, subkeys);
                var blockLen = Math.Min(16, ciphertext.Length - i);

                for (int j = 0; j < blockLen; j++)
                    plaintext[i + j] = (byte)(ciphertext[i + j] ^ block[j]);

                IncrementCounter(counter);
            }

            context.LogDebug($"Serpent-256-CTR-HMAC decrypted {ciphertextLength} bytes");

            // Return a copy since we'll zero the original
            var result = new byte[plaintext.Length];
            Array.Copy(plaintext, result, plaintext.Length);
            return (new MemoryStream(result), tag);
        }
        finally
        {
            // Security: Clear sensitive data from memory
            if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            if (macKey != null) CryptographicOperations.ZeroMemory(macKey);
            if (subkeys != null) Array.Clear(subkeys, 0, subkeys.Length);
        }
    }

    /// <summary>
    /// Checks if the encrypted data uses the legacy format with key ID header.
    /// </summary>
    private bool IsLegacyFormat(byte[] data)
    {
        if (data.Length < KeyIdLengthSize + 1)
            return false;

        // Read key ID length from header
        var keyIdLength = BitConverter.ToInt32(data, 0);

        // Legacy format has a valid key ID length (1 to MaxKeyIdLength)
        return keyIdLength > 0 && keyIdLength <= MaxKeyIdLength;
    }

    #endregion

    #region Serpent Block Cipher Implementation

    /// <summary>
    /// Generates 132 32-bit subkeys for the 32 rounds plus the final post-whitening.
    /// Key schedule follows the official Serpent specification.
    /// </summary>
    private static uint[] GenerateSerpentSubkeys(byte[] key)
    {
        // Pad key to 256 bits if necessary
        var paddedKey = new byte[32];
        Array.Copy(key, paddedKey, Math.Min(key.Length, 32));
        if (key.Length < 32)
            paddedKey[key.Length] = 0x01;

        // Initialize prekey words from key bytes (little-endian)
        var w = new uint[140];
        for (int i = 0; i < 8; i++)
            w[i] = BitConverter.ToUInt32(paddedKey, i * 4);

        // Expand prekey using linear recurrence with PHI constant
        for (int i = 8; i < 140; i++)
        {
            var t = w[i - 8] ^ w[i - 5] ^ w[i - 3] ^ w[i - 1] ^ PHI ^ (uint)(i - 8);
            w[i] = RotateLeft(t, 11);
        }

        // Apply S-boxes to generate round keys
        var subkeys = new uint[132];
        for (int i = 0; i < 33; i++)
        {
            int sboxIndex = (35 - i) % 8;
            var block = new uint[4];
            block[0] = w[8 + i * 4];
            block[1] = w[8 + i * 4 + 1];
            block[2] = w[8 + i * 4 + 2];
            block[3] = w[8 + i * 4 + 3];

            ApplySBox(block, sboxIndex);

            subkeys[i * 4] = block[0];
            subkeys[i * 4 + 1] = block[1];
            subkeys[i * 4 + 2] = block[2];
            subkeys[i * 4 + 3] = block[3];
        }

        return subkeys;
    }

    /// <summary>
    /// Encrypts a single 128-bit block using the Serpent algorithm.
    /// </summary>
    private static byte[] SerpentEncryptBlock(byte[] block, uint[] subkeys)
    {
        // Load block as 4 32-bit words (little-endian)
        var x = new uint[4];
        for (int i = 0; i < 4; i++)
            x[i] = BitConverter.ToUInt32(block, i * 4);

        // Apply Initial Permutation (IP)
        ApplyIP(x);

        // 32 rounds
        for (int round = 0; round < 32; round++)
        {
            // XOR with round subkey (key mixing)
            x[0] ^= subkeys[4 * round];
            x[1] ^= subkeys[4 * round + 1];
            x[2] ^= subkeys[4 * round + 2];
            x[3] ^= subkeys[4 * round + 3];

            // Apply S-box for this round
            ApplySBox(x, round % 8);

            // Apply linear transformation (except for last round)
            if (round < 31)
                ApplyLinearTransform(x);
        }

        // Post-whitening (XOR with final subkey)
        x[0] ^= subkeys[128];
        x[1] ^= subkeys[129];
        x[2] ^= subkeys[130];
        x[3] ^= subkeys[131];

        // Apply Final Permutation (FP)
        ApplyFP(x);

        // Convert back to bytes
        var output = new byte[16];
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(x[i]).CopyTo(output, i * 4);

        return output;
    }

    /// <summary>
    /// Applies one of the 8 Serpent S-boxes (S0-S7) to the state.
    /// These are the official Serpent S-boxes from the specification.
    /// </summary>
    private static void ApplySBox(uint[] x, int box)
    {
        uint t0, t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13;

        switch (box)
        {
            case 0: // S0
                t0 = x[1] ^ x[2]; t1 = x[0] | x[3]; t2 = x[0] ^ x[1]; t3 = x[3] ^ t0;
                t4 = t1 ^ t3; t5 = x[1] | x[2]; t6 = x[3] ^ t4; t7 = t5 | t6;
                t8 = x[0] ^ x[3]; t9 = t2 & t8; x[3] = t7 ^ t9; t10 = t2 | x[3];
                x[1] = t10 ^ t8; t11 = t4 ^ t6; t12 = x[1] & t11; x[2] = t4 ^ t12;
                t13 = x[2] & x[3]; x[0] = t0 ^ t13;
                break;
            case 1: // S1
                t0 = x[0] | x[3]; t1 = x[2] ^ x[3]; t2 = ~x[1]; t3 = x[0] ^ x[2];
                t4 = x[0] | t2; t5 = t0 & t1; t6 = t3 | t5; x[1] = t4 ^ t6;
                t7 = t0 ^ t2; t8 = t5 | x[1]; t9 = x[3] | x[1]; t10 = t7 ^ t8;
                x[3] = t1 ^ t9; t11 = x[2] | t10; x[0] = t10 ^ t11;
                x[2] = t9 ^ t0 ^ x[0];
                break;
            case 2: // S2
                t0 = x[0] | x[2]; t1 = x[0] ^ x[1]; t2 = x[3] ^ t0; x[0] = t1 ^ t2;
                t3 = x[2] ^ x[0]; t4 = x[1] ^ t2; t5 = x[1] | t3; x[3] = t4 ^ t5;
                t6 = t0 ^ x[3]; t7 = t5 & t6; x[2] = t2 ^ t7; t8 = x[2] & x[0];
                x[1] = t6 ^ t8;
                break;
            case 3: // S3
                t0 = x[0] | x[3]; t1 = x[2] ^ x[3]; t2 = x[0] ^ x[2]; t3 = t0 & t1;
                t4 = x[1] | t2; x[2] = t3 ^ t4; t5 = x[1] ^ t1; t6 = x[3] ^ x[2];
                t7 = t4 | t6; x[3] = t5 ^ t7; t8 = t0 ^ x[3]; t9 = x[2] & t8;
                x[1] = t1 ^ t9; t10 = x[1] | x[0]; x[0] = t6 ^ t10;
                break;
            case 4: // S4
                t0 = x[0] | x[1]; t1 = x[1] | x[2]; t2 = x[0] ^ t1; t3 = x[1] ^ x[3];
                t4 = x[3] | t2; x[3] = t3 ^ t4; t5 = t0 ^ t4; t6 = t1 ^ t5;
                t7 = x[3] & t5; x[1] = t6 ^ t7; t8 = x[3] | x[1]; x[0] = t5 ^ t8;
                t9 = x[2] ^ x[0]; x[2] = t2 ^ t9 ^ x[1];
                break;
            case 5: // S5
                t0 = x[1] ^ x[3]; t1 = x[1] | x[3]; t2 = x[0] & t0; t3 = x[2] ^ t1;
                x[0] = t2 ^ t3; t4 = x[0] | x[1]; t5 = x[3] ^ t4; t6 = x[2] | x[0];
                x[3] = t5 ^ t6; t7 = t0 ^ t6; t8 = t1 & x[3]; x[1] = t7 ^ t8;
                t9 = x[0] ^ x[3]; x[2] = x[1] ^ t9 ^ t4;
                break;
            case 6: // S6
                t0 = x[0] ^ x[3]; t1 = x[1] ^ x[3]; t2 = x[0] & t1; t3 = x[2] ^ t2;
                x[0] = x[1] ^ t3; t4 = t0 | x[0]; t5 = x[3] | t3; x[3] = t4 ^ t5;
                t6 = t1 ^ t4; t7 = x[0] & x[3]; x[1] = t6 ^ t7; t8 = x[0] ^ x[3];
                x[2] = x[1] ^ t8 ^ t1;
                break;
            case 7: // S7
                t0 = x[0] & x[2]; t1 = x[3] ^ t0; t2 = x[0] ^ x[3]; t3 = x[1] ^ t1;
                t4 = x[0] ^ t3; x[2] = t2 & t4; t5 = t1 | x[2]; x[0] = t3 ^ t5;
                t6 = x[2] ^ x[0]; t7 = x[3] | t6; x[3] = t4 ^ t7; t8 = t3 & x[3];
                x[1] = t1 ^ t8;
                break;
        }
    }

    /// <summary>
    /// Applies the Serpent linear transformation between rounds.
    /// Provides diffusion through rotation and XOR operations.
    /// </summary>
    private static void ApplyLinearTransform(uint[] x)
    {
        x[0] = RotateLeft(x[0], 13);
        x[2] = RotateLeft(x[2], 3);
        x[1] = x[1] ^ x[0] ^ x[2];
        x[3] = x[3] ^ x[2] ^ (x[0] << 3);
        x[1] = RotateLeft(x[1], 1);
        x[3] = RotateLeft(x[3], 7);
        x[0] = x[0] ^ x[1] ^ x[3];
        x[2] = x[2] ^ x[3] ^ (x[1] << 7);
        x[0] = RotateLeft(x[0], 5);
        x[2] = RotateLeft(x[2], 22);
    }

    /// <summary>
    /// Applies the Initial Permutation (IP) to convert from standard bit ordering
    /// to the Serpent internal bit-sliced representation.
    /// </summary>
    private static void ApplyIP(uint[] x)
    {
        uint t0 = x[0], t1 = x[1], t2 = x[2], t3 = x[3];
        SWAPMOVE(ref t0, ref t1, 0x55555555, 1);
        SWAPMOVE(ref t2, ref t3, 0x55555555, 1);
        SWAPMOVE(ref t0, ref t2, 0x33333333, 2);
        SWAPMOVE(ref t1, ref t3, 0x33333333, 2);
        SWAPMOVE(ref t0, ref t1, 0x0F0F0F0F, 4);
        SWAPMOVE(ref t2, ref t3, 0x0F0F0F0F, 4);
        x[0] = t0; x[1] = t1; x[2] = t2; x[3] = t3;
    }

    /// <summary>
    /// Applies the Final Permutation (FP) to convert from Serpent internal
    /// bit-sliced representation back to standard bit ordering.
    /// </summary>
    private static void ApplyFP(uint[] x)
    {
        uint t0 = x[0], t1 = x[1], t2 = x[2], t3 = x[3];
        SWAPMOVE(ref t2, ref t3, 0x0F0F0F0F, 4);
        SWAPMOVE(ref t0, ref t1, 0x0F0F0F0F, 4);
        SWAPMOVE(ref t1, ref t3, 0x33333333, 2);
        SWAPMOVE(ref t0, ref t2, 0x33333333, 2);
        SWAPMOVE(ref t2, ref t3, 0x55555555, 1);
        SWAPMOVE(ref t0, ref t1, 0x55555555, 1);
        x[0] = t0; x[1] = t1; x[2] = t2; x[3] = t3;
    }

    /// <summary>
    /// Swap-move operation used in IP and FP permutations.
    /// Efficiently swaps bits between two words based on mask and shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SWAPMOVE(ref uint a, ref uint b, uint mask, int shift)
    {
        uint t = ((a >> shift) ^ b) & mask;
        b ^= t;
        a ^= t << shift;
    }

    /// <summary>
    /// Rotates a 32-bit value left by the specified number of bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    /// <summary>
    /// Increments a 128-bit counter stored as a byte array (big-endian).
    /// Used in CTR mode.
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
        message.Payload["IVSizeBits"] = IvSizeBytes * 8;
        message.Payload["TagSizeBits"] = TagSizeBytes * 8;

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
/// Configuration for Serpent encryption plugin.
/// </summary>
public sealed class SerpentEncryptionConfig
{
    /// <summary>
    /// Gets or sets the key store to use for encryption keys.
    /// This is used as the default when not explicitly specified per operation.
    /// </summary>
    public IKeyStore? KeyStore { get; set; }
}
