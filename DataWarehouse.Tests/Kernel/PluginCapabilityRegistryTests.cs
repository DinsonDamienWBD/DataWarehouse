using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.Kernel.Registry;

namespace DataWarehouse.Tests.Kernel
{
    /// <summary>
    /// Unit tests for PluginCapabilityRegistry enhancements - idempotent registration and reload handling.
    /// Tests capability registration, unregistration, querying, and event notifications.
    /// </summary>
    public class PluginCapabilityRegistryTests
    {
        #region Idempotent Registration Tests

        [Fact]
        public async Task RegisterAsync_FirstTime_ReturnsTrue()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);
            var isNew = await registry.RegisterAsync(capability);

            Assert.True(isNew);
        }

        [Fact]
        public async Task RegisterAsync_SameCapabilityTwice_SecondReturnsFalse()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);

            var first = await registry.RegisterAsync(capability);
            var second = await registry.RegisterAsync(capability);

            Assert.True(first);
            // Idempotent: second registration is an update of same capability from same plugin
            // Returns false because it's not new
            Assert.False(second);
        }

        [Fact]
        public async Task RegisterAsync_SameCapabilityDifferentPlugin_UpdatesCapability()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability1 = CreateCapability("test.capability", CapabilityCategory.Encryption, pluginId: "Plugin1");
            var capability2 = CreateCapability("test.capability", CapabilityCategory.Encryption, pluginId: "Plugin2");

            await registry.RegisterAsync(capability1);
            var isNew = await registry.RegisterAsync(capability2);

            // Should replace (conflict scenario)
            Assert.True(isNew); // Different plugin = treated as new
            var retrieved = registry.GetCapability("test.capability");
            Assert.Equal("Plugin2", retrieved?.PluginId);
        }

        [Fact]
        public async Task RegisterAsync_PluginReload_UpdatesExistingCapability()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability1 = CreateCapability("test.capability", CapabilityCategory.Encryption, pluginId: "Plugin1", displayName: "Old Name");
            var capability2 = CreateCapability("test.capability", CapabilityCategory.Encryption, pluginId: "Plugin1", displayName: "New Name");

            await registry.RegisterAsync(capability1);
            await registry.RegisterAsync(capability2);

            var retrieved = registry.GetCapability("test.capability");
            Assert.Equal("New Name", retrieved?.DisplayName);
        }

        [Fact]
        public async Task RegisterAsync_NoDuplicatesOnReload()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);

            await registry.RegisterAsync(capability);
            await registry.RegisterAsync(capability);
            await registry.RegisterAsync(capability);

            var all = registry.GetAll();
            var count = all.Count(c => c.CapabilityId == "test.capability");

            Assert.Equal(1, count); // Should only have one entry
        }

        #endregion

        #region Registration Tests

        [Fact]
        public async Task RegisterAsync_ValidCapability_Succeeds()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("encryption.aes-256", CapabilityCategory.Encryption);
            var result = await registry.RegisterAsync(capability);

            Assert.True(result);
            Assert.NotNull(registry.GetCapability("encryption.aes-256"));
        }

        [Fact]
        public async Task RegisterAsync_NullCapability_ThrowsArgumentNullException()
        {
            using var registry = new PluginCapabilityRegistry();

            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await registry.RegisterAsync(null!);
            });
        }

        [Fact]
        public async Task RegisterBatchAsync_RegistersMultipleCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("cap1", CapabilityCategory.Encryption),
                CreateCapability("cap2", CapabilityCategory.Compression),
                CreateCapability("cap3", CapabilityCategory.Storage)
            };

            var count = await registry.RegisterBatchAsync(capabilities);

            Assert.Equal(3, count);
            Assert.Equal(3, registry.GetAll().Count);
        }

        [Fact]
        public async Task UnregisterAsync_ExistingCapability_ReturnsTrue()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);
            await registry.RegisterAsync(capability);

            var result = await registry.UnregisterAsync("test.capability");

            Assert.True(result);
            Assert.Null(registry.GetCapability("test.capability"));
        }

        [Fact]
        public async Task UnregisterAsync_NonExistentCapability_ReturnsFalse()
        {
            using var registry = new PluginCapabilityRegistry();

            var result = await registry.UnregisterAsync("nonexistent");

            Assert.False(result);
        }

        [Fact]
        public async Task UnregisterPluginAsync_RemovesAllPluginCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("plugin1.cap1", CapabilityCategory.Encryption, pluginId: "Plugin1"),
                CreateCapability("plugin1.cap2", CapabilityCategory.Compression, pluginId: "Plugin1"),
                CreateCapability("plugin2.cap1", CapabilityCategory.Storage, pluginId: "Plugin2")
            };

            await registry.RegisterBatchAsync(capabilities);

            var count = await registry.UnregisterPluginAsync("Plugin1");

            Assert.Equal(2, count);
            Assert.Single(registry.GetAll());
            Assert.Equal("Plugin2", registry.GetAll()[0].PluginId);
        }

        #endregion

        #region Discovery Tests

        [Fact]
        public void GetCapability_ExistingCapability_ReturnsCapability()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);
            registry.RegisterAsync(capability).Wait();

            var retrieved = registry.GetCapability("test.capability");

            Assert.NotNull(retrieved);
            Assert.Equal("test.capability", retrieved.CapabilityId);
        }

        [Fact]
        public void GetCapability_NonExistentCapability_ReturnsNull()
        {
            using var registry = new PluginCapabilityRegistry();

            var retrieved = registry.GetCapability("nonexistent");

            Assert.Null(retrieved);
        }

        [Fact]
        public void IsCapabilityAvailable_AvailableCapability_ReturnsTrue()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption, isAvailable: true);
            registry.RegisterAsync(capability).Wait();

            Assert.True(registry.IsCapabilityAvailable("test.capability"));
        }

        [Fact]
        public void IsCapabilityAvailable_UnavailableCapability_ReturnsFalse()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption, isAvailable: false);
            registry.RegisterAsync(capability).Wait();

            Assert.False(registry.IsCapabilityAvailable("test.capability"));
        }

        [Fact]
        public void GetByCategory_ReturnsCorrectCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("enc1", CapabilityCategory.Encryption),
                CreateCapability("enc2", CapabilityCategory.Encryption),
                CreateCapability("comp1", CapabilityCategory.Compression)
            };

            registry.RegisterBatchAsync(capabilities).Wait();

            var encryptionCaps = registry.GetByCategory(CapabilityCategory.Encryption);

            Assert.Equal(2, encryptionCaps.Count);
            Assert.All(encryptionCaps, c => Assert.Equal(CapabilityCategory.Encryption, c.Category));
        }

        [Fact]
        public void GetByPlugin_ReturnsCorrectCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("p1.c1", CapabilityCategory.Encryption, pluginId: "Plugin1"),
                CreateCapability("p1.c2", CapabilityCategory.Compression, pluginId: "Plugin1"),
                CreateCapability("p2.c1", CapabilityCategory.Storage, pluginId: "Plugin2")
            };

            registry.RegisterBatchAsync(capabilities).Wait();

            var plugin1Caps = registry.GetByPlugin("Plugin1");

            Assert.Equal(2, plugin1Caps.Count);
            Assert.All(plugin1Caps, c => Assert.Equal("Plugin1", c.PluginId));
        }

        [Fact]
        public void GetByTags_ReturnsCorrectCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("c1", CapabilityCategory.Encryption, tags: new[] { "fast", "secure" }),
                CreateCapability("c2", CapabilityCategory.Encryption, tags: new[] { "fast" }),
                CreateCapability("c3", CapabilityCategory.Encryption, tags: new[] { "secure" })
            };

            registry.RegisterBatchAsync(capabilities).Wait();

            var fastCaps = registry.GetByTags("fast");

            Assert.Equal(2, fastCaps.Count);
            Assert.All(fastCaps, c => Assert.Contains("fast", c.Tags, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void FindBest_ReturnsHighestPriorityCapability()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("low", CapabilityCategory.Encryption, priority: 10),
                CreateCapability("high", CapabilityCategory.Encryption, priority: 100),
                CreateCapability("medium", CapabilityCategory.Encryption, priority: 50)
            };

            registry.RegisterBatchAsync(capabilities).Wait();

            var best = registry.FindBest(CapabilityCategory.Encryption);

            Assert.NotNull(best);
            Assert.Equal("high", best.CapabilityId);
            Assert.Equal(100, best.Priority);
        }

        #endregion

        #region Query Tests

        [Fact]
        public async Task QueryAsync_WithCategoryFilter_ReturnsFilteredResults()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("enc1", CapabilityCategory.Encryption),
                CreateCapability("comp1", CapabilityCategory.Compression)
            };

            await registry.RegisterBatchAsync(capabilities);

            var query = new CapabilityQuery { Category = CapabilityCategory.Encryption };
            var result = await registry.QueryAsync(query);

            Assert.Single(result.Capabilities);
            Assert.Equal(CapabilityCategory.Encryption, result.Capabilities[0].Category);
        }

        [Fact]
        public async Task QueryAsync_WithOnlyAvailableFilter_ReturnsOnlyAvailableCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("available", CapabilityCategory.Encryption, isAvailable: true),
                CreateCapability("unavailable", CapabilityCategory.Encryption, isAvailable: false)
            };

            await registry.RegisterBatchAsync(capabilities);

            var query = new CapabilityQuery { OnlyAvailable = true };
            var result = await registry.QueryAsync(query);

            Assert.Single(result.Capabilities);
            Assert.Equal("available", result.Capabilities[0].CapabilityId);
        }

        [Fact]
        public async Task QueryAsync_WithSearchText_ReturnsMatchingCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("encryption.aes", CapabilityCategory.Encryption, displayName: "AES Encryption"),
                CreateCapability("encryption.rsa", CapabilityCategory.Encryption, displayName: "RSA Encryption"),
                CreateCapability("compression.gzip", CapabilityCategory.Compression, displayName: "GZIP Compression")
            };

            await registry.RegisterBatchAsync(capabilities);

            var query = new CapabilityQuery { SearchText = "encryption" };
            var result = await registry.QueryAsync(query);

            Assert.Equal(2, result.TotalCount);
        }

        #endregion

        #region Availability Management Tests

        [Fact]
        public async Task SetPluginAvailabilityAsync_UpdatesAllPluginCapabilities()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("p1.c1", CapabilityCategory.Encryption, pluginId: "Plugin1", isAvailable: true),
                CreateCapability("p1.c2", CapabilityCategory.Compression, pluginId: "Plugin1", isAvailable: true)
            };

            await registry.RegisterBatchAsync(capabilities);

            await registry.SetPluginAvailabilityAsync("Plugin1", false);

            Assert.False(registry.IsCapabilityAvailable("p1.c1"));
            Assert.False(registry.IsCapabilityAvailable("p1.c2"));
        }

        #endregion

        #region Event Tests

        [Fact]
        public async Task OnCapabilityRegistered_FiresWhenCapabilityAdded()
        {
            using var registry = new PluginCapabilityRegistry();

            RegisteredCapability? capturedCapability = null;
            registry.OnCapabilityRegistered(cap => capturedCapability = cap);

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);
            await registry.RegisterAsync(capability);

            Assert.NotNull(capturedCapability);
            Assert.Equal("test.capability", capturedCapability.CapabilityId);
        }

        [Fact]
        public async Task OnCapabilityUnregistered_FiresWhenCapabilityRemoved()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption);
            await registry.RegisterAsync(capability);

            string? capturedId = null;
            registry.OnCapabilityUnregistered(id => capturedId = id);

            await registry.UnregisterAsync("test.capability");

            Assert.Equal("test.capability", capturedId);
        }

        [Fact]
        public async Task OnAvailabilityChanged_FiresWhenAvailabilityChanges()
        {
            using var registry = new PluginCapabilityRegistry();

            var capability = CreateCapability("test.capability", CapabilityCategory.Encryption, pluginId: "Plugin1");
            await registry.RegisterAsync(capability);

            string? capturedId = null;
            bool? capturedAvailability = null;
            registry.OnAvailabilityChanged((id, isAvailable) =>
            {
                capturedId = id;
                capturedAvailability = isAvailable;
            });

            await registry.SetPluginAvailabilityAsync("Plugin1", false);

            Assert.Equal("test.capability", capturedId);
            Assert.False(capturedAvailability);
        }

        #endregion

        #region Statistics Tests

        [Fact]
        public void GetStatistics_ReturnsCorrectCounts()
        {
            using var registry = new PluginCapabilityRegistry();

            var capabilities = new[]
            {
                CreateCapability("enc1", CapabilityCategory.Encryption, pluginId: "Plugin1", isAvailable: true),
                CreateCapability("enc2", CapabilityCategory.Encryption, pluginId: "Plugin2", isAvailable: false),
                CreateCapability("comp1", CapabilityCategory.Compression, pluginId: "Plugin1", isAvailable: true)
            };

            registry.RegisterBatchAsync(capabilities).Wait();

            var stats = registry.GetStatistics();

            Assert.Equal(3, stats.TotalCapabilities);
            Assert.Equal(2, stats.AvailableCapabilities);
            Assert.Equal(2, stats.RegisteredPlugins);
            Assert.Equal(2, stats.ByCategory[CapabilityCategory.Encryption]);
            Assert.Equal(1, stats.ByCategory[CapabilityCategory.Compression]);
        }

        #endregion

        #region Helper Methods

        private static RegisteredCapability CreateCapability(
            string id,
            CapabilityCategory category,
            string pluginId = "TestPlugin",
            string displayName = "Test Capability",
            bool isAvailable = true,
            int priority = 100,
            string[]? tags = null)
        {
            return new RegisteredCapability
            {
                CapabilityId = id,
                DisplayName = displayName,
                Description = $"Test capability {id}",
                Category = category,
                SubCategory = null,
                PluginId = pluginId,
                PluginName = pluginId,
                PluginVersion = "1.0.0",
                Priority = priority,
                IsAvailable = isAvailable,
                Tags = tags ?? Array.Empty<string>(),
                RegisteredAt = DateTimeOffset.UtcNow,
                ParameterSchema = null,
                Metadata = new Dictionary<string, object>()
            };
        }

        #endregion
    }
}
