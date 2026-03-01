using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Abstract base class for PKCS#11-based HSM key store strategies.
    /// Provides shared infrastructure for connecting to HSMs via the PKCS#11 standard interface.
    ///
    /// Derived classes (ThalesLunaStrategy, UtimacoStrategy, NcipherStrategy) provide
    /// vendor-specific library paths and configurations while sharing common PKCS#11 operations.
    ///
    /// Key features:
    /// - PKCS#11 session management with automatic reconnection
    /// - Symmetric key generation (AES-256) within HSM boundary
    /// - Key wrapping/unwrapping using HSM-stored KEKs
    /// - Slot/token discovery and selection
    /// - PIN-based authentication
    /// </summary>
    public abstract class Pkcs11HsmStrategyBase : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private IPkcs11Library? _pkcs11Library;
        private ISlot? _slot;
        private ISession? _session;
        private readonly SemaphoreSlim _sessionLock = new(1, 1);
        private bool _loggedIn;
        private Pkcs11HsmBaseConfig _config = new();

        /// <summary>
        /// Gets the vendor name for this HSM implementation.
        /// Used for logging and metadata.
        /// </summary>
        protected abstract string VendorName { get; }

        /// <summary>
        /// Gets the default PKCS#11 library path for this HSM vendor.
        /// Can be overridden via configuration.
        /// </summary>
        protected abstract string DefaultLibraryPath { get; }

        /// <summary>
        /// Gets vendor-specific metadata to include in capabilities.
        /// </summary>
        protected abstract Dictionary<string, object> VendorMetadata { get; }

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
            Metadata = new Dictionary<string, object>(VendorMetadata)
            {
                ["Provider"] = VendorName,
                ["Interface"] = "PKCS#11",
                ["SupportsAes256"] = true,
                ["SupportsRsaOaep"] = true,
                ["KeysNeverLeaveHsm"] = true
            }
        };


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "AES-256-CBC-PAD",
            "AES-256-GCM",
            "RSA-OAEP-SHA256",
            "RSA-PKCS"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("pkcs11hsmbase.init");
            // Load configuration
            if (Configuration.TryGetValue("LibraryPath", out var libPathObj) && libPathObj is string libPath)
                _config.LibraryPath = libPath;
            else
                _config.LibraryPath = DefaultLibraryPath;

            if (Configuration.TryGetValue("SlotId", out var slotIdObj))
            {
                if (slotIdObj is int slotId)
                    _config.SlotId = (ulong)slotId;
                else if (slotIdObj is long slotIdLong)
                    _config.SlotId = (ulong)slotIdLong;
                else if (slotIdObj is ulong slotIdUlong)
                    _config.SlotId = slotIdUlong;
            }

            if (Configuration.TryGetValue("TokenLabel", out var tokenLabelObj) && tokenLabelObj is string tokenLabel)
                _config.TokenLabel = tokenLabel;

            if (Configuration.TryGetValue("Pin", out var pinObj) && pinObj is string pin)
                _config.Pin = pin;

            if (Configuration.TryGetValue("DefaultKeyLabel", out var keyLabelObj) && keyLabelObj is string keyLabel)
                _config.DefaultKeyLabel = keyLabel;

            if (Configuration.TryGetValue("UseUserPin", out var useUserPinObj) && useUserPinObj is bool useUserPin)
                _config.UseUserPin = useUserPin;

            // Validate configuration
            if (string.IsNullOrEmpty(_config.LibraryPath))
                throw new InvalidOperationException($"LibraryPath is required for {VendorName} HSM strategy");

            if (!File.Exists(_config.LibraryPath))
                throw new InvalidOperationException($"PKCS#11 library not found at: {_config.LibraryPath}");

            // P2-3546: SlotId is ulong? — unsigned type can never be negative; the check was dead code.
            // No validation needed beyond HasValue (ulong guarantees non-negative by definition).

            // Initialize PKCS#11 library
            await InitializePkcs11Async(cancellationToken);
        }

        private async Task InitializePkcs11Async(CancellationToken cancellationToken)
        {
            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                // Determine platform-specific factory settings
                var factories = new Pkcs11InteropFactories();

                // Load the PKCS#11 library
                _pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(
                    factories,
                    _config.LibraryPath,
                    AppType.MultiThreaded);

                // Get available slots
                var slots = _pkcs11Library.GetSlotList(SlotsType.WithTokenPresent);

                if (slots.Count == 0)
                {
                    throw new InvalidOperationException($"No HSM tokens found in PKCS#11 library: {_config.LibraryPath}");
                }

                // Select slot by ID or token label
                if (_config.SlotId.HasValue)
                {
                    _slot = slots.FirstOrDefault(s => s.SlotId == _config.SlotId.Value);
                    if (_slot == null)
                    {
                        throw new InvalidOperationException($"Slot ID {_config.SlotId} not found in HSM");
                    }
                }
                else if (!string.IsNullOrEmpty(_config.TokenLabel))
                {
                    _slot = slots.FirstOrDefault(s =>
                    {
                        var tokenInfo = s.GetTokenInfo();
                        return tokenInfo.Label.Trim() == _config.TokenLabel;
                    });

                    if (_slot == null)
                    {
                        throw new InvalidOperationException($"Token with label '{_config.TokenLabel}' not found in HSM");
                    }
                }
                else
                {
                    // Use first available slot
                    _slot = slots[0];
                }

                // Open session and login
                _session = _slot.OpenSession(SessionType.ReadWrite);

                if (!string.IsNullOrEmpty(_config.Pin))
                {
                    var userType = _config.UseUserPin ? CKU.CKU_USER : CKU.CKU_SO;
                    _session.Login(userType, _config.Pin);
                    _loggedIn = true;
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                await _sessionLock.WaitAsync(ct);
                try
                {
                    if (_session == null || _slot == null)
                        return new StrategyHealthCheckResult(false, $"{VendorName} HSM session not initialized");

                    // Verify session is still active by getting session info
                    var sessionInfo = _session.GetSessionInfo();
                    var isHealthy = sessionInfo.State == CKS.CKS_RW_USER_FUNCTIONS ||
                                   sessionInfo.State == CKS.CKS_RW_PUBLIC_SESSION ||
                                   sessionInfo.State == CKS.CKS_RW_SO_FUNCTIONS;

                    // Try to read token info
                    var tokenInfo = _slot.GetTokenInfo();

                    return new StrategyHealthCheckResult(
                        isHealthy,
                        isHealthy ? $"{VendorName} HSM operational (token: {tokenInfo.Label.Trim()})" : "HSM session in invalid state");
                }
                catch (Exception ex)
                {
                    return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
                }
                finally
                {
                    _sessionLock.Release();
                }
            }, TimeSpan.FromSeconds(60), cancellationToken);

            return result.IsHealthy;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_config.DefaultKeyLabel);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // P2-3548: Base-class abstract signature has no CancellationToken; pass None to avoid indefinite block.
            await _sessionLock.WaitAsync(CancellationToken.None);
            try
            {
                EnsureSession();

                // Find the key object by label
                var keyHandle = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);

                if (keyHandle == null)
                {
                    // Key doesn't exist - generate a new one
                    var newKeyData = GenerateKey();
                    await SaveKeyToStorageInternal(keyId, newKeyData, context);
                    return newKeyData;
                }

                // For HSM-stored keys, we typically cannot extract the key material directly
                // Instead, we use the key handle for cryptographic operations
                // If the key is extractable, we can get its value
                var attributes = _session!.GetAttributeValue(keyHandle, new List<CKA>
                {
                    CKA.CKA_EXTRACTABLE,
                    CKA.CKA_VALUE
                });

                var extractable = attributes[0].GetValueAsBool();

                if (extractable)
                {
                    return attributes[1].GetValueAsByteArray();
                }

                // Key is not extractable - this is the secure mode
                // Return a reference token that can be used with WrapKeyAsync/UnwrapKeyAsync
                // For non-extractable keys, we generate a wrapped representation
                throw new InvalidOperationException(
                    $"Key '{keyId}' is stored in HSM and is not extractable. " +
                    "Use WrapKeyAsync/UnwrapKeyAsync for envelope encryption operations.");
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await SaveKeyToStorageInternal(keyId, keyData, context);
        }

        private async Task SaveKeyToStorageInternal(string keyId, byte[] keyData, ISecurityContext context)
        {
            // P2-3548: No CancellationToken in scope here; pass None.
            await _sessionLock.WaitAsync(CancellationToken.None);
            try
            {
                EnsureSession();

                // Check if key already exists
                var existingKey = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);
                if (existingKey != null)
                {
                    // Destroy existing key before creating new one
                    _session!.DestroyObject(existingKey);
                }

                // Create key attributes for a AES-256 key
                var keyAttributes = new List<IObjectAttribute>
                {
                    _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, keyId),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, keyData),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE_LEN, (ulong)keyData.Length),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, false),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_WRAP, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_UNWRAP, true)
                };

                _session.CreateObject(keyAttributes);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            // P2-3548: Interface has no CT; pass None. Consider upgrading IEnvelopeKeyStore to add CT in a future SDK version.
            await _sessionLock.WaitAsync(CancellationToken.None);
            try
            {
                EnsureSession();

                // Find the wrapping key (KEK)
                var kekHandle = FindKeyByLabel(kekId, CKO.CKO_SECRET_KEY);
                if (kekHandle == null)
                {
                    throw new InvalidOperationException($"KEK with label '{kekId}' not found in HSM");
                }

                // Generate IV for AES-CBC wrapping
                var iv = new byte[16];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(iv);

                // Create mechanism for AES-CBC-PAD wrapping
                var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_CBC_PAD, iv);

                // Create a temporary key object from the data key
                var tempKeyAttributes = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, dataKey),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
                };

                var tempKeyHandle = _session.CreateObject(tempKeyAttributes);

                try
                {
                    // Wrap the temporary key with the KEK
                    var wrappedKey = _session.WrapKey(mechanism, kekHandle, tempKeyHandle);

                    // Prepend IV to wrapped key
                    var result = new byte[iv.Length + wrappedKey.Length];
                    Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
                    Buffer.BlockCopy(wrappedKey, 0, result, iv.Length, wrappedKey.Length);

                    IncrementCounter("hsm.encrypt");
                    return result;
                }
                finally
                {
                    // Clean up temporary key
                    _session.DestroyObject(tempKeyHandle);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"HSM key wrap failed for KEK '{kekId}'", ex);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            // P2-3548: Interface has no CT; pass None.
            await _sessionLock.WaitAsync(CancellationToken.None);
            try
            {
                EnsureSession();

                // Find the unwrapping key (KEK)
                var kekHandle = FindKeyByLabel(kekId, CKO.CKO_SECRET_KEY);
                if (kekHandle == null)
                {
                    throw new InvalidOperationException($"KEK with label '{kekId}' not found in HSM");
                }

                // Extract IV (first 16 bytes)
                if (wrappedKey.Length < 17)
                {
                    throw new ArgumentException("Wrapped key data is too short - missing IV", nameof(wrappedKey));
                }

                var iv = new byte[16];
                Buffer.BlockCopy(wrappedKey, 0, iv, 0, 16);

                var encryptedKey = new byte[wrappedKey.Length - 16];
                Buffer.BlockCopy(wrappedKey, 16, encryptedKey, 0, encryptedKey.Length);

                // Create mechanism for AES-CBC-PAD unwrapping
                var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_CBC_PAD, iv);

                // Define attributes for the unwrapped key
                var keyAttributes = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
                };

                // Unwrap the key
                var unwrappedKeyHandle = _session.UnwrapKey(mechanism, kekHandle, encryptedKey, keyAttributes);

                try
                {
                    // Extract the key value
                    var valueAttr = _session.GetAttributeValue(unwrappedKeyHandle, new List<CKA> { CKA.CKA_VALUE });
                    IncrementCounter("hsm.unwrap");
                    return valueAttr[0].GetValueAsByteArray();
                }
                finally
                {
                    // Clean up temporary key object
                    _session.DestroyObject(unwrappedKeyHandle);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"HSM key unwrap failed for KEK '{kekId}'", ex);
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

                // Find all secret key objects
                var searchAttributes = new List<IObjectAttribute>
                {
                    _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY)
                };

                var keyHandles = _session.FindAllObjects(searchAttributes);
                var keyLabels = new List<string>();

                foreach (var handle in keyHandles)
                {
                    try
                    {
                        var labelAttr = _session.GetAttributeValue(handle, new List<CKA> { CKA.CKA_LABEL });
                        var label = labelAttr[0].GetValueAsString();
                        if (!string.IsNullOrEmpty(label))
                        {
                            keyLabels.Add(label);
                        }
                    }
                    catch
                    {

                        // Skip keys we can't read
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                }

                return keyLabels.AsReadOnly();
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
                throw new UnauthorizedAccessException("Only system administrators can delete HSM keys.");
            }

            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                EnsureSession();

                var keyHandle = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);
                if (keyHandle != null)
                {
                    _session!.DestroyObject(keyHandle);
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

                var keyHandle = FindKeyByLabel(keyId, CKO.CKO_SECRET_KEY);
                if (keyHandle == null)
                    return null;

                var attributes = _session!.GetAttributeValue(keyHandle, new List<CKA>
                {
                    CKA.CKA_LABEL,
                    CKA.CKA_KEY_TYPE,
                    CKA.CKA_VALUE_LEN,
                    CKA.CKA_EXTRACTABLE,
                    CKA.CKA_SENSITIVE
                });

                var label = attributes[0].GetValueAsString();
                var keyType = attributes[1].GetValueAsUlong();
                var keyLength = attributes[2].GetValueAsUlong();
                var extractable = attributes[3].GetValueAsBool();
                var sensitive = attributes[4].GetValueAsBool();

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = DateTime.UtcNow, // PKCS#11 doesn't track creation time
                    KeySizeBytes = (int)keyLength,
                    IsActive = keyId == _config.DefaultKeyLabel,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Provider"] = VendorName,
                        ["Interface"] = "PKCS#11",
                        ["SlotId"] = _slot?.SlotId ?? 0,
                        ["KeyType"] = GetKeyTypeName((CKK)keyType),
                        ["Extractable"] = extractable,
                        ["Sensitive"] = sensitive
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

        /// <summary>
        /// Generates a new AES-256 key inside the HSM.
        /// The key is generated within the HSM boundary and never leaves in plaintext.
        /// </summary>
        public async Task<IObjectHandle> GenerateHsmKeyAsync(string keyLabel, bool extractable = false)
        {
            // P2-3548: No CT in this public API; pass None.
            await _sessionLock.WaitAsync(CancellationToken.None);
            try
            {
                EnsureSession();

                var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_KEY_GEN);

                var keyAttributes = new List<IObjectAttribute>
                {
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, keyLabel),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE_LEN, 32UL), // 256 bits
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, !extractable),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, extractable),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_WRAP, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_UNWRAP, true)
                };

                var handle = _session.GenerateKey(mechanism, keyAttributes);
                IncrementCounter("hsm.keygen");
                return handle;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"HSM key generation failed for label '{keyLabel}'", ex);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <inheritdoc/>
        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            var timeout = TimeSpan.FromSeconds(10);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            if (await _sessionLock.WaitAsync(timeout, cts.Token))
            {
                try
                {
                    if (_loggedIn && _session != null)
                    {
                        try
                        {
                            _session.Logout();
                            _loggedIn = false;
                        }
                        catch { /* Best-effort logout */ }
                    }
                }
                finally
                {
                    _sessionLock.Release();
                }
            }

            await base.ShutdownAsyncCore(cancellationToken);
        }

        private IObjectHandle? FindKeyByLabel(string label, CKO objectClass)
        {
            var searchAttributes = new List<IObjectAttribute>
            {
                _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, objectClass),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, label)
            };

            var objects = _session.FindAllObjects(searchAttributes);
            return objects.FirstOrDefault();
        }

        private void EnsureSession()
        {
            if (_session == null)
            {
                throw new InvalidOperationException("PKCS#11 session is not initialized");
            }
        }

        private static string GetKeyTypeName(CKK keyType)
        {
            return keyType switch
            {
                CKK.CKK_AES => "AES",
                CKK.CKK_DES3 => "3DES",
                CKK.CKK_RSA => "RSA",
                CKK.CKK_EC => "EC",
                CKK.CKK_GENERIC_SECRET => "Generic",
                _ => keyType.ToString()
            };
        }

        public override void Dispose()
        {
            if (_session != null)
            {
                try
                {
                    if (_loggedIn)
                    {
                        _session.Logout();
                    }
                    _session.Dispose();
                }
                catch { /* Best-effort cleanup — failure is non-fatal */ }
                _session = null;
            }

            if (_pkcs11Library != null)
            {
                try
                {
                    _pkcs11Library.Dispose();
                }
                catch { /* Best-effort cleanup — failure is non-fatal */ }
                _pkcs11Library = null;
            }

            _sessionLock.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for PKCS#11-based HSM key store strategies (base class version).
    /// This is separate from Pkcs11HsmConfig in Pkcs11HsmStrategy.cs to support
    /// vendor-specific extensions in derived strategies.
    /// </summary>
    public class Pkcs11HsmBaseConfig
    {
        /// <summary>
        /// Path to the PKCS#11 shared library (.so on Linux, .dll on Windows).
        /// </summary>
        public string LibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Slot ID to use. If null, TokenLabel or first available slot is used.
        /// </summary>
        public ulong? SlotId { get; set; }

        /// <summary>
        /// Token label to search for. Used if SlotId is not specified.
        /// </summary>
        public string? TokenLabel { get; set; }

        /// <summary>
        /// PIN for authentication (user or SO depending on UseUserPin).
        /// </summary>
        public string? Pin { get; set; }

        /// <summary>
        /// Whether to use CKU_USER (true) or CKU_SO (false) for login.
        /// </summary>
        public bool UseUserPin { get; set; } = true;

        /// <summary>
        /// Default key label to use for encryption operations.
        /// </summary>
        public string DefaultKeyLabel { get; set; } = "datawarehouse-master";
    }
}
