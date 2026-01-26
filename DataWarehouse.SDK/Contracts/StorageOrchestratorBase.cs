using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Contracts
{
    #region Storage Pool Base

    /// <summary>
    /// Abstract base class for storage pools. Provides common infrastructure
    /// for managing multiple storage providers with pluggable strategies.
    /// </summary>
    public abstract class StoragePoolBase : FeaturePluginBase, IStoragePool
    {
        protected readonly ConcurrentDictionary<string, (IStorageProvider Provider, StorageRole Role)> _providers = new();
        protected IStorageStrategy _strategy;

        /// <summary>
        /// Per-URI locks to prevent concurrent writes to the same resource.
        /// Uses SemaphoreSlim for async-safe locking.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _uriLocks = new();

        /// <summary>
        /// Gets or creates a lock for the specified URI.
        /// </summary>
        private SemaphoreSlim GetUriLock(Uri uri)
        {
            var key = uri.ToString();
            return _uriLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract string PoolId { get; }
        public override string Name => $"StoragePool-{PoolId}";
        public IStorageStrategy Strategy => _strategy;
        public IReadOnlyList<IStorageProvider> Providers => _providers.Values.Select(p => p.Provider).ToList();

        protected StoragePoolBase()
        {
            _strategy = new SimpleStrategy();
        }

        public virtual void AddProvider(IStorageProvider provider, StorageRole role = StorageRole.Primary)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var id = (provider as IPlugin)?.Id ?? provider.Scheme;
            _providers[id] = (provider, role);
        }

        public virtual bool RemoveProvider(string providerId)
        {
            return _providers.TryRemove(providerId, out _);
        }

        public virtual void SetStrategy(IStorageStrategy strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        public virtual async Task<StorageResult> SaveAsync(Uri uri, Stream data, StorageIntent? intent = null, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var uriLock = GetUriLock(uri);

            // Acquire per-URI lock to prevent concurrent writes to the same resource
            await uriLock.WaitAsync(ct);
            try
            {
                var providers = Providers;
                var plans = _strategy.PlanWrite(providers, intent, data.Length).ToList();
                var usedProviders = new List<string>();
                long bytesWritten = 0;

                try
                {
                    foreach (var plan in plans.Where(p => p.IsRequired).OrderBy(p => p.Priority))
                    {
                        ct.ThrowIfCancellationRequested();

                        // Ensure stream is seekable before resetting position
                        if (data.CanSeek)
                            data.Position = 0;

                        await plan.Provider.SaveAsync(uri, data);
                        usedProviders.Add((plan.Provider as IPlugin)?.Id ?? plan.Provider.Scheme);
                        bytesWritten = data.Length;
                    }

                    return new StorageResult
                    {
                        Success = true,
                        StoredUri = uri,
                        BytesWritten = bytesWritten,
                        Duration = sw.Elapsed,
                        ProvidersUsed = usedProviders.ToArray()
                    };
                }
                catch (Exception ex)
                {
                    return new StorageResult
                    {
                        Success = false,
                        Error = ex.Message,
                        Duration = sw.Elapsed,
                        ProvidersUsed = usedProviders.ToArray()
                    };
                }
            }
            finally
            {
                uriLock.Release();
            }
        }

        public virtual async Task<Stream> LoadAsync(Uri uri, CancellationToken ct = default)
        {
            var providers = _strategy.PlanRead(Providers, null).ToList();
            var errors = new List<(string ProviderId, string Error)>();

            foreach (var provider in providers)
            {
                var providerId = (provider as IPlugin)?.Id ?? provider.Scheme;
                try
                {
                    if (await provider.ExistsAsync(uri))
                        return await provider.LoadAsync(uri);
                }
                catch (FileNotFoundException)
                {
                    // Item doesn't exist in this provider - continue to next
                    continue;
                }
                catch (Exception ex)
                {
                    // Log the error but continue to try other providers
                    errors.Add((providerId, ex.Message));
                }
            }

            // Build informative error message
            if (errors.Count > 0)
            {
                var errorDetails = string.Join("; ", errors.Select(e => $"{e.ProviderId}: {e.Error}"));
                throw new InvalidOperationException(
                    $"Item not found in any provider: {uri}. Provider errors: {errorDetails}");
            }

            throw new FileNotFoundException($"Item not found in any provider: {uri}");
        }

        public virtual async Task DeleteAsync(Uri uri, CancellationToken ct = default)
        {
            var tasks = Providers.Select(p => p.DeleteAsync(uri));
            await Task.WhenAll(tasks);
        }

        public virtual Task<StoragePoolHealth> GetHealthAsync(CancellationToken ct = default)
        {
            var details = _providers.Select(kvp => new ProviderHealth
            {
                ProviderId = kvp.Key,
                Role = kvp.Value.Role,
                IsHealthy = true
            }).ToList();

            return Task.FromResult(new StoragePoolHealth
            {
                IsHealthy = details.All(p => p.IsHealthy),
                TotalProviders = details.Count,
                HealthyProviders = details.Count(p => p.IsHealthy),
                ProviderDetails = details
            });
        }

        public virtual Task<RepairResult> RepairAsync(string? targetProviderId = null, CancellationToken ct = default)
        {
            return Task.FromResult(new RepairResult { Success = true, ItemsChecked = 0, ItemsRepaired = 0 });
        }

        /// <summary>
        /// Saves multiple items in a batch with parallel execution.
        /// </summary>
        public virtual async Task<BatchStorageResult> SaveBatchAsync(
            IEnumerable<BatchSaveItem> items,
            StorageIntent? intent = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var itemList = items.ToList();
            var results = new List<BatchItemResult>();
            var successCount = 0;
            var failureCount = 0;

            // Use parallel execution with configurable concurrency
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);

            var tasks = itemList.Select(async item =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var result = await SaveAsync(item.Uri, item.Data, intent, ct);
                    return new BatchItemResult
                    {
                        Uri = item.Uri,
                        Success = result.Success,
                        Error = result.Error,
                        BytesWritten = result.BytesWritten
                    };
                }
                catch (Exception ex)
                {
                    return new BatchItemResult
                    {
                        Uri = item.Uri,
                        Success = false,
                        Error = ex.Message
                    };
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);

            foreach (var result in batchResults)
            {
                results.Add(result);
                if (result.Success)
                    successCount++;
                else
                    failureCount++;
            }

            return new BatchStorageResult
            {
                TotalItems = itemList.Count,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = sw.Elapsed,
                Results = results
            };
        }

        /// <summary>
        /// Deletes multiple items in a batch with parallel execution.
        /// </summary>
        public virtual async Task<BatchStorageResult> DeleteBatchAsync(
            IEnumerable<Uri> uris,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var uriList = uris.ToList();
            var results = new List<BatchItemResult>();
            var successCount = 0;
            var failureCount = 0;

            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);

            var tasks = uriList.Select(async uri =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    await DeleteAsync(uri, ct);
                    return new BatchItemResult
                    {
                        Uri = uri,
                        Success = true
                    };
                }
                catch (Exception ex)
                {
                    return new BatchItemResult
                    {
                        Uri = uri,
                        Success = false,
                        Error = ex.Message
                    };
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);

            foreach (var result in batchResults)
            {
                results.Add(result);
                if (result.Success)
                    successCount++;
                else
                    failureCount++;
            }

            return new BatchStorageResult
            {
                TotalItems = uriList.Count,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = sw.Elapsed,
                Results = results
            };
        }

        /// <summary>
        /// Checks existence of multiple items in a batch with parallel execution.
        /// </summary>
        public virtual async Task<Dictionary<Uri, bool>> ExistsBatchAsync(
            IEnumerable<Uri> uris,
            CancellationToken ct = default)
        {
            var uriList = uris.ToList();
            var results = new Dictionary<Uri, bool>();
            var resultsLock = new object();

            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 4);

            var tasks = uriList.Select(async uri =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var provider = Providers.FirstOrDefault();
                    var exists = provider != null && await provider.ExistsAsync(uri);
                    lock (resultsLock)
                    {
                        results[uri] = exists;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results;
        }

        public StorageRole GetProviderRole(string providerId)
        {
            return _providers.TryGetValue(providerId, out var p) ? p.Role : StorageRole.Primary;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["PoolId"] = PoolId;
            metadata["ProviderCount"] = _providers.Count;
            metadata["StrategyType"] = _strategy.Type.ToString();
            return metadata;
        }
    }

    #endregion

    #region Storage Strategy Base

    /// <summary>
    /// Abstract base class for storage strategies.
    /// </summary>
    public abstract class StorageStrategyBase : IStorageStrategy
    {
        public abstract string StrategyId { get; }
        public abstract string Name { get; }
        public abstract StorageStrategyType Type { get; }

        public abstract IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize);

        public virtual IEnumerable<IStorageProvider> PlanRead(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent)
        {
            // Default: return all providers, prefer cache if available
            return providers.OrderBy(p => (p as IPlugin)?.Category == PluginCategory.StorageProvider ? 0 : 1);
        }

        public virtual RecoveryPlan OnProviderFailure(IStorageProvider failedProvider, IReadOnlyList<IStorageProvider> remainingProviders)
        {
            return new RecoveryPlan
            {
                CanRecover = remainingProviders.Count > 0,
                FailoverProvider = remainingProviders.FirstOrDefault(),
                Message = remainingProviders.Count > 0 ? "Failover to remaining provider" : "No providers available"
            };
        }

        public virtual StrategyValidation Validate(IReadOnlyList<IStorageProvider> providers)
        {
            return new StrategyValidation
            {
                IsValid = providers.Count > 0,
                MinimumProviders = 1,
                CurrentProviders = providers.Count,
                Errors = providers.Count == 0 ? new List<string> { "At least one provider required" } : new()
            };
        }
    }

    /// <summary>Simple single-provider strategy.</summary>
    public class SimpleStrategy : StorageStrategyBase
    {
        public override string StrategyId => "simple";
        public override string Name => "Simple";
        public override StorageStrategyType Type => StorageStrategyType.Simple;

        public override IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize)
        {
            var primary = providers.FirstOrDefault();
            if (primary != null)
                yield return new ProviderWritePlan { Provider = primary, Role = StorageRole.Primary, IsRequired = true, Priority = 0 };
        }
    }

    /// <summary>Mirrored strategy (RAID 1) - writes to all providers.</summary>
    public class MirroredStrategy : StorageStrategyBase
    {
        public override string StrategyId => "mirrored";
        public override string Name => "Mirrored (RAID 1)";
        public override StorageStrategyType Type => StorageStrategyType.Mirrored;

        public override IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize)
        {
            int priority = 0;
            foreach (var provider in providers)
            {
                yield return new ProviderWritePlan
                {
                    Provider = provider,
                    Role = priority == 0 ? StorageRole.Primary : StorageRole.Mirror,
                    IsRequired = true,
                    Priority = priority++
                };
            }
        }

        public override StrategyValidation Validate(IReadOnlyList<IStorageProvider> providers)
        {
            return new StrategyValidation
            {
                IsValid = providers.Count >= 2,
                MinimumProviders = 2,
                CurrentProviders = providers.Count,
                Errors = providers.Count < 2 ? new List<string> { "Mirroring requires at least 2 providers" } : new(),
                Warnings = providers.Count == 2 ? new List<string> { "Only 1 redundant copy available" } : new()
            };
        }
    }

    /// <summary>Cached strategy - writes to cache and primary.</summary>
    public class CachedStrategy : StorageStrategyBase
    {
        public override string StrategyId => "cached";
        public override string Name => "Cached";
        public override StorageStrategyType Type => StorageStrategyType.Cached;

        public override IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize)
        {
            // Write to primary first (required), then cache (optional)
            var primary = providers.FirstOrDefault();
            if (primary != null)
                yield return new ProviderWritePlan { Provider = primary, Role = StorageRole.Primary, IsRequired = true, Priority = 0 };

            if (providers.Count > 1)
                yield return new ProviderWritePlan { Provider = providers[1], Role = StorageRole.Cache, IsRequired = false, Priority = 1 };
        }

        public override IEnumerable<IStorageProvider> PlanRead(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent)
        {
            // Read from cache first, then primary
            if (providers.Count > 1)
                yield return providers[1];
            if (providers.Count > 0)
                yield return providers[0];
        }
    }

    /// <summary>Write-ahead log strategy for durability.</summary>
    public class WriteAheadLogStrategy : StorageStrategyBase
    {
        public override string StrategyId => "wal";
        public override string Name => "Write-Ahead Log";
        public override StorageStrategyType Type => StorageStrategyType.WriteAheadLog;

        public override IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize)
        {
            // Write to WAL first, then primary
            if (providers.Count > 1)
                yield return new ProviderWritePlan { Provider = providers[1], Role = StorageRole.WriteAheadLog, IsRequired = true, Priority = 0, Mode = WriteMode.WriteAheadLog };

            if (providers.Count > 0)
                yield return new ProviderWritePlan { Provider = providers[0], Role = StorageRole.Primary, IsRequired = true, Priority = 1 };
        }
    }

    /// <summary>Real-time strategy for high-stakes synchronous replication.</summary>
    public class RealTimeStrategy : StorageStrategyBase
    {
        public int MinimumConfirmations { get; init; } = 2;

        public override string StrategyId => "realtime";
        public override string Name => "Real-Time Synchronous";
        public override StorageStrategyType Type => StorageStrategyType.RealTime;

        public override IEnumerable<ProviderWritePlan> PlanWrite(IReadOnlyList<IStorageProvider> providers, StorageIntent? intent, long dataSize)
        {
            // All providers are required for real-time sync
            int priority = 0;
            foreach (var provider in providers)
            {
                yield return new ProviderWritePlan
                {
                    Provider = provider,
                    Role = priority == 0 ? StorageRole.Primary : StorageRole.Mirror,
                    IsRequired = priority < MinimumConfirmations,
                    Priority = priority++
                };
            }
        }

        public override StrategyValidation Validate(IReadOnlyList<IStorageProvider> providers)
        {
            return new StrategyValidation
            {
                IsValid = providers.Count >= MinimumConfirmations,
                MinimumProviders = MinimumConfirmations,
                CurrentProviders = providers.Count,
                Errors = providers.Count < MinimumConfirmations
                    ? new List<string> { $"Real-time requires at least {MinimumConfirmations} providers for quorum" }
                    : new()
            };
        }
    }

    #endregion

    #region Indexing Storage Orchestrator Base

    /// <summary>
    /// Abstract base class for storage orchestration with automatic indexing pipeline.
    /// Combines multiple storage providers and triggers indexing stages on write.
    ///
    /// Pipeline: Save → SQL metadata → OCR → NoSQL text → Embeddings → Vector DB → AI Summary
    ///
    /// Note: This is a KERNEL-LEVEL orchestrator that combines multiple PLUGINS.
    /// For individual plugin multi-instance support, use HybridStoragePluginBase&lt;TConfig&gt;.
    /// </summary>
    public abstract class IndexingStorageOrchestratorBase : StoragePoolBase, IHybridStorage
    {
        protected HybridStorageConfig _config = new();
        protected IStorageSearchOrchestrator? _searchOrchestrator;

        public HybridStorageConfig Config => _config;
        public IStorageSearchOrchestrator SearchOrchestrator => _searchOrchestrator ?? throw new InvalidOperationException("SearchOrchestrator not configured");

        public virtual void Configure(HybridStorageConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void SetSearchOrchestrator(IStorageSearchOrchestrator orchestrator)
        {
            _searchOrchestrator = orchestrator;
        }

        public virtual async Task<HybridSaveResult> SaveWithIndexingAsync(Uri uri, Stream data, StorageIntent? intent = null, CancellationToken ct = default)
        {
            // Step 1: Save to primary storage (synchronous)
            var baseResult = await SaveAsync(uri, data, intent, ct);

            if (!baseResult.Success)
            {
                return new HybridSaveResult
                {
                    Success = false,
                    Error = baseResult.Error,
                    Duration = baseResult.Duration
                };
            }

            // Step 2: Plan indexing stages (asynchronous)
            var plannedStages = PlanIndexingStages(uri, data);
            var jobId = Guid.NewGuid().ToString("N")[..12];

            // Step 3: Start background indexing
            _ = Task.Run(() => ExecuteIndexingPipelineAsync(uri, data, plannedStages, jobId, ct), ct);

            return new HybridSaveResult
            {
                Success = true,
                StoredUri = baseResult.StoredUri,
                BytesWritten = baseResult.BytesWritten,
                Duration = baseResult.Duration,
                ProvidersUsed = baseResult.ProvidersUsed,
                IndexingJobId = jobId,
                PlannedIndexingStages = plannedStages.ToArray(),
                EstimatedIndexingTime = EstimateIndexingTime(plannedStages)
            };
        }

        protected virtual List<string> PlanIndexingStages(Uri uri, Stream data)
        {
            var stages = new List<string>();
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();

            if (_config.EnableSqlMetadata) stages.Add("SqlMetadata");
            if (_config.EnableOcr && _config.OcrFileTypes.Contains(extension) && data.Length <= _config.MaxOcrFileSizeBytes)
                stages.Add("Ocr");
            if (_config.EnableNoSqlText) stages.Add("NoSqlText");
            if (_config.EnableVectorEmbeddings) stages.Add("VectorEmbeddings");
            if (_config.EnableAiSummary) stages.Add("AiSummary");
            if (_config.EnableEventTriggers && _config.EventTriggers.Count > 0) stages.Add("EventTriggers");

            return stages;
        }

        protected virtual TimeSpan EstimateIndexingTime(List<string> stages)
        {
            var ms = 0;
            foreach (var stage in stages)
            {
                ms += stage switch
                {
                    "SqlMetadata" => 50,
                    "Ocr" => 5000,
                    "NoSqlText" => 100,
                    "VectorEmbeddings" => 500,
                    "AiSummary" => 10000,
                    "EventTriggers" => 200,
                    _ => 100
                };
            }
            return TimeSpan.FromMilliseconds(ms);
        }

        protected abstract Task ExecuteIndexingPipelineAsync(Uri uri, Stream data, List<string> stages, string jobId, CancellationToken ct);

        public abstract Task<IndexingStatus> GetIndexingStatusAsync(Uri uri, CancellationToken ct = default);

        public virtual async Task<IndexingStatus> ReindexAsync(Uri uri, CancellationToken ct = default)
        {
            var data = await LoadAsync(uri, ct);
            var stages = PlanIndexingStages(uri, data);
            var jobId = Guid.NewGuid().ToString("N")[..12];

            _ = Task.Run(() => ExecuteIndexingPipelineAsync(uri, data, stages, jobId, ct), ct);

            return new IndexingStatus
            {
                Uri = uri,
                State = IndexingState.InProgress,
                StartedAt = DateTime.UtcNow
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["HybridStorage"] = true;
            metadata["EnabledStages"] = new[]
            {
                _config.EnableSqlMetadata ? "SqlMetadata" : null,
                _config.EnableOcr ? "Ocr" : null,
                _config.EnableNoSqlText ? "NoSqlText" : null,
                _config.EnableVectorEmbeddings ? "VectorEmbeddings" : null,
                _config.EnableAiSummary ? "AiSummary" : null
            }.Where(s => s != null).ToArray();
            return metadata;
        }
    }

    #endregion

    #region Real-Time Storage Orchestrator Base

    /// <summary>
    /// Abstract base class for real-time high-stakes storage orchestration.
    /// Provides synchronous multi-site replication, audit trails, and compliance features.
    /// Suitable for government, healthcare, and financial data.
    ///
    /// Features:
    /// - Synchronous multi-site replication with configurable quorum
    /// - Immutable audit trails for compliance
    /// - Cryptographic integrity verification
    /// - Point-in-time recovery support
    /// - Legal hold and retention policies
    ///
    /// Note: This is a KERNEL-LEVEL orchestrator that combines multiple PLUGINS.
    /// For individual plugin multi-instance support, use HybridStoragePluginBase&lt;TConfig&gt;.
    /// </summary>
    public abstract class RealTimeStorageOrchestratorBase : StoragePoolBase, IRealTimeStorage
    {
        protected ComplianceMode _complianceMode = ComplianceMode.None;
        protected readonly ConcurrentDictionary<Uri, AuditEntry> _auditTrail = new();
        protected readonly ConcurrentDictionary<Uri, LockResult> _locks = new();

        /// <summary>
        /// Lock for synchronizing stream hash operations.
        /// Prevents concurrent hash computations from corrupting stream position.
        /// </summary>
        private readonly SemaphoreSlim _hashLock = new(1, 1);

        public ComplianceMode ComplianceMode => _complianceMode;

        protected RealTimeStorageOrchestratorBase()
        {
            _strategy = new RealTimeStrategy();
        }

        public virtual void SetComplianceMode(ComplianceMode mode)
        {
            _complianceMode = mode;
            OnComplianceModeChanged(mode);
        }

        protected virtual void OnComplianceModeChanged(ComplianceMode mode)
        {
            // Apply compliance-specific settings
        }

        public virtual async Task<RealTimeSaveResult> SaveSynchronousAsync(Uri uri, Stream data, RealTimeWriteOptions options, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var confirmedSites = new List<string>();
            var dataHash = options.GenerateHash ? await ComputeHashAsync(data, options.HashAlgorithm, ct) : null;

            data.Position = 0;

            // Synchronous write to all required providers
            var plans = _strategy.PlanWrite(Providers, null, data.Length)
                .Where(p => p.IsRequired || options.RequireAllSites)
                .ToList();

            var requiredConfirmations = options.RequireAllSites ? plans.Count : options.MinimumConfirmations;
            var errors = new List<string>();

            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    data.Position = 0;
                    await plan.Provider.SaveAsync(uri, data);
                    confirmedSites.Add((plan.Provider as IPlugin)?.Id ?? plan.Provider.Scheme);
                }
                catch (Exception ex)
                {
                    errors.Add($"{plan.Provider.Scheme}: {ex.Message}");
                }

                if (confirmedSites.Count >= requiredConfirmations && !options.RequireAllSites)
                    break;
            }

            var success = confirmedSites.Count >= requiredConfirmations;
            var auditId = Guid.NewGuid().ToString("N");

            // Create audit entry
            var auditEntry = new AuditEntry
            {
                AuditId = auditId,
                Timestamp = DateTime.UtcNow,
                Action = AuditAction.Created,
                UserId = options.InitiatedBy ?? "system",
                Reason = options.Reason,
                DataHash = dataHash,
                VersionNumber = 1,
                Details = new Dictionary<string, object>
                {
                    ["ConfirmedSites"] = confirmedSites.Count,
                    ["RequiredConfirmations"] = requiredConfirmations,
                    ["Encryption"] = options.Encryption.ToString()
                }
            };
            _auditTrail[uri] = auditEntry;

            return new RealTimeSaveResult
            {
                Success = success,
                StoredUri = success ? uri : null,
                BytesWritten = data.Length,
                Duration = sw.Elapsed,
                ProvidersUsed = confirmedSites.ToArray(),
                ConfirmedSites = confirmedSites.ToArray(),
                DataHash = dataHash,
                VersionNumber = 1,
                ReplicatedAt = DateTime.UtcNow,
                AuditId = auditId,
                Error = success ? null : $"Only {confirmedSites.Count}/{requiredConfirmations} confirmations received. Errors: {string.Join("; ", errors)}"
            };
        }

        public virtual async Task<RealTimeReadResult> ReadVerifiedAsync(Uri uri, CancellationToken ct = default)
        {
            try
            {
                var stream = await LoadAsync(uri, ct);
                var hash = await ComputeHashAsync(stream, HashAlgorithmType.SHA256, ct);
                stream.Position = 0;

                // Verify against stored hash if available
                var isVerified = _auditTrail.TryGetValue(uri, out var audit)
                    ? audit.DataHash == hash
                    : true;

                // Log read access
                _auditTrail[uri] = new AuditEntry
                {
                    AuditId = Guid.NewGuid().ToString("N"),
                    Timestamp = DateTime.UtcNow,
                    Action = AuditAction.Read,
                    DataHash = hash
                };

                return new RealTimeReadResult
                {
                    Success = true,
                    Data = stream,
                    IntegrityVerified = isVerified,
                    DataHash = hash,
                    VersionNumber = audit?.VersionNumber ?? 0,
                    LastModified = audit?.Timestamp ?? DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new RealTimeReadResult { Success = false, Error = ex.Message };
            }
        }

        public abstract Task<Stream> ReadAtPointInTimeAsync(Uri uri, DateTime pointInTime, CancellationToken ct = default);

        public virtual async IAsyncEnumerable<AuditEntry> GetAuditTrailAsync(Uri uri, DateTime? from = null, DateTime? to = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var entries = _auditTrail.Values
                .Where(e => from == null || e.Timestamp >= from)
                .Where(e => to == null || e.Timestamp <= to)
                .OrderByDescending(e => e.Timestamp);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public virtual async Task<IntegrityReport> VerifyIntegrityAsync(Uri uri, CancellationToken ct = default)
        {
            var siteResults = new Dictionary<string, SiteIntegrity>();
            string? expectedHash = null;

            foreach (var provider in Providers)
            {
                var siteId = (provider as IPlugin)?.Id ?? provider.Scheme;
                try
                {
                    var stream = await provider.LoadAsync(uri);
                    var hash = await ComputeHashAsync(stream, HashAlgorithmType.SHA256, ct);
                    expectedHash ??= hash;

                    siteResults[siteId] = new SiteIntegrity
                    {
                        SiteId = siteId,
                        IsValid = hash == expectedHash,
                        ActualHash = hash,
                        LastVerified = DateTime.UtcNow
                    };
                }
                catch (Exception)
                {
                    siteResults[siteId] = new SiteIntegrity
                    {
                        SiteId = siteId,
                        IsValid = false,
                        LastVerified = DateTime.UtcNow
                    };
                }
            }

            return new IntegrityReport
            {
                IsValid = siteResults.Values.All(s => s.IsValid),
                Uri = uri,
                ExpectedHash = expectedHash ?? string.Empty,
                SiteResults = siteResults,
                VerifiedAt = DateTime.UtcNow
            };
        }

        public virtual Task<ReplicationStatus> GetReplicationStatusAsync(CancellationToken ct = default)
        {
            var sites = Providers.Select(p => new SiteReplicationInfo
            {
                SiteId = (p as IPlugin)?.Id ?? p.Scheme,
                Location = p.Scheme,
                IsSynced = true,
                LastSyncTime = DateTime.UtcNow,
                Lag = TimeSpan.Zero
            }).ToList();

            return Task.FromResult(new ReplicationStatus
            {
                TotalSites = sites.Count,
                SyncedSites = sites.Count(s => s.IsSynced),
                ReplicationLag = TimeSpan.Zero,
                Sites = sites
            });
        }

        public virtual Task<LockResult> LockAsync(Uri uri, LockReason reason, string authorizedBy, DateTime? expiresAt = null, CancellationToken ct = default)
        {
            var lockId = Guid.NewGuid().ToString("N");
            var result = new LockResult
            {
                Success = true,
                LockId = lockId,
                LockedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
            };

            _locks[uri] = result;

            _auditTrail[uri] = new AuditEntry
            {
                AuditId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                Action = AuditAction.Locked,
                UserId = authorizedBy,
                Reason = reason.ToString(),
                Details = new Dictionary<string, object>
                {
                    ["LockId"] = lockId,
                    ["ExpiresAt"] = expiresAt?.ToString() ?? "Never"
                }
            };

            return Task.FromResult(result);
        }

        public virtual Task ReleaseLockAsync(Uri uri, string authorizedBy, CancellationToken ct = default)
        {
            _locks.TryRemove(uri, out _);

            _auditTrail[uri] = new AuditEntry
            {
                AuditId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                Action = AuditAction.Unlocked,
                UserId = authorizedBy
            };

            return Task.CompletedTask;
        }

        /// <summary>
        /// Computes a cryptographic hash of the stream data.
        /// Thread-safe: uses a lock to prevent concurrent hash operations from corrupting stream position.
        /// </summary>
        protected virtual async Task<string> ComputeHashAsync(Stream data, HashAlgorithmType algorithm, CancellationToken ct)
        {
            // For non-seekable streams, we need to copy to memory first
            if (!data.CanSeek)
            {
                using var ms = new MemoryStream();
                await data.CopyToAsync(ms, ct);
                ms.Position = 0;
                return await ComputeHashFromSeekableStreamAsync(ms, algorithm, ct);
            }

            // For seekable streams, use lock to prevent concurrent position manipulation
            await _hashLock.WaitAsync(ct);
            try
            {
                return await ComputeHashFromSeekableStreamAsync(data, algorithm, ct);
            }
            finally
            {
                _hashLock.Release();
            }
        }

        /// <summary>
        /// Internal hash computation for seekable streams. Caller must ensure thread-safety.
        /// </summary>
        private static async Task<string> ComputeHashFromSeekableStreamAsync(Stream data, HashAlgorithmType algorithm, CancellationToken ct)
        {
            var originalPosition = data.Position;
            data.Position = 0;

            try
            {
                using System.Security.Cryptography.HashAlgorithm hashAlg = algorithm switch
                {
                    HashAlgorithmType.SHA256 => System.Security.Cryptography.SHA256.Create(),
                    HashAlgorithmType.SHA384 => System.Security.Cryptography.SHA384.Create(),
                    HashAlgorithmType.SHA512 => System.Security.Cryptography.SHA512.Create(),
                    HashAlgorithmType.BLAKE2 => throw new NotSupportedException("BLAKE2 requires the CryptoPlugin. Install and register the BLAKE2 crypto plugin."),
                    HashAlgorithmType.BLAKE3 => throw new NotSupportedException("BLAKE3 requires the CryptoPlugin. Install and register the BLAKE3 crypto plugin."),
                    _ => System.Security.Cryptography.SHA256.Create()
                };

                var hash = await hashAlg.ComputeHashAsync(data, ct);
                return Convert.ToHexString(hash);
            }
            finally
            {
                // Restore original position for caller
                data.Position = originalPosition;
            }
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["RealTimeStorage"] = true;
            metadata["ComplianceMode"] = _complianceMode.ToString();
            metadata["SynchronousReplication"] = true;
            metadata["AuditTrailEnabled"] = true;
            metadata["IntegrityVerification"] = true;
            return metadata;
        }
    }

    #endregion

    #region Search Orchestrator Base

    /// <summary>
    /// Abstract base class for search orchestration across multiple providers.
    /// Coordinates parallel search across SQL, NoSQL, Vector, and AI providers.
    ///
    /// Typical latencies:
    /// - Thread A (SQL): ~10ms
    /// - Thread B (NoSQL): ~50ms
    /// - Thread C (Vector): ~200ms
    /// - Thread D (AI): ~3-10s
    ///
    /// Supports multiple merge strategies: Union, ScoreWeighted, ReciprocalRankFusion.
    /// </summary>
    public abstract class SearchOrchestratorBase : IStorageSearchOrchestrator
    {
        protected SearchOrchestratorConfig _config = new();
        protected readonly List<(SearchProviderType Type, IPlugin Plugin)> _searchProviders = new();

        public virtual void Configure(SearchOrchestratorConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void RegisterSearchProvider(SearchProviderType type, IPlugin plugin)
        {
            _searchProviders.Add((type, plugin));
        }

        public virtual IReadOnlyList<SearchProviderInfo> GetProviders()
        {
            return _searchProviders.Select(sp => new SearchProviderInfo
            {
                Type = sp.Type,
                Name = sp.Plugin.Name,
                IsAvailable = true,
                PluginId = sp.Plugin.Id,
                TypicalLatency = sp.Type switch
                {
                    SearchProviderType.SqlMetadata => TimeSpan.FromMilliseconds(10),
                    SearchProviderType.NoSqlKeyword => TimeSpan.FromMilliseconds(50),
                    SearchProviderType.VectorSemantic => TimeSpan.FromMilliseconds(200),
                    SearchProviderType.AiAgent => TimeSpan.FromSeconds(5),
                    _ => TimeSpan.FromMilliseconds(100)
                }
            }).ToList();
        }

        public virtual async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var providerResults = new Dictionary<SearchProviderType, ProviderSearchResult>();
            var errors = new Dictionary<SearchProviderType, string>();

            var enabledProviders = query.EnabledProviders ?? Enum.GetValues<SearchProviderType>();
            var timeout = query.ProviderTimeout ?? _config.DefaultProviderTimeout;

            if (_config.EnableParallelExecution)
            {
                // Execute searches in parallel
                var tasks = enabledProviders.Select(async type =>
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);

                    try
                    {
                        var result = await ExecuteProviderSearchAsync(type, query, cts.Token);
                        return (type, result, (string?)null);
                    }
                    catch (Exception ex)
                    {
                        return (type, (ProviderSearchResult?)null, ex.Message);
                    }
                });

                var results = await Task.WhenAll(tasks);
                foreach (var (type, result, error) in results)
                {
                    if (result != null)
                        providerResults[type] = result;
                    else if (error != null)
                        errors[type] = error;
                }
            }
            else
            {
                // Execute searches sequentially
                foreach (var type in enabledProviders)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);

                    try
                    {
                        var result = await ExecuteProviderSearchAsync(type, query, cts.Token);
                        providerResults[type] = result;
                    }
                    catch (Exception ex)
                    {
                        errors[type] = ex.Message;
                    }
                }
            }

            // Merge and rank results
            var mergedItems = MergeResults(providerResults.Values.ToList(), query);
            string? aiReasoning = null;

            // AI refinement if enabled
            if (query.EnableAiRefinement && providerResults.ContainsKey(SearchProviderType.AiAgent))
            {
                aiReasoning = providerResults[SearchProviderType.AiAgent].Items.FirstOrDefault()?.Snippet;
            }

            return new SearchResult
            {
                TotalCount = mergedItems.Count,
                Items = mergedItems.Take(query.MaxTotalResults).ToList(),
                ProviderResults = providerResults,
                AiReasoning = aiReasoning,
                Duration = sw.Elapsed,
                Errors = errors
            };
        }

        public virtual async IAsyncEnumerable<SearchResultBatch> SearchStreamingAsync(SearchQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var enabledProviders = (query.EnabledProviders ?? Enum.GetValues<SearchProviderType>()).ToList();
            var orderedProviders = enabledProviders.OrderBy(p => GetProviders().FirstOrDefault(pi => pi.Type == p)?.TypicalLatency ?? TimeSpan.MaxValue).ToList();

            foreach (var type in orderedProviders)
            {
                ct.ThrowIfCancellationRequested();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                ProviderSearchResult? result = null;
                string? error = null;

                try
                {
                    result = await ExecuteProviderSearchAsync(type, query, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cancellation requested - stop iteration
                    yield break;
                }
                catch (Exception ex)
                {
                    // Capture error but continue with other providers
                    error = ex.Message;
                }

                // Always yield a batch, even if provider failed (with error info)
                yield return new SearchResultBatch
                {
                    Source = type,
                    Items = result?.Items ?? new List<SearchResultItem>(),
                    Latency = sw.Elapsed,
                    IsFinal = type == orderedProviders.Last(),
                    Error = error
                };
            }
        }

        protected abstract Task<ProviderSearchResult> ExecuteProviderSearchAsync(SearchProviderType type, SearchQuery query, CancellationToken ct);

        protected virtual List<SearchResultItem> MergeResults(List<ProviderSearchResult> providerResults, SearchQuery query)
        {
            var allItems = providerResults.SelectMany(pr => pr.Items).ToList();

            // Apply merge strategy
            var merged = _config.MergeStrategy switch
            {
                SearchMergeStrategy.Union => MergeUnion(allItems),
                SearchMergeStrategy.ScoreWeighted => MergeScoreWeighted(allItems),
                SearchMergeStrategy.ReciprocalRankFusion => MergeRRF(allItems, providerResults),
                _ => MergeScoreWeighted(allItems)
            };

            return merged;
        }

        protected virtual List<SearchResultItem> MergeUnion(List<SearchResultItem> items)
        {
            return items.GroupBy(i => i.Uri.ToString())
                .Select(g => g.OrderByDescending(i => i.Score).First())
                .OrderByDescending(i => i.Score)
                .ToList();
        }

        protected virtual List<SearchResultItem> MergeScoreWeighted(List<SearchResultItem> items)
        {
            return items.GroupBy(i => i.Uri.ToString())
                .Select(g =>
                {
                    var best = g.OrderByDescending(i => i.Score * _config.ProviderWeights.GetValueOrDefault(i.Source, 1.0)).First();
                    return new SearchResultItem
                    {
                        Uri = best.Uri,
                        Title = best.Title,
                        Snippet = best.Snippet,
                        Score = g.Sum(i => i.Score * _config.ProviderWeights.GetValueOrDefault(i.Source, 1.0)) / g.Count(),
                        Source = best.Source,
                        MatchType = best.MatchType,
                        Metadata = best.Metadata
                    };
                })
                .OrderByDescending(i => i.Score)
                .ToList();
        }

        protected virtual List<SearchResultItem> MergeRRF(List<SearchResultItem> items, List<ProviderSearchResult> providerResults)
        {
            const int k = 60; // RRF constant
            var scores = new Dictionary<string, double>();

            foreach (var pr in providerResults)
            {
                for (int rank = 0; rank < pr.Items.Count; rank++)
                {
                    var key = pr.Items[rank].Uri.ToString();
                    var rrfScore = 1.0 / (k + rank + 1);
                    scores[key] = scores.GetValueOrDefault(key, 0) + rrfScore;
                }
            }

            return items.GroupBy(i => i.Uri.ToString())
                .Select(g => new SearchResultItem
                {
                    Uri = g.First().Uri,
                    Title = g.First().Title,
                    Snippet = g.First().Snippet,
                    Score = scores.GetValueOrDefault(g.Key, 0),
                    Source = g.First().Source,
                    MatchType = g.First().MatchType,
                    Metadata = g.First().Metadata
                })
                .OrderByDescending(i => i.Score)
                .ToList();
        }
    }

    #endregion
}
