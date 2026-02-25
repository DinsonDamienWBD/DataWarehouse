namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Entrust nShield HSM KeyStore strategy via PKCS#11.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for HSM-backed key management.
    ///
    /// Entrust nShield (formerly nCipher) HSMs are FIPS 140-2 Level 3 certified
    /// hardware security modules with unique Security World architecture for
    /// flexible key management and disaster recovery.
    ///
    /// Supported features:
    /// - AES-256, RSA, and EC key generation within HSM boundary
    /// - Key wrapping/unwrapping (envelope encryption)
    /// - PKCS#11 v2.40 compliant interface
    /// - Security World key management architecture
    /// - Operator Card Set (OCS) authentication
    /// - Key recovery with Administrator Card Set (ACS)
    /// - softcard and module-protected key modes
    ///
    /// Configuration:
    /// - LibraryPath: Path to nShield PKCS#11 library (default: /opt/nfast/toolkits/pkcs11/libcknfast.so)
    /// - SlotId: HSM module slot ID (optional)
    /// - TokenLabel: Token/module label
    /// - Pin: Operator Card Set passphrase or module protection passphrase
    /// - DefaultKeyLabel: Default key label for encryption (default: "datawarehouse-master")
    /// - UseUserPin: Use operator card (true) or module protection (false)
    ///
    /// nShield Prerequisites:
    /// - nShield Security World created and initialized
    /// - nShield Connect or Solo installed and configured
    /// - PKCS#11 toolkit installed
    /// - Operator Card Set created (for OCS-protected keys)
    /// - Hardserver running and connected to HSM
    ///
    /// Example Configuration:
    /// {
    ///     "LibraryPath": "/opt/nfast/toolkits/pkcs11/libcknfast.so",
    ///     "TokenLabel": "accelerator",
    ///     "Pin": "ocs-passphrase",
    ///     "DefaultKeyLabel": "dw-master-key",
    ///     "UseUserPin": true,
    ///     "SecurityWorldPath": "/opt/nfast/kmdata/local"
    /// }
    /// </summary>
    public sealed class NcipherStrategy : Pkcs11HsmStrategyBase
    {
        protected override string VendorName => "Entrust nShield";

        protected override string DefaultLibraryPath => GetPlatformDefaultLibraryPath();

        protected override Dictionary<string, object> VendorMetadata => new()
        {
            ["HsmFamily"] = "nShield",
            ["Manufacturer"] = "Entrust (formerly nCipher)",
            ["FipsLevel"] = "140-2 Level 3",
            ["CommonCriteria"] = "EAL4+",
            ["PkcsVersion"] = "2.40",
            ["SupportsSecurityWorld"] = true,
            ["SupportsOcs"] = true,
            ["SupportsSoftcard"] = true,
            ["SupportsModuleProtection"] = true,
            ["SupportsKeyRecovery"] = true,
            ["SupportsRemoteAdmin"] = true
        };

        /// <summary>
        /// Gets the platform-specific default library path for nShield PKCS#11.
        /// </summary>
        private static string GetPlatformDefaultLibraryPath()
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: nShield installation path
                return @"C:\Program Files\nCipher\nfast\toolkits\pkcs11\cknfast.dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS: nShield toolkit path (less common)
                return "/opt/nfast/toolkits/pkcs11/libcknfast.dylib";
            }
            else
            {
                // Linux: Standard nShield installation path
                return "/opt/nfast/toolkits/pkcs11/libcknfast.so";
            }
        }
    }

    /// <summary>
    /// nShield-specific configuration extensions.
    /// </summary>
    public class NcipherConfig : Pkcs11HsmConfig
    {
        /// <summary>
        /// Path to Security World key management data.
        /// Default: /opt/nfast/kmdata/local (Linux) or C:\ProgramData\nCipher\Key Management Data\local (Windows)
        /// </summary>
        public string? SecurityWorldPath { get; set; }

        /// <summary>
        /// Key protection mode: OCS (Operator Card Set), Softcard, or Module.
        /// </summary>
        public NshieldProtectionMode ProtectionMode { get; set; } = NshieldProtectionMode.Module;

        /// <summary>
        /// Operator Card Set name for OCS-protected keys.
        /// </summary>
        public string? OcsName { get; set; }

        /// <summary>
        /// Softcard name for softcard-protected keys.
        /// </summary>
        public string? SoftcardName { get; set; }

        /// <summary>
        /// Number of OCS cards required for quorum (K of N).
        /// </summary>
        public int OcsQuorum { get; set; } = 1;

        /// <summary>
        /// Enable preload of OCS/softcard credentials.
        /// Allows non-interactive authentication.
        /// </summary>
        public bool EnablePreload { get; set; }

        /// <summary>
        /// Path to preload credentials file.
        /// </summary>
        public string? PreloadPath { get; set; }

        /// <summary>
        /// Module number for multi-module configurations.
        /// </summary>
        public int? ModuleNumber { get; set; }

        /// <summary>
        /// Enable strict FIPS mode enforcement.
        /// </summary>
        public bool StrictFipsMode { get; set; }

        /// <summary>
        /// Connection timeout for remote nShield Connect.
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// nShield key protection modes.
    /// </summary>
    public enum NshieldProtectionMode
    {
        /// <summary>
        /// Module-protected keys - accessible without cards after HSM initialization.
        /// Lower security but easier automation.
        /// </summary>
        Module,

        /// <summary>
        /// Operator Card Set protected - requires OCS card(s) present.
        /// Higher security with physical authentication.
        /// </summary>
        Ocs,

        /// <summary>
        /// Softcard protected - passphrase-based protection without physical cards.
        /// Balance of security and automation.
        /// </summary>
        Softcard
    }
}
