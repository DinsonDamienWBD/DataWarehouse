using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement
{
    public class KeyRotationScheduler : IDisposable
    {
        private readonly ConcurrentDictionary<string, IKeyStoreStrategy> _strategies;
        private readonly ConcurrentDictionary<string, KeyRotationPolicy> _policies;
        private readonly ConcurrentDictionary<string, DateTime> _lastRotationTimes;
        private readonly IMessageBus? _messageBus;
        private readonly UltimateKeyManagementConfig _config;
        private readonly CancellationTokenSource _shutdownCts = new();
        private Task? _schedulerTask;
        private bool _disposed;

        public KeyRotationScheduler(UltimateKeyManagementConfig config, IMessageBus? messageBus = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _messageBus = messageBus;
            _strategies = new ConcurrentDictionary<string, IKeyStoreStrategy>();
            _policies = new ConcurrentDictionary<string, KeyRotationPolicy>();
            _lastRotationTimes = new ConcurrentDictionary<string, DateTime>();
        }

        public void RegisterStrategy(string strategyId, IKeyStoreStrategy strategy, KeyRotationPolicy? policy = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            ArgumentNullException.ThrowIfNull(strategy);

            _strategies[strategyId] = strategy;
            var rotationPolicy = policy ?? _config.DefaultRotationPolicy;
            _policies[strategyId] = rotationPolicy;
            _lastRotationTimes.TryAdd(strategyId, DateTime.UtcNow);
        }

        public bool UnregisterStrategy(string strategyId)
        {
            var removed = _strategies.TryRemove(strategyId, out _);
            _policies.TryRemove(strategyId, out _);
            _lastRotationTimes.TryRemove(strategyId, out _);
            return removed;
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KeyRotationScheduler));

            if (_schedulerTask != null)
                return;

            _schedulerTask = Task.Run(async () => await RunSchedulerLoopAsync());
        }

        public async Task StopAsync(TimeSpan? timeout = null)
        {
            if (_schedulerTask == null)
                return;

            _shutdownCts.Cancel();

            var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
            var completedTask = await Task.WhenAny(_schedulerTask, Task.Delay(actualTimeout));
        }

        private async Task RunSchedulerLoopAsync()
        {
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAndRotateKeysAsync(_shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await PublishErrorEventAsync("Rotation evaluation failed", ex);
                }

                try
                {
                    await Task.Delay(_config.RotationCheckInterval, _shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task EvaluateAndRotateKeysAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            foreach (var (strategyId, strategy) in _strategies)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (!_policies.TryGetValue(strategyId, out var policy))
                    continue;

                if (!policy.Enabled)
                    continue;

                if (!_lastRotationTimes.TryGetValue(strategyId, out var lastRotation))
                    lastRotation = DateTime.MinValue;

                var timeSinceRotation = now - lastRotation;

                bool rotationDue = timeSinceRotation >= policy.RotationInterval;
                bool maxAgeExceeded = policy.MaxKeyAge.HasValue && timeSinceRotation >= policy.MaxKeyAge.Value;

                if (rotationDue || maxAgeExceeded)
                {
                    await RotateStrategyKeysAsync(strategyId, strategy, policy, ct);
                }
            }
        }

        private async Task RotateStrategyKeysAsync(
            string strategyId,
            IKeyStoreStrategy strategy,
            KeyRotationPolicy policy,
            CancellationToken ct)
        {
            var retryPolicy = policy.RetryPolicy;
            var attempt = 0;

            while (attempt <= retryPolicy.MaxRetries)
            {
                try
                {
                    await PerformRotationAsync(strategyId, strategy, policy, ct);

                    _lastRotationTimes[strategyId] = DateTime.UtcNow;

                    if (policy.NotifyOnRotation)
                    {
                        await PublishRotationEventAsync(strategyId, success: true);
                    }

                    return;
                }
                catch (Exception ex)
                {
                    attempt++;

                    if (attempt > retryPolicy.MaxRetries)
                    {
                        await PublishRotationEventAsync(strategyId, success: false, error: ex.Message);
                        await PublishErrorEventAsync($"Key rotation failed for strategy '{strategyId}' after {retryPolicy.MaxRetries} retries", ex);
                        return;
                    }

                    var delay = CalculateBackoffDelay(attempt, retryPolicy);

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private async Task PerformRotationAsync(
            string strategyId,
            IKeyStoreStrategy strategy,
            KeyRotationPolicy policy,
            CancellationToken ct)
        {
            var keysToRotate = await DetermineKeysToRotateAsync(strategy, policy, ct);

            if (keysToRotate.Count == 0)
                return;

            var systemContext = CreateSystemContext();

            foreach (var keyId in keysToRotate)
            {
                if (ct.IsCancellationRequested)
                    break;

                await strategy.CreateKeyAsync(keyId, systemContext);
            }
        }

        private async Task<List<string>> DetermineKeysToRotateAsync(
            IKeyStoreStrategy strategy,
            KeyRotationPolicy policy,
            CancellationToken ct)
        {
            var keysToRotate = new List<string>();

            if (policy.TargetKeyIds.Count > 0)
            {
                keysToRotate.AddRange(policy.TargetKeyIds);
            }
            else
            {
                try
                {
                    var systemContext = CreateSystemContext();
                    var allKeys = await strategy.ListKeysAsync(systemContext, ct);

                    foreach (var keyId in allKeys)
                    {
                        var metadata = await strategy.GetKeyMetadataAsync(keyId, systemContext, ct);

                        if (metadata == null)
                            continue;

                        var keyAge = DateTime.UtcNow - metadata.CreatedAt;
                        var lastRotationAge = metadata.LastRotatedAt.HasValue
                            ? DateTime.UtcNow - metadata.LastRotatedAt.Value
                            : keyAge;

                        bool rotationDue = lastRotationAge >= policy.RotationInterval;
                        bool maxAgeExceeded = policy.MaxKeyAge.HasValue && keyAge >= policy.MaxKeyAge.Value;

                        if (rotationDue || maxAgeExceeded)
                        {
                            keysToRotate.Add(keyId);
                        }
                    }
                }
                catch
                {
                    var currentKeyId = await strategy.GetCurrentKeyIdAsync();
                    keysToRotate.Add(currentKeyId);
                }
            }

            return keysToRotate;
        }

        private TimeSpan CalculateBackoffDelay(int attempt, RotationRetryPolicy retryPolicy)
        {
            var delay = retryPolicy.InitialDelay.TotalMilliseconds * Math.Pow(retryPolicy.BackoffMultiplier, attempt - 1);
            var cappedDelay = Math.Min(delay, retryPolicy.MaxDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(cappedDelay);
        }

        private ISecurityContext CreateSystemContext()
        {
            return new SystemSecurityContext();
        }

        private async Task PublishRotationEventAsync(string strategyId, bool success, string? error = null)
        {
            if (_messageBus == null || !_config.PublishKeyEvents)
                return;

            try
            {
                var message = new PluginMessage
                {
                    Type = "keymanagement.rotation",
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyId"] = strategyId,
                        ["success"] = success,
                        ["timestamp"] = DateTime.UtcNow,
                        ["error"] = error ?? ""
                    }
                };

                await _messageBus.PublishAsync("keymanagement.rotation", message);
            }
            catch
            {
            }
        }

        private async Task PublishErrorEventAsync(string errorMessage, Exception ex)
        {
            if (_messageBus == null || !_config.PublishKeyEvents)
                return;

            try
            {
                var message = new PluginMessage
                {
                    Type = "keymanagement.error",
                    Payload = new Dictionary<string, object>
                    {
                        ["message"] = errorMessage,
                        ["exception"] = ex.GetType().Name,
                        ["exceptionMessage"] = ex.Message,
                        ["timestamp"] = DateTime.UtcNow
                    }
                };

                await _messageBus.PublishAsync("keymanagement.error", message);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            StopAsync().GetAwaiter().GetResult();

            _shutdownCts.Dispose();

            GC.SuppressFinalize(this);
        }

        private sealed class SystemSecurityContext : ISecurityContext
        {
            public string UserId => "SYSTEM_KEY_ROTATION";
            public string? TenantId => null;
            public IEnumerable<string> Roles => new[] { "SYSTEM", "KEY_ROTATION" };
            public bool IsSystemAdmin => true;
        }
    }
}
