using DataWarehouse.Kernel;
using DataWarehouse.Kernel.Messaging;
using DataWarehouse.Kernel.Pipeline;
using DataWarehouse.Kernel.Storage;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration
{
    /// <summary>
    /// Integration tests that verify multiple components working together.
    /// These tests ensure the kernel, plugins, message bus, pipeline, and storage
    /// work correctly as a cohesive system.
    /// </summary>
    public class KernelIntegrationTests : IAsyncLifetime
    {
        private string _tempPath = null!;
        private DataWarehouseKernel _kernel = null!;

        public async Task InitializeAsync()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), $"integration-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempPath);

            _kernel = new KernelBuilder()
                .WithRootPath(_tempPath)
                .WithOperatingMode(OperatingMode.Laptop)
                .Build();

            await _kernel.InitializeAsync();
        }

        public async Task DisposeAsync()
        {
            if (_kernel != null)
            {
                await _kernel.DisposeAsync();
            }

            if (Directory.Exists(_tempPath))
            {
                try { Directory.Delete(_tempPath, recursive: true); }
                catch { /* Ignore cleanup errors */ }
            }
        }

        #region End-to-End Storage Flow

        [Fact]
        public async Task Storage_SaveAndLoad_ShouldPreserveData()
        {
            // Arrange
            var plugin = new InMemoryTestStoragePlugin();
            await _kernel.RegisterPluginAsync(plugin);

            var testData = "Hello, DataWarehouse!"u8.ToArray();
            var uri = new Uri("test://container/file.txt");

            // Act
            using (var inputStream = new MemoryStream(testData))
            {
                await plugin.SaveAsync(uri, inputStream);
            }

            using var outputStream = await plugin.LoadAsync(uri);
            using var ms = new MemoryStream();
            await outputStream.CopyToAsync(ms);
            var loadedData = ms.ToArray();

            // Assert
            loadedData.Should().BeEquivalentTo(testData);
        }

        [Fact]
        public async Task Storage_ExistsCheck_ShouldBeAccurate()
        {
            // Arrange
            var plugin = new InMemoryTestStoragePlugin();
            await _kernel.RegisterPluginAsync(plugin);

            var uri = new Uri("test://container/exists-check.txt");

            // Act & Assert - before save
            var existsBefore = await plugin.ExistsAsync(uri);
            existsBefore.Should().BeFalse();

            // Save data
            using (var stream = new MemoryStream("test"u8.ToArray()))
            {
                await plugin.SaveAsync(uri, stream);
            }

            // After save
            var existsAfter = await plugin.ExistsAsync(uri);
            existsAfter.Should().BeTrue();

            // After delete
            await plugin.DeleteAsync(uri);
            var existsAfterDelete = await plugin.ExistsAsync(uri);
            existsAfterDelete.Should().BeFalse();
        }

        #endregion

        #region Message Bus Integration

        [Fact]
        public async Task MessageBus_PluginCommunication_ShouldWork()
        {
            // Arrange
            var messageBus = _kernel.GetMessageBus();
            var receivedEvents = new List<string>();

            // Plugin A subscribes to storage events
            messageBus.Subscribe(MessageTopics.StorageSave, msg =>
            {
                receivedEvents.Add("StorageSave");
                return Task.CompletedTask;
            });

            messageBus.Subscribe(MessageTopics.StorageDelete, msg =>
            {
                receivedEvents.Add("StorageDelete");
                return Task.CompletedTask;
            });

            // Act - Simulate storage operations publishing events
            await messageBus.PublishAsync(MessageTopics.StorageSave, new PluginMessage
            {
                Type = "storage.save",
                Payload = new Dictionary<string, object> { ["Uri"] = "test://file.txt" }
            });

            await messageBus.PublishAsync(MessageTopics.StorageDelete, new PluginMessage
            {
                Type = "storage.delete",
                Payload = new Dictionary<string, object> { ["Uri"] = "test://file.txt" }
            });

            await Task.Delay(100);

            // Assert
            receivedEvents.Should().Contain("StorageSave");
            receivedEvents.Should().Contain("StorageDelete");
        }

        [Fact]
        public async Task AdvancedMessageBus_ReliableDelivery_ShouldRetryOnFailure()
        {
            // Arrange
            var context = new TestKernelContext(_tempPath);
            var config = new AdvancedMessageBusConfig
            {
                MaxRetries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(50)
            };
            var messageBus = new AdvancedMessageBus(context, config);

            var attemptCount = 0;

            messageBus.Subscribe("flaky.topic", msg =>
            {
                attemptCount++;
                if (attemptCount < 2)
                {
                    throw new Exception("Simulated failure");
                }
                return Task.CompletedTask;
            });

            // Act
            var result = await messageBus.PublishWithConfirmationAsync("flaky.topic", new PluginMessage
            {
                Type = "test"
            });

            // Assert
            result.Success.Should().BeTrue();
            attemptCount.Should().BeGreaterOrEqualTo(1);
        }

        #endregion

        #region Circuit Breaker Integration

        [Fact]
        public async Task CircuitBreaker_WithFailingOperation_ShouldOpenCircuit()
        {
            // Arrange
            var config = new ResiliencePolicyConfig
            {
                FailureThreshold = 3,
                FailureWindow = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                MaxRetries = 0
            };

            var circuitBreaker = new CircuitBreakerPolicy("test-cb", config);
            var failureCount = 0;

            // Act - Force failures to open circuit
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await circuitBreaker.ExecuteAsync(async ct =>
                    {
                        failureCount++;
                        throw new Exception("Simulated failure");
                    });
                }
                catch (Exception ex) when (ex is not CircuitBreakerOpenException)
                {
                    // Expected failures
                }
                catch (CircuitBreakerOpenException)
                {
                    // Circuit is now open
                    break;
                }
            }

            // Assert
            circuitBreaker.State.Should().Be(CircuitState.Open);
            failureCount.Should().BeLessOrEqualTo(config.FailureThreshold + 1);
        }

        [Fact]
        public async Task CircuitBreaker_AfterRecovery_ShouldClose()
        {
            // Arrange
            var config = new ResiliencePolicyConfig
            {
                FailureThreshold = 2,
                BreakDuration = TimeSpan.FromMilliseconds(100),
                MaxRetries = 0
            };

            var circuitBreaker = new CircuitBreakerPolicy("recovery-test", config);
            var shouldFail = true;

            // Force circuit open
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await circuitBreaker.ExecuteAsync(async ct =>
                    {
                        if (shouldFail) throw new Exception("Fail");
                        return 1;
                    });
                }
                catch { }
            }

            // Wait for half-open
            await Task.Delay(150);

            // Enable success
            shouldFail = false;

            // Act - successful execution should close circuit
            var result = await circuitBreaker.ExecuteAsync(async ct => 42);

            // Assert
            result.Should().Be(42);
            circuitBreaker.State.Should().Be(CircuitState.Closed);
        }

        #endregion

        #region AI Rate Limiting Integration

        [Fact]
        public async Task AIRateLimiter_ShouldEnforceRequestLimits()
        {
            // Arrange
            var config = new AIRateLimitConfig
            {
                CompletionRequestsPerMinute = 3,
                MaxConcurrentRequests = 2,
                QueueExcessRequests = false
            };

            var mockProvider = new MockAIProvider();
            var rateLimitedProvider = new RateLimitedAIProvider(mockProvider, config);

            // Act
            var successCount = 0;
            var rateLimitedCount = 0;
            var tasks = new List<Task>();

            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await rateLimitedProvider.CompleteAsync(new AIRequest { Prompt = "test" });
                        Interlocked.Increment(ref successCount);
                    }
                    catch (AIRateLimitExceededException)
                    {
                        Interlocked.Increment(ref rateLimitedCount);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Assert
            successCount.Should().BeLessOrEqualTo(config.MaxConcurrentRequests);
            rateLimitedCount.Should().BeGreaterThan(0);

            // Check stats
            var stats = rateLimitedProvider.GetStats();
            stats.TotalRequests.Should().Be(5);
            stats.RejectedRequests.Should().BeGreaterThan(0);

            rateLimitedProvider.Dispose();
        }

        #endregion

        #region Pipeline Integration

        [Fact]
        public async Task Pipeline_WithMultipleStages_ShouldExecuteInOrder()
        {
            // Arrange
            var context = new TestKernelContext(_tempPath);
            var messageBus = new DefaultMessageBus(context);
            var orchestrator = new PipelineOrchestrator(context, messageBus);

            var executionOrder = new List<string>();

            var stage1 = new TestPipelineStage("Stage1", s =>
            {
                executionOrder.Add("Stage1");
                return s;
            });

            var stage2 = new TestPipelineStage("Stage2", s =>
            {
                executionOrder.Add("Stage2");
                return s;
            });

            orchestrator.RegisterStage(stage1);
            orchestrator.RegisterStage(stage2);

            var config = new PipelineConfiguration
            {
                WriteStages = new List<PipelineStageConfig>
                {
                    new() { StageType = "Stage1", Order = 100, Enabled = true, PluginId = "stage1" },
                    new() { StageType = "Stage2", Order = 200, Enabled = true, PluginId = "stage2" }
                }
            };
            orchestrator.SetConfiguration(config);

            // Act
            using var input = new MemoryStream("test data"u8.ToArray());
            var pipelineContext = new PipelineContext();
            var output = await orchestrator.ExecuteWritePipelineAsync(input, pipelineContext);

            // Assert
            executionOrder.Should().ContainInOrder("Stage1", "Stage2");
            pipelineContext.ExecutedStages.Should().ContainInOrder("Stage1", "Stage2");
        }

        #endregion

        #region Test Helpers

        private class InMemoryTestStoragePlugin : StorageProviderPluginBase
        {
            private readonly Dictionary<string, byte[]> _storage = new();

            public override string Id => "inmemory-test";
            public override string Name => "In-Memory Test Storage";
            public override string Scheme => "test";

            public override Task SaveAsync(Uri uri, Stream data)
            {
                using var ms = new MemoryStream();
                data.CopyTo(ms);
                _storage[uri.ToString()] = ms.ToArray();
                return Task.CompletedTask;
            }

            public override Task<Stream> LoadAsync(Uri uri)
            {
                if (_storage.TryGetValue(uri.ToString(), out var data))
                {
                    return Task.FromResult<Stream>(new MemoryStream(data));
                }
                throw new FileNotFoundException();
            }

            public override Task DeleteAsync(Uri uri)
            {
                _storage.Remove(uri.ToString());
                return Task.CompletedTask;
            }

            public override Task<bool> ExistsAsync(Uri uri)
            {
                return Task.FromResult(_storage.ContainsKey(uri.ToString()));
            }
        }

        private class TestKernelContext : IKernelContext
        {
            public string RootPath { get; }
            public OperatingMode Mode => OperatingMode.Laptop;
            public IKernelStorageService Storage => throw new NotImplementedException();

            public TestKernelContext(string rootPath)
            {
                RootPath = rootPath;
            }

            public void LogDebug(string message) { }
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message, Exception? ex = null) { }
            public T? GetPlugin<T>() where T : class, IPlugin => null;
            public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => Enumerable.Empty<T>();
        }

        private class MockAIProvider : IAIProvider
        {
            public string ProviderId => "mock-ai";
            public string DisplayName => "Mock AI Provider";
            public bool IsAvailable => true;
            public AICapabilities Capabilities => AICapabilities.TextCompletion | AICapabilities.Embeddings;

            public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
            {
                await Task.Delay(50, ct); // Simulate API latency
                return new AIResponse
                {
                    Text = "Mock response",
                    TokensUsed = 10
                };
            }

            public async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new AIStreamChunk { Content = "Mock", IsFinal = false };
                await Task.Delay(10, ct);
                yield return new AIStreamChunk { Content = " response", IsFinal = true };
            }

            public Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
            {
                return Task.FromResult(new float[] { 0.1f, 0.2f, 0.3f });
            }

            public Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
            {
                return Task.FromResult(texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToArray());
            }
        }

        private class TestPipelineStage : DataTransformationPluginBase
        {
            private readonly Func<Stream, Stream> _transform;

            public override string Id { get; }
            public override string Name { get; }
            public override string SubCategory => "Test";

            public TestPipelineStage(string id, Func<Stream, Stream> transform)
            {
                Id = id;
                Name = id;
                _transform = transform;
            }

            public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
            {
                return _transform(input);
            }

            public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
            {
                return stored;
            }
        }

        #endregion
    }
}
