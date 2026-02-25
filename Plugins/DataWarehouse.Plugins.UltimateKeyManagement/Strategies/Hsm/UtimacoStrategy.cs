namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Utimaco CryptoServer HSM KeyStore strategy via PKCS#11.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for HSM-backed key management.
    ///
    /// Utimaco CryptoServer HSMs are FIPS 140-2 Level 3 and Common Criteria EAL4+
    /// certified hardware security modules used in banking, government, and enterprise.
    ///
    /// Supported features:
    /// - AES-256, RSA, and EC key generation within HSM boundary
    /// - Key wrapping/unwrapping (envelope encryption)
    /// - PKCS#11 v2.40 compliant interface
    /// - Simulator mode for development/testing
    /// - Key backup and restore capabilities
    /// - Multi-tenant partitioning with key separation
    ///
    /// Configuration:
    /// - LibraryPath: Path to Utimaco PKCS#11 library (default: /opt/utimaco/lib/libcs_pkcs11_R3.so)
    /// - SlotId: HSM slot ID (optional)
    /// - TokenLabel: Partition/token label
    /// - Pin: SO PIN or User PIN
    /// - DefaultKeyLabel: Default key label for encryption (default: "datawarehouse-master")
    /// - UseUserPin: Use User PIN (true) or SO PIN (false)
    ///
    /// CryptoServer Prerequisites:
    /// - CryptoServer SDK installed with PKCS#11 module
    /// - Device configuration file (cs_pkcs11_R3.cfg) properly set up
    /// - Network connectivity or direct connection to CryptoServer
    /// - Valid authentication credentials (PIN)
    ///
    /// Example Configuration:
    /// {
    ///     "LibraryPath": "/opt/utimaco/lib/libcs_pkcs11_R3.so",
    ///     "TokenLabel": "CryptoServer PKCS11 Token",
    ///     "Pin": "1234567890",
    ///     "DefaultKeyLabel": "dw-master-key",
    ///     "UseUserPin": true,
    ///     "ConfigFile": "/opt/utimaco/etc/cs_pkcs11_R3.cfg"
    /// }
    /// </summary>
    public sealed class UtimacoStrategy : Pkcs11HsmStrategyBase
    {
        protected override string VendorName => "Utimaco CryptoServer";

        protected override string DefaultLibraryPath => GetPlatformDefaultLibraryPath();

        protected override Dictionary<string, object> VendorMetadata => new()
        {
            ["HsmFamily"] = "CryptoServer",
            ["Manufacturer"] = "Utimaco",
            ["FipsLevel"] = "140-2 Level 3",
            ["CommonCriteria"] = "EAL4+",
            ["PkcsVersion"] = "2.40",
            ["SupportsSimulator"] = true,
            ["SupportsKeyBackup"] = true,
            ["SupportsRemoteAdmin"] = true,
            ["SupportsFirmwareUpdate"] = true,
            ["SupportsAuditLog"] = true
        };

        /// <summary>
        /// Gets the platform-specific default library path for Utimaco PKCS#11.
        /// </summary>
        private static string GetPlatformDefaultLibraryPath()
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: Utimaco SDK installation
                return @"C:\Program Files\Utimaco\CryptoServer\lib\cs_pkcs11_R3.dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS: Utimaco SDK path
                return "/opt/utimaco/lib/libcs_pkcs11_R3.dylib";
            }
            else
            {
                // Linux: Standard Utimaco SDK installation
                return "/opt/utimaco/lib/libcs_pkcs11_R3.so";
            }
        }
    }

    /// <summary>
    /// Utimaco-specific configuration extensions.
    /// </summary>
    public class UtimacoConfig : Pkcs11HsmConfig
    {
        /// <summary>
        /// Path to CryptoServer configuration file.
        /// Specifies device connection parameters.
        /// </summary>
        public string? ConfigFile { get; set; }

        /// <summary>
        /// Enable simulator mode for development/testing.
        /// Does not require physical HSM hardware.
        /// </summary>
        public bool UseSimulator { get; set; }

        /// <summary>
        /// Device address for network-connected CryptoServer.
        /// Format: hostname:port or IP:port
        /// </summary>
        public string? DeviceAddress { get; set; }

        /// <summary>
        /// Timeout for CryptoServer operations in milliseconds.
        /// </summary>
        public int OperationTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// Enable key backup capability for exported keys.
        /// Requires appropriate permissions on the HSM.
        /// </summary>
        public bool EnableKeyBackup { get; set; }

        /// <summary>
        /// Authentication domain for multi-tenant deployments.
        /// </summary>
        public string? AuthDomain { get; set; }

        /// <summary>
        /// Enable audit logging for all key operations.
        /// </summary>
        public bool EnableAuditLog { get; set; } = true;
    }
}
