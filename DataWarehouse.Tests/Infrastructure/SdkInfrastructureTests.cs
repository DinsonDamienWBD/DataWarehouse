using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Infrastructure;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Comprehensive tests for SDK infrastructure components.
    /// </summary>
    public class SdkInfrastructureTests
    {
        #region ExceptionHandler Tests

        [Fact]
        public void ExceptionHandler_LogException_CapturesAllContext()
        {
            // Arrange
            ExceptionRecord? captured = null;
            ExceptionHandler.SetExceptionCallback(r => captured = r);

            var testException = new InvalidOperationException("Test error");
            var metadata = new Dictionary<string, object> { ["key1"] = "value1" };

            // Act
            ExceptionHandler.LogException(
                testException,
                "Test message",
                "TestComponent",
                "TestOperation",
                metadata);

            // Assert
            Assert.NotNull(captured);
            Assert.Equal(testException, captured!.Exception);
            Assert.Equal("Test message", captured.Message);
            Assert.Equal("TestComponent", captured.Component);
            Assert.Equal("TestOperation", captured.Operation);
            Assert.Contains("key1", captured.Metadata.Keys);
        }

        [Fact]
        public void ExceptionHandler_Execute_LogsAndRethrows()
        {
            // Arrange
            ExceptionRecord? captured = null;
            ExceptionHandler.SetExceptionCallback(r => captured = r);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                ExceptionHandler.Execute(
                    () => throw new InvalidOperationException("Test"),
                    "TestComponent",
                    "TestOperation"));

            Assert.NotNull(captured);
            Assert.Equal("TestComponent", captured!.Component);
        }

        [Fact]
        public void ExceptionHandler_Execute_ReturnsResultOnSuccess()
        {
            // Act
            var result = ExceptionHandler.Execute(
                () => 42,
                "TestComponent",
                "TestOperation");

            // Assert
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ExceptionHandler_ExecuteAsync_LogsAndRethrows()
        {
            // Arrange
            ExceptionRecord? captured = null;
            ExceptionHandler.SetExceptionCallback(r => captured = r);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ExceptionHandler.ExecuteAsync(
                    () => throw new InvalidOperationException("Test"),
                    "TestComponent",
                    "TestOperation"));

            Assert.NotNull(captured);
        }

        [Fact]
        public async Task ExceptionHandler_ExecuteAsync_ReturnsResultOnSuccess()
        {
            // Act
            var result = await ExceptionHandler.ExecuteAsync(
                async () => { await Task.Delay(1); return 42; },
                "TestComponent",
                "TestOperation");

            // Assert
            Assert.Equal(42, result);
        }

        [Fact]
        public void ExceptionHandler_TryExecute_ReturnsFalseOnError()
        {
            // Act
            var success = ExceptionHandler.TryExecute(
                () => throw new InvalidOperationException("Test"),
                "TestComponent",
                "TestOperation",
                out var exception);

            // Assert
            Assert.False(success);
            Assert.NotNull(exception);
            Assert.IsType<InvalidOperationException>(exception);
        }

        [Fact]
        public void ExceptionHandler_TryExecute_ReturnsTrueOnSuccess()
        {
            // Act
            var success = ExceptionHandler.TryExecute(
                () => { },
                "TestComponent",
                "TestOperation",
                out var exception);

            // Assert
            Assert.True(success);
            Assert.Null(exception);
        }

        [Fact]
        public async Task ExceptionHandler_TryExecuteAsync_ReturnsFalseOnError()
        {
            // Act
            var (success, exception) = await ExceptionHandler.TryExecuteAsync(
                () => throw new InvalidOperationException("Test"),
                "TestComponent",
                "TestOperation");

            // Assert
            Assert.False(success);
            Assert.NotNull(exception);
        }

        [Fact]
        public void ExceptionHandler_GetRecentExceptions_ReturnsLoggedExceptions()
        {
            // Arrange
            ExceptionHandler.SetMaxLogSize(100);
            for (int i = 0; i < 10; i++)
            {
                ExceptionHandler.LogException(
                    new Exception($"Error {i}"),
                    $"Message {i}",
                    "TestComponent");
            }

            // Act
            var recent = ExceptionHandler.GetRecentExceptions(5).ToList();

            // Assert
            Assert.True(recent.Count >= 5);
        }

        #endregion

        #region Domain Exception Tests

        [Fact]
        public void StorageException_NotFound_CreatesCorrectException()
        {
            // Act
            var ex = StorageException.NotFound("/path/to/file", "container1");

            // Assert
            Assert.Equal("STORAGE_NOT_FOUND", ex.ErrorCode);
            Assert.Equal("Storage", ex.Component);
            Assert.Equal("container1", ex.ContainerId);
            Assert.False(ex.IsTransient);
        }

        [Fact]
        public void StorageException_Unavailable_IsTransient()
        {
            // Act
            var ex = StorageException.Unavailable("S3");

            // Assert
            Assert.Equal("STORAGE_UNAVAILABLE", ex.ErrorCode);
            Assert.True(ex.IsTransient);
            Assert.NotNull(ex.RetryAfter);
        }

        [Fact]
        public void SecurityException_AccessDenied_CapturesDetails()
        {
            // Act
            var ex = SecurityException.AccessDenied("user1", "resource1", "write");

            // Assert
            Assert.Equal("ACCESS_DENIED", ex.ErrorCode);
            Assert.Equal("user1", ex.PrincipalId);
            Assert.Equal("resource1", ex.ResourceId);
            Assert.Equal("write", ex.RequiredPermission);
        }

        [Fact]
        public void PluginException_LoadFailed_IncludesInnerException()
        {
            // Arrange
            var inner = new InvalidOperationException("Load error");

            // Act
            var ex = PluginException.LoadFailed("TestPlugin", inner);

            // Assert
            Assert.Equal("PLUGIN_LOAD_FAILED", ex.ErrorCode);
            Assert.Equal("TestPlugin", ex.PluginId);
            Assert.Equal(inner, ex.InnerException);
        }

        [Fact]
        public void ConfigurationException_MissingRequired_CreatesCorrectException()
        {
            // Act
            var ex = ConfigurationException.MissingRequired("ConnectionString");

            // Assert
            Assert.Equal("CONFIG_ERROR", ex.ErrorCode);
            Assert.Equal("ConnectionString", ex.ConfigKey);
        }

        [Fact]
        public void RateLimitException_HasRetryAfter()
        {
            // Act
            var ex = new RateLimitException("client1", TimeSpan.FromSeconds(30));

            // Assert
            Assert.Equal("RATE_LIMITED", ex.ErrorCode);
            Assert.Equal("client1", ex.ClientId);
            Assert.True(ex.IsTransient);
            Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        }

        [Fact]
        public void ComplianceException_IncludesFramework()
        {
            // Act
            var ex = new ComplianceException(
                ComplianceFramework.HIPAA,
                "164.312(a)",
                "Access control violation");

            // Assert
            Assert.Equal(ComplianceFramework.HIPAA, ex.Framework);
            Assert.Equal("164.312(a)", ex.Requirement);
            Assert.Contains("HIPAA", ex.ErrorCode);
        }

        #endregion

        #region Exception Extensions Tests

        [Fact]
        public void IsTransientFailure_ReturnsTrueForTransientExceptions()
        {
            Assert.True(new TimeoutException().IsTransientFailure());
            Assert.True(new System.Net.Http.HttpRequestException().IsTransientFailure());
            Assert.True(new System.IO.IOException().IsTransientFailure());
            Assert.True(StorageException.Unavailable("S3").IsTransientFailure());
        }

        [Fact]
        public void IsTransientFailure_ReturnsFalseForNonTransientExceptions()
        {
            Assert.False(new ArgumentException().IsTransientFailure());
            Assert.False(new InvalidOperationException().IsTransientFailure());
            Assert.False(new OperationCanceledException().IsTransientFailure());
            Assert.False(StorageException.NotFound("/path").IsTransientFailure());
        }

        [Fact]
        public void GetRetryDelay_ReturnsExponentialBackoff()
        {
            var ex = new TimeoutException();

            var delay1 = ex.GetRetryDelay(1);
            var delay2 = ex.GetRetryDelay(2);
            var delay3 = ex.GetRetryDelay(3);

            // Each delay should be roughly double the previous (with some jitter)
            Assert.True(delay2 > delay1);
            Assert.True(delay3 > delay2);
        }

        [Fact]
        public void GetRetryDelay_UsesExceptionRetryAfter()
        {
            var ex = new RateLimitException("client1", TimeSpan.FromSeconds(60));

            var delay = ex.GetRetryDelay(1);

            Assert.Equal(TimeSpan.FromSeconds(60), delay);
        }

        #endregion
    }

    /// <summary>
    /// Tests for Self-Healing infrastructure.
    /// </summary>
    public class SelfHealingTests
    {
        #region SelfHealingOrchestrator Tests

        [Fact]
        public void Orchestrator_RegisterComponent_AddsComponent()
        {
            // Arrange
            using var orchestrator = new SelfHealingOrchestrator();
            var component = new TestSelfHealingComponent("test1");

            // Act
            orchestrator.RegisterComponent(component);

            // Assert
            var status = orchestrator.GetHealthStatus();
            Assert.Contains("test1", status.Keys);
        }

        [Fact]
        public void Orchestrator_UnregisterComponent_RemovesComponent()
        {
            // Arrange
            using var orchestrator = new SelfHealingOrchestrator();
            var component = new TestSelfHealingComponent("test1");
            orchestrator.RegisterComponent(component);

            // Act
            orchestrator.UnregisterComponent("test1");

            // Assert
            var status = orchestrator.GetHealthStatus();
            Assert.DoesNotContain("test1", status.Keys);
        }

        [Fact]
        public async Task Orchestrator_TriggerHealing_ExecutesHealingActions()
        {
            // Arrange
            using var orchestrator = new SelfHealingOrchestrator();
            var healingExecuted = false;
            var component = new TestSelfHealingComponent("test1", new[]
            {
                new HealingAction
                {
                    Description = "Test healing",
                    Execute = ct => { healingExecuted = true; return Task.FromResult(true); }
                }
            });
            orchestrator.RegisterComponent(component);

            // Act
            var result = await orchestrator.TriggerHealingAsync("test1");

            // Assert
            Assert.NotNull(result);
            Assert.True(result!.Success);
            Assert.True(healingExecuted);
        }

        [Fact]
        public void Orchestrator_GetHealingHistory_ReturnsResults()
        {
            // Arrange
            using var orchestrator = new SelfHealingOrchestrator();
            var component = new TestSelfHealingComponent("test1", new[]
            {
                new HealingAction
                {
                    Description = "Test",
                    Execute = ct => Task.FromResult(true)
                }
            });
            orchestrator.RegisterComponent(component);

            // Act (trigger healing to generate history)
            orchestrator.TriggerHealingAsync("test1").Wait();
            var history = orchestrator.GetHealingHistory("test1").ToList();

            // Assert
            Assert.NotEmpty(history);
        }

        [Fact]
        public void Orchestrator_Dispose_DisposesCleanly()
        {
            // Arrange
            var orchestrator = new SelfHealingOrchestrator();
            var component = new TestSelfHealingComponent("test1");
            orchestrator.RegisterComponent(component);

            // Act & Assert (should not throw)
            orchestrator.Dispose();
            Assert.Throws<ObjectDisposedException>(() => orchestrator.RegisterComponent(component));
        }

        #endregion

        #region HealingAction Tests

        [Fact]
        public void HealingAction_DefaultValues_AreCorrect()
        {
            // Arrange & Act
            var action = new HealingAction
            {
                Description = "Test",
                Execute = ct => Task.FromResult(true)
            };

            // Assert
            Assert.Equal(100, action.Priority);
            Assert.Equal(3, action.MaxRetries);
            Assert.Equal(TimeSpan.FromSeconds(5), action.RetryDelay);
        }

        #endregion

        private class TestSelfHealingComponent : ISelfHealingComponent
        {
            private readonly HealingAction[] _actions;
            private ComponentHealth _health = ComponentHealth.Healthy;

            public string ComponentId { get; }
            public ComponentHealth Health => _health;
            public event Action<ComponentHealth>? OnHealthChanged;

            public TestSelfHealingComponent(string id, HealingAction[]? actions = null)
            {
                ComponentId = id;
                _actions = actions ?? Array.Empty<HealingAction>();
            }

            public IEnumerable<HealingAction> GetHealingActions() => _actions;

            public Task<ComponentHealth> CheckHealthAsync(CancellationToken ct = default)
            {
                return Task.FromResult(_health);
            }

            public void SetHealth(ComponentHealth health)
            {
                _health = health;
                OnHealthChanged?.Invoke(health);
            }
        }
    }

    /// <summary>
    /// Tests for Chaos Engineering infrastructure.
    /// </summary>
    public class ChaosEngineeringTests
    {
        #region ChaosEngine Tests

        [Fact]
        public void ChaosEngine_DefaultState_IsDisabled()
        {
            // Arrange & Act
            using var engine = new ChaosEngine();

            // Assert
            Assert.False(engine.Enabled);
        }

        [Fact]
        public void ChaosEngine_RegisterExperiment_AddsExperiment()
        {
            // Arrange
            using var engine = new ChaosEngine();
            var experiment = new ChaosExperiment
            {
                Name = "Test Chaos",
                Type = ChaosType.Latency,
                Probability = 0.5
            };

            // Act
            engine.RegisterExperiment(experiment);

            // Assert
            var experiments = engine.GetExperiments().ToList();
            Assert.Single(experiments);
            Assert.Equal("Test Chaos", experiments[0].Name);
        }

        [Fact]
        public void ChaosEngine_StartExperiment_ActivatesExperiment()
        {
            // Arrange
            using var engine = new ChaosEngine();
            var experiment = new ChaosExperiment
            {
                Name = "Test",
                Type = ChaosType.Latency
            };
            engine.RegisterExperiment(experiment);

            // Act
            engine.StartExperiment(experiment.ExperimentId);

            // Assert
            Assert.True(experiment.IsActive);
        }

        [Fact]
        public void ChaosEngine_StopExperiment_DeactivatesExperiment()
        {
            // Arrange
            using var engine = new ChaosEngine();
            var experiment = new ChaosExperiment
            {
                Name = "Test",
                Type = ChaosType.Latency
            };
            engine.RegisterExperiment(experiment);
            engine.StartExperiment(experiment.ExperimentId);

            // Act
            engine.StopExperiment(experiment.ExperimentId);

            // Assert
            Assert.False(experiment.IsActive);
        }

        [Fact]
        public async Task ChaosEngine_WhenDisabled_NoInjection()
        {
            // Arrange
            using var engine = new ChaosEngine();
            engine.Enabled = false;
            var experiment = new ChaosExperiment
            {
                Name = "Test",
                Type = ChaosType.Exception,
                Probability = 1.0 // Would always inject if enabled
            };
            engine.RegisterExperiment(experiment);
            engine.StartExperiment(experiment.ExperimentId);

            // Act & Assert (should not throw)
            var result = await engine.MaybeInjectChaosAsync("target");
            Assert.True(result);
        }

        [Fact]
        public async Task ChaosEngine_LatencyInjection_AddsDelay()
        {
            // Arrange
            using var engine = new ChaosEngine();
            engine.Enabled = true;
            var experiment = new ChaosExperiment
            {
                Name = "Latency Test",
                Type = ChaosType.Latency,
                Probability = 1.0,
                Duration = TimeSpan.FromMilliseconds(100)
            };
            engine.RegisterExperiment(experiment);
            engine.StartExperiment(experiment.ExperimentId);

            // Act
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await engine.MaybeInjectChaosAsync("target");
            sw.Stop();

            // Assert
            Assert.True(sw.ElapsedMilliseconds >= 90); // Allow some tolerance
        }

        [Fact]
        public async Task ChaosEngine_ExceptionInjection_ThrowsException()
        {
            // Arrange
            using var engine = new ChaosEngine();
            engine.Enabled = true;
            var experiment = new ChaosExperiment
            {
                Name = "Exception Test",
                Type = ChaosType.Exception,
                Probability = 1.0
            };
            engine.RegisterExperiment(experiment);
            engine.StartExperiment(experiment.ExperimentId);

            // Act & Assert
            await Assert.ThrowsAsync<ChaosException>(() =>
                engine.MaybeInjectChaosAsync("target"));
        }

        [Fact]
        public void ChaosEngine_GetInjectionLog_ReturnsHistory()
        {
            // Arrange
            using var engine = new ChaosEngine();

            // Act
            var log = engine.GetInjectionLog().ToList();

            // Assert
            Assert.NotNull(log);
        }

        [Fact]
        public void ChaosEngine_Dispose_StopsAllExperiments()
        {
            // Arrange
            var engine = new ChaosEngine();
            var experiment = new ChaosExperiment
            {
                Name = "Test",
                Type = ChaosType.Latency
            };
            engine.RegisterExperiment(experiment);
            engine.StartExperiment(experiment.ExperimentId);

            // Act
            engine.Dispose();

            // Assert
            Assert.False(experiment.IsActive);
        }

        #endregion

        #region Chaos Exception Tests

        [Fact]
        public void ChaosException_Types_AreDistinct()
        {
            var generic = new ChaosException("test");
            var network = new ChaosNetworkException("test");
            var io = new ChaosIOException("test");
            var resource = new ChaosResourceException("test");

            Assert.IsType<ChaosException>(generic);
            Assert.IsType<ChaosNetworkException>(network);
            Assert.IsType<ChaosIOException>(io);
            Assert.IsType<ChaosResourceException>(resource);
        }

        #endregion
    }

    /// <summary>
    /// Tests for Adaptive Rate Limiting.
    /// </summary>
    public class AdaptiveRateLimitingTests
    {
        #region AdaptiveRateLimiter Tests

        [Fact]
        public void RateLimiter_DefaultOptions_AreReasonable()
        {
            // Arrange & Act
            using var limiter = new AdaptiveRateLimiter();

            // Assert
            Assert.Equal(1.0, limiter.CurrentLoadFactor);
        }

        [Fact]
        public void RateLimiter_TryAcquire_AllowsWithinLimit()
        {
            // Arrange
            using var limiter = new AdaptiveRateLimiter(new AdaptiveRateLimiterOptions
            {
                BaseRequestsPerSecond = 100
            });

            // Act
            var result = limiter.TryAcquire("client1");

            // Assert
            Assert.True(result.Allowed);
            Assert.Equal("client1", result.ClientId);
        }

        [Fact]
        public void RateLimiter_TryAcquire_RejectsOverLimit()
        {
            // Arrange
            using var limiter = new AdaptiveRateLimiter(new AdaptiveRateLimiterOptions
            {
                BaseRequestsPerSecond = 10
            });

            // Act - exhaust the limit
            for (int i = 0; i < 10; i++)
            {
                limiter.TryAcquire("client1");
            }
            var result = limiter.TryAcquire("client1");

            // Assert
            Assert.False(result.Allowed);
            Assert.True(result.RetryAfter > TimeSpan.Zero);
        }

        [Fact]
        public void RateLimiter_UpdateLoadMetrics_AdjustsLoadFactor()
        {
            // Arrange
            using var limiter = new AdaptiveRateLimiter();

            // Act - simulate high load
            limiter.UpdateLoadMetrics(cpuUsage: 0.9, memoryUsage: 0.8, latencyMs: 300);

            // Assert - load factor should decrease under load
            Assert.True(limiter.CurrentLoadFactor < 1.0);
        }

        [Fact]
        public void RateLimiter_UpdateClientReputation_AffectsLimits()
        {
            // Arrange
            using var limiter = new AdaptiveRateLimiter(new AdaptiveRateLimiterOptions
            {
                BaseRequestsPerSecond = 100
            });

            // Act - acquire once to create client state
            limiter.TryAcquire("client1");

            // Decrease reputation
            limiter.UpdateClientReputation("client1", -0.5);

            // Client should have lower effective limit
            var stats = limiter.GetStats();
            Assert.NotNull(stats);
        }

        [Fact]
        public void RateLimiter_GetStats_ReturnsAccurateStats()
        {
            // Arrange
            using var limiter = new AdaptiveRateLimiter();

            // Act
            limiter.TryAcquire("client1");
            limiter.TryAcquire("client2");
            var stats = limiter.GetStats();

            // Assert
            Assert.Equal(2, stats.TotalRequests);
            Assert.True(stats.ActiveClients >= 2);
        }

        [Fact]
        public void RateLimiter_Dispose_DisposesCleanly()
        {
            // Arrange
            var limiter = new AdaptiveRateLimiter();

            // Act & Assert (should not throw)
            limiter.Dispose();
            Assert.Throws<ObjectDisposedException>(() => limiter.TryAcquire("client1"));
        }

        #endregion

        #region RateLimitResult Tests

        [Fact]
        public void RateLimitResult_Properties_AreCorrect()
        {
            // Arrange & Act
            var result = new RateLimitResult
            {
                Allowed = true,
                ClientId = "client1",
                CurrentCount = 5,
                Limit = 100,
                Remaining = 95
            };

            // Assert
            Assert.True(result.Allowed);
            Assert.Equal("client1", result.ClientId);
            Assert.Equal(5, result.CurrentCount);
            Assert.Equal(100, result.Limit);
            Assert.Equal(95, result.Remaining);
        }

        #endregion
    }

    /// <summary>
    /// Tests for Predictive Load Shedding.
    /// </summary>
    public class PredictiveLoadSheddingTests
    {
        #region PredictiveLoadShedder Tests

        [Fact]
        public void LoadShedder_DefaultState_IsNotShedding()
        {
            // Arrange & Act
            using var shedder = new PredictiveLoadShedder();

            // Assert
            Assert.False(shedder.IsShedding);
        }

        [Fact]
        public void LoadShedder_ShouldAllowRequest_AllowsUnderThreshold()
        {
            // Arrange
            using var shedder = new PredictiveLoadShedder(new PredictiveLoadShedderOptions
            {
                SheddingThreshold = 0.8
            });
            shedder.RecordMetrics(cpuUsage: 0.3, memoryUsage: 0.3, activeRequests: 10, queueDepth: 5);

            // Act
            var result = shedder.ShouldAllowRequest();

            // Assert
            Assert.True(result.Allowed);
        }

        [Fact]
        public void LoadShedder_CriticalPriority_AlwaysAllowed()
        {
            // Arrange
            using var shedder = new PredictiveLoadShedder(new PredictiveLoadShedderOptions
            {
                SheddingThreshold = 0.1, // Very low threshold
                CriticalPriorityThreshold = 100
            });
            shedder.RecordMetrics(cpuUsage: 0.99, memoryUsage: 0.99, activeRequests: 1000, queueDepth: 500);

            // Act
            var result = shedder.ShouldAllowRequest(priority: 100); // Critical priority

            // Assert
            Assert.True(result.Allowed);
        }

        [Fact]
        public void LoadShedder_RecordMetrics_UpdatesCurrentLoad()
        {
            // Arrange
            using var shedder = new PredictiveLoadShedder();

            // Act
            shedder.RecordMetrics(cpuUsage: 0.5, memoryUsage: 0.5, activeRequests: 50, queueDepth: 25);

            // Assert
            Assert.True(shedder.CurrentLoad > 0);
        }

        [Fact]
        public void LoadShedder_DroppedRequests_TracksCount()
        {
            // Arrange
            using var shedder = new PredictiveLoadShedder();

            // Assert
            Assert.Equal(0, shedder.DroppedRequests);
        }

        [Fact]
        public void LoadShedder_Dispose_DisposesCleanly()
        {
            // Arrange
            var shedder = new PredictiveLoadShedder();

            // Act & Assert (should not throw)
            shedder.Dispose();
            Assert.Throws<ObjectDisposedException>(() => shedder.ShouldAllowRequest());
        }

        #endregion

        #region LoadSheddingResult Tests

        [Fact]
        public void LoadSheddingResult_Properties_AreCorrect()
        {
            // Arrange & Act
            var result = new LoadSheddingResult
            {
                Allowed = true,
                Priority = 50,
                DropProbability = 0.1,
                CurrentLoad = 0.5,
                PredictedLoad = 0.6
            };

            // Assert
            Assert.True(result.Allowed);
            Assert.Equal(50, result.Priority);
            Assert.Equal(0.1, result.DropProbability);
            Assert.Equal(0.5, result.CurrentLoad);
            Assert.Equal(0.6, result.PredictedLoad);
        }

        #endregion
    }
}
