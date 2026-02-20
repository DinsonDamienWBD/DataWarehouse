using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// Hardware Security Module (HSM) provider using PKCS#11 Cryptoki API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides cryptographic operations via HSM hardware where key material NEVER leaves the device:
    /// <list type="bullet">
    /// <item><description>HSM connection via PKCS#11 (slot ID + PIN authentication)</description></item>
    /// <item><description>Key generation (RSA, ECC, AES) inside HSM</description></item>
    /// <item><description>Digital signatures with HSM-stored keys</description></item>
    /// <item><description>Encryption/decryption operations</description></item>
    /// <item><description>Key listing and management</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Security Guarantee</strong>: PKCS#11 HSMs never export private key material.
    /// All private key operations (signing, decryption) happen inside the HSM hardware.
    /// Only public keys, signatures, and ciphertexts cross the HSM boundary.
    /// </para>
    /// <para>
    /// <strong>PKCS#11 Workflow</strong>:
    /// <list type="number">
    /// <item><description>Load PKCS#11 library (vendor-specific DLL/SO)</description></item>
    /// <item><description>Initialize library with C_Initialize</description></item>
    /// <item><description>Open session to HSM slot with C_OpenSession</description></item>
    /// <item><description>Authenticate with PIN via C_Login</description></item>
    /// <item><description>Perform cryptographic operations (C_GenerateKey, C_Sign, etc.)</description></item>
    /// <item><description>Close session and finalize library</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Supported HSMs</strong>:
    /// <list type="bullet">
    /// <item><description><strong>Thales/SafeNet Luna HSM</strong>: Enterprise-grade PCIe/network HSMs</description></item>
    /// <item><description><strong>AWS CloudHSM</strong>: Cloud-based HSM service</description></item>
    /// <item><description><strong>Azure Dedicated HSM</strong>: Azure-hosted Thales Luna HSMs</description></item>
    /// <item><description><strong>SoftHSM</strong>: Software HSM for testing (not production-secure)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Phase 35 Implementation Note</strong>: This implementation provides PKCS#11 connection,
    /// session management, and API contracts with placeholder cryptographic operations. Full PKCS#11
    /// mechanism/template marshaling (building CK_MECHANISM structures, attribute templates, etc.)
    /// is complex and deferred to future phases. Production use may require a higher-level PKCS#11
    /// library like Pkcs11Interop (NuGet package).
    /// </para>
    /// <para>
    /// <strong>Usage Pattern</strong>:
    /// <code>
    /// var hsm = new HsmProvider();
    /// await hsm.ConnectAsync("0", "1234"); // slot 0, PIN "1234"
    /// if (hsm.IsConnected)
    /// {
    ///     var keyId = await hsm.GenerateKeyAsync("mykey", new HsmKeySpec("RSA", 2048, false));
    ///     var signature = await hsm.SignAsync("mykey", dataToSign, HsmSignatureAlgorithm.RsaPkcs1Sha256);
    /// }
    /// await hsm.DisconnectAsync();
    /// </code>
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 35: PKCS#11 HSM provider (HW-04)")]
    public sealed class HsmProvider : IHsmProvider, IDisposable, IAsyncDisposable
    {
        private IntPtr _pkcs11Library = IntPtr.Zero;
        private Pkcs11Wrapper.CK_FUNCTION_LIST _functions;
        private uint _session = 0;
        private bool _isConnected = false;
        private readonly BoundedDictionary<string, uint> _keyHandles = new BoundedDictionary<string, uint>(1000); // label -> HSM object handle
        private readonly object _lock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="HsmProvider"/> class.
        /// </summary>
        public HsmProvider()
        {
        }

        /// <summary>
        /// Gets whether the provider is currently connected to an HSM.
        /// </summary>
        /// <remarks>
        /// Returns true after successful <see cref="ConnectAsync"/> authentication.
        /// Returns false if not connected, connection failed, or after <see cref="DisconnectAsync"/>.
        /// </remarks>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// Connects to the HSM with the specified slot and PIN.
        /// </summary>
        /// <param name="slotId">HSM slot identifier (typically "0" for first slot).</param>
        /// <param name="pin">PIN for user authentication.</param>
        /// <returns>A task representing the asynchronous connection operation.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when:
        /// <list type="bullet">
        /// <item><description>Already connected</description></item>
        /// <item><description>PKCS#11 library not found</description></item>
        /// <item><description>Library initialization fails</description></item>
        /// <item><description>Session open fails</description></item>
        /// <item><description>Login fails (incorrect PIN or slot)</description></item>
        /// </list>
        /// </exception>
        public async Task ConnectAsync(string slotId, string pin)
        {
            ArgumentException.ThrowIfNullOrEmpty(slotId, nameof(slotId));
            ArgumentException.ThrowIfNullOrEmpty(pin, nameof(pin));

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_isConnected)
                        throw new InvalidOperationException("Already connected to HSM.");

                    // 1. Load PKCS#11 library
                    _pkcs11Library = Pkcs11Wrapper.LoadLibrary();
                    if (_pkcs11Library == IntPtr.Zero)
                        throw new InvalidOperationException($"Failed to load PKCS#11 library from {Pkcs11Wrapper.Pkcs11LibraryPath}");

                    // 2. Get function list
                    _functions = Pkcs11Wrapper.GetFunctionList(_pkcs11Library);

                    // 3. Initialize PKCS#11 library
                    var initialize = Marshal.GetDelegateForFunctionPointer<Pkcs11Wrapper.C_Initialize>(_functions.C_Initialize);
                    uint rv = initialize(IntPtr.Zero);
                    if (rv != Pkcs11Wrapper.CKR_OK)
                        throw new InvalidOperationException($"C_Initialize failed: 0x{rv:X8}");

                    // 4. Open session
                    uint slot = uint.Parse(slotId);
                    var openSession = Marshal.GetDelegateForFunctionPointer<Pkcs11Wrapper.C_OpenSession>(_functions.C_OpenSession);
                    rv = openSession(
                        slot,
                        Pkcs11Wrapper.CKF_SERIAL_SESSION | Pkcs11Wrapper.CKF_RW_SESSION,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out _session);
                    if (rv != Pkcs11Wrapper.CKR_OK)
                        throw new InvalidOperationException($"C_OpenSession failed: 0x{rv:X8}");

                    // 5. Login with PIN
                    var login = Marshal.GetDelegateForFunctionPointer<Pkcs11Wrapper.C_Login>(_functions.C_Login);
                    byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
                    rv = login(_session, Pkcs11Wrapper.CKU_USER, pinBytes, (uint)pinBytes.Length);
                    if (rv != Pkcs11Wrapper.CKR_OK)
                        throw new InvalidOperationException($"C_Login failed (incorrect PIN?): 0x{rv:X8}");

                    _isConnected = true;
                }
            });
        }

        /// <summary>
        /// Disconnects from the HSM and releases all resources.
        /// </summary>
        /// <returns>A task representing the asynchronous disconnection operation.</returns>
        /// <remarks>
        /// Safe to call multiple times. No-op if not connected.
        /// </remarks>
        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (!_isConnected) return;

                    // Logout
                    if (_functions.C_Logout != IntPtr.Zero)
                    {
                        var logout = Marshal.GetDelegateForFunctionPointer<Pkcs11Wrapper.C_Logout>(_functions.C_Logout);
                        logout(_session);
                    }

                    // Close session
                    if (_functions.C_CloseSession != IntPtr.Zero)
                    {
                        var closeSession = Marshal.GetDelegateForFunctionPointer<Pkcs11Wrapper.C_CloseSession>(_functions.C_CloseSession);
                        closeSession(_session);
                    }

                    // Finalize library
                    if (_functions.C_Finalize != IntPtr.Zero)
                    {
                        var finalize = Marshal.GetDelegateForFunctionPointer<Pkcs11Wrapper.C_Finalize>(_functions.C_Finalize);
                        finalize(IntPtr.Zero);
                    }

                    // Unload library
                    if (_pkcs11Library != IntPtr.Zero)
                    {
                        NativeLibrary.Free(_pkcs11Library);
                        _pkcs11Library = IntPtr.Zero;
                    }

                    _isConnected = false;
                    _keyHandles.Clear();
                }
            });
        }

        /// <summary>
        /// Lists all keys stored in the HSM.
        /// </summary>
        /// <returns>Array of key labels.</returns>
        /// <exception cref="InvalidOperationException">Thrown when not connected to HSM.</exception>
        /// <remarks>
        /// <para>Actual implementation requires C_FindObjectsInit, C_FindObjects, C_FindObjectsFinal.</para>
        /// For Phase 35, returns keys from local cache (keys generated via <see cref="GenerateKeyAsync"/>).
        /// </remarks>
        public Task<string[]> ListKeysAsync()
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected to HSM. Call ConnectAsync first.");

            lock (_lock)
            {
                // SIMPLIFIED for Phase 35: return stored key labels
                // Production: use C_FindObjectsInit / C_FindObjects / C_FindObjectsFinal

                return Task.FromResult(_keyHandles.Keys.ToArray());
            }
        }

        /// <summary>
        /// Generates a new key in the HSM.
        /// </summary>
        /// <param name="label">Key label (human-readable identifier).</param>
        /// <param name="spec">Key specification (algorithm, size, exportability).</param>
        /// <returns>Key handle or identifier (PKCS#11 object handle as bytes).</returns>
        /// <exception cref="InvalidOperationException">Thrown when not connected or key already exists.</exception>
        /// <remarks>
        /// The generated key is stored in HSM non-volatile memory and persists across sessions.
        /// <para>Actual implementation requires building CK_ATTRIBUTE template and calling C_GenerateKey
        /// or C_GenerateKeyPair (for asymmetric keys).
        /// </remarks>
        public Task<byte[]> GenerateKeyAsync(string label, HsmKeySpec spec)
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected to HSM.");

            ArgumentException.ThrowIfNullOrEmpty(label, nameof(label));
            ArgumentNullException.ThrowIfNull(spec, nameof(spec));

            lock (_lock)
            {
                if (_keyHandles.ContainsKey(label))
                    throw new InvalidOperationException($"Key '{label}' already exists in HSM.");

                // Phase 35: PKCS#11 mechanism/template marshaling not yet implemented
                // Production implementation would:
                // 1. Build CK_ATTRIBUTE template for key spec (algorithm, key size, token/session flags)
                // 2. Call C_GenerateKey (symmetric) or C_GenerateKeyPair (asymmetric)
                // 3. Store returned object handle

                throw new InvalidOperationException(
                    "HSM key generation not yet implemented. Full PKCS#11 mechanism/template marshaling " +
                    "requires building CK_MECHANISM and CK_ATTRIBUTE structures and is deferred to future phases.");
            }
        }

        /// <summary>
        /// Signs data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to sign.</param>
        /// <param name="algorithm">Signature algorithm.</param>
        /// <returns>Digital signature.</returns>
        /// <exception cref="InvalidOperationException">Thrown when not connected to HSM.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the specified key does not exist.</exception>
        /// <remarks>
        /// The private key never leaves the HSM. Signing happens inside the HSM hardware.
        /// <para>Actual implementation requires building CK_MECHANISM for algorithm, calling
        /// C_SignInit and C_Sign.
        /// </remarks>
        public Task<byte[]> SignAsync(string keyLabel, byte[] data, HsmSignatureAlgorithm algorithm)
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected to HSM.");

            ArgumentException.ThrowIfNullOrEmpty(keyLabel, nameof(keyLabel));
            ArgumentNullException.ThrowIfNull(data, nameof(data));

            lock (_lock)
            {
                if (!_keyHandles.ContainsKey(keyLabel))
                    throw new KeyNotFoundException($"Key '{keyLabel}' not found in HSM.");

                // Phase 35: PKCS#11 mechanism/template marshaling not yet implemented
                throw new InvalidOperationException(
                    "HSM signing not yet implemented. Full PKCS#11 C_SignInit/C_Sign operations " +
                    "require building CK_MECHANISM structures and are deferred to future phases.");
            }
        }

        /// <summary>
        /// Encrypts data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Data to encrypt.</param>
        /// <returns>Encrypted data.</returns>
        /// <exception cref="InvalidOperationException">Thrown when not connected to HSM.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the specified key does not exist.</exception>
        /// <remarks>
        /// Actual implementation requires C_EncryptInit and C_Encrypt with CK_MECHANISM structures.
        /// </remarks>
        public Task<byte[]> EncryptAsync(string keyLabel, byte[] data)
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected to HSM.");

            ArgumentException.ThrowIfNullOrEmpty(keyLabel, nameof(keyLabel));
            ArgumentNullException.ThrowIfNull(data, nameof(data));

            lock (_lock)
            {
                if (!_keyHandles.ContainsKey(keyLabel))
                    throw new KeyNotFoundException($"Key '{keyLabel}' not found in HSM.");

                // Phase 35: PKCS#11 mechanism/template marshaling not yet implemented
                throw new InvalidOperationException(
                    "HSM encryption not yet implemented. Full PKCS#11 C_EncryptInit/C_Encrypt operations " +
                    "require building CK_MECHANISM structures and are deferred to future phases.");
            }
        }

        /// <summary>
        /// Decrypts data using an HSM-stored key.
        /// </summary>
        /// <param name="keyLabel">Key label.</param>
        /// <param name="data">Encrypted data.</param>
        /// <returns>Decrypted data.</returns>
        /// <exception cref="InvalidOperationException">Thrown when not connected to HSM.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the specified key does not exist.</exception>
        /// <remarks>
        /// The private key never leaves the HSM. Decryption happens inside the HSM hardware.
        /// <para>Actual implementation requires C_DecryptInit and C_Decrypt with CK_MECHANISM structures.</para>
        /// </remarks>
        public Task<byte[]> DecryptAsync(string keyLabel, byte[] data)
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected to HSM.");

            ArgumentException.ThrowIfNullOrEmpty(keyLabel, nameof(keyLabel));
            ArgumentNullException.ThrowIfNull(data, nameof(data));

            lock (_lock)
            {
                if (!_keyHandles.ContainsKey(keyLabel))
                    throw new KeyNotFoundException($"Key '{keyLabel}' not found in HSM.");

                // Phase 35: PKCS#11 mechanism/template marshaling not yet implemented
                throw new InvalidOperationException(
                    "HSM decryption not yet implemented. Full PKCS#11 C_DecryptInit/C_Decrypt operations " +
                    "require building CK_MECHANISM structures and are deferred to future phases.");
            }
        }

        /// <summary>
        /// Asynchronously disposes HSM resources and closes connection.
        /// Preferred over <see cref="Dispose"/> to avoid sync-over-async.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        /// <summary>
        /// Synchronously disposes HSM resources. Prefer <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose()
        {
            // Fire-and-forget in sync dispose path — DisposeAsync is preferred
            DisconnectAsync().ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
