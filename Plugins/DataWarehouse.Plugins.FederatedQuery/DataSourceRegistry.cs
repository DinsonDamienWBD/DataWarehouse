using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.FederatedQuery
{
    /// <summary>
    /// Registry tracking all available data sources in the DataWarehouse.
    /// Maps logical names to physical storage locations and provides unified access.
    /// </summary>
    public sealed class DataSourceRegistry
    {
        private readonly ConcurrentDictionary<string, DataSource> _sources;
        private readonly ConcurrentDictionary<string, IDataSourceAdapter> _adapters;
        private readonly ReaderWriterLockSlim _lock;

        public DataSourceRegistry()
        {
            _sources = new ConcurrentDictionary<string, DataSource>(StringComparer.OrdinalIgnoreCase);
            _adapters = new ConcurrentDictionary<string, IDataSourceAdapter>(StringComparer.OrdinalIgnoreCase);
            _lock = new ReaderWriterLockSlim();
        }

        /// <summary>
        /// Registers a new data source.
        /// </summary>
        public void RegisterSource(DataSource source, IDataSourceAdapter adapter)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (adapter == null)
                throw new ArgumentNullException(nameof(adapter));

            _lock.EnterWriteLock();
            try
            {
                _sources[source.Name] = source;
                _adapters[source.Name] = adapter;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Unregisters a data source.
        /// </summary>
        public bool UnregisterSource(string name)
        {
            _lock.EnterWriteLock();
            try
            {
                var removed = _sources.TryRemove(name, out _);
                if (removed)
                {
                    _adapters.TryRemove(name, out _);
                }
                return removed;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets all registered data sources.
        /// </summary>
        public IReadOnlyList<DataSource> GetAllSources()
        {
            _lock.EnterReadLock();
            try
            {
                return _sources.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets a specific data source by name.
        /// </summary>
        public DataSource? GetSource(string name)
        {
            return _sources.TryGetValue(name, out var source) ? source : null;
        }

        /// <summary>
        /// Gets the adapter for a data source.
        /// </summary>
        public IDataSourceAdapter? GetAdapter(string name)
        {
            return _adapters.TryGetValue(name, out var adapter) ? adapter : null;
        }

        /// <summary>
        /// Gets all sources of a specific type.
        /// </summary>
        public IReadOnlyList<DataSource> GetSourcesByType(DataSourceType type)
        {
            _lock.EnterReadLock();
            try
            {
                return _sources.Values
                    .Where(s => s.Type == type)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets all available (online) sources.
        /// </summary>
        public IReadOnlyList<DataSource> GetAvailableSources()
        {
            _lock.EnterReadLock();
            try
            {
                return _sources.Values
                    .Where(s => s.IsAvailable)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Checks if a source exists.
        /// </summary>
        public bool SourceExists(string name)
        {
            return _sources.ContainsKey(name);
        }

        /// <summary>
        /// Gets the total number of registered sources.
        /// </summary>
        public int Count => _sources.Count;

        /// <summary>
        /// Clears all registered sources.
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _sources.Clear();
                _adapters.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }

    /// <summary>
    /// Interface for data source adapters that execute queries against specific sources.
    /// </summary>
    public interface IDataSourceAdapter
    {
        /// <summary>
        /// Executes a query against this data source.
        /// </summary>
        Task<List<UnifiedResult>> QueryAsync(
            FederatedQuery query,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the schema for this data source.
        /// </summary>
        Task<DataSourceSchema> GetSchemaAsync(CancellationToken ct = default);

        /// <summary>
        /// Tests if this data source is available.
        /// </summary>
        Task<bool> TestConnectionAsync(CancellationToken ct = default);

        /// <summary>
        /// Estimates the cost of executing a query.
        /// </summary>
        double EstimateQueryCost(FederatedQuery query);
    }

    /// <summary>
    /// Base implementation for data source adapters.
    /// </summary>
    public abstract class DataSourceAdapterBase : IDataSourceAdapter
    {
        protected readonly string SourceName;

        protected DataSourceAdapterBase(string sourceName)
        {
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        }

        public abstract Task<List<UnifiedResult>> QueryAsync(
            FederatedQuery query,
            CancellationToken ct = default);

        public abstract Task<DataSourceSchema> GetSchemaAsync(CancellationToken ct = default);

        public virtual Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public virtual double EstimateQueryCost(FederatedQuery query)
        {
            // Basic cost estimation: 1.0 = simple query, higher for complex queries
            double cost = 1.0;

            if (!string.IsNullOrEmpty(query.QueryText))
                cost += 0.5;
            if (query.Filters.Count > 0)
                cost += 0.3 * query.Filters.Count;
            if (query.IncludeFileContent)
                cost += 2.0; // Content search is expensive

            return cost;
        }

        protected UnifiedResult CreateResult(
            string id,
            DataSourceType sourceType,
            string title,
            Dictionary<string, object>? metadata = null,
            Dictionary<string, object>? data = null)
        {
            return new UnifiedResult
            {
                Id = id,
                SourceType = sourceType,
                SourceName = SourceName,
                Score = 1.0,
                Title = title,
                Metadata = metadata ?? new Dictionary<string, object>(),
                Data = data
            };
        }
    }
}
