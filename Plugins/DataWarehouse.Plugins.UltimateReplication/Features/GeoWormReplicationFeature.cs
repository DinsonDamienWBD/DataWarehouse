using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Geo-Dispersed WORM Replication Feature (T5.5).
    /// Enables cross-region WORM (Write Once Read Many) replication with compliance-mode
    /// selection, geofencing checks, and retention period enforcement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replicates data TO WORM-capable storage across geographic regions. Not immutable
    /// transfer -- the data becomes immutable at the destination.
    /// </para>
    /// <para>WORM Modes:</para>
    /// <list type="bullet">
    ///   <item><b>Compliance</b>: True immutability -- data cannot be deleted or modified by anyone,
    ///   including privileged users. Required for HIPAA, SEC 17a-4, FINRA regulations.</item>
    ///   <item><b>Enterprise</b>: Administrative immutability -- data is protected from normal deletion
    ///   but allows privileged deletion with audit trail. Suitable for internal governance.</item>
    /// </list>
    /// <para>Integration via message bus:</para>
    /// <list type="bullet">
    ///   <item>"compliance.geofence.check" -- verify target region is compliant for data jurisdiction</item>
    ///   <item>"storage.write" with WormMode parameter -- write to WORM-capable storage backend</item>
    ///   <item>"storage.read" -- read-back verification of WORM writes</item>
    /// </list>
    /// </remarks>
    public sealed class GeoWormReplicationFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, WormReplicationRecord> _replicationRecords = new();
        private readonly ConcurrentDictionary<string, GeoWormRegion> _regions = new();
        private readonly TimeSpan _operationTimeout;
        private bool _disposed;
        private IDisposable? _wormRequestSubscription;

        // Topics
        private const string ComplianceGeofenceCheckTopic = "compliance.geofence.check";
        private const string ComplianceGeofenceResponseTopic = "compliance.geofence.check.response";
        private const string StorageWriteTopic = "storage.write";
        private const string StorageWriteResponseTopic = "storage.write.response";
        private const string StorageReadTopic = "storage.read";
        private const string StorageReadResponseTopic = "storage.read.response";
        private const string WormReplicationRequestTopic = "replication.ultimate.worm.replicate";
        private const string WormReplicationStatusTopic = "replication.ultimate.worm.status";

        // Statistics
        private long _totalWormReplications;
        private long _complianceModeWrites;
        private long _enterpriseModeWrites;
        private long _geofenceChecks;
        private long _geofenceRejections;
        private long _integrityVerificationsPassed;
        private long _integrityVerificationsFailed;
        private long _totalBytesReplicated;

        /// <summary>
        /// Initializes a new instance of the GeoWormReplicationFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for compliance, storage, and inter-plugin communication.</param>
        /// <param name="operationTimeout">Timeout for individual operations. Default: 60 seconds.</param>
        public GeoWormReplicationFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            TimeSpan? operationTimeout = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(60);

            _wormRequestSubscription = _messageBus.Subscribe(WormReplicationRequestTopic, HandleWormReplicationRequestAsync);
            InitializeDefaultRegions();
        }

        /// <summary>Gets total WORM replications performed.</summary>
        public long TotalWormReplications => Interlocked.Read(ref _totalWormReplications);

        /// <summary>Gets Compliance-mode WORM writes.</summary>
        public long ComplianceModeWrites => Interlocked.Read(ref _complianceModeWrites);

        /// <summary>Gets Enterprise-mode WORM writes.</summary>
        public long EnterpriseModeWrites => Interlocked.Read(ref _enterpriseModeWrites);

        /// <summary>Gets geofence check rejections.</summary>
        public long GeofenceRejections => Interlocked.Read(ref _geofenceRejections);

        /// <summary>
        /// Replicates data to WORM storage across specified geographic regions.
        /// Performs geofencing compliance checks before writing.
        /// </summary>
        /// <param name="request">Replication request with data and options.</param>
        /// <param name="options">Geo-WORM options (target regions, WORM mode, retention).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Replication result with per-region outcomes.</returns>
        public async Task<GeoWormReplicationResult> ReplicateToWormAsync(
            WormReplicationRequest request,
            GeoWormOptions options,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalWormReplications);

            var dataHash = ComputeSha256(request.Data.Span);
            var regionResults = new Dictionary<string, WormRegionResult>();
            var startTime = DateTimeOffset.UtcNow;

            // Determine target regions based on compliance requirements
            var targetRegions = options.TargetRegions.Count > 0
                ? options.TargetRegions
                : SelectRegionsForCompliance(options.ComplianceFrameworks);

            foreach (var regionId in targetRegions)
            {
                // Step 1: Geofence compliance check
                Interlocked.Increment(ref _geofenceChecks);
                var geofenceResult = await CheckGeofenceComplianceAsync(
                    regionId, request.DataClassification, options.ComplianceFrameworks, ct);

                if (!geofenceResult.IsCompliant)
                {
                    Interlocked.Increment(ref _geofenceRejections);
                    regionResults[regionId] = new WormRegionResult
                    {
                        RegionId = regionId,
                        Success = false,
                        Phase = WormReplicationPhase.GeofenceCheck,
                        Reason = $"Geofence rejected: {geofenceResult.RejectionReason}",
                        DurationMs = 0
                    };
                    continue;
                }

                // Step 2: Write to WORM storage
                var writeStart = DateTimeOffset.UtcNow;
                var writeSuccess = await WriteToWormStorageAsync(
                    request.DataId, request.Data, regionId, options.WormMode,
                    options.RetentionPeriods.GetValueOrDefault(regionId, options.DefaultRetentionPeriod), ct);

                if (!writeSuccess)
                {
                    regionResults[regionId] = new WormRegionResult
                    {
                        RegionId = regionId,
                        Success = false,
                        Phase = WormReplicationPhase.WormWrite,
                        Reason = "WORM write failed",
                        DurationMs = (long)(DateTimeOffset.UtcNow - writeStart).TotalMilliseconds
                    };
                    continue;
                }

                // Step 3: Integrity verification via read-back
                var verified = await VerifyWormIntegrityAsync(request.DataId, regionId, dataHash, ct);

                if (verified)
                {
                    Interlocked.Increment(ref _integrityVerificationsPassed);
                    Interlocked.Add(ref _totalBytesReplicated, request.Data.Length);
                }
                else
                {
                    Interlocked.Increment(ref _integrityVerificationsFailed);
                }

                // Track WORM mode statistics
                if (options.WormMode == WormMode.Compliance)
                    Interlocked.Increment(ref _complianceModeWrites);
                else
                    Interlocked.Increment(ref _enterpriseModeWrites);

                regionResults[regionId] = new WormRegionResult
                {
                    RegionId = regionId,
                    Success = verified,
                    Phase = verified ? WormReplicationPhase.Verified : WormReplicationPhase.IntegrityCheck,
                    Reason = verified ? "Replicated and verified" : "Integrity verification failed",
                    DurationMs = (long)(DateTimeOffset.UtcNow - writeStart).TotalMilliseconds,
                    WormMode = options.WormMode,
                    RetentionPeriod = options.RetentionPeriods.GetValueOrDefault(regionId, options.DefaultRetentionPeriod)
                };
            }

            var record = new WormReplicationRecord
            {
                DataId = request.DataId,
                DataHash = dataHash,
                DataSizeBytes = request.Data.Length,
                WormMode = options.WormMode,
                RegionResults = regionResults,
                StartedAt = startTime,
                CompletedAt = DateTimeOffset.UtcNow
            };

            _replicationRecords[request.DataId] = record;

            return new GeoWormReplicationResult
            {
                DataId = request.DataId,
                OverallSuccess = regionResults.Values.Any(r => r.Success),
                SuccessfulRegions = regionResults.Values.Where(r => r.Success).Select(r => r.RegionId).ToArray(),
                FailedRegions = regionResults.Values.Where(r => !r.Success).Select(r => r.RegionId).ToArray(),
                RegionResults = regionResults,
                TotalDurationMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds,
                WormMode = options.WormMode
            };
        }

        /// <summary>
        /// Registers a WORM-capable geographic region.
        /// </summary>
        public void RegisterRegion(GeoWormRegion region)
        {
            _regions[region.RegionId] = region;
        }

        /// <summary>
        /// Gets all registered WORM regions.
        /// </summary>
        public IReadOnlyDictionary<string, GeoWormRegion> GetRegions()
        {
            return _regions;
        }

        /// <summary>
        /// Gets replication records for auditing.
        /// </summary>
        public IReadOnlyDictionary<string, WormReplicationRecord> GetReplicationRecords()
        {
            return _replicationRecords;
        }

        #region Private Methods

        private void InitializeDefaultRegions()
        {
            RegisterRegion(new GeoWormRegion { RegionId = "us-east-1", Name = "US East (Virginia)", Continent = "NA", Country = "US", WormCapable = true, ComplianceFrameworks = new[] { "SEC17a4", "HIPAA", "SOX", "FINRA" } });
            RegisterRegion(new GeoWormRegion { RegionId = "us-west-2", Name = "US West (Oregon)", Continent = "NA", Country = "US", WormCapable = true, ComplianceFrameworks = new[] { "SEC17a4", "HIPAA", "SOX" } });
            RegisterRegion(new GeoWormRegion { RegionId = "eu-west-1", Name = "EU West (Ireland)", Continent = "EU", Country = "IE", WormCapable = true, ComplianceFrameworks = new[] { "GDPR", "DORA", "NIS2" } });
            RegisterRegion(new GeoWormRegion { RegionId = "eu-central-1", Name = "EU Central (Frankfurt)", Continent = "EU", Country = "DE", WormCapable = true, ComplianceFrameworks = new[] { "GDPR", "DORA", "NIS2", "BaFin" } });
            RegisterRegion(new GeoWormRegion { RegionId = "ap-southeast-1", Name = "Asia Pacific (Singapore)", Continent = "AP", Country = "SG", WormCapable = true, ComplianceFrameworks = new[] { "MAS-TRM", "PDPA" } });
            RegisterRegion(new GeoWormRegion { RegionId = "ap-northeast-1", Name = "Asia Pacific (Tokyo)", Continent = "AP", Country = "JP", WormCapable = true, ComplianceFrameworks = new[] { "APPI", "FISC" } });
        }

        private IReadOnlyList<string> SelectRegionsForCompliance(IReadOnlyList<string> frameworks)
        {
            if (frameworks.Count == 0)
                return _regions.Values.Where(r => r.WormCapable).Select(r => r.RegionId).Take(3).ToList();

            return _regions.Values
                .Where(r => r.WormCapable && r.ComplianceFrameworks.Intersect(frameworks).Any())
                .Select(r => r.RegionId)
                .ToList();
        }

        private async Task<GeofenceCheckResult> CheckGeofenceComplianceAsync(
            string regionId,
            string dataClassification,
            IReadOnlyList<string> complianceFrameworks,
            CancellationToken ct)
        {
            var correlationId = $"geofence-{regionId}-{Guid.NewGuid():N}"[..32];
            var tcs = new TaskCompletionSource<GeofenceCheckResult>();

            var subscription = _messageBus.Subscribe(ComplianceGeofenceResponseTopic, msg =>
            {
                if (msg.CorrelationId == correlationId)
                {
                    var compliant = msg.Payload.GetValueOrDefault("compliant") is true;
                    var reason = msg.Payload.GetValueOrDefault("reason")?.ToString() ?? "";

                    tcs.TrySetResult(new GeofenceCheckResult
                    {
                        RegionId = regionId,
                        IsCompliant = compliant,
                        RejectionReason = compliant ? null : reason
                    });
                }
                return Task.CompletedTask;
            });

            try
            {
                await _messageBus.PublishAsync(ComplianceGeofenceCheckTopic, new PluginMessage
                {
                    Type = ComplianceGeofenceCheckTopic,
                    CorrelationId = correlationId,
                    Source = "replication.ultimate.geo-worm",
                    Payload = new Dictionary<string, object>
                    {
                        ["regionId"] = regionId,
                        ["dataClassification"] = dataClassification,
                        ["complianceFrameworks"] = complianceFrameworks.ToArray(),
                        ["operation"] = "worm-replicate",
                        ["checkType"] = "data-residency"
                    }
                }, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Geofence check timed out -- fail safe (reject)
                return new GeofenceCheckResult
                {
                    RegionId = regionId,
                    IsCompliant = false,
                    RejectionReason = "Geofence compliance check timed out"
                };
            }
            finally
            {
                subscription?.Dispose();
            }
        }

        private async Task<bool> WriteToWormStorageAsync(
            string dataId,
            ReadOnlyMemory<byte> data,
            string regionId,
            WormMode wormMode,
            TimeSpan retentionPeriod,
            CancellationToken ct)
        {
            var correlationId = $"worm-write-{dataId}-{regionId}";
            var tcs = new TaskCompletionSource<bool>();

            var subscription = _messageBus.Subscribe(StorageWriteResponseTopic, msg =>
            {
                if (msg.CorrelationId == correlationId)
                {
                    tcs.TrySetResult(msg.Payload.GetValueOrDefault("success") is true);
                }
                return Task.CompletedTask;
            });

            try
            {
                await _messageBus.PublishAsync(StorageWriteTopic, new PluginMessage
                {
                    Type = StorageWriteTopic,
                    CorrelationId = correlationId,
                    Source = "replication.ultimate.geo-worm",
                    Payload = new Dictionary<string, object>
                    {
                        ["key"] = $"worm/{regionId}/{dataId}",
                        ["data"] = Convert.ToBase64String(data.ToArray()),
                        ["region"] = regionId,
                        ["wormMode"] = wormMode.ToString(),
                        ["retentionDays"] = (int)retentionPeriod.TotalDays,
                        ["operation"] = "worm-write",
                        ["immutable"] = true,
                        ["dataSize"] = data.Length
                    }
                }, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_operationTimeout);

                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch
            {
                return false;
            }
            finally
            {
                subscription?.Dispose();
            }
        }

        private async Task<bool> VerifyWormIntegrityAsync(
            string dataId, string regionId, string expectedHash, CancellationToken ct)
        {
            var correlationId = $"worm-verify-{dataId}-{regionId}";
            var tcs = new TaskCompletionSource<bool>();

            var subscription = _messageBus.Subscribe(StorageReadResponseTopic, msg =>
            {
                if (msg.CorrelationId == correlationId)
                {
                    var dataB64 = msg.Payload.GetValueOrDefault("data")?.ToString();
                    if (!string.IsNullOrEmpty(dataB64))
                    {
                        var readData = Convert.FromBase64String(dataB64);
                        tcs.TrySetResult(ComputeSha256(readData) == expectedHash);
                    }
                    else
                    {
                        tcs.TrySetResult(false);
                    }
                }
                return Task.CompletedTask;
            });

            try
            {
                await _messageBus.PublishAsync(StorageReadTopic, new PluginMessage
                {
                    Type = StorageReadTopic,
                    CorrelationId = correlationId,
                    Source = "replication.ultimate.geo-worm",
                    Payload = new Dictionary<string, object>
                    {
                        ["key"] = $"worm/{regionId}/{dataId}",
                        ["region"] = regionId,
                        ["operation"] = "worm-verify"
                    }
                }, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch
            {
                return false;
            }
            finally
            {
                subscription?.Dispose();
            }
        }

        private async Task HandleWormReplicationRequestAsync(PluginMessage message)
        {
            var dataId = message.Payload.GetValueOrDefault("dataId")?.ToString();
            var dataB64 = message.Payload.GetValueOrDefault("data")?.ToString();
            var wormModeStr = message.Payload.GetValueOrDefault("wormMode")?.ToString() ?? "Compliance";
            var regionsObj = message.Payload.GetValueOrDefault("targetRegions") as IEnumerable<object>;
            var retentionDays = message.Payload.GetValueOrDefault("retentionDays") is int rd ? rd : 365;

            if (string.IsNullOrEmpty(dataId) || string.IsNullOrEmpty(dataB64))
                return;

            var request = new WormReplicationRequest
            {
                DataId = dataId,
                Data = Convert.FromBase64String(dataB64),
                DataClassification = message.Payload.GetValueOrDefault("dataClassification")?.ToString() ?? "standard"
            };

            var options = new GeoWormOptions
            {
                WormMode = Enum.TryParse<WormMode>(wormModeStr, true, out var wm) ? wm : WormMode.Compliance,
                TargetRegions = regionsObj?.Select(r => r.ToString()!).ToList() ?? new List<string>(),
                DefaultRetentionPeriod = TimeSpan.FromDays(retentionDays),
                ComplianceFrameworks = new List<string>()
            };

            var result = await ReplicateToWormAsync(request, options);

            await _messageBus.PublishAsync(WormReplicationStatusTopic, new PluginMessage
            {
                Type = WormReplicationStatusTopic,
                CorrelationId = message.CorrelationId,
                Source = "replication.ultimate.geo-worm",
                Payload = new Dictionary<string, object>
                {
                    ["dataId"] = result.DataId,
                    ["success"] = result.OverallSuccess,
                    ["successfulRegions"] = result.SuccessfulRegions,
                    ["failedRegions"] = result.FailedRegions,
                    ["wormMode"] = result.WormMode.ToString(),
                    ["durationMs"] = result.TotalDurationMs
                }
            });
        }

        private static string ComputeSha256(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            return Convert.ToHexString(hash);
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wormRequestSubscription?.Dispose();
        }
    }

    #region Geo-WORM Types

    /// <summary>
    /// WORM (Write Once Read Many) storage mode.
    /// </summary>
    public enum WormMode
    {
        /// <summary>
        /// Compliance WORM: True immutability. Data cannot be deleted or modified by
        /// anyone, including privileged administrators. Required for HIPAA, SEC 17a-4, FINRA.
        /// </summary>
        Compliance,

        /// <summary>
        /// Enterprise WORM: Administrative immutability. Data is protected from normal
        /// deletion but allows privileged deletion with full audit trail.
        /// Suitable for internal governance policies.
        /// </summary>
        Enterprise
    }

    /// <summary>
    /// Phase of WORM replication processing.
    /// </summary>
    public enum WormReplicationPhase
    {
        /// <summary>Geofence compliance check.</summary>
        GeofenceCheck,
        /// <summary>WORM write to storage.</summary>
        WormWrite,
        /// <summary>Integrity verification read-back.</summary>
        IntegrityCheck,
        /// <summary>Replication verified successfully.</summary>
        Verified
    }

    /// <summary>
    /// WORM replication request.
    /// </summary>
    public sealed class WormReplicationRequest
    {
        /// <summary>Unique data identifier.</summary>
        public required string DataId { get; init; }
        /// <summary>Data to replicate.</summary>
        public required ReadOnlyMemory<byte> Data { get; init; }
        /// <summary>Data classification for geofencing.</summary>
        public required string DataClassification { get; init; }
    }

    /// <summary>
    /// Options for geo-dispersed WORM replication.
    /// </summary>
    public sealed class GeoWormOptions
    {
        /// <summary>WORM mode (Compliance or Enterprise).</summary>
        public required WormMode WormMode { get; init; }
        /// <summary>Target region IDs. If empty, auto-selected based on compliance.</summary>
        public required IReadOnlyList<string> TargetRegions { get; init; }
        /// <summary>Default retention period for all regions.</summary>
        public required TimeSpan DefaultRetentionPeriod { get; init; }
        /// <summary>Per-region retention period overrides.</summary>
        public Dictionary<string, TimeSpan> RetentionPeriods { get; init; } = new();
        /// <summary>Compliance frameworks to enforce (e.g., "HIPAA", "GDPR").</summary>
        public required IReadOnlyList<string> ComplianceFrameworks { get; init; }
    }

    /// <summary>
    /// WORM-capable geographic region.
    /// </summary>
    public sealed class GeoWormRegion
    {
        /// <summary>Region identifier.</summary>
        public required string RegionId { get; init; }
        /// <summary>Display name.</summary>
        public required string Name { get; init; }
        /// <summary>Continent code (NA, EU, AP, SA, AF, OC).</summary>
        public required string Continent { get; init; }
        /// <summary>Country code (ISO 3166-1).</summary>
        public required string Country { get; init; }
        /// <summary>Whether this region supports WORM storage.</summary>
        public required bool WormCapable { get; init; }
        /// <summary>Compliance frameworks supported in this region.</summary>
        public required string[] ComplianceFrameworks { get; init; }
    }

    /// <summary>
    /// Result for a single WORM region replication.
    /// </summary>
    public sealed class WormRegionResult
    {
        /// <summary>Region identifier.</summary>
        public required string RegionId { get; init; }
        /// <summary>Whether replication succeeded.</summary>
        public required bool Success { get; init; }
        /// <summary>Phase reached.</summary>
        public required WormReplicationPhase Phase { get; init; }
        /// <summary>Success/failure reason.</summary>
        public required string Reason { get; init; }
        /// <summary>Duration in ms.</summary>
        public required long DurationMs { get; init; }
        /// <summary>WORM mode used.</summary>
        public WormMode? WormMode { get; init; }
        /// <summary>Retention period for this region.</summary>
        public TimeSpan? RetentionPeriod { get; init; }
    }

    /// <summary>
    /// Overall result of geo-dispersed WORM replication.
    /// </summary>
    public sealed class GeoWormReplicationResult
    {
        /// <summary>Data identifier.</summary>
        public required string DataId { get; init; }
        /// <summary>Whether at least one region succeeded.</summary>
        public required bool OverallSuccess { get; init; }
        /// <summary>Regions where replication succeeded.</summary>
        public required string[] SuccessfulRegions { get; init; }
        /// <summary>Regions where replication failed.</summary>
        public required string[] FailedRegions { get; init; }
        /// <summary>Per-region results.</summary>
        public required Dictionary<string, WormRegionResult> RegionResults { get; init; }
        /// <summary>Total duration in ms.</summary>
        public required long TotalDurationMs { get; init; }
        /// <summary>WORM mode used.</summary>
        public required WormMode WormMode { get; init; }
    }

    /// <summary>
    /// Record of a WORM replication for audit.
    /// </summary>
    public sealed class WormReplicationRecord
    {
        /// <summary>Data identifier.</summary>
        public required string DataId { get; init; }
        /// <summary>Data hash.</summary>
        public required string DataHash { get; init; }
        /// <summary>Data size.</summary>
        public required long DataSizeBytes { get; init; }
        /// <summary>WORM mode used.</summary>
        public required WormMode WormMode { get; init; }
        /// <summary>Per-region results.</summary>
        public required Dictionary<string, WormRegionResult> RegionResults { get; init; }
        /// <summary>When started.</summary>
        public required DateTimeOffset StartedAt { get; init; }
        /// <summary>When completed.</summary>
        public required DateTimeOffset CompletedAt { get; init; }
    }

    /// <summary>
    /// Geofence compliance check result.
    /// </summary>
    public sealed class GeofenceCheckResult
    {
        /// <summary>Region checked.</summary>
        public required string RegionId { get; init; }
        /// <summary>Whether the region is compliant.</summary>
        public required bool IsCompliant { get; init; }
        /// <summary>Reason for rejection (null if compliant).</summary>
        public string? RejectionReason { get; init; }
    }

    #endregion
}
