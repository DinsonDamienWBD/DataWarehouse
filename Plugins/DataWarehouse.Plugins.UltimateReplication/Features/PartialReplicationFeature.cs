using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Partial Replication Feature (C5).
    /// Enables selective replication of data subsets based on configurable
    /// filters, tags, and inclusion/exclusion rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filter types:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Tag-based</b>: Include/exclude data by tags (e.g., "region:us-east", "tier:hot")</item>
    ///   <item><b>Pattern-based</b>: Include/exclude by key/path pattern (regex)</item>
    ///   <item><b>Size-based</b>: Filter by data size range</item>
    ///   <item><b>Age-based</b>: Filter by data age (replicate only recent data)</item>
    ///   <item><b>Combined</b>: Logical AND/OR of multiple filter rules</item>
    /// </list>
    /// </remarks>
    public sealed class PartialReplicationFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, ReplicationFilter> _filters = new BoundedDictionary<string, ReplicationFilter>(1000);
        private readonly BoundedDictionary<string, FilterSubscription> _subscriptions = new BoundedDictionary<string, FilterSubscription>(1000);
        private bool _disposed;
        private IDisposable? _messageBusSubscription;

        // Topics
        private const string FilterEvaluateTopic = "replication.ultimate.filter.evaluate";
        private const string FilterResultTopic = "replication.ultimate.filter.result";

        // Statistics
        private long _totalEvaluated;
        private long _totalIncluded;
        private long _totalExcluded;

        /// <summary>
        /// Initializes a new instance of the PartialReplicationFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for communication.</param>
        public PartialReplicationFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));

            _messageBusSubscription = _messageBus.Subscribe(FilterEvaluateTopic, HandleFilterEvaluateAsync);
        }

        /// <summary>Gets total items evaluated.</summary>
        public long TotalEvaluated => Interlocked.Read(ref _totalEvaluated);

        /// <summary>Gets total items included for replication.</summary>
        public long TotalIncluded => Interlocked.Read(ref _totalIncluded);

        /// <summary>Gets total items excluded from replication.</summary>
        public long TotalExcluded => Interlocked.Read(ref _totalExcluded);

        /// <summary>
        /// Creates a replication filter with the given rules.
        /// </summary>
        /// <param name="filterId">Unique filter identifier.</param>
        /// <param name="rules">Filter rules to apply.</param>
        /// <param name="combineMode">How to combine multiple rules (And/Or).</param>
        /// <returns>The created filter.</returns>
        public ReplicationFilter CreateFilter(
            string filterId,
            IReadOnlyList<FilterRule> rules,
            FilterCombineMode combineMode = FilterCombineMode.And)
        {
            var filter = new ReplicationFilter
            {
                FilterId = filterId,
                Rules = rules.ToList(),
                CombineMode = combineMode,
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = true
            };

            _filters[filterId] = filter;
            return filter;
        }

        /// <summary>
        /// Creates a subscription binding a filter to a target node.
        /// Only data matching the filter is replicated to that node.
        /// </summary>
        /// <param name="subscriptionId">Unique subscription identifier.</param>
        /// <param name="filterId">Filter to apply.</param>
        /// <param name="targetNode">Target node that receives filtered data.</param>
        /// <returns>The created subscription.</returns>
        public FilterSubscription CreateSubscription(string subscriptionId, string filterId, string targetNode)
        {
            if (!_filters.ContainsKey(filterId))
                throw new ArgumentException($"Filter '{filterId}' not found");

            var subscription = new FilterSubscription
            {
                SubscriptionId = subscriptionId,
                FilterId = filterId,
                TargetNode = targetNode,
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = true
            };

            _subscriptions[subscriptionId] = subscription;
            return subscription;
        }

        /// <summary>
        /// Evaluates whether a data item should be replicated to a target node.
        /// </summary>
        /// <param name="targetNode">The target node.</param>
        /// <param name="dataKey">Data identifier/key.</param>
        /// <param name="tags">Tags associated with the data.</param>
        /// <param name="dataSizeBytes">Size of the data in bytes.</param>
        /// <param name="dataAge">Age of the data.</param>
        /// <returns>True if the data should be replicated to this target.</returns>
        public bool ShouldReplicate(
            string targetNode,
            string dataKey,
            IReadOnlySet<string>? tags = null,
            long dataSizeBytes = 0,
            TimeSpan? dataAge = null)
        {
            Interlocked.Increment(ref _totalEvaluated);

            var activeSubscriptions = _subscriptions.Values
                .Where(s => s.IsActive && s.TargetNode == targetNode)
                .ToList();

            // If no subscriptions for this target, default to replicate everything
            if (activeSubscriptions.Count == 0)
            {
                Interlocked.Increment(ref _totalIncluded);
                return true;
            }

            // Any matching subscription means include
            foreach (var sub in activeSubscriptions)
            {
                if (_filters.TryGetValue(sub.FilterId, out var filter) && filter.IsActive)
                {
                    if (EvaluateFilter(filter, dataKey, tags, dataSizeBytes, dataAge))
                    {
                        Interlocked.Increment(ref _totalIncluded);
                        return true;
                    }
                }
            }

            Interlocked.Increment(ref _totalExcluded);
            return false;
        }

        /// <summary>
        /// Gets all registered filters.
        /// </summary>
        public IReadOnlyDictionary<string, ReplicationFilter> GetFilters()
        {
            return _filters;
        }

        /// <summary>
        /// Gets all active subscriptions.
        /// </summary>
        public IReadOnlyDictionary<string, FilterSubscription> GetSubscriptions()
        {
            return _subscriptions;
        }

        /// <summary>
        /// Removes a filter by ID.
        /// </summary>
        public bool RemoveFilter(string filterId)
        {
            return _filters.TryRemove(filterId, out _);
        }

        #region Private Methods

        private bool EvaluateFilter(
            ReplicationFilter filter,
            string dataKey,
            IReadOnlySet<string>? tags,
            long dataSizeBytes,
            TimeSpan? dataAge)
        {
            var results = filter.Rules.Select(rule => EvaluateRule(rule, dataKey, tags, dataSizeBytes, dataAge));

            return filter.CombineMode == FilterCombineMode.And
                ? results.All(r => r)
                : results.Any(r => r);
        }

        private static bool EvaluateRule(
            FilterRule rule,
            string dataKey,
            IReadOnlySet<string>? tags,
            long dataSizeBytes,
            TimeSpan? dataAge)
        {
            var match = rule.Type switch
            {
                FilterRuleType.TagInclude => tags != null && rule.Values.Any(v => tags.Contains(v)),
                FilterRuleType.TagExclude => tags == null || !rule.Values.Any(v => tags.Contains(v)),
                FilterRuleType.KeyPattern => !string.IsNullOrEmpty(rule.Pattern) && Regex.IsMatch(dataKey, rule.Pattern),
                FilterRuleType.KeyPatternExclude => string.IsNullOrEmpty(rule.Pattern) || !Regex.IsMatch(dataKey, rule.Pattern),
                FilterRuleType.MinSize => dataSizeBytes >= rule.SizeThreshold,
                FilterRuleType.MaxSize => dataSizeBytes <= rule.SizeThreshold,
                FilterRuleType.MaxAge => dataAge.HasValue && dataAge.Value <= rule.AgeThreshold,
                FilterRuleType.MinAge => dataAge.HasValue && dataAge.Value >= rule.AgeThreshold,
                _ => true
            };

            return rule.Negate ? !match : match;
        }

        private async Task HandleFilterEvaluateAsync(PluginMessage message)
        {
            var targetNode = message.Payload.GetValueOrDefault("targetNode")?.ToString() ?? "";
            var dataKey = message.Payload.GetValueOrDefault("dataKey")?.ToString() ?? "";
            var tagsArray = message.Payload.GetValueOrDefault("tags") as IEnumerable<object>;
            var dataSizeStr = message.Payload.GetValueOrDefault("dataSize")?.ToString();

            var tags = tagsArray != null
                ? new HashSet<string>(tagsArray.Select(t => t.ToString()!))
                : null;

            var result = ShouldReplicate(
                targetNode,
                dataKey,
                tags,
                long.TryParse(dataSizeStr, out var ds) ? ds : 0);

            await _messageBus.PublishAsync(FilterResultTopic, new PluginMessage
            {
                Type = FilterResultTopic,
                CorrelationId = message.CorrelationId,
                Source = "replication.ultimate.filter",
                Payload = new Dictionary<string, object>
                {
                    ["shouldReplicate"] = result,
                    ["targetNode"] = targetNode,
                    ["dataKey"] = dataKey
                }
            });
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _messageBusSubscription?.Dispose();
        }
    }

    #region Filter Types

    /// <summary>
    /// How to combine filter rules.
    /// </summary>
    public enum FilterCombineMode
    {
        /// <summary>All rules must match (logical AND).</summary>
        And,
        /// <summary>Any rule must match (logical OR).</summary>
        Or
    }

    /// <summary>
    /// Type of filter rule.
    /// </summary>
    public enum FilterRuleType
    {
        /// <summary>Include data with matching tags.</summary>
        TagInclude,
        /// <summary>Exclude data with matching tags.</summary>
        TagExclude,
        /// <summary>Include data matching key pattern (regex).</summary>
        KeyPattern,
        /// <summary>Exclude data matching key pattern (regex).</summary>
        KeyPatternExclude,
        /// <summary>Include data above minimum size.</summary>
        MinSize,
        /// <summary>Include data below maximum size.</summary>
        MaxSize,
        /// <summary>Include data newer than maximum age.</summary>
        MaxAge,
        /// <summary>Include data older than minimum age.</summary>
        MinAge
    }

    /// <summary>
    /// A single filter rule.
    /// </summary>
    public sealed class FilterRule
    {
        /// <summary>Rule type.</summary>
        public required FilterRuleType Type { get; init; }
        /// <summary>Tag values (for tag-based rules).</summary>
        public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
        /// <summary>Regex pattern (for pattern-based rules).</summary>
        public string? Pattern { get; init; }
        /// <summary>Size threshold in bytes (for size-based rules).</summary>
        public long SizeThreshold { get; init; }
        /// <summary>Age threshold (for age-based rules).</summary>
        public TimeSpan AgeThreshold { get; init; }
        /// <summary>Negate the rule result.</summary>
        public bool Negate { get; init; }
    }

    /// <summary>
    /// Replication filter configuration.
    /// </summary>
    public sealed class ReplicationFilter
    {
        /// <summary>Filter identifier.</summary>
        public required string FilterId { get; init; }
        /// <summary>Filter rules.</summary>
        public required List<FilterRule> Rules { get; init; }
        /// <summary>How to combine rules.</summary>
        public required FilterCombineMode CombineMode { get; init; }
        /// <summary>When the filter was created.</summary>
        public required DateTimeOffset CreatedAt { get; init; }
        /// <summary>Whether the filter is active.</summary>
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Subscription binding a filter to a target node.
    /// </summary>
    public sealed class FilterSubscription
    {
        /// <summary>Subscription identifier.</summary>
        public required string SubscriptionId { get; init; }
        /// <summary>Filter to apply.</summary>
        public required string FilterId { get; init; }
        /// <summary>Target node.</summary>
        public required string TargetNode { get; init; }
        /// <summary>When the subscription was created.</summary>
        public required DateTimeOffset CreatedAt { get; init; }
        /// <summary>Whether the subscription is active.</summary>
        public bool IsActive { get; set; }
    }

    #endregion
}
