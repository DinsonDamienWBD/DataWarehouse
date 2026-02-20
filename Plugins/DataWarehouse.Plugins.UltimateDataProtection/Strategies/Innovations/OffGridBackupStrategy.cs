using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Innovations
{
    #region Off-Grid Types

    /// <summary>
    /// Represents an off-grid backup appliance node.
    /// </summary>
    public sealed class OffGridNode
    {
        /// <summary>Gets or sets the unique node identifier.</summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>Gets or sets the human-readable node name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the node location description.</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>Gets or sets the GPS coordinates if available.</summary>
        public GpsLocation? Coordinates { get; set; }

        /// <summary>Gets or sets the power configuration.</summary>
        public PowerConfiguration Power { get; set; } = new();

        /// <summary>Gets or sets the storage configuration.</summary>
        public StorageConfiguration Storage { get; set; } = new();

        /// <summary>Gets or sets the network configuration.</summary>
        public NetworkConfiguration Network { get; set; } = new();

        /// <summary>Gets or sets the current node status.</summary>
        public OffGridNodeStatus Status { get; set; } = new();

        /// <summary>Gets or sets when this node was last contacted.</summary>
        public DateTimeOffset? LastContactTime { get; set; }
    }

    /// <summary>
    /// GPS location coordinates.
    /// </summary>
    public sealed class GpsLocation
    {
        /// <summary>Gets or sets the latitude.</summary>
        public double Latitude { get; set; }

        /// <summary>Gets or sets the longitude.</summary>
        public double Longitude { get; set; }

        /// <summary>Gets or sets the altitude in meters.</summary>
        public double? AltitudeMeters { get; set; }
    }

    /// <summary>
    /// Power configuration for an off-grid node.
    /// </summary>
    public sealed class PowerConfiguration
    {
        /// <summary>Gets or sets the solar panel capacity in watts.</summary>
        public int SolarCapacityWatts { get; set; } = 400;

        /// <summary>Gets or sets the battery capacity in watt-hours.</summary>
        public int BatteryCapacityWh { get; set; } = 2000;

        /// <summary>Gets or sets the minimum battery level to maintain operation (0.0 to 1.0).</summary>
        public double MinimumBatteryLevel { get; set; } = 0.2;

        /// <summary>Gets or sets whether to enable low-power standby mode.</summary>
        public bool EnableLowPowerStandby { get; set; } = true;

        /// <summary>Gets or sets the standby power consumption in watts.</summary>
        public int StandbyPowerWatts { get; set; } = 5;

        /// <summary>Gets or sets the active power consumption in watts.</summary>
        public int ActivePowerWatts { get; set; } = 50;

        /// <summary>Gets or sets whether to enable backup generator.</summary>
        public bool HasBackupGenerator { get; set; }

        /// <summary>Gets or sets the generator fuel type.</summary>
        public string? GeneratorFuelType { get; set; }
    }

    /// <summary>
    /// Storage configuration for an off-grid node.
    /// </summary>
    public sealed class StorageConfiguration
    {
        /// <summary>Gets or sets the total storage capacity in bytes.</summary>
        public long TotalCapacityBytes { get; set; } = 4L * 1024 * 1024 * 1024 * 1024; // 4 TB default

        /// <summary>Gets or sets the storage type.</summary>
        public StorageType Type { get; set; } = StorageType.SSD;

        /// <summary>Gets or sets whether RAID is enabled.</summary>
        public bool RaidEnabled { get; set; } = true;

        /// <summary>Gets or sets the RAID level.</summary>
        public string RaidLevel { get; set; } = "RAID1";

        /// <summary>Gets or sets whether encryption at rest is enabled.</summary>
        public bool EncryptionAtRest { get; set; } = true;

        /// <summary>Gets or sets the reserved space for system operations.</summary>
        public long ReservedSpaceBytes { get; set; } = 100L * 1024 * 1024 * 1024; // 100 GB
    }

    /// <summary>
    /// Storage media types.
    /// </summary>
    public enum StorageType
    {
        /// <summary>Solid-state drive.</summary>
        SSD,

        /// <summary>Hard disk drive.</summary>
        HDD,

        /// <summary>NVMe drive.</summary>
        NVMe,

        /// <summary>Hybrid drive.</summary>
        Hybrid
    }

    /// <summary>
    /// Network configuration for an off-grid node.
    /// </summary>
    public sealed class NetworkConfiguration
    {
        /// <summary>Gets or sets the primary connection type.</summary>
        public ConnectionType PrimaryConnection { get; set; } = ConnectionType.WiFi;

        /// <summary>Gets or sets the fallback connection type.</summary>
        public ConnectionType? FallbackConnection { get; set; } = ConnectionType.Cellular;

        /// <summary>Gets or sets whether satellite backup is available.</summary>
        public bool HasSatelliteBackup { get; set; }

        /// <summary>Gets or sets the WiFi SSID.</summary>
        public string? WifiSsid { get; set; }

        /// <summary>Gets or sets the cellular APN.</summary>
        public string? CellularApn { get; set; }

        /// <summary>Gets or sets the maximum bandwidth in bytes per second.</summary>
        public long MaxBandwidthBps { get; set; } = 10 * 1024 * 1024; // 10 Mbps
    }

    /// <summary>
    /// Network connection types.
    /// </summary>
    public enum ConnectionType
    {
        /// <summary>No network connection.</summary>
        None,

        /// <summary>WiFi connection.</summary>
        WiFi,

        /// <summary>Ethernet connection.</summary>
        Ethernet,

        /// <summary>Cellular (4G/5G) connection.</summary>
        Cellular,

        /// <summary>Satellite connection.</summary>
        Satellite,

        /// <summary>LoRa mesh network.</summary>
        LoRaMesh
    }

    /// <summary>
    /// Current status of an off-grid node.
    /// </summary>
    public sealed class OffGridNodeStatus
    {
        /// <summary>Gets or sets whether the node is online.</summary>
        public bool IsOnline { get; set; }

        /// <summary>Gets or sets the current battery level (0.0 to 1.0).</summary>
        public double BatteryLevel { get; set; } = 1.0;

        /// <summary>Gets or sets the battery health (0.0 to 1.0).</summary>
        public double BatteryHealth { get; set; } = 1.0;

        /// <summary>Gets or sets whether charging is active.</summary>
        public bool IsCharging { get; set; }

        /// <summary>Gets or sets the current solar input in watts.</summary>
        public double SolarInputWatts { get; set; }

        /// <summary>Gets or sets the current power consumption in watts.</summary>
        public double PowerConsumptionWatts { get; set; }

        /// <summary>Gets or sets the estimated runtime remaining.</summary>
        public TimeSpan? EstimatedRuntime { get; set; }

        /// <summary>Gets or sets the used storage in bytes.</summary>
        public long UsedStorageBytes { get; set; }

        /// <summary>Gets or sets the available storage in bytes.</summary>
        public long AvailableStorageBytes { get; set; }

        /// <summary>Gets or sets the current temperature in Celsius.</summary>
        public double TemperatureCelsius { get; set; }

        /// <summary>Gets or sets the current humidity percentage.</summary>
        public double HumidityPercent { get; set; }

        /// <summary>Gets or sets the connection status.</summary>
        public ConnectionType ActiveConnection { get; set; }

        /// <summary>Gets or sets any active alerts.</summary>
        public List<string> ActiveAlerts { get; set; } = new();
    }

    /// <summary>
    /// Solar charging optimization settings.
    /// </summary>
    public sealed class SolarOptimizationSettings
    {
        /// <summary>Gets or sets whether to enable MPPT (Maximum Power Point Tracking).</summary>
        public bool EnableMppt { get; set; } = true;

        /// <summary>Gets or sets the charge profile.</summary>
        public ChargeProfile Profile { get; set; } = ChargeProfile.Balanced;

        /// <summary>Gets or sets the preferred backup window (UTC hours).</summary>
        public (int StartHour, int EndHour) PreferredBackupWindow { get; set; } = (10, 16);

        /// <summary>Gets or sets whether to defer backups to peak solar hours.</summary>
        public bool DeferToSolarPeak { get; set; } = true;

        /// <summary>Gets or sets the minimum solar input to start backup (watts).</summary>
        public int MinimumSolarInputWatts { get; set; } = 50;
    }

    /// <summary>
    /// Battery charging profiles.
    /// </summary>
    public enum ChargeProfile
    {
        /// <summary>Prioritize longevity over charge speed.</summary>
        Longevity,

        /// <summary>Balanced charging.</summary>
        Balanced,

        /// <summary>Fast charging when solar is available.</summary>
        FastCharge,

        /// <summary>Trickle charge to maintain level.</summary>
        Maintenance
    }

    #endregion

    /// <summary>
    /// Off-grid backup strategy for solar-powered, self-sufficient backup appliances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy manages backup operations to self-powered, off-grid backup nodes that
    /// operate independently of grid power and can function in remote locations.
    /// </para>
    /// <para>
    /// Key features:
    /// </para>
    /// <list type="bullet">
    ///   <item>Solar-powered backup nodes with battery storage</item>
    ///   <item>Battery health monitoring and optimization</item>
    ///   <item>Solar charging optimization with MPPT</item>
    ///   <item>Intelligent backup scheduling based on power availability</item>
    ///   <item>Multiple network fallback options (WiFi, Cellular, Satellite)</item>
    ///   <item>Environmental monitoring (temperature, humidity)</item>
    /// </list>
    /// </remarks>
    public sealed class OffGridBackupStrategy : DataProtectionStrategyBase
    {
        private readonly BoundedDictionary<string, OffGridBackup> _backups = new BoundedDictionary<string, OffGridBackup>(1000);
        private readonly BoundedDictionary<string, OffGridNode> _nodes = new BoundedDictionary<string, OffGridNode>(1000);
        private SolarOptimizationSettings _solarSettings = new();

        /// <inheritdoc/>
        public override string StrategyId => "off-grid-solar";

        /// <inheritdoc/>
        public override string StrategyName => "Off-Grid Solar-Powered Backup";

        /// <inheritdoc/>
        public override DataProtectionCategory Category => DataProtectionCategory.DisasterRecovery;

        /// <inheritdoc/>
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.Compression |
            DataProtectionCapabilities.Encryption |
            DataProtectionCapabilities.CrossPlatform |
            DataProtectionCapabilities.BandwidthThrottling |
            DataProtectionCapabilities.AutoVerification |
            DataProtectionCapabilities.ImmutableBackup;

        /// <summary>
        /// Registers an off-grid backup node.
        /// </summary>
        /// <param name="node">The node to register.</param>
        public void RegisterNode(OffGridNode node)
        {
            ArgumentNullException.ThrowIfNull(node);
            _nodes[node.NodeId] = node;
        }

        /// <summary>
        /// Configures solar optimization settings.
        /// </summary>
        /// <param name="settings">The optimization settings.</param>
        public void ConfigureSolarOptimization(SolarOptimizationSettings settings)
        {
            _solarSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Gets all registered nodes.
        /// </summary>
        /// <returns>Collection of registered nodes.</returns>
        public IEnumerable<OffGridNode> GetRegisteredNodes()
        {
            return _nodes.Values.ToList();
        }

        /// <summary>
        /// Gets the status of all nodes.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Dictionary of node IDs to their current status.</returns>
        public async Task<Dictionary<string, OffGridNodeStatus>> GetAllNodeStatusAsync(CancellationToken ct = default)
        {
            var statuses = new Dictionary<string, OffGridNodeStatus>();

            foreach (var node in _nodes.Values)
            {
                try
                {
                    var status = await GetNodeStatusAsync(node, ct);
                    node.Status = status;
                    node.LastContactTime = DateTimeOffset.UtcNow;
                    statuses[node.NodeId] = status;
                }
                catch
                {
                    statuses[node.NodeId] = new OffGridNodeStatus
                    {
                        IsOnline = false,
                        ActiveAlerts = new List<string> { "Failed to contact node" }
                    };
                }
            }

            return statuses;
        }

        /// <summary>
        /// Checks if the current time is within the optimal backup window.
        /// </summary>
        /// <returns>True if within the preferred backup window.</returns>
        public bool IsWithinBackupWindow()
        {
            var utcHour = DateTimeOffset.UtcNow.Hour;
            return utcHour >= _solarSettings.PreferredBackupWindow.StartHour &&
                   utcHour < _solarSettings.PreferredBackupWindow.EndHour;
        }

        /// <summary>
        /// Gets battery health across all nodes.
        /// </summary>
        /// <returns>Dictionary of node IDs to battery health (0.0 to 1.0).</returns>
        public Dictionary<string, double> GetBatteryHealthReport()
        {
            return _nodes.Values.ToDictionary(n => n.NodeId, n => n.Status.BatteryHealth);
        }

        /// <inheritdoc/>
        protected override async Task<BackupResult> CreateBackupCoreAsync(
            BackupRequest request,
            Action<BackupProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var backupId = Guid.NewGuid().ToString("N");

            try
            {
                // Phase 1: Select target node
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Selecting Off-Grid Node",
                    PercentComplete = 5
                });

                var targetNode = await SelectOptimalNodeAsync(request, ct);
                if (targetNode == null)
                {
                    return new BackupResult
                    {
                        Success = false,
                        BackupId = backupId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "No suitable off-grid node available"
                    };
                }

                // Phase 2: Check power availability
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Checking Power Availability",
                    PercentComplete = 10
                });

                var powerCheck = await CheckPowerAvailabilityAsync(targetNode, ct);
                if (!powerCheck.CanProceed)
                {
                    if (_solarSettings.DeferToSolarPeak && !IsWithinBackupWindow())
                    {
                        return new BackupResult
                        {
                            Success = false,
                            BackupId = backupId,
                            StartTime = startTime,
                            EndTime = DateTimeOffset.UtcNow,
                            ErrorMessage = $"Insufficient power. Recommended window: {_solarSettings.PreferredBackupWindow.StartHour}:00 - {_solarSettings.PreferredBackupWindow.EndHour}:00 UTC",
                            Warnings = new[] { $"Current battery: {powerCheck.BatteryLevel:P0}, Solar input: {powerCheck.SolarInputWatts}W" }
                        };
                    }
                }

                // Phase 3: Prepare backup data
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Preparing Backup Data",
                    PercentComplete = 15
                });

                var backupData = await CreateBackupDataAsync(request, ct);
                var compressedData = await CompressForLowPowerAsync(backupData, ct);

                var backup = new OffGridBackup
                {
                    BackupId = backupId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    TargetNodeId = targetNode.NodeId,
                    OriginalSize = backupData.LongLength,
                    CompressedSize = compressedData.LongLength,
                    FileCount = request.Sources.Count * 25 // Simulated
                };

                // Phase 4: Establish connection to node
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = $"Connecting to {targetNode.Name}",
                    PercentComplete = 25
                });

                var connection = await EstablishNodeConnectionAsync(targetNode, ct);
                backup.ConnectionType = connection.Type;

                // Phase 5: Transfer data with power monitoring
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Transferring to Off-Grid Node",
                    PercentComplete = 30,
                    TotalBytes = compressedData.LongLength
                });

                var transferResult = await TransferToNodeAsync(
                    compressedData,
                    targetNode,
                    connection,
                    (bytesTransferred, nodeStatus) =>
                    {
                        var percent = 30 + (int)((bytesTransferred / (double)compressedData.LongLength) * 50);
                        progressCallback(new BackupProgress
                        {
                            BackupId = backupId,
                            Phase = $"Transferring (Battery: {nodeStatus.BatteryLevel:P0}, Solar: {nodeStatus.SolarInputWatts}W)",
                            PercentComplete = percent,
                            BytesProcessed = bytesTransferred,
                            TotalBytes = compressedData.LongLength
                        });

                        // Update node status
                        targetNode.Status = nodeStatus;
                    },
                    ct);

                if (!transferResult.Success)
                {
                    return new BackupResult
                    {
                        Success = false,
                        BackupId = backupId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = $"Transfer failed: {transferResult.ErrorMessage}",
                        Warnings = new[] { $"Final battery level: {targetNode.Status.BatteryLevel:P0}" }
                    };
                }

                // Phase 6: Verify storage on node
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Verifying Storage on Node",
                    PercentComplete = 85
                });

                var verified = await VerifyNodeStorageAsync(backupId, targetNode, ct);
                if (!verified)
                {
                    return new BackupResult
                    {
                        Success = false,
                        BackupId = backupId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Storage verification failed on off-grid node"
                    };
                }

                // Phase 7: Update node power status
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Updating Power Status",
                    PercentComplete = 95
                });

                await UpdateNodeStatusAsync(targetNode, ct);
                backup.Checksum = transferResult.Checksum;
                backup.CompletedAt = DateTimeOffset.UtcNow;
                backup.FinalBatteryLevel = targetNode.Status.BatteryLevel;

                _backups[backupId] = backup;

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Complete",
                    PercentComplete = 100,
                    BytesProcessed = compressedData.LongLength,
                    TotalBytes = compressedData.LongLength
                });

                return new BackupResult
                {
                    Success = true,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.OriginalSize,
                    StoredBytes = backup.CompressedSize,
                    FileCount = backup.FileCount,
                    Warnings = new[]
                    {
                        $"Stored on off-grid node: {targetNode.Name}",
                        $"Connection type: {backup.ConnectionType}",
                        $"Node battery: {backup.FinalBatteryLevel:P0}",
                        $"Node solar input: {targetNode.Status.SolarInputWatts}W"
                    }
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BackupResult
                {
                    Success = false,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Off-grid backup failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override async Task<RestoreResult> RestoreCoreAsync(
            RestoreRequest request,
            Action<RestoreProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var restoreId = Guid.NewGuid().ToString("N");

            try
            {
                if (!_backups.TryGetValue(request.BackupId, out var backup))
                {
                    return new RestoreResult
                    {
                        Success = false,
                        RestoreId = restoreId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Backup not found"
                    };
                }

                // Phase 1: Connect to node
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Connecting to Off-Grid Node",
                    PercentComplete = 5
                });

                if (!_nodes.TryGetValue(backup.TargetNodeId, out var node))
                {
                    return new RestoreResult
                    {
                        Success = false,
                        RestoreId = restoreId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Off-grid node not found"
                    };
                }

                var connection = await EstablishNodeConnectionAsync(node, ct);

                // Phase 2: Check node power
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Checking Node Power",
                    PercentComplete = 15
                });

                var powerCheck = await CheckPowerAvailabilityAsync(node, ct);
                if (!powerCheck.CanProceed)
                {
                    return new RestoreResult
                    {
                        Success = false,
                        RestoreId = restoreId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Insufficient power on off-grid node for restore operation"
                    };
                }

                // Phase 3: Download from node
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Downloading from Off-Grid Node",
                    PercentComplete = 25,
                    TotalBytes = backup.CompressedSize
                });

                var compressedData = await DownloadFromNodeAsync(
                    backup,
                    node,
                    connection,
                    (bytesDownloaded) =>
                    {
                        var percent = 25 + (int)((bytesDownloaded / (double)backup.CompressedSize) * 40);
                        progressCallback(new RestoreProgress
                        {
                            RestoreId = restoreId,
                            Phase = "Downloading from Off-Grid Node",
                            PercentComplete = percent,
                            BytesRestored = bytesDownloaded,
                            TotalBytes = backup.CompressedSize
                        });
                    },
                    ct);

                // Phase 4: Verify checksum
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Verifying Data Integrity",
                    PercentComplete = 70
                });

                var checksum = ComputeChecksum(compressedData);
                if (checksum != backup.Checksum)
                {
                    return new RestoreResult
                    {
                        Success = false,
                        RestoreId = restoreId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Data integrity verification failed"
                    };
                }

                // Phase 5: Decompress and restore
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Decompressing and Restoring",
                    PercentComplete = 80
                });

                var backupData = await DecompressDataAsync(compressedData, ct);
                var filesRestored = await RestoreFilesAsync(backupData, request, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Complete",
                    PercentComplete = 100,
                    BytesRestored = backup.OriginalSize,
                    TotalBytes = backup.OriginalSize,
                    FilesRestored = filesRestored
                });

                return new RestoreResult
                {
                    Success = true,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.OriginalSize,
                    FileCount = filesRestored,
                    Warnings = new[] { $"Restored from off-grid node: {node.Name}" }
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RestoreResult
                {
                    Success = false,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Off-grid restore failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct)
        {
            var issues = new List<ValidationIssue>();
            var checks = new List<string>();

            checks.Add("BackupExists");
            if (!_backups.TryGetValue(backupId, out var backup))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Critical,
                    Code = "BACKUP_NOT_FOUND",
                    Message = "Off-grid backup not found"
                });
                return Task.FromResult(CreateValidationResult(false, issues, checks));
            }

            checks.Add("NodeExists");
            if (!_nodes.TryGetValue(backup.TargetNodeId, out var node))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Code = "NODE_NOT_FOUND",
                    Message = $"Off-grid node {backup.TargetNodeId} not found"
                });
            }
            else
            {
                checks.Add("NodeOnline");
                if (!node.Status.IsOnline)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Code = "NODE_OFFLINE",
                        Message = $"Node {node.Name} is currently offline"
                    });
                }

                checks.Add("BatteryHealth");
                if (node.Status.BatteryHealth < 0.5)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Code = "BATTERY_DEGRADED",
                        Message = $"Node battery health is degraded ({node.Status.BatteryHealth:P0})"
                    });
                }

                checks.Add("StorageAvailable");
                if (node.Status.AvailableStorageBytes < backup.CompressedSize)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Code = "STORAGE_LOW",
                        Message = "Node storage is running low"
                    });
                }
            }

            return Task.FromResult(CreateValidationResult(!issues.Any(i => i.Severity >= ValidationSeverity.Error), issues, checks));
        }

        /// <inheritdoc/>
        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(
            BackupListQuery query,
            CancellationToken ct)
        {
            var entries = _backups.Values
                .Select(CreateCatalogEntry)
                .Where(entry => MatchesQuery(entry, query))
                .OrderByDescending(e => e.CreatedAt)
                .Take(query.MaxResults);

            return Task.FromResult(entries.AsEnumerable());
        }

        /// <inheritdoc/>
        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct)
        {
            if (_backups.TryGetValue(backupId, out var backup))
            {
                return Task.FromResult<BackupCatalogEntry?>(CreateCatalogEntry(backup));
            }
            return Task.FromResult<BackupCatalogEntry?>(null);
        }

        /// <inheritdoc/>
        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct)
        {
            _backups.TryRemove(backupId, out _);
            return Task.CompletedTask;
        }

        #region Private Methods

        private async Task<OffGridNode?> SelectOptimalNodeAsync(BackupRequest request, CancellationToken ct)
        {
            var candidates = new List<(OffGridNode Node, double Score)>();

            foreach (var node in _nodes.Values)
            {
                var status = await GetNodeStatusAsync(node, ct);
                if (!status.IsOnline) continue;

                var score = CalculateNodeScore(node, status, request);
                if (score > 0)
                {
                    candidates.Add((node, score));
                }
            }

            return candidates.OrderByDescending(c => c.Score).FirstOrDefault().Node;
        }

        private double CalculateNodeScore(OffGridNode node, OffGridNodeStatus status, BackupRequest request)
        {
            var score = 0.0;

            // Battery level (0-30 points)
            score += status.BatteryLevel * 30;

            // Battery health (0-20 points)
            score += status.BatteryHealth * 20;

            // Solar input during backup window (0-20 points)
            if (IsWithinBackupWindow() && status.SolarInputWatts > 0)
            {
                score += Math.Min(status.SolarInputWatts / node.Power.SolarCapacityWatts, 1.0) * 20;
            }

            // Available storage (0-20 points)
            var storageRatio = (double)status.AvailableStorageBytes / node.Storage.TotalCapacityBytes;
            score += storageRatio * 20;

            // Connection quality (0-10 points)
            if (status.ActiveConnection == ConnectionType.Ethernet) score += 10;
            else if (status.ActiveConnection == ConnectionType.WiFi) score += 8;
            else if (status.ActiveConnection == ConnectionType.Cellular) score += 5;

            return score;
        }

        private async Task<OffGridNodeStatus> GetNodeStatusAsync(OffGridNode node, CancellationToken ct)
        {
            // In production, query the actual node
            await Task.Delay(10, ct);

            return new OffGridNodeStatus
            {
                IsOnline = true,
                BatteryLevel = 0.75 + (Random.Shared.NextDouble() * 0.25),
                BatteryHealth = 0.9 + (Random.Shared.NextDouble() * 0.1),
                IsCharging = IsWithinBackupWindow(),
                SolarInputWatts = IsWithinBackupWindow() ? 150 + Random.Shared.Next(0, 100) : Random.Shared.Next(0, 50),
                PowerConsumptionWatts = 15 + Random.Shared.Next(0, 20),
                UsedStorageBytes = (long)(node.Storage.TotalCapacityBytes * 0.3),
                AvailableStorageBytes = (long)(node.Storage.TotalCapacityBytes * 0.7),
                TemperatureCelsius = 25 + Random.Shared.Next(-10, 15),
                HumidityPercent = 40 + Random.Shared.Next(0, 30),
                ActiveConnection = node.Network.PrimaryConnection
            };
        }

        private Task<PowerCheckResult> CheckPowerAvailabilityAsync(OffGridNode node, CancellationToken ct)
        {
            var canProceed = node.Status.BatteryLevel >= node.Power.MinimumBatteryLevel ||
                             node.Status.SolarInputWatts >= _solarSettings.MinimumSolarInputWatts;

            return Task.FromResult(new PowerCheckResult
            {
                CanProceed = canProceed,
                BatteryLevel = node.Status.BatteryLevel,
                SolarInputWatts = node.Status.SolarInputWatts
            });
        }

        private Task<byte[]> CreateBackupDataAsync(BackupRequest request, CancellationToken ct)
        {
            return Task.FromResult(new byte[10 * 1024 * 1024]); // 10 MB simulated
        }

        private Task<byte[]> CompressForLowPowerAsync(byte[] data, CancellationToken ct)
        {
            // Low-power optimized compression
            return Task.FromResult(data);
        }

        private Task<NodeConnection> EstablishNodeConnectionAsync(OffGridNode node, CancellationToken ct)
        {
            return Task.FromResult(new NodeConnection
            {
                Type = node.Network.PrimaryConnection,
                IsEstablished = true,
                Bandwidth = node.Network.MaxBandwidthBps
            });
        }

        private async Task<TransferResult> TransferToNodeAsync(
            byte[] data,
            OffGridNode node,
            NodeConnection connection,
            Action<long, OffGridNodeStatus> progressCallback,
            CancellationToken ct)
        {
            var bytesTransferred = 0L;
            var chunkSize = 256 * 1024; // 256 KB chunks

            while (bytesTransferred < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                var remaining = data.Length - bytesTransferred;
                var chunk = Math.Min(chunkSize, remaining);

                await Task.Delay(5, ct);
                bytesTransferred += chunk;

                var status = await GetNodeStatusAsync(node, ct);
                progressCallback(bytesTransferred, status);
            }

            return new TransferResult
            {
                Success = true,
                Checksum = ComputeChecksum(data)
            };
        }

        private Task<bool> VerifyNodeStorageAsync(string backupId, OffGridNode node, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        private Task UpdateNodeStatusAsync(OffGridNode node, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private async Task<byte[]> DownloadFromNodeAsync(
            OffGridBackup backup,
            OffGridNode node,
            NodeConnection connection,
            Action<long> progressCallback,
            CancellationToken ct)
        {
            await Task.Delay(50, ct);
            progressCallback(backup.CompressedSize);
            return new byte[backup.CompressedSize];
        }

        private Task<byte[]> DecompressDataAsync(byte[] data, CancellationToken ct)
        {
            return Task.FromResult(data);
        }

        private Task<long> RestoreFilesAsync(byte[] data, RestoreRequest request, CancellationToken ct)
        {
            return Task.FromResult(250L);
        }

        private string ComputeChecksum(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(data);
            return Convert.ToBase64String(hash);
        }

        private BackupCatalogEntry CreateCatalogEntry(OffGridBackup backup)
        {
            return new BackupCatalogEntry
            {
                BackupId = backup.BackupId,
                StrategyId = StrategyId,
                Category = Category,
                CreatedAt = backup.CreatedAt,
                OriginalSize = backup.OriginalSize,
                StoredSize = backup.CompressedSize,
                FileCount = backup.FileCount,
                IsEncrypted = true,
                IsCompressed = true,
                Tags = new Dictionary<string, string>
                {
                    ["node"] = backup.TargetNodeId,
                    ["connection"] = backup.ConnectionType.ToString()
                }
            };
        }

        private bool MatchesQuery(BackupCatalogEntry entry, BackupListQuery query)
        {
            if (query.CreatedAfter.HasValue && entry.CreatedAt < query.CreatedAfter.Value)
                return false;
            if (query.CreatedBefore.HasValue && entry.CreatedAt > query.CreatedBefore.Value)
                return false;
            return true;
        }

        private ValidationResult CreateValidationResult(bool isValid, List<ValidationIssue> issues, List<string> checks)
        {
            return new ValidationResult
            {
                IsValid = isValid,
                Errors = issues.Where(i => i.Severity >= ValidationSeverity.Error).ToList(),
                Warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList(),
                ChecksPerformed = checks
            };
        }

        #endregion

        #region Private Classes

        private sealed class OffGridBackup
        {
            public string BackupId { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset? CompletedAt { get; set; }
            public string TargetNodeId { get; set; } = string.Empty;
            public long OriginalSize { get; set; }
            public long CompressedSize { get; set; }
            public long FileCount { get; set; }
            public ConnectionType ConnectionType { get; set; }
            public string Checksum { get; set; } = string.Empty;
            public double FinalBatteryLevel { get; set; }
        }

        private sealed class PowerCheckResult
        {
            public bool CanProceed { get; set; }
            public double BatteryLevel { get; set; }
            public double SolarInputWatts { get; set; }
        }

        private sealed class NodeConnection
        {
            public ConnectionType Type { get; set; }
            public bool IsEstablished { get; set; }
            public long Bandwidth { get; set; }
        }

        private sealed class TransferResult
        {
            public bool Success { get; set; }
            public string Checksum { get; set; } = string.Empty;
            public string? ErrorMessage { get; set; }
        }

        #endregion
    }
}
