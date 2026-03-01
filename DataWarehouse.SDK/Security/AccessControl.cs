// Cat 15 (finding 564): File was named AccessControl.cs but contains ContainerConfig.
// Renamed file to ContainerConfig.cs via project tooling; this source is retained
// with the class content for backward compatibility.
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Security
{
    /// <summary>
    /// Container access and storage configuration, specifying encryption, compression,
    /// and granular access-control-list settings for a logical storage container.
    /// </summary>
    public class ContainerConfig
    {
        /// <summary>
        /// Container ID
        /// </summary>
        public string ContainerId { get; set; } = "default";

        /// <summary>
        /// Is encrypted
        /// </summary>
        public bool IsEncrypted { get; set; } = false;

        /// <summary>
        /// Is compressed
        /// </summary>
        public bool IsCompressed { get; set; } = false;

        /// <summary>
        /// Granular ACLs: UserId -> Level
        /// </summary>
        public Dictionary<string, AccessLevel> AccessControlList { get; set; } = new();
    }
}