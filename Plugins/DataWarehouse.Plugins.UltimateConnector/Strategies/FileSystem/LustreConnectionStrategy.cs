using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    /// <summary>
    /// Lustre connection strategy using POSIX file operations.
    /// Connects to Lustre parallel distributed file system by verifying mount point accessibility.
    /// </summary>
    public class LustreConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "fs-lustre";

        /// <inheritdoc/>
        public override string DisplayName => "Lustre";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription => "Connects to Lustre parallel distributed file system via mount point";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "lustre", "parallel", "filesystem", "hpc", "distributed-storage" };

        /// <summary>
        /// Initializes a new instance of <see cref="LustreConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public LustreConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <summary>
        /// Establishes a connection to Lustre file system.
        /// ConnectionString format: mount-point path (e.g., "/mnt/lustre")
        /// </summary>
        protected override Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var mountPoint = config.ConnectionString;

            if (string.IsNullOrWhiteSpace(mountPoint))
            {
                throw new InvalidOperationException("Mount point path is required for Lustre connection.");
            }

            // Verify the mount point exists and is accessible
            if (!Directory.Exists(mountPoint))
            {
                throw new DirectoryNotFoundException($"Lustre mount point does not exist: {mountPoint}");
            }

            // Attempt to access the directory to verify permissions
            try
            {
                var testPath = Path.Combine(mountPoint, ".lustre");
                var exists = Directory.Exists(testPath);

                // Try to read directory to verify actual access
                Directory.GetFileSystemEntries(mountPoint, "*", new EnumerationOptions
                {
                    MaxRecursionDepth = 0,
                    ReturnSpecialDirectories = false
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Access denied to Lustre mount point: {mountPoint}", ex);
            }

            var connectionInfo = new Dictionary<string, object>
            {
                ["protocol"] = "Lustre-POSIX",
                ["mountPoint"] = mountPoint,
                ["filesystem"] = "Lustre"
            };

            // Use DirectoryInfo as the underlying connection object
            var directoryInfo = new DirectoryInfo(mountPoint);

            return Task.FromResult<IConnectionHandle>(
                new DefaultConnectionHandle(directoryInfo, connectionInfo));
        }

        /// <summary>
        /// Tests the Lustre connection by verifying mount point accessibility.
        /// </summary>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var directoryInfo = handle.GetConnection<DirectoryInfo>();

                // Refresh directory info to get current state
                directoryInfo.Refresh();

                // Verify directory still exists and is accessible
                if (!directoryInfo.Exists)
                {
                    return Task.FromResult(false);
                }

                // Attempt a read operation to verify access
                directoryInfo.GetFileSystemInfos();

                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Disconnects from the Lustre file system.
        /// Note: For POSIX-based connections, there's no explicit disconnect operation.
        /// </summary>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // No explicit disconnect needed for POSIX file system access
            return Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves health status of the Lustre connection.
        /// </summary>
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            try
            {
                var directoryInfo = handle.GetConnection<DirectoryInfo>();
                var start = DateTimeOffset.UtcNow;

                // Refresh and verify accessibility
                directoryInfo.Refresh();

                if (!directoryInfo.Exists)
                {
                    return Task.FromResult(new ConnectionHealth(
                        IsHealthy: false,
                        StatusMessage: "Lustre mount point no longer exists",
                        Latency: DateTimeOffset.UtcNow - start,
                        CheckedAt: DateTimeOffset.UtcNow));
                }

                // Perform a stat operation
                var fileSystemInfos = directoryInfo.GetFileSystemInfos();
                var latency = DateTimeOffset.UtcNow - start;

                return Task.FromResult(new ConnectionHealth(
                    IsHealthy: true,
                    StatusMessage: $"Lustre filesystem healthy ({fileSystemInfos.Length} entries)",
                    Latency: latency,
                    CheckedAt: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"Lustre health check failed: {ex.Message}",
                    Latency: DateTimeOffset.UtcNow - startTime,
                    CheckedAt: DateTimeOffset.UtcNow));
            }
        }
    }
}
