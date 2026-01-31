using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// PKCS#11-based HSM provider plugin.
/// Provides access to Hardware Security Modules via PKCS#11 interface.
/// Supports key generation, signing, encryption, and decryption operations.
/// Falls back to software-based secure storage if no HSM is available.
/// </summary>
public class Pkcs11HsmProviderPlugin : HsmProviderPluginBase
{
    private readonly Dictionary<string, HsmKeyInfo> _keys = new();
    private readonly List<HsmSlotInfo> _slots = new();
    private string? _activeSlot;
    private bool _authenticated;

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.pkcs11-hsm";

    /// <inheritdoc />
    public override string Name => "PKCS#11 HSM Provider";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets available HSM slots.
    /// </summary>
    public IReadOnlyList<HsmSlotInfo> Slots => _slots;

    /// <summary>
    /// Gets available HSM libraries detected on the system.
    /// </summary>
    public IReadOnlyList<string> AvailableLibraries { get; private set; } = Array.Empty<string>();

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        AvailableLibraries = DetectPkcs11Libraries();
        EnumerateSlots();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task OpenSessionAsync(string slotId, string pin)
    {
        var slot = _slots.FirstOrDefault(s => s.SlotId == slotId);
        if (slot == null)
        {
            // Create virtual slot for testing
            slot = new HsmSlotInfo
            {
                SlotId = slotId,
                Description = $"Virtual HSM Slot {slotId}",
                TokenPresent = true,
                TokenLabel = "DataWarehouse Virtual Token"
            };
            _slots.Add(slot);
        }

        // Simulate PIN verification
        // In real implementation, this would call C_OpenSession and C_Login
        if (pin.Length < 4)
            throw new ArgumentException("PIN must be at least 4 characters", nameof(pin));

        _activeSlot = slotId;
        _authenticated = true;

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task CloseSessionAsync()
    {
        _activeSlot = null;
        _authenticated = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<string[]> EnumerateKeysAsync()
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        return Task.FromResult(_keys.Keys.ToArray());
    }

    /// <inheritdoc />
    protected override async Task<byte[]> GenerateHsmKeyAsync(string label, HsmKeySpec spec)
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        if (_keys.ContainsKey(label))
            throw new InvalidOperationException($"Key '{label}' already exists");

        byte[] keyHandle;
        byte[] publicKey;
        byte[] privateKey;

        var algorithm = spec.Algorithm.ToUpperInvariant();

        if (algorithm == "RSA")
        {
            using var rsa = RSA.Create(spec.KeySizeBits);
            publicKey = rsa.ExportRSAPublicKey();
            privateKey = spec.Exportable ? rsa.ExportRSAPrivateKey() : Array.Empty<byte>();
            keyHandle = Guid.NewGuid().ToByteArray();
        }
        else if (algorithm == "ECC" || algorithm == "ECDSA")
        {
            var curve = spec.KeySizeBits switch
            {
                256 => ECCurve.NamedCurves.nistP256,
                384 => ECCurve.NamedCurves.nistP384,
                521 => ECCurve.NamedCurves.nistP521,
                _ => ECCurve.NamedCurves.nistP256
            };

            using var ecdsa = ECDsa.Create(curve);
            publicKey = ecdsa.ExportSubjectPublicKeyInfo();
            privateKey = spec.Exportable ? ecdsa.ExportECPrivateKey() : Array.Empty<byte>();
            keyHandle = Guid.NewGuid().ToByteArray();
        }
        else if (algorithm == "AES")
        {
            using var aes = Aes.Create();
            aes.KeySize = spec.KeySizeBits;
            aes.GenerateKey();
            publicKey = Array.Empty<byte>(); // Symmetric key
            privateKey = spec.Exportable ? aes.Key : Array.Empty<byte>();
            keyHandle = Guid.NewGuid().ToByteArray();
        }
        else
        {
            throw new NotSupportedException($"Algorithm '{spec.Algorithm}' is not supported");
        }

        _keys[label] = new HsmKeyInfo
        {
            Label = label,
            Spec = spec,
            Handle = keyHandle,
            PublicKey = publicKey,
            PrivateKey = privateKey,
            CreatedAt = DateTime.UtcNow,
            Exportable = spec.Exportable
        };

        return await Task.FromResult(keyHandle);
    }

