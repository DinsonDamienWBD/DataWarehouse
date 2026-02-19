using DataWarehouse.SDK.Security;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// Nitrokey Hardware KeyStore strategy using PKCS#11 interface.
    /// Nitrokeys are open-source USB security keys supporting:
    /// - PKCS#11 for general-purpose cryptography (Nitrokey HSM, Pro, Storage)
    /// - OpenPGP for email encryption/signing
    /// - FIDO2/U2F for web authentication
    ///
    /// Supported Models:
    /// - Nitrokey HSM 2: Full PKCS#11 with smart card integration
    /// - Nitrokey Pro 2: OpenPGP + PKCS#11
    /// - Nitrokey Storage 2: Encrypted storage + PKCS#11
    /// - Nitrokey 3: FIDO2 + PKCS#11 + OpenPGP
    ///
    /// Features:
    /// - AES-256 key generation and storage
    /// - RSA 2048/4096 key pairs
    /// - ECDSA P-256/P-384 key pairs
    /// - Key wrapping/unwrapping
    /// - PIN-protected access
    /// - Tamper-evident hardware
    ///
    /// Requirements:
    /// - Nitrokey device with PKCS#11 support
    /// - OpenSC or Nitrokey PKCS#11 library installed
    /// </summary>
    public sealed class NitrokeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private IPkcs11Library? _pkcs11Library;
        private ISlot? _slot;
        private ISession? _session;
        private NitrokeyConfig _config = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _sessionLock = new(1, 1);
        private bool _loggedIn;
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Nitrokey",
                ["Manufacturer"] = "Nitrokey GmbH",
                ["Interface"] = "PKCS#11",
                ["OpenSource"] = true,
                ["SupportsAes"] = true,
                ["SupportsRsa"] = true,
                ["SupportsEc"] = true,
                ["TamperEvident"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("nitrokey.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "AES-256-CBC-PAD",
            "AES-256-KWP",
            "RSA-OAEP-SHA256",
            "RSA-PKCS"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("nitrokey.init");
            // Load configuration
            if (Configuration.TryGetValue("LibraryPath", out var libObj) && libObj is string lib)
                _config.LibraryPath = lib;
            if (Configuration.TryGetValue("SlotId", out var slotObj))
            {
                if (slotObj is int slotId) _config.SlotId = (ulong)slotId;
                else if (slotObj is long slotIdLong) _config.SlotId = (ulong)slotIdLong;
                else if (slotObj is ulong slotIdUlong) _config.SlotId = slotIdUlong;
            }
            if (Configuration.TryGetValue("UserPin", out var pinObj) && pinObj is string pin)
                _config.UserPin = pin;
            if (Configuration.TryGetValue("SoPin", out var soPinObj) && soPinObj is string soPin)
                _config.SoPin = soPin;
            if (Configuration.TryGetValue("DefaultKeyLabel", out var labelObj) && labelObj is string label)
                _config.DefaultKeyLabel = label;
            if (Configuration.TryGetValue("Model", out var modelObj) && modelObj is string model)
                _config.Model = Enum.Parse<NitrokeyModel>(model, ignoreCase: true);

            await Task.Run(() => InitializePkcs11(), cancellationToken);
        }

        private void InitializePkcs11()
        {
            if (!_sessionLock.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timeout waiting for session lock during PKCS#11 initialization");
            }
            try
            {
                // Get platform-specific library path
                var libraryPath = string.IsNullOrEmpty(_config.LibraryPath)
                    ? GetDefaultLibraryPath()
                    : _config.LibraryPath;

                // Initialize PKCS#11
                var factories = new Pkcs11InteropFactories();
                _pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(
                    factories,
                    libraryPath,
                    AppType.MultiThreaded);

                // Get slots with tokens
                var slots = _pkcs11Library.GetSlotList(SlotsType.WithTokenPresent);

                if (slots.Count == 0)
                {
                    throw new InvalidOperationException("No Nitrokey devices found.");
                }

                // Select slot
                if (_config.SlotId.HasValue)
                {
                    _slot = slots.FirstOrDefault(s => s.SlotId == _config.SlotId.Value)
                        ?? throw new InvalidOperationException($"Slot {_config.SlotId} not found.");
                }
                else
                {
                    // Find Nitrokey by checking manufacturer/label
                    _slot = slots.FirstOrDefault(s =>
                    {
                        var info = s.GetTokenInfo();
                        return info.ManufacturerId.Contains("Nitrokey", StringComparison.OrdinalIgnoreCase) ||
                               info.Label.Contains("Nitrokey", StringComparison.OrdinalIgnoreCase);
                    }) ?? slots[0];
                }

                // Open session
                _session = _slot.OpenSession(SessionType.ReadWrite);

                // Login with user PIN
                if (!string.IsNullOrEmpty(_config.UserPin))
                {
                    _session.Login(CKU.CKU_USER, _config.UserPin);
                    _loggedIn = true;
                }

                // Set current key ID
                _currentKeyId = _config.DefaultKeyLabel;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private string GetDefaultLibraryPath()
        {
            // Model-specific library paths
            if (_config.Model == NitrokeyModel.Hsm2)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return @"C:\Program Files\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return "/Library/OpenSC/lib/opensc-pkcs11.so";
                else
                    return "/usr/lib/x86_64-linux-gnu/opensc-pkcs11.so";
            }

            // Generic OpenSC for other models
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return @"C:\Program Files\OpenSC Project\OpenSC\pkcs11\opensc-pkcs11.dll";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "/Library/OpenSC/lib/opensc-pkcs11.so";
            else
                return "/usr/lib/x86_64-linux-gnu/opensc-pkcs11.so";
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                if (_session == null || _disposed)
                    return false;

                var info = _session.GetSessionInfo();
                return info.State == CKS.CKS_RW_USER_FUNCTIONS ||
                       info.State == CKS.CKS_RW_PUBLIC_SESSION;
            }
            catch
            {
                return false;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _sessionLock.WaitAsync();
            try
            {
                EnsureSession();

                var keyHandle = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);
                if (keyHandle == null)
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found on Nitrokey.");
                }

                // Check if key is extractable
                var attrs = _session!.GetAttributeValue(keyHandle, new List<CKA>
                {
                    CKA.CKA_EXTRACTABLE,
                    CKA.CKA_VALUE
                });

                if (!attrs[0].GetValueAsBool())
                {
                    throw new InvalidOperationException(
                        $"Key '{keyId}' is not extractable. Use envelope encryption with WrapKeyAsync/UnwrapKeyAsync.");
                }

                return attrs[1].GetValueAsByteArray();
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _sessionLock.WaitAsync();
            try
            {
                EnsureSession();

                // Delete existing key if present
                var existingKey = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);
                if (existingKey != null)
                {
                    _session!.DestroyObject(existingKey);
                }

                // Create new AES key
                var keyAttributes = new List<IObjectAttribute>
                {
                    _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, keyId),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ID, Encoding.UTF8.GetBytes(keyId)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, keyData),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE_LEN, (ulong)keyData.Length),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, _config.AllowExtractableKeys),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_WRAP, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_UNWRAP, true)
                };

                _session.CreateObject(keyAttributes);
                _currentKeyId = keyId;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _sessionLock.WaitAsync();
            try
            {
                EnsureSession();

                var kekHandle = FindKeyByLabel(kekId, CKO.CKO_SECRET_KEY);
                if (kekHandle == null)
                {
                    // Try RSA public key
                    kekHandle = FindKeyByLabel(kekId, CKO.CKO_PUBLIC_KEY);
                    if (kekHandle == null)
                    {
                        throw new KeyNotFoundException($"KEK '{kekId}' not found on Nitrokey.");
                    }

                    // RSA wrapping
                    return RsaWrap(kekHandle, dataKey);
                }

                // AES wrapping
                return AesWrap(kekHandle, dataKey);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _sessionLock.WaitAsync();
            try
            {
                EnsureSession();

                var kekHandle = FindKeyByLabel(kekId, CKO.CKO_SECRET_KEY);
                if (kekHandle == null)
                {
                    // Try RSA private key
                    kekHandle = FindKeyByLabel(kekId, CKO.CKO_PRIVATE_KEY);
                    if (kekHandle == null)
                    {
                        throw new KeyNotFoundException($"KEK '{kekId}' not found on Nitrokey.");
                    }

                    // RSA unwrapping
                    return RsaUnwrap(kekHandle, wrappedKey);
                }

                // AES unwrapping
                return AesUnwrap(kekHandle, wrappedKey);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private byte[] AesWrap(IObjectHandle kekHandle, byte[] dataKey)
        {
            // Generate IV
            var iv = new byte[16];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(iv);

            // Create mechanism for AES-CBC-PAD wrapping
            var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_CBC_PAD, iv);

            // Create temporary key object
            var tempKeyAttrs = new List<IObjectAttribute>
            {
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, dataKey),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
            };

            var tempKeyHandle = _session.CreateObject(tempKeyAttrs);

            try
            {
                var wrapped = _session.WrapKey(mechanism, kekHandle, tempKeyHandle);

                // Prepend IV
                var result = new byte[iv.Length + wrapped.Length];
                iv.CopyTo(result, 0);
                wrapped.CopyTo(result, iv.Length);

                return result;
            }
            finally
            {
                _session.DestroyObject(tempKeyHandle);
            }
        }

        private byte[] AesUnwrap(IObjectHandle kekHandle, byte[] wrappedKey)
        {
            if (wrappedKey.Length < 17)
            {
                throw new ArgumentException("Wrapped key too short.", nameof(wrappedKey));
            }

            // Extract IV
            var iv = wrappedKey.AsSpan(0, 16).ToArray();
            var encrypted = wrappedKey.AsSpan(16).ToArray();

            var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_CBC_PAD, iv);

            var unwrappedAttrs = new List<IObjectAttribute>
            {
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
            };

            var unwrappedHandle = _session.UnwrapKey(mechanism, kekHandle, encrypted, unwrappedAttrs);

            try
            {
                var valueAttr = _session.GetAttributeValue(unwrappedHandle, new List<CKA> { CKA.CKA_VALUE });
                return valueAttr[0].GetValueAsByteArray();
            }
            finally
            {
                _session.DestroyObject(unwrappedHandle);
            }
        }

        private byte[] RsaWrap(IObjectHandle publicKeyHandle, byte[] dataKey)
        {
            // Use RSA-OAEP for wrapping
            var oaepParams = _session!.Factories.MechanismParamsFactory.CreateCkRsaPkcsOaepParams(
                (ulong)CKM.CKM_SHA256,
                (ulong)CKG.CKG_MGF1_SHA256,
                (ulong)CKZ.CKZ_DATA_SPECIFIED,
                null);

            var mechanism = _session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_OAEP, oaepParams);

            // Create temporary key for wrapping
            var tempKeyAttrs = new List<IObjectAttribute>
            {
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, dataKey),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
            };

            var tempKeyHandle = _session.CreateObject(tempKeyAttrs);

            try
            {
                return _session.WrapKey(mechanism, publicKeyHandle, tempKeyHandle);
            }
            finally
            {
                _session.DestroyObject(tempKeyHandle);
            }
        }

        private byte[] RsaUnwrap(IObjectHandle privateKeyHandle, byte[] wrappedKey)
        {
            var oaepParams = _session!.Factories.MechanismParamsFactory.CreateCkRsaPkcsOaepParams(
                (ulong)CKM.CKM_SHA256,
                (ulong)CKG.CKG_MGF1_SHA256,
                (ulong)CKZ.CKZ_DATA_SPECIFIED,
                null);

            var mechanism = _session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_OAEP, oaepParams);

            var unwrappedAttrs = new List<IObjectAttribute>
            {
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
            };

            var unwrappedHandle = _session.UnwrapKey(mechanism, privateKeyHandle, wrappedKey, unwrappedAttrs);

            try
            {
                var valueAttr = _session.GetAttributeValue(unwrappedHandle, new List<CKA> { CKA.CKA_VALUE });
                return valueAttr[0].GetValueAsByteArray();
            }
            finally
            {
                _session.DestroyObject(unwrappedHandle);
            }
        }

        /// <summary>
        /// Generates an RSA key pair on the Nitrokey.
        /// </summary>
        public async Task<(IObjectHandle PublicKey, IObjectHandle PrivateKey)> GenerateRsaKeyPairAsync(
            string keyLabel, int keySizeBits = 2048)
        {
            await _sessionLock.WaitAsync();
            try
            {
                EnsureSession();

                var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_KEY_PAIR_GEN);

                var publicKeyAttrs = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, keyLabel),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ID, Encoding.UTF8.GetBytes(keyLabel)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_MODULUS_BITS, (ulong)keySizeBits),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PUBLIC_EXPONENT, new byte[] { 0x01, 0x00, 0x01 }),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VERIFY, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_WRAP, true)
                };

                var privateKeyAttrs = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, keyLabel),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ID, Encoding.UTF8.GetBytes(keyLabel)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, false),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SIGN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_UNWRAP, true)
                };

                _session.GenerateKeyPair(mechanism, publicKeyAttrs, privateKeyAttrs,
                    out var publicKey, out var privateKey);

                return (publicKey, privateKey);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Generates an AES key inside the Nitrokey.
        /// </summary>
        public async Task<IObjectHandle> GenerateAesKeyAsync(string keyLabel, int keySizeBits = 256)
        {
            await _sessionLock.WaitAsync();
            try
            {
                EnsureSession();

                var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_KEY_GEN);

                var keyAttrs = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, keyLabel),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ID, Encoding.UTF8.GetBytes(keyLabel)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE_LEN, (ulong)(keySizeBits / 8)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, _config.AllowExtractableKeys),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_WRAP, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_UNWRAP, true)
                };

                return _session.GenerateKey(mechanism, keyAttrs);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                EnsureSession();

                var keys = new List<string>();

                // Find all secret keys
                var secretKeyAttrs = new List<IObjectAttribute>
                {
                    _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY)
                };

                foreach (var handle in _session.FindAllObjects(secretKeyAttrs))
                {
                    try
                    {
                        var labelAttr = _session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_LABEL });
                        var label = labelAttr[0].GetValueAsString();
                        if (!string.IsNullOrEmpty(label))
                        {
                            keys.Add($"secret:{label}");
                        }
                    }
                    catch { /* Attribute read failure — skip this key */ }
                }

                // Find all key pairs
                var privateKeyAttrs = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY)
                };

                foreach (var handle in _session.FindAllObjects(privateKeyAttrs))
                {
                    try
                    {
                        var labelAttr = _session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_LABEL });
                        var label = labelAttr[0].GetValueAsString();
                        if (!string.IsNullOrEmpty(label))
                        {
                            keys.Add($"keypair:{label}");
                        }
                    }
                    catch { /* Attribute read failure — skip this key */ }
                }

                return keys.AsReadOnly();
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete Nitrokey keys.");
            }

            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                EnsureSession();

                // Try secret key first
                var handle = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);
                if (handle == null)
                {
                    // Try private key
                    handle = FindKeyByLabel(keyId, CKO.CKO_PRIVATE_KEY);
                    if (handle != null)
                    {
                        // Also delete corresponding public key
                        var publicHandle = FindKeyByLabel(keyId, CKO.CKO_PUBLIC_KEY);
                        if (publicHandle != null)
                        {
                            _session!.DestroyObject(publicHandle);
                        }
                    }
                }

                if (handle != null)
                {
                    _session!.DestroyObject(handle);
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                EnsureSession();

                var handle = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY)
                          ?? FindKeyByLabel(keyId, CKO.CKO_PRIVATE_KEY);

                if (handle == null)
                    return null;

                var attrs = _session!.GetAttributeValue(handle, new List<CKA>
                {
                    CKA.CKA_CLASS,
                    CKA.CKA_KEY_TYPE,
                    CKA.CKA_LABEL
                });

                var keyClass = (CKO)attrs[0].GetValueAsUlong();
                var keyType = (CKK)attrs[1].GetValueAsUlong();

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Backend"] = "Nitrokey PKCS#11",
                        ["Model"] = _config.Model.ToString(),
                        ["KeyClass"] = keyClass.ToString(),
                        ["KeyType"] = keyType.ToString(),
                        ["SlotId"] = _slot?.SlotId ?? 0
                    }
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private IObjectHandle? FindKeyByLabel(string label, CKO objectClass)
        {
            var searchAttrs = new List<IObjectAttribute>
            {
                _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, objectClass),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, label)
            };

            return _session.FindAllObjects(searchAttrs).FirstOrDefault();
        }

        private void EnsureSession()
        {
            if (_session == null || _disposed)
            {
                throw new ObjectDisposedException(nameof(NitrokeyStrategy));
            }
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_sessionLock.Wait(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    if (_session != null)
                    {
                        if (_loggedIn)
                        {
                            try { _session.Logout(); } catch { /* Best-effort cleanup — failure is non-fatal */ }
                        }
                        _session.Dispose();
                    }

                    _pkcs11Library?.Dispose();
                }
                finally
                {
                    _sessionLock.Release();
                    _sessionLock.Dispose();
                }
            }
            else
            {
                // Timeout - force disposal
                _sessionLock.Dispose();
            }

            base.Dispose();
        }
    }

    /// <summary>
    /// Nitrokey device models.
    /// </summary>
    public enum NitrokeyModel
    {
        /// <summary>Nitrokey HSM 2</summary>
        Hsm2,
        /// <summary>Nitrokey Pro 2</summary>
        Pro2,
        /// <summary>Nitrokey Storage 2</summary>
        Storage2,
        /// <summary>Nitrokey 3</summary>
        Nitrokey3
    }

    /// <summary>
    /// Configuration for Nitrokey key store strategy.
    /// </summary>
    public class NitrokeyConfig
    {
        /// <summary>
        /// Path to PKCS#11 library. Auto-detected if not specified.
        /// </summary>
        public string? LibraryPath { get; set; }

        /// <summary>
        /// PKCS#11 slot ID. Auto-detected if not specified.
        /// </summary>
        public ulong? SlotId { get; set; }

        /// <summary>
        /// User PIN for key access.
        /// </summary>
        public string? UserPin { get; set; }

        /// <summary>
        /// Security Officer PIN for administrative operations.
        /// </summary>
        public string? SoPin { get; set; }

        /// <summary>
        /// Default key label for operations.
        /// </summary>
        public string DefaultKeyLabel { get; set; } = "datawarehouse-master";

        /// <summary>
        /// Nitrokey model for configuration hints.
        /// </summary>
        public NitrokeyModel Model { get; set; } = NitrokeyModel.Hsm2;

        /// <summary>
        /// Allow creation of extractable keys.
        /// </summary>
        public bool AllowExtractableKeys { get; set; } = false;
    }
}
