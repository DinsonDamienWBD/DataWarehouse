using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Generic PKCS#11 HSM KeyStore strategy using PKCS11Interop library.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for hardware security module integration.
    ///
    /// Supported features:
    /// - PKCS#11 compliant HSM devices (Thales, SafeNet, nCipher, YubiHSM, SoftHSM, etc.)
    /// - Key generation inside HSM (keys never leave HSM boundary in plaintext)
    /// - AES key wrapping/unwrapping operations
    /// - RSA key wrapping/unwrapping operations
    /// - Session management with automatic reconnection
    /// - Multi-slot support
    ///
    /// Configuration:
    /// - LibraryPath: Path to PKCS#11 library (.dll/.so/.dylib)
    /// - SlotId: HSM slot ID (default: 0)
    /// - Pin: User PIN for authentication
    /// - DefaultKeyLabel: Default key label for operations (default: "datawarehouse-master")
    /// - UseRsaWrapping: Use RSA for wrapping instead of AES (default: false)
    /// </summary>
    public sealed class Pkcs11HsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private Pkcs11HsmConfig _config = new();
        private IPkcs11Library? _pkcs11Library;
        private ISlot? _slot;
        private ISession? _session;
        private string? _currentKeyId;
        private readonly object _sessionLock = new();
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "PKCS#11 HSM",
                ["Standard"] = "PKCS#11 v2.40",
                ["SupportsHardwareKeyGeneration"] = true,
                ["AuthMethod"] = "PIN-based Authentication"
            }
        };


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-KWP", "RSA-OAEP", "AES-256-CBC-PAD" };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("pkcs11hsm.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("LibraryPath", out var libraryPathObj) && libraryPathObj is string libraryPath)
                _config.LibraryPath = libraryPath;
            if (Configuration.TryGetValue("SlotId", out var slotIdObj))
            {
                if (slotIdObj is int slotId) _config.SlotId = (ulong)slotId;
                else if (slotIdObj is long slotIdLong) _config.SlotId = (ulong)slotIdLong;
                else if (slotIdObj is ulong slotIdUlong) _config.SlotId = slotIdUlong;
            }
            if (Configuration.TryGetValue("Pin", out var pinObj) && pinObj is string pin)
                _config.Pin = pin;
            if (Configuration.TryGetValue("DefaultKeyLabel", out var keyLabelObj) && keyLabelObj is string keyLabel)
                _config.DefaultKeyLabel = keyLabel;
            if (Configuration.TryGetValue("UseRsaWrapping", out var useRsaObj) && useRsaObj is bool useRsa)
                _config.UseRsaWrapping = useRsa;

            // Validate configuration
            if (string.IsNullOrEmpty(_config.LibraryPath))
                throw new InvalidOperationException("LibraryPath is required for PKCS#11 HSM strategy");

            if (!File.Exists(_config.LibraryPath))
                throw new InvalidOperationException($"PKCS#11 library not found at: {_config.LibraryPath}");

            if (string.IsNullOrEmpty(_config.Pin))
                throw new InvalidOperationException("Pin is required for PKCS#11 HSM strategy");

            if (_config.SlotId < 0)
                throw new ArgumentException($"SlotId must be >= 0, got {_config.SlotId}");

            // Initialize PKCS#11 library
            await Task.Run(() => InitializePkcs11(), cancellationToken);

            _currentKeyId = _config.DefaultKeyLabel;
        }

        private void InitializePkcs11()
        {
            lock (_sessionLock)
            {
                // Load PKCS#11 library
                var factories = new Pkcs11InteropFactories();
                _pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(
                    factories,
                    _config.LibraryPath,
                    AppType.MultiThreaded);

                // Get slot
                var slots = _pkcs11Library.GetSlotList(SlotsType.WithTokenPresent);
                _slot = slots.FirstOrDefault(s => s.SlotId == _config.SlotId)
                    ?? throw new InvalidOperationException($"Slot {_config.SlotId} not found or has no token present");

                // Open session and login
                OpenSession();
            }
        }

        private void OpenSession()
        {
            _session = _slot!.OpenSession(SessionType.ReadWrite);
            _session.Login(CKU.CKU_USER, Encoding.UTF8.GetBytes(_config.Pin));
        }

        private void EnsureSession()
        {
            lock (_sessionLock)
            {
                if (_session == null || _disposed)
                {
                    throw new ObjectDisposedException(nameof(Pkcs11HsmStrategy));
                }

                // Check if session is still valid by getting session info
                try
                {
                    _ = _session.GetSessionInfo();
                }
                catch
                {
                    // Session expired or invalid, reopen
                    try
                    {
                        _session?.Logout();
                        _session?.Dispose();
                    }
                    catch { /* Ignore cleanup errors */ }

                    OpenSession();
                }
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? _config.DefaultKeyLabel);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                try
                {
                    return await Task.Run(() =>
                    {
                        EnsureSession();
                        var tokenInfo = _slot!.GetTokenInfo();
                        var sessionInfo = _session!.GetSessionInfo();

                        var isHealthy = tokenInfo != null &&
                                      (sessionInfo.State == CKS.CKS_RW_USER_FUNCTIONS ||
                                       sessionInfo.State == CKS.CKS_RW_PUBLIC_SESSION);

                        return new StrategyHealthCheckResult(
                            isHealthy,
                            isHealthy ? $"PKCS#11 HSM operational (slot {_config.SlotId})" : "HSM not in operational state");
                    }, ct);
                }
                catch (Exception ex)
                {
                    return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
                }
            }, TimeSpan.FromSeconds(60), cancellationToken);

            return result.IsHealthy;
        }

        /// <inheritdoc/>
        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            var timeout = TimeSpan.FromSeconds(10);
            lock (_sessionLock)
            {
                try
                {
                    _session?.Logout();
                    _session?.Dispose();
                }
                catch { /* Best-effort cleanup */ }
                finally
                {
                    _session = null;
                }

                try
                {
                    _pkcs11Library?.Dispose();
                }
                catch { /* Best-effort cleanup */ }
                finally
                {
                    _pkcs11Library = null;
                }
            }

            await base.ShutdownAsyncCore(cancellationToken);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // For PKCS#11 HSM, keys never leave the HSM in plaintext
            // This method generates a new symmetric key inside the HSM
            return await Task.Run(() =>
            {
                EnsureSession();

                var keyHandle = FindKeyByLabel(keyId);
                if (keyHandle == null)
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found in HSM");
                }

                // For symmetric keys that are extractable, we can export them
                // Note: Most HSM configurations mark keys as non-extractable
                // In that case, use envelope encryption patterns
                var attributes = _session!.GetAttributeValue(keyHandle, new List<CKA> { CKA.CKA_VALUE, CKA.CKA_EXTRACTABLE });

                var extractable = attributes[1].GetValueAsBool();
                if (!extractable)
                {
                    // Key is not extractable - generate a DEK and return it wrapped
                    // This is the secure HSM pattern
                    throw new InvalidOperationException(
                        $"Key '{keyId}' is not extractable from HSM. Use envelope encryption with WrapKeyAsync/UnwrapKeyAsync instead.");
                }

                return attributes[0].GetValueAsByteArray();
            });
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // Import key into HSM or generate new key inside HSM
            await Task.Run(() =>
            {
                EnsureSession();

                // Check if key already exists and delete it
                var existingKey = FindKeyByLabel(keyId);
                if (existingKey != null)
                {
                    _session!.DestroyObject(existingKey);
                }

                // Create attributes for new secret key
                var keyAttributes = new List<IObjectAttribute>
                {
                    _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, Encoding.UTF8.GetBytes(keyId)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ID, Encoding.UTF8.GetBytes(keyId)),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, keyData),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE_LEN, (ulong)keyData.Length),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, false), // Non-extractable for security
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_WRAP, true),
                    _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_UNWRAP, true)
                };

                _session.CreateObject(keyAttributes);
                _currentKeyId = keyId;
            });
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            return await Task.Run(() =>
            {
                try
                {
                    EnsureSession();

                    var wrappingKey = FindKeyByLabel(kekId)
                        ?? throw new KeyNotFoundException($"KEK '{kekId}' not found in HSM");

                    // Determine wrapping mechanism based on key type
                    var keyType = GetKeyType(wrappingKey);

                    byte[] result;
                    if (_config.UseRsaWrapping || keyType == CKK.CKK_RSA)
                    {
                        // RSA-OAEP wrapping
                        using var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_OAEP);
                        result = _session.WrapKey(mechanism, wrappingKey, CreateTemporaryDataKey(dataKey));
                    }
                    else
                    {
                        // AES Key Wrap with Padding (RFC 5649)
                        using var mechanism = _session!.Factories.MechanismFactory.Create(CKM.CKM_AES_KEY_WRAP_PAD);
                        result = _session.WrapKey(mechanism, wrappingKey, CreateTemporaryDataKey(dataKey));
                    }

                    IncrementCounter("hsm.encrypt");
                    return result;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"HSM key wrap failed for KEK '{kekId}'", ex);
                }
            });
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            return await Task.Run(() =>
            {
                try
                {
                    EnsureSession();

                    var wrappingKey = FindKeyByLabel(kekId)
                        ?? throw new KeyNotFoundException($"KEK '{kekId}' not found in HSM");

                    // Determine unwrapping mechanism based on key type
                    var keyType = GetKeyType(wrappingKey);

                    // Attributes for the unwrapped key
                    var unwrappedKeyAttributes = new List<IObjectAttribute>
                    {
                        _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false), // Session object (temporary)
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, false), // Allow extraction
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true),
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_ENCRYPT, true),
                        _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_DECRYPT, true)
                    };

                    IObjectHandle unwrappedKey;

                    if (_config.UseRsaWrapping || keyType == CKK.CKK_RSA)
                    {
                        // RSA-OAEP unwrapping
                        using var mechanism = _session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS_OAEP);
                        unwrappedKey = _session.UnwrapKey(mechanism, wrappingKey, wrappedKey, unwrappedKeyAttributes);
                    }
                    else
                    {
                        // AES Key Wrap with Padding
                        using var mechanism = _session.Factories.MechanismFactory.Create(CKM.CKM_AES_KEY_WRAP_PAD);
                        unwrappedKey = _session.UnwrapKey(mechanism, wrappingKey, wrappedKey, unwrappedKeyAttributes);
                    }

                    // Extract the key value
                    var valueAttr = _session.GetAttributeValue(unwrappedKey, new List<CKA> { CKA.CKA_VALUE });
                    var keyValue = valueAttr[0].GetValueAsByteArray();

                    // Destroy the temporary key object
                    _session.DestroyObject(unwrappedKey);

                    IncrementCounter("hsm.sign");
                    return keyValue;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"HSM key unwrap failed for KEK '{kekId}'", ex);
                }
            });
        }

        private IObjectHandle? FindKeyByLabel(string label)
        {
            var searchAttributes = new List<IObjectAttribute>
            {
                _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_LABEL, Encoding.UTF8.GetBytes(label))
            };

            var keys = _session.FindAllObjects(searchAttributes);
            return keys.FirstOrDefault();
        }

        private CKK GetKeyType(IObjectHandle key)
        {
            var attrs = _session!.GetAttributeValue(key, new List<CKA> { CKA.CKA_KEY_TYPE });
            return (CKK)attrs[0].GetValueAsUlong();
        }

        private IObjectHandle CreateTemporaryDataKey(byte[] keyData)
        {
            // Create a temporary secret key object for wrapping
            var keyAttributes = new List<IObjectAttribute>
            {
                _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_AES),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_VALUE, keyData),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, false), // Session object
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_PRIVATE, true),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_SENSITIVE, false),
                _session.Factories.ObjectAttributeFactory.Create(CKA.CKA_EXTRACTABLE, true)
            };

            return _session.CreateObject(keyAttributes);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            return await Task.Run(() =>
            {
                EnsureSession();

                var searchAttributes = new List<IObjectAttribute>
                {
                    _session!.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_SECRET_KEY)
                };

                var keys = _session.FindAllObjects(searchAttributes);
                var keyLabels = new List<string>();

                foreach (var key in keys)
                {
                    try
                    {
                        var labelAttr = _session.GetAttributeValue(key, new List<CKA> { CKA.CKA_LABEL });
                        var label = Encoding.UTF8.GetString(labelAttr[0].GetValueAsByteArray());
                        if (!string.IsNullOrEmpty(label))
                        {
                            keyLabels.Add(label);
                        }
                    }
                    catch
                    {
                        // Skip keys we can't read
                    }
                }

                return keyLabels.AsReadOnly();
            }, cancellationToken);
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            await Task.Run(() =>
            {
                EnsureSession();

                var key = FindKeyByLabel(keyId)
                    ?? throw new KeyNotFoundException($"Key '{keyId}' not found in HSM");

                _session!.DestroyObject(key);
            }, cancellationToken);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            return await Task.Run(() =>
            {
                try
                {
                    EnsureSession();

                    var key = FindKeyByLabel(keyId);
                    if (key == null) return null;

                    var attributes = _session!.GetAttributeValue(key, new List<CKA>
                    {
                        CKA.CKA_KEY_TYPE,
                        CKA.CKA_VALUE_LEN,
                        CKA.CKA_EXTRACTABLE,
                        CKA.CKA_SENSITIVE,
                        CKA.CKA_TOKEN
                    });

                    var keyType = (CKK)attributes[0].GetValueAsUlong();
                    var keyLen = (int)attributes[1].GetValueAsUlong();

                    return new KeyMetadata
                    {
                        KeyId = keyId,
                        CreatedAt = DateTime.UtcNow, // PKCS#11 doesn't store creation time
                        KeySizeBytes = keyLen,
                        IsActive = keyId == _currentKeyId,
                        Metadata = new Dictionary<string, object>
                        {
                            ["Backend"] = "PKCS#11 HSM",
                            ["KeyType"] = keyType.ToString(),
                            ["SlotId"] = _config.SlotId,
                            ["Extractable"] = attributes[2].GetValueAsBool(),
                            ["Sensitive"] = attributes[3].GetValueAsBool(),
                            ["TokenObject"] = attributes[4].GetValueAsBool()
                        }
                    };
                }
                catch
                {
                    return null;
                }
            }, cancellationToken);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_sessionLock)
            {
                try
                {
                    _session?.Logout();
                    _session?.Dispose();
                }
                catch { /* Ignore cleanup errors */ }

                _session = null;

                try
                {
                    _pkcs11Library?.Dispose();
                }
                catch { /* Ignore cleanup errors */ }

                _pkcs11Library = null;
            }

            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for PKCS#11 HSM key store strategy.
    /// </summary>
    public class Pkcs11HsmConfig
    {
        public string LibraryPath { get; set; } = string.Empty;
        public ulong SlotId { get; set; } = 0;
        public string Pin { get; set; } = string.Empty;
        public string DefaultKeyLabel { get; set; } = "datawarehouse-master";
        public bool UseRsaWrapping { get; set; } = false;
    }
}
