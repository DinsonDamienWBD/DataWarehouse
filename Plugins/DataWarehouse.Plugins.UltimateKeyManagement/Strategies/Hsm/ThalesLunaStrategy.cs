namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Thales Luna Network HSM KeyStore strategy via PKCS#11.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for HSM-backed key management.
    ///
    /// Thales Luna HSMs are FIPS 140-2 Level 3 certified network-attached HSMs widely used
    /// in enterprise environments for cryptographic key protection.
    ///
    /// Supported features:
    /// - AES-256 key generation within HSM boundary
    /// - Key wrapping/unwrapping (envelope encryption)
    /// - PKCS#11 v2.40+ compliant interface
    /// - Multi-slot support for partitioned key storage
    /// - High availability with Luna Network HSM clusters
    /// - PED (PIN Entry Device) authentication support
    ///
    /// Configuration:
    /// - LibraryPath: Path to Luna PKCS#11 library (default: /usr/safenet/lunaclient/lib/libCryptoki2_64.so)
    /// - SlotId: HSM partition slot ID (optional, uses TokenLabel if not set)
    /// - TokenLabel: Partition label (e.g., "partition1")
    /// - Pin: Crypto Officer or Crypto User PIN
    /// - DefaultKeyLabel: Default key label for encryption (default: "datawarehouse-master")
    /// - UseUserPin: Use Crypto User (true) or Crypto Officer (false) role
    ///
    /// Luna Client Prerequisites:
    /// - Luna Network HSM Client software installed
    /// - Partition configured and registered with client
    /// - Network connectivity to Luna HSM appliance
    /// - Valid partition certificate in local client configuration
    ///
    /// Example Configuration:
    /// {
    ///     "LibraryPath": "/usr/safenet/lunaclient/lib/libCryptoki2_64.so",
    ///     "TokenLabel": "mypartition",
    ///     "Pin": "partition-crypto-user-pin",
    ///     "DefaultKeyLabel": "dw-master-key",
    ///     "UseUserPin": true
    /// }
    /// </summary>
    public sealed class ThalesLunaStrategy : Pkcs11HsmStrategyBase
    {
        protected override string VendorName => "Thales Luna Network HSM";

        protected override string DefaultLibraryPath => GetPlatformDefaultLibraryPath();

        protected override Dictionary<string, object> VendorMetadata => new()
        {
            ["HsmFamily"] = "Luna Network HSM",
            ["Manufacturer"] = "Thales",
            ["FipsLevel"] = "140-2 Level 3",
            ["PkcsVersion"] = "2.40",
            ["SupportsHa"] = true,
            ["SupportsPed"] = true,
            ["SupportsRemoteKeyLoading"] = true,
            ["SupportsCloning"] = true,
            ["MaxPartitions"] = 100
        };

        /// <summary>
        /// Gets the platform-specific default library path for Luna PKCS#11.
        /// </summary>
        private static string GetPlatformDefaultLibraryPath()
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: Luna Client typically installs to Program Files
                return @"C:\Program Files\SafeNet\LunaClient\cryptoki.dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS: Luna Client path
                return "/usr/safenet/lunaclient/lib/libCryptoki2.dylib";
            }
            else
            {
                // Linux: Standard Luna Client installation path
                return "/usr/safenet/lunaclient/lib/libCryptoki2_64.so";
            }
        }
    }

    /// <summary>
    /// Luna-specific configuration extensions.
    /// </summary>
    public class ThalesLunaConfig : Pkcs11HsmConfig
    {
        /// <summary>
        /// Enable High Availability mode for Luna clusters.
        /// When enabled, operations automatically failover between HSM members.
        /// </summary>
        public bool EnableHa { get; set; }

        /// <summary>
        /// HA group label when using Luna HA.
        /// </summary>
        public string? HaGroupLabel { get; set; }

        /// <summary>
        /// Connection timeout in seconds for network HSM operations.
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Enable automatic session recovery on network errors.
        /// </summary>
        public bool EnableAutoReconnect { get; set; } = true;

        /// <summary>
        /// Use PED (PIN Entry Device) authentication instead of password.
        /// Requires physical PED device connected to client machine.
        /// </summary>
        public bool UsePedAuthentication { get; set; }
    }
}
