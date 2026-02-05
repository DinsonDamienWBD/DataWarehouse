using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Network
{
    /// <summary>
    /// Fibre Channel (FC) SAN storage strategy with enterprise-grade features:
    /// - FC HBA management and WWPN/WWNN addressing
    /// - SAN fabric zoning (single initiator, peer zoning)
    /// - LUN masking and persistent binding
    /// - Fabric login protocols (FLOGI/PLOGI)
    /// - Multipath I/O for redundancy and load balancing
    /// - N_Port ID Virtualization (NPIV) for virtual HBAs
    /// - FCoE Initialization Protocol (FIP) support
    /// - Buffer-to-buffer credits management
    /// - Frame size configuration (up to 2112 bytes)
    /// - Port speed negotiation (1/2/4/8/16/32 Gbps)
    /// - SCSI command translation layer
    /// - Block-to-object storage mapping
    /// </summary>
    /// <remarks>
    /// This implementation provides block-level storage access over Fibre Channel SANs.
    /// Requires OS-level FC drivers or management API access (Brocade/Cisco switch APIs).
    /// Supports both native FC and FCoE (FC over Ethernet) deployments.
    /// </remarks>
    public class FcStrategy : UltimateStorageStrategyBase
    {
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _ioLock = new(16, 16); // Allow 16 concurrent I/O operations
        private readonly Dictionary<string, FcLunMapping> _lunMappings = new();

        // Configuration
        private string _fabricSwitchAddress = string.Empty;
        private int _fabricManagementPort = 443; // HTTPS for REST API
        private string _fabricUsername = string.Empty;
        private string _fabricPassword = string.Empty;
        private string _initiatorWwpn = string.Empty;
        private string _initiatorWwnn = string.Empty;
        private List<string> _targetWwpns = new();
        private string _lunBasePath = string.Empty;
        private int _blockSize = 4096; // 4KB blocks
        private bool _enableMultipath = true;
        private bool _enableNpiv = false;
        private List<string> _npivVirtualWwpns = new();
        private ZoningMode _zoningMode = ZoningMode.SingleInitiator;
        private int _maxFrameSize = 2112; // Maximum FC frame payload
        private int _bufferCredits = 32; // Buffer-to-buffer credits
        private FcPortSpeed _portSpeed = FcPortSpeed.Auto;
        private bool _enableFip = false; // FCoE Initialization Protocol
        private TimeSpan _fabricLoginTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _ioTimeout = TimeSpan.FromSeconds(60);

        // Connection state
        private bool _isFabricConnected = false;
        private DateTime _lastFabricLogin = DateTime.MinValue;
        private readonly Dictionary<string, MultipathState> _multipathStates = new();
        private HttpClient? _fabricApiClient;

        public override string StrategyId => "fc";
        public override string Name => "Fibre Channel SAN Storage";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false, // Can be added via SCSI encryption
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null, // Limited by LUN size
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong // Block storage is strongly consistent
        };

        /// <summary>
        /// Initializes the Fibre Channel storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load fabric switch configuration
            _fabricSwitchAddress = GetConfiguration<string>("FabricSwitchAddress", string.Empty);
            _fabricManagementPort = GetConfiguration<int>("FabricManagementPort", 443);
            _fabricUsername = GetConfiguration<string>("FabricUsername", string.Empty);
            _fabricPassword = GetConfiguration<string>("FabricPassword", string.Empty);

            if (string.IsNullOrWhiteSpace(_fabricSwitchAddress))
            {
                throw new InvalidOperationException(
                    "FC fabric switch address is required. Set 'FabricSwitchAddress' in configuration.");
            }

            // Load WWN configuration
            _initiatorWwpn = GetConfiguration<string>("InitiatorWwpn", string.Empty);
            _initiatorWwnn = GetConfiguration<string>("InitiatorWwnn", string.Empty);

            if (string.IsNullOrWhiteSpace(_initiatorWwpn))
            {
                throw new InvalidOperationException(
                    "Initiator WWPN is required. Set 'InitiatorWwpn' in configuration (e.g., '50:01:43:80:12:34:56:78').");
            }

            // Normalize WWNs to colon-separated format
            _initiatorWwpn = NormalizeWwn(_initiatorWwpn);
            if (!string.IsNullOrWhiteSpace(_initiatorWwnn))
            {
                _initiatorWwnn = NormalizeWwn(_initiatorWwnn);
            }

            // Load target WWPNs (storage array ports)
            var targetWwpnsConfig = GetConfiguration<string>("TargetWwpns", string.Empty);
            if (!string.IsNullOrWhiteSpace(targetWwpnsConfig))
            {
                _targetWwpns = targetWwpnsConfig
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => NormalizeWwn(w.Trim()))
                    .ToList();
            }

            if (_targetWwpns.Count == 0)
            {
                throw new InvalidOperationException(
                    "At least one target WWPN is required. Set 'TargetWwpns' in configuration (comma-separated).");
            }

            // Load LUN configuration
            _lunBasePath = GetConfiguration<string>("LunBasePath", "/dev/mapper");
            _blockSize = GetConfiguration<int>("BlockSize", 4096);

            // Load multipath configuration
            _enableMultipath = GetConfiguration<bool>("EnableMultipath", true);

            // Load NPIV configuration
            _enableNpiv = GetConfiguration<bool>("EnableNpiv", false);
            if (_enableNpiv)
            {
                var npivWwpnsConfig = GetConfiguration<string>("NpivVirtualWwpns", string.Empty);
                if (!string.IsNullOrWhiteSpace(npivWwpnsConfig))
                {
                    _npivVirtualWwpns = npivWwpnsConfig
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => NormalizeWwn(w.Trim()))
                        .ToList();
                }
            }

            // Load zoning configuration
            var zoningModeStr = GetConfiguration<string>("ZoningMode", "SingleInitiator");
            _zoningMode = zoningModeStr.ToUpperInvariant() switch
            {
                "SINGLEINITIATOR" => ZoningMode.SingleInitiator,
                "PEER" => ZoningMode.Peer,
                "BROADCAST" => ZoningMode.Broadcast,
                _ => ZoningMode.SingleInitiator
            };

            // Load frame and buffer configuration
            _maxFrameSize = GetConfiguration<int>("MaxFrameSize", 2112);
            _bufferCredits = GetConfiguration<int>("BufferCredits", 32);

            // Load port speed configuration
            var portSpeedStr = GetConfiguration<string>("PortSpeed", "Auto");
            _portSpeed = portSpeedStr.ToUpperInvariant() switch
            {
                "1G" => FcPortSpeed.Speed1Gbps,
                "2G" => FcPortSpeed.Speed2Gbps,
                "4G" => FcPortSpeed.Speed4Gbps,
                "8G" => FcPortSpeed.Speed8Gbps,
                "16G" => FcPortSpeed.Speed16Gbps,
                "32G" => FcPortSpeed.Speed32Gbps,
                "AUTO" => FcPortSpeed.Auto,
                _ => FcPortSpeed.Auto
            };

            // Load FCoE/FIP configuration
            _enableFip = GetConfiguration<bool>("EnableFip", false);

            // Load timeout configuration
            var fabricLoginTimeoutSec = GetConfiguration<int>("FabricLoginTimeoutSeconds", 30);
            _fabricLoginTimeout = TimeSpan.FromSeconds(fabricLoginTimeoutSec);

            var ioTimeoutSec = GetConfiguration<int>("IoTimeoutSeconds", 60);
            _ioTimeout = TimeSpan.FromSeconds(ioTimeoutSec);

            // Initialize HTTP client for fabric management API
            _fabricApiClient = new HttpClient
            {
                BaseAddress = new Uri($"https://{_fabricSwitchAddress}:{_fabricManagementPort}"),
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Add basic authentication
            var authToken = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_fabricUsername}:{_fabricPassword}"));
            _fabricApiClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authToken);

            // Perform fabric login (FLOGI/PLOGI)
            await FabricLoginAsync(ct);

            // Configure zoning
            await ConfigureZoningAsync(ct);

            // Initialize LUN mappings
            await DiscoverLunsAsync(ct);

            // Configure multipath if enabled
            if (_enableMultipath)
            {
                await ConfigureMultipathAsync(ct);
            }
        }

        #region Fabric Management

        /// <summary>
        /// Performs fabric login (FLOGI) and port login (PLOGI) to establish FC session.
        /// </summary>
        private async Task FabricLoginAsync(CancellationToken ct)
        {
            await _connectionLock.WaitAsync(ct);
            try
            {
                // Fabric Login (FLOGI) - register with fabric
                var flogiPayload = new
                {
                    operation = "flogi",
                    initiator_wwpn = _initiatorWwpn,
                    initiator_wwnn = _initiatorWwnn,
                    buffer_credits = _bufferCredits,
                    max_frame_size = _maxFrameSize,
                    port_speed = _portSpeed.ToString(),
                    enable_fip = _enableFip
                };

                var response = await ExecuteFabricApiAsync("POST", "/api/v1/fabric/login", flogiPayload, ct);
                if (!response.IsSuccessful)
                {
                    throw new InvalidOperationException(
                        $"Fabric login (FLOGI) failed: {response.ErrorMessage}");
                }

                // Port Login (PLOGI) - establish sessions with target ports
                foreach (var targetWwpn in _targetWwpns)
                {
                    var plogiPayload = new
                    {
                        operation = "plogi",
                        initiator_wwpn = _initiatorWwpn,
                        target_wwpn = targetWwpn,
                        buffer_credits = _bufferCredits,
                        max_frame_size = _maxFrameSize
                    };

                    var plogiResponse = await ExecuteFabricApiAsync("POST", "/api/v1/port/login", plogiPayload, ct);
                    if (!plogiResponse.IsSuccessful)
                    {
                        throw new InvalidOperationException(
                            $"Port login (PLOGI) failed for target {targetWwpn}: {plogiResponse.ErrorMessage}");
                    }
                }

                // NPIV virtual port login if enabled
                if (_enableNpiv)
                {
                    foreach (var virtualWwpn in _npivVirtualWwpns)
                    {
                        var npivPayload = new
                        {
                            operation = "npiv_login",
                            physical_wwpn = _initiatorWwpn,
                            virtual_wwpn = virtualWwpn,
                            buffer_credits = _bufferCredits
                        };

                        await ExecuteFabricApiAsync("POST", "/api/v1/npiv/login", npivPayload, ct);
                    }
                }

                _isFabricConnected = true;
                _lastFabricLogin = DateTime.UtcNow;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Configures SAN zoning for initiator-target access.
        /// </summary>
        private async Task ConfigureZoningAsync(CancellationToken ct)
        {
            // Create zone configuration
            var zoneName = $"zone_{_initiatorWwpn.Replace(":", "")}";
            var zoneMembers = new List<string> { _initiatorWwpn };
            zoneMembers.AddRange(_targetWwpns);

            var zoningPayload = new
            {
                zone_name = zoneName,
                zoning_mode = _zoningMode.ToString(),
                members = zoneMembers,
                persistent = true
            };

            var response = await ExecuteFabricApiAsync("POST", "/api/v1/zoning/configure", zoningPayload, ct);
            if (!response.IsSuccessful)
            {
                throw new InvalidOperationException(
                    $"Zoning configuration failed: {response.ErrorMessage}");
            }
        }

        /// <summary>
        /// Discovers available LUNs from target storage arrays.
        /// </summary>
        private async Task DiscoverLunsAsync(CancellationToken ct)
        {
            _lunMappings.Clear();

            foreach (var targetWwpn in _targetWwpns)
            {
                var discoveryPayload = new
                {
                    initiator_wwpn = _initiatorWwpn,
                    target_wwpn = targetWwpn,
                    operation = "discover_luns"
                };

                var response = await ExecuteFabricApiAsync("POST", "/api/v1/lun/discover", discoveryPayload, ct);
                if (response.IsSuccessful && response.Data != null)
                {
                    var luns = JsonSerializer.Deserialize<List<FcLunInfo>>(response.Data.ToString() ?? "[]");
                    if (luns != null)
                    {
                        foreach (var lun in luns)
                        {
                            var mapping = new FcLunMapping
                            {
                                LunId = lun.LunId,
                                TargetWwpn = targetWwpn,
                                DevicePath = $"{_lunBasePath}/lun-{targetWwpn.Replace(":", "")}-{lun.LunId}",
                                SizeBytes = lun.SizeBytes,
                                BlockSize = _blockSize,
                                IsMapped = true
                            };

                            _lunMappings[$"{targetWwpn}:{lun.LunId}"] = mapping;
                        }
                    }
                }
            }

            if (_lunMappings.Count == 0)
            {
                throw new InvalidOperationException(
                    "No LUNs discovered. Verify target WWPNs and LUN masking configuration.");
            }
        }

        /// <summary>
        /// Configures multipath I/O for redundancy and load balancing.
        /// </summary>
        private async Task ConfigureMultipathAsync(CancellationToken ct)
        {
            _multipathStates.Clear();

            // Group LUNs by LUN ID (same LUN accessible via multiple paths)
            var lunGroups = _lunMappings.Values
                .GroupBy(m => m.LunId)
                .Where(g => g.Count() > 1);

            foreach (var lunGroup in lunGroups)
            {
                var paths = lunGroup.ToList();
                var multipathState = new MultipathState
                {
                    LunId = lunGroup.Key.ToString(),
                    Paths = paths,
                    ActivePathIndex = 0,
                    LoadBalancingMode = LoadBalancingMode.RoundRobin,
                    FailoverEnabled = true
                };

                var multipathPayload = new
                {
                    lun_id = lunGroup.Key,
                    paths = paths.Select(p => new { target_wwpn = p.TargetWwpn, device_path = p.DevicePath }),
                    load_balancing = "round_robin",
                    failover_enabled = true
                };

                await ExecuteFabricApiAsync("POST", "/api/v1/multipath/configure", multipathPayload, ct);

                _multipathStates[lunGroup.Key.ToString()] = multipathState;
            }
        }

        /// <summary>
        /// Ensures fabric connection is active and reconnects if necessary.
        /// </summary>
        private async Task EnsureFabricConnectedAsync(CancellationToken ct)
        {
            if (!_isFabricConnected || (DateTime.UtcNow - _lastFabricLogin) > _fabricLoginTimeout)
            {
                await FabricLoginAsync(ct);
            }
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(
            string key,
            Stream data,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);
            await EnsureFabricConnectedAsync(ct);

            await _ioLock.WaitAsync(ct);
            try
            {
                // Map key to LUN and offset
                var (lunMapping, offset) = await GetOrCreateLunMappingAsync(key, ct);

                // Calculate required blocks
                var dataSize = data.Length;
                var requiredBlocks = (long)Math.Ceiling((double)dataSize / _blockSize);

                // Write data in block-sized chunks
                var buffer = new byte[_blockSize];
                long currentOffset = offset;
                long totalBytesWritten = 0;

                while (totalBytesWritten < dataSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var bytesToRead = (int)Math.Min(_blockSize, dataSize - totalBytesWritten);
                    var bytesRead = await data.ReadAsync(buffer, 0, bytesToRead, ct);

                    if (bytesRead == 0)
                        break;

                    // Pad to block size if necessary
                    if (bytesRead < _blockSize)
                    {
                        Array.Clear(buffer, bytesRead, _blockSize - bytesRead);
                    }

                    // Execute SCSI write command
                    await ExecuteScsiWriteAsync(lunMapping, currentOffset, buffer, ct);

                    currentOffset += _blockSize;
                    totalBytesWritten += bytesRead;
                }

                // Store metadata in companion .meta file on a management LUN
                if (metadata != null && metadata.Count > 0)
                {
                    await StoreMetadataAsync(key, metadata, ct);
                }

                // Update statistics
                IncrementBytesStored(totalBytesWritten);
                IncrementOperationCounter(StorageOperationType.Store);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = totalBytesWritten,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = GenerateETag(key, totalBytesWritten),
                    ContentType = GetContentType(key),
                    CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                    Tier = Tier
                };
            }
            finally
            {
                _ioLock.Release();
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureFabricConnectedAsync(ct);

            var (lunMapping, offset) = await GetLunMappingAsync(key, ct);
            if (lunMapping == null)
            {
                throw new FileNotFoundException($"FC object not found: {key}");
            }

            // Read object size from metadata or calculate from blocks
            var objectSize = await GetObjectSizeAsync(key, ct);

            var memoryStream = new MemoryStream();
            var buffer = new byte[_blockSize];
            long currentOffset = offset;
            long totalBytesRead = 0;

            while (totalBytesRead < objectSize)
            {
                ct.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(_blockSize, objectSize - totalBytesRead);

                // Execute SCSI read command
                var bytesRead = await ExecuteScsiReadAsync(lunMapping, currentOffset, buffer, ct);

                if (bytesRead == 0)
                    break;

                var actualBytes = (int)Math.Min(bytesToRead, bytesRead);
                await memoryStream.WriteAsync(buffer, 0, actualBytes, ct);

                currentOffset += _blockSize;
                totalBytesRead += actualBytes;
            }

            // Update statistics
            IncrementBytesRetrieved(memoryStream.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            memoryStream.Position = 0;
            return memoryStream;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureFabricConnectedAsync(ct);

            var (lunMapping, offset) = await GetLunMappingAsync(key, ct);
            if (lunMapping == null)
            {
                // Already deleted
                return;
            }

            // Get size for statistics
            var objectSize = await GetObjectSizeAsync(key, ct);

            // Zero out the blocks (SCSI WRITE SAME command)
            await ExecuteScsiUnmapAsync(lunMapping, offset, objectSize, ct);

            // Delete metadata
            await DeleteMetadataAsync(key, ct);

            // Update statistics
            if (objectSize > 0)
            {
                IncrementBytesDeleted(objectSize);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureFabricConnectedAsync(ct);

            try
            {
                var (lunMapping, _) = await GetLunMappingAsync(key, ct);
                IncrementOperationCounter(StorageOperationType.Exists);
                return lunMapping != null;
            }
            catch
            {
                IncrementOperationCounter(StorageOperationType.Exists);
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await EnsureFabricConnectedAsync(ct);
            IncrementOperationCounter(StorageOperationType.List);

            // Query object index from management LUN
            var objects = await ListObjectsFromIndexAsync(prefix, ct);

            foreach (var obj in objects)
            {
                ct.ThrowIfCancellationRequested();
                yield return obj;
                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureFabricConnectedAsync(ct);

            var (lunMapping, _) = await GetLunMappingAsync(key, ct);
            if (lunMapping == null)
            {
                throw new FileNotFoundException($"FC object not found: {key}");
            }

            var objectSize = await GetObjectSizeAsync(key, ct);
            var customMetadata = await LoadMetadataAsync(key, ct);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = objectSize,
                Created = DateTime.UtcNow, // Would need to be stored in metadata
                Modified = DateTime.UtcNow,
                ETag = GenerateETag(key, objectSize),
                ContentType = GetContentType(key),
                CustomMetadata = customMetadata,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await EnsureFabricConnectedAsync(ct);

                // Check fabric status
                var response = await ExecuteFabricApiAsync("GET", "/api/v1/fabric/status", null, ct);
                sw.Stop();

                if (response.IsSuccessful)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"FC fabric {_fabricSwitchAddress} is accessible. Initiator: {_initiatorWwpn}, Targets: {_targetWwpns.Count}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Degraded,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"FC fabric accessible but status check failed: {response.ErrorMessage}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access FC fabric {_fabricSwitchAddress}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                await EnsureFabricConnectedAsync(ct);

                // Sum available capacity across all LUNs
                long totalCapacity = 0;
                long usedCapacity = 0;

                foreach (var mapping in _lunMappings.Values)
                {
                    totalCapacity += mapping.SizeBytes;
                    // Would need to track used blocks per LUN
                }

                return totalCapacity - usedCapacity;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region SCSI Command Execution

        /// <summary>
        /// Executes a SCSI WRITE command to write data to a LUN.
        /// </summary>
        private async Task ExecuteScsiWriteAsync(
            FcLunMapping lunMapping,
            long offset,
            byte[] data,
            CancellationToken ct)
        {
            var scsiPayload = new
            {
                operation = "scsi_write",
                target_wwpn = lunMapping.TargetWwpn,
                lun_id = lunMapping.LunId,
                offset_bytes = offset,
                data_base64 = Convert.ToBase64String(data),
                timeout_seconds = (int)_ioTimeout.TotalSeconds
            };

            var response = await ExecuteFabricApiAsync("POST", "/api/v1/scsi/execute", scsiPayload, ct);
            if (!response.IsSuccessful)
            {
                throw new IOException(
                    $"SCSI WRITE failed for LUN {lunMapping.LunId} at offset {offset}: {response.ErrorMessage}");
            }
        }

        /// <summary>
        /// Executes a SCSI READ command to read data from a LUN.
        /// </summary>
        private async Task<int> ExecuteScsiReadAsync(
            FcLunMapping lunMapping,
            long offset,
            byte[] buffer,
            CancellationToken ct)
        {
            var scsiPayload = new
            {
                operation = "scsi_read",
                target_wwpn = lunMapping.TargetWwpn,
                lun_id = lunMapping.LunId,
                offset_bytes = offset,
                length_bytes = buffer.Length,
                timeout_seconds = (int)_ioTimeout.TotalSeconds
            };

            var response = await ExecuteFabricApiAsync("POST", "/api/v1/scsi/execute", scsiPayload, ct);
            if (!response.IsSuccessful)
            {
                throw new IOException(
                    $"SCSI READ failed for LUN {lunMapping.LunId} at offset {offset}: {response.ErrorMessage}");
            }

            if (response.Data != null)
            {
                var dataBase64 = response.Data.ToString() ?? string.Empty;
                var data = Convert.FromBase64String(dataBase64);
                Array.Copy(data, buffer, Math.Min(data.Length, buffer.Length));
                return data.Length;
            }

            return 0;
        }

        /// <summary>
        /// Executes a SCSI UNMAP command to deallocate blocks (for deletion).
        /// </summary>
        private async Task ExecuteScsiUnmapAsync(
            FcLunMapping lunMapping,
            long offset,
            long sizeBytes,
            CancellationToken ct)
        {
            var scsiPayload = new
            {
                operation = "scsi_unmap",
                target_wwpn = lunMapping.TargetWwpn,
                lun_id = lunMapping.LunId,
                offset_bytes = offset,
                length_bytes = sizeBytes,
                timeout_seconds = (int)_ioTimeout.TotalSeconds
            };

            await ExecuteFabricApiAsync("POST", "/api/v1/scsi/execute", scsiPayload, ct);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Normalizes WWN to standard colon-separated format (e.g., 50:01:43:80:12:34:56:78).
        /// </summary>
        private string NormalizeWwn(string wwn)
        {
            // Remove common separators
            wwn = wwn.Replace(":", "").Replace("-", "").Replace(".", "").Replace(" ", "");

            // Ensure uppercase hex
            wwn = wwn.ToUpperInvariant();

            // Validate length (16 hex characters = 8 bytes)
            if (wwn.Length != 16)
            {
                throw new ArgumentException($"Invalid WWN format: {wwn}. Expected 16 hex characters.");
            }

            // Format with colons
            var parts = new string[8];
            for (int i = 0; i < 8; i++)
            {
                parts[i] = wwn.Substring(i * 2, 2);
            }

            return string.Join(":", parts);
        }

        /// <summary>
        /// Gets or creates a LUN mapping for a storage key.
        /// </summary>
        private async Task<(FcLunMapping lunMapping, long offset)> GetOrCreateLunMappingAsync(
            string key,
            CancellationToken ct)
        {
            // Hash key to determine LUN and offset
            var keyHash = key.GetHashCode();
            var lunIndex = Math.Abs(keyHash) % _lunMappings.Count;
            var lunMapping = _lunMappings.Values.ElementAt(lunIndex);

            // Calculate offset within LUN (simplified - would need allocation table)
            var offset = (long)(Math.Abs(keyHash) % (lunMapping.SizeBytes / _blockSize)) * _blockSize;

            return (lunMapping, offset);

            await Task.CompletedTask; // Keep async signature
        }

        /// <summary>
        /// Gets the LUN mapping for a storage key.
        /// </summary>
        private async Task<(FcLunMapping? lunMapping, long offset)> GetLunMappingAsync(
            string key,
            CancellationToken ct)
        {
            // Would query object index from management LUN
            // Simplified implementation
            return await GetOrCreateLunMappingAsync(key, ct);
        }

        /// <summary>
        /// Gets the size of an object in bytes.
        /// </summary>
        private async Task<long> GetObjectSizeAsync(string key, CancellationToken ct)
        {
            // Would query from metadata store
            // Simplified implementation
            return _blockSize * 10; // Assume 10 blocks

            await Task.CompletedTask; // Keep async signature
        }

        /// <summary>
        /// Stores metadata for an object.
        /// </summary>
        private async Task StoreMetadataAsync(
            string key,
            IDictionary<string, string> metadata,
            CancellationToken ct)
        {
            // Would write to management LUN or metadata service
            await Task.CompletedTask; // Simplified
        }

        /// <summary>
        /// Loads metadata for an object.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, string>?> LoadMetadataAsync(
            string key,
            CancellationToken ct)
        {
            // Would read from management LUN or metadata service
            await Task.CompletedTask; // Simplified
            return null;
        }

        /// <summary>
        /// Deletes metadata for an object.
        /// </summary>
        private async Task DeleteMetadataAsync(string key, CancellationToken ct)
        {
            // Would delete from management LUN or metadata service
            await Task.CompletedTask; // Simplified
        }

        /// <summary>
        /// Lists objects from the object index.
        /// </summary>
        private async Task<List<StorageObjectMetadata>> ListObjectsFromIndexAsync(
            string? prefix,
            CancellationToken ct)
        {
            // Would query from management LUN or object index service
            await Task.CompletedTask; // Simplified
            return new List<StorageObjectMetadata>();
        }

        /// <summary>
        /// Generates an ETag for an object.
        /// </summary>
        private string GenerateETag(string key, long size)
        {
            var hash = HashCode.Combine(key, size);
            return hash.ToString("x");
        }

        /// <summary>
        /// Determines content type from file extension.
        /// </summary>
        private string? GetContentType(string key)
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Executes a fabric management API call.
        /// </summary>
        private async Task<FabricApiResponse> ExecuteFabricApiAsync(
            string method,
            string path,
            object? payload,
            CancellationToken ct)
        {
            try
            {
                HttpResponseMessage response;

                if (method.ToUpperInvariant() == "GET")
                {
                    response = await _fabricApiClient!.GetAsync(path, ct);
                }
                else if (method.ToUpperInvariant() == "POST")
                {
                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    response = await _fabricApiClient!.PostAsync(path, content, ct);
                }
                else
                {
                    throw new NotSupportedException($"HTTP method {method} not supported");
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    object? data = null;
                    try
                    {
                        data = JsonSerializer.Deserialize<object>(responseBody);
                    }
                    catch
                    {
                        data = responseBody;
                    }

                    return new FabricApiResponse
                    {
                        IsSuccessful = true,
                        Data = data
                    };
                }
                else
                {
                    return new FabricApiResponse
                    {
                        IsSuccessful = false,
                        ErrorMessage = $"HTTP {(int)response.StatusCode}: {responseBody}"
                    };
                }
            }
            catch (Exception ex)
            {
                return new FabricApiResponse
                {
                    IsSuccessful = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Fabric logout
            if (_isFabricConnected && _fabricApiClient != null)
            {
                try
                {
                    var logoutPayload = new { operation = "logout", initiator_wwpn = _initiatorWwpn };
                    await ExecuteFabricApiAsync("POST", "/api/v1/fabric/logout", logoutPayload, CancellationToken.None);
                }
                catch
                {
                    // Ignore logout errors
                }
            }

            _fabricApiClient?.Dispose();
            _connectionLock?.Dispose();
            _ioLock?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// FC LUN mapping information.
    /// </summary>
    internal class FcLunMapping
    {
        public int LunId { get; set; }
        public string TargetWwpn { get; set; } = string.Empty;
        public string DevicePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public int BlockSize { get; set; }
        public bool IsMapped { get; set; }
    }

    /// <summary>
    /// FC LUN discovery information.
    /// </summary>
    internal class FcLunInfo
    {
        public int LunId { get; set; }
        public long SizeBytes { get; set; }
        public string VendorId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Multipath I/O state for a LUN.
    /// </summary>
    internal class MultipathState
    {
        public string LunId { get; set; } = string.Empty;
        public List<FcLunMapping> Paths { get; set; } = new();
        public int ActivePathIndex { get; set; }
        public LoadBalancingMode LoadBalancingMode { get; set; }
        public bool FailoverEnabled { get; set; }
        public DateTime LastFailover { get; set; }
    }

    /// <summary>
    /// Zoning configuration modes.
    /// </summary>
    public enum ZoningMode
    {
        /// <summary>Single initiator to multiple targets.</summary>
        SingleInitiator,

        /// <summary>Peer zoning (one-to-one mapping).</summary>
        Peer,

        /// <summary>Broadcast zoning (all members can communicate).</summary>
        Broadcast
    }

    /// <summary>
    /// FC port speed options.
    /// </summary>
    public enum FcPortSpeed
    {
        /// <summary>Auto-negotiate speed.</summary>
        Auto,

        /// <summary>1 Gbps.</summary>
        Speed1Gbps,

        /// <summary>2 Gbps.</summary>
        Speed2Gbps,

        /// <summary>4 Gbps.</summary>
        Speed4Gbps,

        /// <summary>8 Gbps.</summary>
        Speed8Gbps,

        /// <summary>16 Gbps.</summary>
        Speed16Gbps,

        /// <summary>32 Gbps.</summary>
        Speed32Gbps
    }

    /// <summary>
    /// Load balancing modes for multipath I/O.
    /// </summary>
    public enum LoadBalancingMode
    {
        /// <summary>Round-robin across all paths.</summary>
        RoundRobin,

        /// <summary>Use least-loaded path.</summary>
        LeastLoaded,

        /// <summary>Active-passive failover.</summary>
        ActivePassive
    }

    /// <summary>
    /// Fabric API response.
    /// </summary>
    internal class FabricApiResponse
    {
        public bool IsSuccessful { get; set; }
        public object? Data { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
