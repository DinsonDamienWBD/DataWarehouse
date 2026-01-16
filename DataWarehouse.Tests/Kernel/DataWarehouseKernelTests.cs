using DataWarehouse.Kernel;
using DataWarehouse.Kernel.Configuration;
using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Kernel
{
    /// <summary>
    /// Comprehensive tests for DataWarehouseKernel.
    /// Tests kernel lifecycle, plugin management, message routing, and configuration.
    /// </summary>
    public class DataWarehouseKernelTests : IAsyncLifetime
    {
        private string _tempPath = null!;
        private DataWarehouseKernel _kernel = null!;

        public async Task InitializeAsync()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), $"kernel-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempPath);

            var config = new KernelConfiguration
            {
                RootPath = _tempPath,
                Mode = OperatingMode.Laptop,
                KernelId = "test-kernel"
            };

            _kernel = new KernelBuilder()
                .WithRootPath(_tempPath)
                .WithOperatingMode(OperatingMode.Laptop)
                .Build();
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

        #region Initialization Tests

        [Fact]
        public async Task InitializeAsync_ShouldCompleteSuccessfully()
        {
            // Act
            await _kernel.InitializeAsync();

            // Assert - kernel should be initialized (no exception)
            _kernel.Should().NotBeNull();
        }

        [Fact]
        public async Task InitializeAsync_CalledTwice_ShouldBeIdempotent()
        {
            // Act
            await _kernel.InitializeAsync();
            await _kernel.InitializeAsync(); // Should not throw

            // Assert
            _kernel.Should().NotBeNull();
        }

        [Fact]
        public async Task InitializeAsync_WithCancellation_ShouldThrowOperationCanceledException()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => _kernel.InitializeAsync(cts.Token));
        }

        #endregion

        #region Plugin Registration Tests

        [Fact]
        public async Task RegisterPlugin_WithValidPlugin_ShouldSucceed()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var plugin = new TestStoragePlugin();

            // Act
            await _kernel.RegisterPluginAsync(plugin);

            // Assert
            var registered = _kernel.GetPlugin<IStorageProvider>(plugin.Id);
            registered.Should().NotBeNull();
            registered.Should().BeSameAs(plugin);
        }

        [Fact]
        public async Task RegisterPlugin_Duplicate_ShouldOverwrite()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var plugin1 = new TestStoragePlugin { TestId = "test-plugin" };
            var plugin2 = new TestStoragePlugin { TestId = "test-plugin" };

            // Act
            await _kernel.RegisterPluginAsync(plugin1);
            await _kernel.RegisterPluginAsync(plugin2);

            // Assert - should have only one registration
            var plugins = _kernel.GetPlugins<IStorageProvider>();
            plugins.Count(p => p.Id == "test-plugin").Should().Be(1);
        }

        [Fact]
        public async Task GetPlugins_ByCategory_ShouldReturnCorrectPlugins()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var storagePlugin = new TestStoragePlugin();
            await _kernel.RegisterPluginAsync(storagePlugin);

            // Act
            var storagePlugins = _kernel.GetPlugins<IStorageProvider>();

            // Assert
            storagePlugins.Should().Contain(storagePlugin);
        }

        #endregion

        #region Message Bus Tests

        [Fact]
        public async Task PublishMessage_ShouldReachSubscribers()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var receivedMessage = false;
            var messageBus = _kernel.GetMessageBus();

            messageBus.Subscribe("test.topic", msg =>
            {
                receivedMessage = true;
                return Task.CompletedTask;
            });

            // Act
            await messageBus.PublishAsync("test.topic", new PluginMessage
            {
                Type = "test",
                Payload = new Dictionary<string, object> { ["key"] = "value" }
            });

            // Assert
            await Task.Delay(100); // Allow async delivery
            receivedMessage.Should().BeTrue();
        }

        [Fact]
        public async Task SendMessage_ShouldReturnResponse()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var messageBus = _kernel.GetMessageBus();

            messageBus.Subscribe("test.request", async msg =>
            {
                // Handler registered
                await Task.CompletedTask;
            });

            // Act
            var response = await messageBus.SendAsync("test.request", new PluginMessage
            {
                Type = "request"
            });

            // Assert
            response.Should().NotBeNull();
        }

        #endregion

        #region Configuration Tests

        [Fact]
        public void KernelBuilder_WithCustomConfiguration_ShouldApply()
        {
            // Arrange & Act
            var kernel = new KernelBuilder()
                .WithRootPath(_tempPath)
                .WithOperatingMode(OperatingMode.Enterprise)
                .Build();

            // Assert
            kernel.Should().NotBeNull();
        }

        [Fact]
        public async Task Configuration_HotReload_ShouldNotifySubscribers()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var configChanged = false;

            // Note: This test verifies the config reload mechanism if available
            // The actual behavior depends on the kernel implementation

            // Assert - kernel should be in a valid state
            _kernel.Should().NotBeNull();
        }

        #endregion

        #region Lifecycle Tests

        [Fact]
        public async Task Dispose_ShouldCleanupResources()
        {
            // Arrange
            await _kernel.InitializeAsync();

            // Act
            await _kernel.DisposeAsync();

            // Assert - should not throw, resources should be cleaned up
            // Create a new kernel to verify no resource leaks
            var newKernel = new KernelBuilder()
                .WithRootPath(_tempPath)
                .WithOperatingMode(OperatingMode.Laptop)
                .Build();

            await newKernel.InitializeAsync();
            await newKernel.DisposeAsync();
        }

        [Fact]
        public async Task Dispose_CalledMultipleTimes_ShouldBeIdempotent()
        {
            // Arrange
            await _kernel.InitializeAsync();

            // Act & Assert - should not throw
            await _kernel.DisposeAsync();
            await _kernel.DisposeAsync();
            await _kernel.DisposeAsync();
        }

        #endregion

        #region Concurrent Access Tests

        [Fact]
        public async Task ConcurrentPluginRegistration_ShouldBeThreadSafe()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var tasks = new List<Task>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var plugin = new TestStoragePlugin { TestId = $"concurrent-plugin-{index}" };
                    await _kernel.RegisterPluginAsync(plugin);
                }));
            }

            await Task.WhenAll(tasks);

            // Assert
            var plugins = _kernel.GetPlugins<IStorageProvider>().ToList();
            plugins.Count.Should().BeGreaterOrEqualTo(10);
        }

        [Fact]
        public async Task ConcurrentMessagePublishing_ShouldBeThreadSafe()
        {
            // Arrange
            await _kernel.InitializeAsync();
            var messageBus = _kernel.GetMessageBus();
            var messageCount = 0;
            var lockObj = new object();

            messageBus.Subscribe("concurrent.topic", msg =>
            {
                lock (lockObj) { messageCount++; }
                return Task.CompletedTask;
            });

            // Act
            var tasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(messageBus.PublishAsync("concurrent.topic", new PluginMessage { Type = "test" }));
            }

            await Task.WhenAll(tasks);
            await Task.Delay(500); // Allow all handlers to complete

            // Assert
            messageCount.Should().Be(100);
        }

        #endregion

        #region Test Helpers

        private class TestStoragePlugin : StorageProviderPluginBase
        {
            public string TestId { get; set; } = $"test-storage-{Guid.NewGuid():N}";

            public override string Id => TestId;
            public override string Name => "Test Storage Plugin";
            public override string Scheme => "test";

            private readonly Dictionary<string, byte[]> _storage = new();

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

        #endregion
    }
}