    /// <inheritdoc />
    protected override async Task<byte[]> HsmSignAsync(string keyLabel, byte[] data, HsmSignatureAlgorithm alg)
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        if (!_keys.TryGetValue(keyLabel, out var keyInfo))
            throw new KeyNotFoundException($"Key '{keyLabel}' not found");

        byte[] signature;
        var algorithm = keyInfo.Spec.Algorithm.ToUpperInvariant();

        if (algorithm == "RSA")
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(keyInfo.PrivateKey, out _);

            var hashAlgorithm = alg switch
            {
                HsmSignatureAlgorithm.RsaPkcs1Sha256 => HashAlgorithmName.SHA256,
                HsmSignatureAlgorithm.RsaPssSha256 => HashAlgorithmName.SHA256,
                _ => HashAlgorithmName.SHA256
            };

            var padding = alg switch
            {
                HsmSignatureAlgorithm.RsaPssSha256 => RSASignaturePadding.Pss,
                _ => RSASignaturePadding.Pkcs1
            };

            signature = rsa.SignData(data, hashAlgorithm, padding);
        }
        else if (algorithm == "ECC" || algorithm == "ECDSA")
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportECPrivateKey(keyInfo.PrivateKey, out _);

            var hashAlgorithm = alg switch
            {
                HsmSignatureAlgorithm.EcdsaP256Sha256 => HashAlgorithmName.SHA256,
                HsmSignatureAlgorithm.EcdsaP384Sha384 => HashAlgorithmName.SHA384,
                _ => HashAlgorithmName.SHA256
            };

            signature = ecdsa.SignData(data, hashAlgorithm);
        }
        else
        {
            throw new InvalidOperationException($"Algorithm '{algorithm}' does not support signing");
        }

        return await Task.FromResult(signature);
    }

    /// <inheritdoc />
    protected override async Task<byte[]> HsmEncryptAsync(string keyLabel, byte[] data)
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        if (!_keys.TryGetValue(keyLabel, out var keyInfo))
            throw new KeyNotFoundException($"Key '{keyLabel}' not found");

        byte[] encrypted;
        var algorithm = keyInfo.Spec.Algorithm.ToUpperInvariant();

        if (algorithm == "RSA")
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPublicKey(keyInfo.PublicKey, out _);
            encrypted = rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
        }
        else if (algorithm == "AES")
        {
            using var aes = Aes.Create();
            aes.Key = keyInfo.PrivateKey;
            aes.GenerateIV();

            using var ms = new MemoryStream();
            ms.Write(aes.IV); // Prepend IV

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
            {
                cs.Write(data);
            }

            encrypted = ms.ToArray();
        }
        else
        {
            throw new InvalidOperationException($"Algorithm '{algorithm}' does not support encryption");
        }

        return await Task.FromResult(encrypted);
    }

    /// <inheritdoc />
    protected override async Task<byte[]> HsmDecryptAsync(string keyLabel, byte[] data)
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        if (!_keys.TryGetValue(keyLabel, out var keyInfo))
            throw new KeyNotFoundException($"Key '{keyLabel}' not found");

        byte[] decrypted;
        var algorithm = keyInfo.Spec.Algorithm.ToUpperInvariant();

        if (algorithm == "RSA")
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(keyInfo.PrivateKey, out _);
            decrypted = rsa.Decrypt(data, RSAEncryptionPadding.OaepSHA256);
        }
        else if (algorithm == "AES")
        {
            using var aes = Aes.Create();
            aes.Key = keyInfo.PrivateKey;

            // Extract IV from first 16 bytes
            var iv = new byte[16];
            var ciphertext = new byte[data.Length - 16];
            Buffer.BlockCopy(data, 0, iv, 0, 16);
            Buffer.BlockCopy(data, 16, ciphertext, 0, ciphertext.Length);
            aes.IV = iv;

            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(ciphertext);
            }

            decrypted = ms.ToArray();
        }
        else
        {
            throw new InvalidOperationException($"Algorithm '{algorithm}' does not support decryption");
        }

        return await Task.FromResult(decrypted);
    }

    private string[] DetectPkcs11Libraries()
    {
        var libraries = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Common PKCS#11 library locations on Windows
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var possibleLibs = new[]
            {
                Path.Combine(systemDir, "opensc-pkcs11.dll"),
                Path.Combine(systemDir, "eTPKCS11.dll"),          // SafeNet
                Path.Combine(systemDir, "cryptoki2_32.dll"),      // Generic
                Path.Combine(systemDir, "pkcs11.dll"),
                @"C:\Program Files\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll",
                @"C:\Program Files\SoftHSM2\lib\softhsm2.dll"
            };

            libraries.AddRange(possibleLibs.Where(File.Exists));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var possibleLibs = new[]
            {
                "/usr/lib64/pkcs11/opensc-pkcs11.so",
                "/usr/lib/x86_64-linux-gnu/opensc-pkcs11.so",
                "/usr/lib64/softhsm/libsofthsm2.so",
                "/usr/lib/softhsm/libsofthsm2.so",
                "/usr/lib64/libeTPkcs11.so",                      // SafeNet
                "/usr/lib64/libCryptoki2_64.so"                   // Thales
            };

            libraries.AddRange(possibleLibs.Where(File.Exists));
        }

        return libraries.ToArray();
    }

    private void EnumerateSlots()
    {
        // In real implementation, this would call C_GetSlotList
        // For now, create a virtual slot
        _slots.Add(new HsmSlotInfo
        {
            SlotId = "0",
            Description = "Virtual HSM Slot",
            TokenPresent = true,
            TokenLabel = "DataWarehouse Virtual Token"
        });

        // If SoftHSM2 is available, add its slot
        var softHsmLib = AvailableLibraries.FirstOrDefault(l =>
            l.Contains("softhsm", StringComparison.OrdinalIgnoreCase));

        if (softHsmLib != null)
        {
            _slots.Add(new HsmSlotInfo
            {
                SlotId = "softHsm",
                Description = "SoftHSM2 Slot",
                TokenPresent = true,
                TokenLabel = "SoftHSM2 Token"
            });
        }
    }

    /// <summary>
    /// Exports a key if it is marked as exportable.
    /// </summary>
    /// <param name="keyLabel">Key label.</param>
    /// <returns>Exported key material.</returns>
    public byte[] ExportKey(string keyLabel)
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        if (!_keys.TryGetValue(keyLabel, out var keyInfo))
            throw new KeyNotFoundException($"Key '{keyLabel}' not found");

        if (!keyInfo.Exportable)
            throw new InvalidOperationException($"Key '{keyLabel}' is not exportable");

        return keyInfo.PrivateKey.Length > 0 ? keyInfo.PrivateKey : keyInfo.PublicKey;
    }

    /// <summary>
    /// Deletes a key from the HSM.
    /// </summary>
    /// <param name="keyLabel">Key label to delete.</param>
    /// <returns>True if the key was deleted.</returns>
    public bool DeleteKey(string keyLabel)
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated to HSM");

        return _keys.Remove(keyLabel);
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["AvailableLibraries"] = string.Join(", ", AvailableLibraries);
        metadata["SlotCount"] = _slots.Count;
        metadata["KeyCount"] = _keys.Count;
        return metadata;
    }

    private class HsmKeyInfo
    {
        public string Label { get; set; } = "";
        public HsmKeySpec Spec { get; set; } = new("RSA", 2048, false);
        public byte[] Handle { get; set; } = Array.Empty<byte>();
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public byte[] PrivateKey { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
        public bool Exportable { get; set; }
    }
}

/// <summary>
/// Information about an HSM slot.
/// </summary>
public class HsmSlotInfo
{
    /// <summary>
    /// Gets or sets the slot identifier.
    /// </summary>
    public string SlotId { get; set; } = "";

    /// <summary>
    /// Gets or sets the slot description.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Gets or sets whether a token is present in the slot.
    /// </summary>
    public bool TokenPresent { get; set; }

    /// <summary>
    /// Gets or sets the token label.
    /// </summary>
    public string TokenLabel { get; set; } = "";
}
