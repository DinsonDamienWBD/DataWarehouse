using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Licensing
{
    /// <summary>
    /// Exception thrown when a subscription is not found.
    /// </summary>
    public class SubscriptionNotFoundException : Exception
    {
        public string CustomerId { get; }

        public SubscriptionNotFoundException(string customerId)
            : base($"No subscription found for customer '{customerId}'. A valid subscription is required.")
        {
            CustomerId = customerId;
        }
    }

    /// <summary>
    /// Interface for subscription management and feature enforcement.
    /// </summary>
    public interface ISubscriptionManager
    {
        /// <summary>Gets the current subscription for a customer.</summary>
        Task<CustomerSubscription?> GetSubscriptionAsync(string customerId, CancellationToken ct = default);

        /// <summary>Checks if a feature is available for a customer.</summary>
        Task<bool> HasFeatureAsync(string customerId, Feature feature, CancellationToken ct = default);

        /// <summary>Validates that a feature is available, throwing if not.</summary>
        Task EnsureFeatureAsync(string customerId, Feature feature, CancellationToken ct = default);

        /// <summary>Gets the effective limits for a customer.</summary>
        Task<TierLimits> GetLimitsAsync(string customerId, CancellationToken ct = default);

        /// <summary>Records usage for a customer.</summary>
        Task RecordUsageAsync(string customerId, UsageRecord usage, CancellationToken ct = default);

        /// <summary>Checks if a customer is within their usage limits.</summary>
        Task<LimitCheckResult> CheckLimitsAsync(string customerId, UsageType usageType, long requestedAmount, CancellationToken ct = default);

        /// <summary>Gets current usage statistics for a customer.</summary>
        Task<UsageStatistics> GetUsageAsync(string customerId, CancellationToken ct = default);
    }

    /// <summary>
    /// Types of resource usage that are tracked.
    /// </summary>
    public enum UsageType
    {
        Storage,
        ApiRequests,
        AITokens,
        Containers,
        Users,
        ConcurrentOperations
    }

    /// <summary>
    /// Records a usage event.
    /// </summary>
    public sealed class UsageRecord
    {
        public UsageType Type { get; init; }
        public long Amount { get; init; }
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public string? ResourceId { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Result of checking usage limits.
    /// </summary>
    public sealed class LimitCheckResult
    {
        public bool IsAllowed { get; init; }
        public UsageType UsageType { get; init; }
        public long CurrentUsage { get; init; }
        public long RequestedAmount { get; init; }
        public long MaxAllowed { get; init; }
        public long Remaining => Math.Max(0, MaxAllowed - CurrentUsage);
        public string? Message { get; init; }
        public CustomerTier? UpgradeTier { get; init; }
    }

    /// <summary>
    /// Current usage statistics for a customer.
    /// </summary>
    public sealed class UsageStatistics
    {
        public string CustomerId { get; init; } = string.Empty;
        public CustomerTier Tier { get; init; }
        public DateTimeOffset PeriodStart { get; init; }
        public DateTimeOffset PeriodEnd { get; init; }

        public long StorageUsedBytes { get; init; }
        public long StorageLimitBytes { get; init; }
        public double StoragePercent => StorageLimitBytes > 0 ? (double)StorageUsedBytes / StorageLimitBytes * 100 : 0;

        public int ContainersUsed { get; init; }
        public int ContainersLimit { get; init; }

        public int UsersActive { get; init; }
        public int UsersLimit { get; init; }

        public long ApiRequestsToday { get; init; }
        public long ApiRequestsLimit { get; init; }

        public long AITokensToday { get; init; }
        public long AITokensLimit { get; init; }

        public int ConcurrentOperations { get; init; }
        public int ConcurrentLimit { get; init; }

        public Dictionary<string, long>? DetailedUsage { get; init; }
    }

    /// <summary>
    /// In-memory implementation of subscription management for development/testing.
    /// Production should use a database-backed implementation.
    /// Thread-safe implementation using proper synchronization.
    /// </summary>
    public sealed class InMemorySubscriptionManager : ISubscriptionManager
    {
        private readonly ConcurrentDictionary<string, CustomerSubscription> _subscriptions = new();
        private readonly ConcurrentDictionary<string, UsageStatistics> _usage = new();
        private readonly ConcurrentDictionary<string, long> _dailyApiRequests = new();
        private readonly ConcurrentDictionary<string, long> _dailyAITokens = new();
        private readonly ConcurrentDictionary<string, int> _concurrentOps = new();
        private long _lastDailyResetTicks = DateTimeOffset.UtcNow.Date.Ticks; // Use Interlocked for thread safety
        private readonly object _resetLock = new();

        /// <summary>
        /// Registers a customer subscription.
        /// </summary>
        public void RegisterSubscription(CustomerSubscription subscription)
        {
            _subscriptions[subscription.CustomerId] = subscription;
            _usage[subscription.CustomerId] = new UsageStatistics
            {
                CustomerId = subscription.CustomerId,
                Tier = subscription.Tier,
                PeriodStart = DateTimeOffset.UtcNow.Date,
                PeriodEnd = DateTimeOffset.UtcNow.Date.AddDays(1)
            };
        }

        /// <summary>
        /// Creates a default Individual tier subscription for new customers.
        /// </summary>
        public CustomerSubscription CreateDefaultSubscription(string customerId, string? organizationName = null)
        {
            var subscription = new CustomerSubscription
            {
                CustomerId = customerId,
                OrganizationName = organizationName,
                Tier = CustomerTier.Individual,
                SubscriptionStart = DateTimeOffset.UtcNow,
                IsActive = true
            };
            RegisterSubscription(subscription);
            return subscription;
        }

        public Task<CustomerSubscription?> GetSubscriptionAsync(string customerId, CancellationToken ct = default)
        {
            _subscriptions.TryGetValue(customerId, out var subscription);
            return Task.FromResult(subscription);
        }

        public async Task<bool> HasFeatureAsync(string customerId, Feature feature, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

            var subscription = await GetSubscriptionAsync(customerId, ct);
            if (subscription == null)
            {
                // Security: Unknown customers must register first - no default access
                throw new SubscriptionNotFoundException(customerId);
            }
            return subscription.HasFeature(feature);
        }

        public async Task EnsureFeatureAsync(string customerId, Feature feature, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

            var subscription = await GetSubscriptionAsync(customerId, ct);
            if (subscription == null)
            {
                throw new SubscriptionNotFoundException(customerId);
            }

            if (!TierManager.HasFeature(subscription.Tier, feature))
            {
                throw new FeatureNotAvailableException(feature, subscription.Tier);
            }
        }

        public async Task<TierLimits> GetLimitsAsync(string customerId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

            var subscription = await GetSubscriptionAsync(customerId, ct);
            if (subscription == null)
            {
                throw new SubscriptionNotFoundException(customerId);
            }
            return subscription.GetEffectiveLimits();
        }

        public Task RecordUsageAsync(string customerId, UsageRecord usage, CancellationToken ct = default)
        {
            ResetDailyCountersIfNeeded();

            switch (usage.Type)
            {
                case UsageType.ApiRequests:
                    _dailyApiRequests.AddOrUpdate(customerId, usage.Amount, (_, current) => current + usage.Amount);
                    break;
                case UsageType.AITokens:
                    _dailyAITokens.AddOrUpdate(customerId, usage.Amount, (_, current) => current + usage.Amount);
                    break;
                case UsageType.ConcurrentOperations:
                    if (usage.Amount > 0)
                        _concurrentOps.AddOrUpdate(customerId, 1, (_, current) => current + 1);
                    else
                        _concurrentOps.AddOrUpdate(customerId, 0, (_, current) => Math.Max(0, current - 1));
                    break;
            }

            return Task.CompletedTask;
        }

        public async Task<LimitCheckResult> CheckLimitsAsync(string customerId, UsageType usageType, long requestedAmount, CancellationToken ct = default)
        {
            ResetDailyCountersIfNeeded();

            var limits = await GetLimitsAsync(customerId, ct);
            var subscription = await GetSubscriptionAsync(customerId, ct);
            var tier = subscription?.Tier ?? CustomerTier.Individual;

            long current, max;
            switch (usageType)
            {
                case UsageType.ApiRequests:
                    _dailyApiRequests.TryGetValue(customerId, out current);
                    max = limits.MaxDailyApiRequests;
                    break;
                case UsageType.AITokens:
                    _dailyAITokens.TryGetValue(customerId, out current);
                    max = limits.MaxDailyAITokens;
                    break;
                case UsageType.ConcurrentOperations:
                    _concurrentOps.TryGetValue(customerId, out var concurrentCurrent);
                    current = concurrentCurrent;
                    max = limits.MaxConcurrentOperations;
                    break;
                case UsageType.Containers:
                    current = 0; // Would need to query actual container count
                    max = limits.MaxContainers;
                    break;
                case UsageType.Users:
                    current = 0; // Would need to query actual user count
                    max = limits.MaxUsers;
                    break;
                case UsageType.Storage:
                    current = 0; // Would need to query actual storage usage
                    max = limits.MaxStorageBytes;
                    break;
                default:
                    return new LimitCheckResult
                    {
                        IsAllowed = true,
                        UsageType = usageType,
                        CurrentUsage = 0,
                        RequestedAmount = requestedAmount,
                        MaxAllowed = long.MaxValue
                    };
            }

            var isAllowed = current + requestedAmount <= max;
            var upgradeTier = !isAllowed ? GetUpgradeTier(tier) : null;

            return new LimitCheckResult
            {
                IsAllowed = isAllowed,
                UsageType = usageType,
                CurrentUsage = current,
                RequestedAmount = requestedAmount,
                MaxAllowed = max,
                Message = isAllowed ? null : $"Daily {usageType} limit exceeded. Upgrade to {TierManager.GetTierName(upgradeTier ?? tier)} for higher limits.",
                UpgradeTier = upgradeTier
            };
        }

        public async Task<UsageStatistics> GetUsageAsync(string customerId, CancellationToken ct = default)
        {
            ResetDailyCountersIfNeeded();

            var subscription = await GetSubscriptionAsync(customerId, ct);
            var tier = subscription?.Tier ?? CustomerTier.Individual;
            var limits = TierManager.GetLimits(tier);

            _dailyApiRequests.TryGetValue(customerId, out var apiRequests);
            _dailyAITokens.TryGetValue(customerId, out var aiTokens);
            _concurrentOps.TryGetValue(customerId, out var concurrentOps);

            return new UsageStatistics
            {
                CustomerId = customerId,
                Tier = tier,
                PeriodStart = DateTimeOffset.UtcNow.Date,
                PeriodEnd = DateTimeOffset.UtcNow.Date.AddDays(1),
                StorageUsedBytes = 0, // Would need actual storage tracking
                StorageLimitBytes = limits.MaxStorageBytes,
                ContainersUsed = 0, // Would need actual container tracking
                ContainersLimit = limits.MaxContainers,
                UsersActive = 0, // Would need actual user tracking
                UsersLimit = limits.MaxUsers,
                ApiRequestsToday = apiRequests,
                ApiRequestsLimit = limits.MaxDailyApiRequests,
                AITokensToday = aiTokens,
                AITokensLimit = limits.MaxDailyAITokens,
                ConcurrentOperations = concurrentOps,
                ConcurrentLimit = limits.MaxConcurrentOperations
            };
        }

        private void ResetDailyCountersIfNeeded()
        {
            var todayTicks = DateTimeOffset.UtcNow.Date.Ticks;
            var lastResetTicks = Interlocked.Read(ref _lastDailyResetTicks);

            if (todayTicks > lastResetTicks)
            {
                lock (_resetLock)
                {
                    // Double-check after acquiring lock
                    lastResetTicks = Interlocked.Read(ref _lastDailyResetTicks);
                    if (todayTicks > lastResetTicks)
                    {
                        _dailyApiRequests.Clear();
                        _dailyAITokens.Clear();
                        Interlocked.Exchange(ref _lastDailyResetTicks, todayTicks);
                    }
                }
            }
        }

        private static CustomerTier? GetUpgradeTier(CustomerTier current)
        {
            return current switch
            {
                CustomerTier.Individual => CustomerTier.SMB,
                CustomerTier.SMB => CustomerTier.HighStakes,
                CustomerTier.HighStakes => CustomerTier.Hyperscale,
                CustomerTier.Hyperscale => null, // Already at max
                _ => null
            };
        }
    }

    /// <summary>
    /// Middleware for enforcing feature and limit requirements.
    /// </summary>
    public sealed class FeatureEnforcementMiddleware
    {
        private readonly ISubscriptionManager _subscriptionManager;

        public FeatureEnforcementMiddleware(ISubscriptionManager subscriptionManager)
        {
            _subscriptionManager = subscriptionManager;
        }

        /// <summary>
        /// Ensures a feature is available before executing an action.
        /// </summary>
        public async Task<T> ExecuteWithFeatureAsync<T>(
            string customerId,
            Feature requiredFeature,
            Func<Task<T>> action,
            CancellationToken ct = default)
        {
            await _subscriptionManager.EnsureFeatureAsync(customerId, requiredFeature, ct);
            return await action();
        }

        /// <summary>
        /// Executes an action while tracking API usage.
        /// </summary>
        public async Task<T> ExecuteWithApiTrackingAsync<T>(
            string customerId,
            Func<Task<T>> action,
            CancellationToken ct = default)
        {
            var check = await _subscriptionManager.CheckLimitsAsync(customerId, UsageType.ApiRequests, 1, ct);
            if (!check.IsAllowed)
            {
                var limits = await _subscriptionManager.GetLimitsAsync(customerId, ct);
                var subscription = await _subscriptionManager.GetSubscriptionAsync(customerId, ct);
                throw new LimitExceededException("DailyApiRequests", check.CurrentUsage, check.MaxAllowed, subscription?.Tier ?? CustomerTier.Individual);
            }

            await _subscriptionManager.RecordUsageAsync(customerId, new UsageRecord
            {
                Type = UsageType.ApiRequests,
                Amount = 1
            }, ct);

            return await action();
        }

        /// <summary>
        /// Executes an action while tracking AI token usage.
        /// </summary>
        public async Task<T> ExecuteWithAITrackingAsync<T>(
            string customerId,
            long estimatedTokens,
            Func<Task<(T Result, long ActualTokens)>> action,
            CancellationToken ct = default)
        {
            // Check if we can proceed
            var check = await _subscriptionManager.CheckLimitsAsync(customerId, UsageType.AITokens, estimatedTokens, ct);
            if (!check.IsAllowed)
            {
                var subscription = await _subscriptionManager.GetSubscriptionAsync(customerId, ct);
                throw new LimitExceededException("DailyAITokens", check.CurrentUsage, check.MaxAllowed, subscription?.Tier ?? CustomerTier.Individual);
            }

            // Execute and record actual usage
            var (result, actualTokens) = await action();

            await _subscriptionManager.RecordUsageAsync(customerId, new UsageRecord
            {
                Type = UsageType.AITokens,
                Amount = actualTokens
            }, ct);

            return result;
        }

        /// <summary>
        /// Tracks a concurrent operation (increment on start, decrement on end).
        /// </summary>
        public async Task<T> ExecuteWithConcurrencyTrackingAsync<T>(
            string customerId,
            Func<Task<T>> action,
            CancellationToken ct = default)
        {
            var check = await _subscriptionManager.CheckLimitsAsync(customerId, UsageType.ConcurrentOperations, 1, ct);
            if (!check.IsAllowed)
            {
                var subscription = await _subscriptionManager.GetSubscriptionAsync(customerId, ct);
                throw new LimitExceededException("ConcurrentOperations", check.CurrentUsage, check.MaxAllowed, subscription?.Tier ?? CustomerTier.Individual);
            }

            // Increment concurrent operation count
            await _subscriptionManager.RecordUsageAsync(customerId, new UsageRecord
            {
                Type = UsageType.ConcurrentOperations,
                Amount = 1
            }, ct);

            try
            {
                return await action();
            }
            finally
            {
                // Decrement concurrent operation count
                await _subscriptionManager.RecordUsageAsync(customerId, new UsageRecord
                {
                    Type = UsageType.ConcurrentOperations,
                    Amount = -1
                }, ct);
            }
        }
    }

    /// <summary>
    /// Extension methods for feature checking.
    /// </summary>
    public static class FeatureExtensions
    {
        /// <summary>
        /// Checks if all specified features are present.
        /// </summary>
        public static bool HasAll(this Feature features, params Feature[] required)
        {
            foreach (var feature in required)
            {
                if ((features & feature) != feature)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if any of the specified features are present.
        /// </summary>
        public static bool HasAny(this Feature features, params Feature[] required)
        {
            foreach (var feature in required)
            {
                if ((features & feature) == feature)
                    return true;
            }
            return false;
        }
    }
}
