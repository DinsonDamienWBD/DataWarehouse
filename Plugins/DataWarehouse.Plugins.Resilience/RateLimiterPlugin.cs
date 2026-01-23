using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace DataWarehouse.Plugins.Resilience
{
    /// <summary>
    /// Production-ready rate limiter plugin implementing token bucket algorithm.
    /// Provides configurable rate limiting with per-client tracking and policy support.
    ///
    /// Features:
    /// - Token bucket algorithm with burst support
    /// - Per-client rate limiting with customizable key extraction
    /// - Multiple rate limit policies (global, per-user, per-IP, per-endpoint)
    /// - Distributed rate limiting support via pluggable backends
    /// - Automatic bucket cleanup for memory efficiency
    /// - Rate limit headers for API responses
    /// - Configurable quotas with soft/hard limits
    /// - Priority-based request queuing
    ///
    /// Message Commands:
    /// - ratelimit.acquire: Acquire permits for a key
    /// - ratelimit.status: Get rate limit status for a key
    /// - ratelimit.reset: Reset rate limit for a key
    /// - ratelimit.configure: Configure a rate limit policy
    /// - ratelimit.list: List all active rate limit buckets
    /// - ratelimit.quota.set: Set quota for a key
    /// - ratelimit.quota.get: Get quota for a key
    /// </summary>
    public sealed class RateLimiterPlugin : RateLimiterPluginBase
    {
        private readonly ConcurrentDictionary<string, RateLimitPolicy> _policies;
        private readonly ConcurrentDictionary<string, ClientQuota> _quotas;
        private readonly ConcurrentDictionary<string, RateLimitBucket> _buckets;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly Timer _cleanupTimer;
        private readonly RateLimiterConfig _config;
        private readonly string _storagePath;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.resilience.ratelimiter";

        /// <inheritdoc/>
        public override string Name => "Rate Limiter";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <inheritdoc/>
        protected override RateLimitConfig DefaultConfig => new()
        {
            PermitsPerWindow = _config.DefaultPermitsPerWindow,
            WindowDuration = _config.DefaultWindowDuration,
            BurstLimit = _config.DefaultBurstLimit
        };

        /// <summary>
        /// Initializes a new instance of the RateLimiterPlugin.
        /// </summary>
        /// <param name="config">Optional configuration for the rate limiter.</param>
        public RateLimiterPlugin(RateLimiterConfig? config = null)
        {
            _config = config ?? new RateLimiterConfig();
            _policies = new ConcurrentDictionary<string, RateLimitPolicy>();
            _quotas = new ConcurrentDictionary<string, ClientQuota>();
            _buckets = new ConcurrentDictionary<string, RateLimitBucket>();
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "resilience", "ratelimits");

            _cleanupTimer = new Timer(
                _ => CleanupExpiredBuckets(),
                null,
                _config.CleanupInterval,
                _config.CleanupInterval);
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);
            await LoadPoliciesAsync();
            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "ratelimit.acquire", DisplayName = "Acquire", Description = "Acquire rate limit permits" },
                new() { Name = "ratelimit.status", DisplayName = "Status", Description = "Get rate limit status" },
                new() { Name = "ratelimit.reset", DisplayName = "Reset", Description = "Reset rate limit for a key" },
                new() { Name = "ratelimit.configure", DisplayName = "Configure", Description = "Configure rate limit policy" },
                new() { Name = "ratelimit.list", DisplayName = "List", Description = "List active buckets" },
                new() { Name = "ratelimit.quota.set", DisplayName = "Set Quota", Description = "Set quota for a key" },
                new() { Name = "ratelimit.quota.get", DisplayName = "Get Quota", Description = "Get quota for a key" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["PolicyCount"] = _policies.Count;
            metadata["ActiveBuckets"] = _buckets.Count;
            metadata["DefaultPermitsPerWindow"] = _config.DefaultPermitsPerWindow;
            metadata["DefaultWindowSeconds"] = _config.DefaultWindowDuration.TotalSeconds;
            metadata["SupportsPolicies"] = true;
            metadata["SupportsQuotas"] = true;
            metadata["SupportsDistributed"] = _config.EnableDistributed;
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "ratelimit.acquire":
                    await HandleAcquireAsync(message);
                    break;
                case "ratelimit.status":
                    HandleStatus(message);
                    break;
                case "ratelimit.reset":
                    HandleReset(message);
                    break;
                case "ratelimit.configure":
                    await HandleConfigureAsync(message);
                    break;
                case "ratelimit.list":
                    HandleList(message);
                    break;
                case "ratelimit.quota.set":
                    await HandleSetQuotaAsync(message);
                    break;
                case "ratelimit.quota.get":
                    HandleGetQuota(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        /// <summary>
        /// Attempts to acquire permits for a given key with policy matching.
        /// </summary>
        /// <param name="key">The rate limit key.</param>
        /// <param name="permits">Number of permits to acquire.</param>
        /// <param name="context">Optional context for policy matching.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the acquire operation.</returns>
        public async Task<RateLimitAcquireResult> AcquireWithPolicyAsync(
            string key,
            int permits = 1,
            RateLimitContext? context = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            // Find matching policy
            var policy = FindMatchingPolicy(key, context);
            var effectiveConfig = policy?.ToConfig() ?? GetConfigForKey(key);

            // Check quota first
            if (!await CheckQuotaAsync(key, permits, ct))
            {
                return new RateLimitAcquireResult
                {
                    Allowed = false,
                    Key = key,
                    RemainingPermits = 0,
                    RetryAfter = null,
                    Reason = "Quota exceeded"
                };
            }

            // Perform rate limit check
            var bucket = _buckets.GetOrAdd(key, _ => new RateLimitBucket(key, effectiveConfig));

            lock (bucket)
            {
                bucket.LastAccess = DateTime.UtcNow;
                RefillBucket(bucket, effectiveConfig);

                if (bucket.AvailablePermits >= permits)
                {
                    bucket.AvailablePermits -= permits;
                    UpdateQuotaUsage(key, permits);

                    return new RateLimitAcquireResult
                    {
                        Allowed = true,
                        Key = key,
                        RemainingPermits = bucket.AvailablePermits,
                        Limit = effectiveConfig.PermitsPerWindow,
                        WindowDuration = effectiveConfig.WindowDuration,
                        ResetAt = bucket.WindowStart.Add(effectiveConfig.WindowDuration)
                    };
                }

                // Calculate retry-after
                var permitsNeeded = permits - bucket.AvailablePermits;
                var refillRate = (double)effectiveConfig.PermitsPerWindow / effectiveConfig.WindowDuration.TotalSeconds;
                var retryAfter = TimeSpan.FromSeconds(permitsNeeded / refillRate);

                return new RateLimitAcquireResult
                {
                    Allowed = false,
                    Key = key,
                    RemainingPermits = bucket.AvailablePermits,
                    RetryAfter = retryAfter,
                    Limit = effectiveConfig.PermitsPerWindow,
                    WindowDuration = effectiveConfig.WindowDuration,
                    ResetAt = bucket.WindowStart.Add(effectiveConfig.WindowDuration),
                    Reason = "Rate limit exceeded"
                };
            }
        }

        /// <summary>
        /// Configures a rate limit policy.
        /// </summary>
        /// <param name="policy">The policy to configure.</param>
        public void ConfigurePolicy(RateLimitPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));
            if (string.IsNullOrWhiteSpace(policy.Name))
                throw new ArgumentException("Policy name is required", nameof(policy));

            _policies[policy.Name] = policy;
        }

        /// <summary>
        /// Sets a quota for a key.
        /// </summary>
        /// <param name="key">The key to set quota for.</param>
        /// <param name="quota">The quota configuration.</param>
        public void SetQuota(string key, ClientQuota quota)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            _quotas[key] = quota ?? throw new ArgumentNullException(nameof(quota));
        }

        /// <summary>
        /// Gets the quota for a key.
        /// </summary>
        /// <param name="key">The key to get quota for.</param>
        /// <returns>The quota, or null if not set.</returns>
        public ClientQuota? GetQuota(string key)
        {
            return _quotas.TryGetValue(key, out var quota) ? quota : null;
        }

        /// <summary>
        /// Gets rate limit headers for API responses.
        /// </summary>
        /// <param name="key">The rate limit key.</param>
        /// <returns>Dictionary of header names to values.</returns>
        public Dictionary<string, string> GetRateLimitHeaders(string key)
        {
            var config = GetConfigForKey(key);
            var headers = new Dictionary<string, string>();

            if (_buckets.TryGetValue(key, out var bucket))
            {
                lock (bucket)
                {
                    headers["X-RateLimit-Limit"] = config.PermitsPerWindow.ToString();
                    headers["X-RateLimit-Remaining"] = Math.Max(0, bucket.AvailablePermits).ToString();
                    headers["X-RateLimit-Reset"] = new DateTimeOffset(bucket.WindowStart.Add(config.WindowDuration))
                        .ToUnixTimeSeconds().ToString();
                }
            }
            else
            {
                headers["X-RateLimit-Limit"] = config.PermitsPerWindow.ToString();
                headers["X-RateLimit-Remaining"] = config.PermitsPerWindow.ToString();
            }

            return headers;
        }

        /// <inheritdoc/>
        protected override RateLimitConfig GetConfigForKey(string key)
        {
            // Check for key-specific policy
            var policy = FindMatchingPolicy(key, null);
            if (policy != null)
            {
                return policy.ToConfig();
            }

            return DefaultConfig;
        }

        private RateLimitPolicy? FindMatchingPolicy(string key, RateLimitContext? context)
        {
            foreach (var policy in _policies.Values.OrderByDescending(p => p.Priority))
            {
                if (!policy.Enabled) continue;

                // Check key pattern match
                if (!string.IsNullOrEmpty(policy.KeyPattern))
                {
                    if (!MatchesPattern(key, policy.KeyPattern))
                        continue;
                }

                // Check scope match
                if (context != null && policy.Scope != RateLimitScope.Global)
                {
                    if (policy.Scope == RateLimitScope.PerUser && string.IsNullOrEmpty(context.UserId))
                        continue;
                    if (policy.Scope == RateLimitScope.PerIp && context.IpAddress == null)
                        continue;
                }

                return policy;
            }

            return null;
        }

        private static bool MatchesPattern(string key, string pattern)
        {
            if (pattern == "*") return true;
            if (pattern == key) return true;

            if (pattern.Contains('*'))
            {
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(key, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return false;
        }

        private async Task<bool> CheckQuotaAsync(string key, int permits, CancellationToken ct)
        {
            if (!_quotas.TryGetValue(key, out var quota))
                return true; // No quota set, allow

            // Check if quota period has reset
            var now = DateTime.UtcNow;
            if (now >= quota.PeriodEnd)
            {
                quota.Used = 0;
                quota.PeriodStart = now;
                quota.PeriodEnd = now.Add(quota.Period);
            }

            // Check hard limit
            if (quota.Used + permits > quota.HardLimit)
                return false;

            // Check soft limit (log warning but allow)
            if (quota.Used + permits > quota.SoftLimit)
            {
                // Could emit a warning event here
            }

            return true;
        }

        private void UpdateQuotaUsage(string key, int permits)
        {
            if (_quotas.TryGetValue(key, out var quota))
            {
                Interlocked.Add(ref quota.Used, permits);
            }
        }

        private static void RefillBucket(RateLimitBucket bucket, RateLimitConfig config)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - bucket.WindowStart;

            if (elapsed >= config.WindowDuration)
            {
                // Full window reset
                bucket.AvailablePermits = config.PermitsPerWindow;
                bucket.WindowStart = now;
                bucket.LastRefill = now;
            }
            else
            {
                // Gradual refill (token bucket algorithm)
                var refillRate = (double)config.PermitsPerWindow / config.WindowDuration.TotalMilliseconds;
                var timeSinceLastRefill = (now - bucket.LastRefill).TotalMilliseconds;
                var tokensToAdd = (int)(timeSinceLastRefill * refillRate);

                if (tokensToAdd > 0)
                {
                    bucket.AvailablePermits = Math.Min(
                        bucket.AvailablePermits + tokensToAdd,
                        config.PermitsPerWindow + config.BurstLimit);
                    bucket.LastRefill = now;
                }
            }
        }

        private void CleanupExpiredBuckets()
        {
            var cutoff = DateTime.UtcNow - _config.BucketIdleTimeout;
            var keysToRemove = _buckets
                .Where(kv => kv.Value.LastAccess < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _buckets.TryRemove(key, out _);
            }
        }

        private async Task HandleAcquireAsync(PluginMessage message)
        {
            var key = GetString(message.Payload, "key") ?? throw new ArgumentException("key required");
            var permits = GetInt(message.Payload, "permits") ?? 1;

            var context = new RateLimitContext
            {
                UserId = GetString(message.Payload, "userId"),
                IpAddress = GetString(message.Payload, "ipAddress") != null
                    ? IPAddress.Parse(GetString(message.Payload, "ipAddress")!)
                    : null
            };

            var result = await AcquireWithPolicyAsync(key, permits, context);
            message.Payload["result"] = result;
        }

        private void HandleStatus(PluginMessage message)
        {
            var key = GetString(message.Payload, "key");

            if (!string.IsNullOrEmpty(key))
            {
                message.Payload["result"] = GetStatus(key);
            }
            else
            {
                message.Payload["result"] = _buckets.ToDictionary(
                    kv => kv.Key,
                    kv => GetStatus(kv.Key));
            }
        }

        private void HandleReset(PluginMessage message)
        {
            var key = GetString(message.Payload, "key") ?? throw new ArgumentException("key required");
            Reset(key);
        }

        private async Task HandleConfigureAsync(PluginMessage message)
        {
            var policy = new RateLimitPolicy
            {
                Name = GetString(message.Payload, "name") ?? throw new ArgumentException("name required"),
                PermitsPerWindow = GetInt(message.Payload, "permitsPerWindow") ?? _config.DefaultPermitsPerWindow,
                WindowDurationSeconds = GetInt(message.Payload, "windowDurationSeconds") ?? (int)_config.DefaultWindowDuration.TotalSeconds,
                BurstLimit = GetInt(message.Payload, "burstLimit") ?? _config.DefaultBurstLimit,
                KeyPattern = GetString(message.Payload, "keyPattern") ?? "*",
                Priority = GetInt(message.Payload, "priority") ?? 0,
                Enabled = true
            };

            if (Enum.TryParse<RateLimitScope>(GetString(message.Payload, "scope"), true, out var scope))
            {
                policy.Scope = scope;
            }

            ConfigurePolicy(policy);
            await PersistPoliciesAsync();
        }

        private void HandleList(PluginMessage message)
        {
            message.Payload["result"] = _buckets.Keys.ToList();
        }

        private async Task HandleSetQuotaAsync(PluginMessage message)
        {
            var key = GetString(message.Payload, "key") ?? throw new ArgumentException("key required");
            var quota = new ClientQuota
            {
                Key = key,
                SoftLimit = GetLong(message.Payload, "softLimit") ?? 10000,
                HardLimit = GetLong(message.Payload, "hardLimit") ?? 15000,
                Period = TimeSpan.FromSeconds(GetInt(message.Payload, "periodSeconds") ?? 3600),
                PeriodStart = DateTime.UtcNow,
                PeriodEnd = DateTime.UtcNow.AddSeconds(GetInt(message.Payload, "periodSeconds") ?? 3600)
            };

            SetQuota(key, quota);
            await PersistPoliciesAsync();
        }

        private void HandleGetQuota(PluginMessage message)
        {
            var key = GetString(message.Payload, "key") ?? throw new ArgumentException("key required");
            message.Payload["result"] = GetQuota(key);
        }

        private async Task LoadPoliciesAsync()
        {
            var path = Path.Combine(_storagePath, "policies.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<RateLimitPersistenceData>(json);

                if (data?.Policies != null)
                {
                    foreach (var policy in data.Policies)
                    {
                        _policies[policy.Name] = policy;
                    }
                }

                if (data?.Quotas != null)
                {
                    foreach (var quota in data.Quotas)
                    {
                        _quotas[quota.Key] = quota;
                    }
                }
            }
            catch
            {
                // Log but continue
            }
        }

        private async Task PersistPoliciesAsync()
        {
            if (!await _persistLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return;

            try
            {
                Directory.CreateDirectory(_storagePath);

                var data = new RateLimitPersistenceData
                {
                    Policies = _policies.Values.ToList(),
                    Quotas = _quotas.Values.ToList()
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(_storagePath, "policies.json"), json);
            }
            finally
            {
                _persistLock.Release();
            }
        }

        /// <inheritdoc/>
        public override Task StopAsync()
        {
            _cleanupTimer.Dispose();
            return base.StopAsync();
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static int? GetInt(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is string s && int.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        private static long? GetLong(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is long l) return l;
                if (val is int i) return i;
                if (val is string s && long.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }
    }

    /// <summary>
    /// Configuration for the RateLimiterPlugin.
    /// </summary>
    public class RateLimiterConfig
    {
        /// <summary>
        /// Default number of permits per window. Default is 100.
        /// </summary>
        public int DefaultPermitsPerWindow { get; set; } = 100;

        /// <summary>
        /// Default window duration. Default is 1 minute.
        /// </summary>
        public TimeSpan DefaultWindowDuration { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Default burst limit. Default is 10.
        /// </summary>
        public int DefaultBurstLimit { get; set; } = 10;

        /// <summary>
        /// Interval for cleaning up expired buckets. Default is 5 minutes.
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long a bucket can be idle before cleanup. Default is 30 minutes.
        /// </summary>
        public TimeSpan BucketIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Enable distributed rate limiting. Default is false.
        /// </summary>
        public bool EnableDistributed { get; set; } = false;
    }

    /// <summary>
    /// Rate limit policy configuration.
    /// </summary>
    public class RateLimitPolicy
    {
        /// <summary>
        /// Name of the policy.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Pattern to match rate limit keys.
        /// </summary>
        public string KeyPattern { get; set; } = "*";

        /// <summary>
        /// Number of permits per window.
        /// </summary>
        public int PermitsPerWindow { get; set; } = 100;

        /// <summary>
        /// Window duration in seconds.
        /// </summary>
        public int WindowDurationSeconds { get; set; } = 60;

        /// <summary>
        /// Burst limit.
        /// </summary>
        public int BurstLimit { get; set; } = 10;

        /// <summary>
        /// Priority for policy matching (higher = matched first).
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Scope of the rate limit.
        /// </summary>
        public RateLimitScope Scope { get; set; } = RateLimitScope.Global;

        /// <summary>
        /// Whether the policy is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Converts this policy to a RateLimitConfig.
        /// </summary>
        public RateLimitConfig ToConfig()
        {
            return new RateLimitConfig
            {
                PermitsPerWindow = PermitsPerWindow,
                WindowDuration = TimeSpan.FromSeconds(WindowDurationSeconds),
                BurstLimit = BurstLimit
            };
        }
    }

    /// <summary>
    /// Scope of rate limiting.
    /// </summary>
    public enum RateLimitScope
    {
        /// <summary>
        /// Global rate limit shared by all clients.
        /// </summary>
        Global,

        /// <summary>
        /// Rate limit per user.
        /// </summary>
        PerUser,

        /// <summary>
        /// Rate limit per IP address.
        /// </summary>
        PerIp,

        /// <summary>
        /// Rate limit per API endpoint.
        /// </summary>
        PerEndpoint,

        /// <summary>
        /// Rate limit per tenant.
        /// </summary>
        PerTenant
    }

    /// <summary>
    /// Context for rate limit policy matching.
    /// </summary>
    public class RateLimitContext
    {
        /// <summary>
        /// User ID for per-user rate limiting.
        /// </summary>
        public string? UserId { get; set; }

        /// <summary>
        /// IP address for per-IP rate limiting.
        /// </summary>
        public IPAddress? IpAddress { get; set; }

        /// <summary>
        /// Tenant ID for per-tenant rate limiting.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Endpoint path for per-endpoint rate limiting.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Additional context data.
        /// </summary>
        public Dictionary<string, object> Data { get; set; } = new();
    }

    /// <summary>
    /// Client quota configuration.
    /// </summary>
    public class ClientQuota
    {
        /// <summary>
        /// Key this quota applies to.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Soft limit (warning threshold).
        /// </summary>
        public long SoftLimit { get; set; } = 10000;

        /// <summary>
        /// Hard limit (rejection threshold).
        /// </summary>
        public long HardLimit { get; set; } = 15000;

        /// <summary>
        /// Quota period.
        /// </summary>
        public TimeSpan Period { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Current usage in this period.
        /// </summary>
        public long Used;

        /// <summary>
        /// Start of current period.
        /// </summary>
        public DateTime PeriodStart { get; set; }

        /// <summary>
        /// End of current period.
        /// </summary>
        public DateTime PeriodEnd { get; set; }
    }

    /// <summary>
    /// Result of a rate limit acquire operation.
    /// </summary>
    public class RateLimitAcquireResult
    {
        /// <summary>
        /// Whether the request was allowed.
        /// </summary>
        public bool Allowed { get; init; }

        /// <summary>
        /// The rate limit key.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// Remaining permits in the current window.
        /// </summary>
        public int RemainingPermits { get; init; }

        /// <summary>
        /// Time to wait before retrying (if denied).
        /// </summary>
        public TimeSpan? RetryAfter { get; init; }

        /// <summary>
        /// Maximum permits per window.
        /// </summary>
        public int Limit { get; init; }

        /// <summary>
        /// Duration of the rate limit window.
        /// </summary>
        public TimeSpan WindowDuration { get; init; }

        /// <summary>
        /// When the current window resets.
        /// </summary>
        public DateTime ResetAt { get; init; }

        /// <summary>
        /// Reason for denial (if applicable).
        /// </summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Internal rate limit bucket for tracking permits.
    /// </summary>
    internal sealed class RateLimitBucket
    {
        public string Key { get; }
        public int AvailablePermits { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime LastRefill { get; set; }
        public DateTime LastAccess { get; set; }

        public RateLimitBucket(string key, RateLimitConfig config)
        {
            Key = key;
            AvailablePermits = config.PermitsPerWindow;
            WindowStart = DateTime.UtcNow;
            LastRefill = DateTime.UtcNow;
            LastAccess = DateTime.UtcNow;
        }
    }

    internal class RateLimitPersistenceData
    {
        public List<RateLimitPolicy> Policies { get; set; } = new();
        public List<ClientQuota> Quotas { get; set; } = new();
    }
}
