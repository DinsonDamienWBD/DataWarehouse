using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts
{
    #region Storage Pool Base

    /// <summary>
    /// Abstract base class for storage pools. Provides common infrastructure
    /// for managing multiple storage providers with pluggable strategies.
    /// </summary>
    public abstract class StoragePoolBase : Hierarchy.InfrastructurePluginBase, IStoragePool
    {
        protected readonly BoundedDictionary<string, (IStorageProvider Provider, StorageRole Role)> _providers = new BoundedDictionary<string, (IStorageProvider Provider, StorageRole Role)>(1000);
        protected IStorageStrategy _strategy;

        /// <summary>
        /// Per-URI locks to prevent concurrent writes to the same resource.
        /// Uses SemaphoreSlim for async-safe locking.
        /// </summary>
        private readonly BoundedDictionary<string, SemaphoreSlim> _uriLocks = new BoundedDictionary<string, SemaphoreSlim>(1000);

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

    #region Storage Orchestration Strategy Base

    /// <summary>
    /// Abstract base class for storage orchestration strategies that plan write/read
    /// distribution across multiple storage providers (e.g., Simple, Mirrored, WAL).
    /// Distinct from <see cref="Storage.StorageStrategyBase"/> which handles individual
    /// storage backend operations (Store, Retrieve, Delete).
    /// </summary>
    public abstract class StorageOrchestrationStrategyBase : StrategyBase, IStorageStrategy
    {
        public override abstract string StrategyId { get; }
        public override abstract string Name { get; }
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
    public class SimpleStrategy : StorageOrchestrationStrategyBase
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
    public class MirroredStrategy : StorageOrchestrationStrategyBase
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
    /// <summary>Write-ahead log strategy for durability.</summary>
    public class WriteAheadLogStrategy : StorageOrchestrationStrategyBase
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

    #endregion
}
