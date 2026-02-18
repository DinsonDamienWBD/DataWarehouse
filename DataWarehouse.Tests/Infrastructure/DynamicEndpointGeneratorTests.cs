using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Moq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.Shared;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for DynamicEndpointGenerator - dynamic API endpoint generation from capability registry.
    /// Tests endpoint creation, idempotent registration, capability change handling.
    /// </summary>
    public class DynamicEndpointGeneratorTests
    {
        #region Construction Tests

        [Fact]
        public void Constructor_WithoutRegistry_CreatesEmptyGenerator()
        {
            using var generator = new DynamicEndpointGenerator();

            var endpoints = generator.GetEndpoints();

            Assert.NotNull(endpoints);
            Assert.Empty(endpoints);
        }

        [Fact]
        public void Constructor_WithRegistry_LoadsInitialCapabilities()
        {
            var mockRegistry = CreateMockRegistry(new[]
            {
                CreateCapability("encryption.aes-256", CapabilityCategory.Encryption),
                CreateCapability("compression.gzip", CapabilityCategory.Compression)
            });

            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoints = generator.GetEndpoints();

            Assert.Equal(2, endpoints.Count);
            Assert.Contains(endpoints, e => e.EndpointId == "encryption.aes-256");
            Assert.Contains(endpoints, e => e.EndpointId == "compression.gzip");
        }

        #endregion

        #region Endpoint Generation Tests

        [Fact]
        public void GetEndpoints_ReturnsOnlyAvailableEndpoints()
        {
            var capabilities = new[]
            {
                CreateCapability("available.endpoint", CapabilityCategory.Encryption, isAvailable: true),
                CreateCapability("unavailable.endpoint", CapabilityCategory.Encryption, isAvailable: false)
            };

            var mockRegistry = CreateMockRegistry(capabilities);
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoints = generator.GetEndpoints();

            Assert.Single(endpoints);
            Assert.Equal("available.endpoint", endpoints[0].EndpointId);
        }

        [Fact]
        public void GetEndpointsByCategory_FiltersCorrectly()
        {
            var capabilities = new[]
            {
                CreateCapability("encryption.aes", CapabilityCategory.Encryption),
                CreateCapability("compression.gzip", CapabilityCategory.Compression),
                CreateCapability("encryption.rsa", CapabilityCategory.Encryption)
            };

            var mockRegistry = CreateMockRegistry(capabilities);
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var encryptionEndpoints = generator.GetEndpointsByCategory(CapabilityCategory.Encryption);

            Assert.Equal(2, encryptionEndpoints.Count);
            Assert.All(encryptionEndpoints, e => Assert.Equal(CapabilityCategory.Encryption, e.Category));
        }

        [Fact]
        public void GetEndpointsByPlugin_FiltersCorrectly()
        {
            var capabilities = new[]
            {
                CreateCapability("plugin1.feature1", CapabilityCategory.Encryption, pluginId: "Plugin1"),
                CreateCapability("plugin1.feature2", CapabilityCategory.Compression, pluginId: "Plugin1"),
                CreateCapability("plugin2.feature1", CapabilityCategory.Encryption, pluginId: "Plugin2")
            };

            var mockRegistry = CreateMockRegistry(capabilities);
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var plugin1Endpoints = generator.GetEndpointsByPlugin("Plugin1");

            Assert.Equal(2, plugin1Endpoints.Count);
            Assert.All(plugin1Endpoints, e => Assert.Equal("Plugin1", e.PluginId));
        }

        [Fact]
        public void GetEndpoint_WithValidId_ReturnsEndpoint()
        {
            var capability = CreateCapability("test.endpoint", CapabilityCategory.Encryption);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("test.endpoint");

            Assert.NotNull(endpoint);
            Assert.Equal("test.endpoint", endpoint.EndpointId);
        }

        [Fact]
        public void GetEndpoint_WithInvalidId_ReturnsNull()
        {
            var mockRegistry = CreateMockRegistry(Array.Empty<RegisteredCapability>());
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("nonexistent");

            Assert.Null(endpoint);
        }

        [Fact]
        public void IsEndpointAvailable_WithAvailableEndpoint_ReturnsTrue()
        {
            var capability = CreateCapability("test.endpoint", CapabilityCategory.Encryption, isAvailable: true);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var isAvailable = generator.IsEndpointAvailable("test.endpoint");

            Assert.True(isAvailable);
        }

        [Fact]
        public void IsEndpointAvailable_WithUnavailableEndpoint_ReturnsFalse()
        {
            var capability = CreateCapability("test.endpoint", CapabilityCategory.Encryption, isAvailable: false);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var isAvailable = generator.IsEndpointAvailable("test.endpoint");

            Assert.False(isAvailable);
        }

        #endregion

        #region HTTP Method Inference Tests

        [Fact]
        public void EndpointGeneration_InfersPostForCreateOperations()
        {
            var capability = CreateCapability("data.create", CapabilityCategory.Storage);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("data.create");

            Assert.NotNull(endpoint);
            Assert.Equal("POST", endpoint.HttpMethod);
        }

        [Fact]
        public void EndpointGeneration_InfersGetForReadOperations()
        {
            var capability = CreateCapability("data.read", CapabilityCategory.Storage);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("data.read");

            Assert.NotNull(endpoint);
            Assert.Equal("GET", endpoint.HttpMethod);
        }

        [Fact]
        public void EndpointGeneration_InfersPutForUpdateOperations()
        {
            var capability = CreateCapability("data.update", CapabilityCategory.Storage);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("data.update");

            Assert.NotNull(endpoint);
            Assert.Equal("PUT", endpoint.HttpMethod);
        }

        [Fact]
        public void EndpointGeneration_InfersDeleteForDeleteOperations()
        {
            var capability = CreateCapability("data.delete", CapabilityCategory.Storage);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("data.delete");

            Assert.NotNull(endpoint);
            Assert.Equal("DELETE", endpoint.HttpMethod);
        }

        #endregion

        #region Path Generation Tests

        [Fact]
        public void EndpointGeneration_GeneratesCorrectPath()
        {
            var capability = CreateCapability("encryption.aes-256-gcm", CapabilityCategory.Encryption);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("encryption.aes-256-gcm");

            Assert.NotNull(endpoint);
            Assert.Equal("/api/v1/encryption/aes-256-gcm", endpoint.Path);
        }

        [Fact]
        public void EndpointGeneration_HandlesUnderscoresInPath()
        {
            var capability = CreateCapability("data.long_name_test", CapabilityCategory.Storage);
            var mockRegistry = CreateMockRegistry(new[] { capability });
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var endpoint = generator.GetEndpoint("data.long_name_test");

            Assert.NotNull(endpoint);
            Assert.Contains("long-name-test", endpoint.Path); // Underscores converted to hyphens
        }

        #endregion

        #region Event Handling Tests

        [Fact]
        public void OnCapabilityRegistered_AddsNewEndpoint()
        {
            var mockRegistry = new Mock<IPluginCapabilityRegistry>();
            var registeredHandlers = new List<Action<RegisteredCapability>>();

            mockRegistry.Setup(r => r.OnCapabilityRegistered(It.IsAny<Action<RegisteredCapability>>()))
                .Callback<Action<RegisteredCapability>>(handler => registeredHandlers.Add(handler))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.OnCapabilityUnregistered(It.IsAny<Action<string>>()))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.OnAvailabilityChanged(It.IsAny<Action<string, bool>>()))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.GetAll()).Returns(Array.Empty<RegisteredCapability>());

            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            EndpointChangeEvent? capturedEvent = null;
            generator.OnEndpointChanged += (evt) => capturedEvent = evt;

            // Simulate capability registration
            var newCapability = CreateCapability("new.endpoint", CapabilityCategory.Encryption);
            foreach (var handler in registeredHandlers)
            {
                handler(newCapability);
            }

            Assert.NotNull(capturedEvent);
            Assert.Equal("added", capturedEvent.ChangeType);
            Assert.Single(capturedEvent.Endpoints);
            Assert.Equal("new.endpoint", capturedEvent.Endpoints[0].EndpointId);
        }

        [Fact]
        public void OnCapabilityUnregistered_RemovesEndpoint()
        {
            var capability = CreateCapability("test.endpoint", CapabilityCategory.Encryption);
            var mockRegistry = new Mock<IPluginCapabilityRegistry>();
            var unregisteredHandlers = new List<Action<string>>();

            mockRegistry.Setup(r => r.OnCapabilityRegistered(It.IsAny<Action<RegisteredCapability>>()))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.OnCapabilityUnregistered(It.IsAny<Action<string>>()))
                .Callback<Action<string>>(handler => unregisteredHandlers.Add(handler))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.OnAvailabilityChanged(It.IsAny<Action<string, bool>>()))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.GetAll()).Returns(new[] { capability });

            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            EndpointChangeEvent? capturedEvent = null;
            generator.OnEndpointChanged += (evt) => capturedEvent = evt;

            // Simulate capability unregistration
            foreach (var handler in unregisteredHandlers)
            {
                handler("test.endpoint");
            }

            Assert.NotNull(capturedEvent);
            Assert.Equal("removed", capturedEvent.ChangeType);
            Assert.Single(capturedEvent.Endpoints);
            Assert.Equal("test.endpoint", capturedEvent.Endpoints[0].EndpointId);
        }

        [Fact]
        public void OnAvailabilityChanged_UpdatesEndpoint()
        {
            var capability = CreateCapability("test.endpoint", CapabilityCategory.Encryption, isAvailable: true);
            var mockRegistry = new Mock<IPluginCapabilityRegistry>();
            var availabilityHandlers = new List<Action<string, bool>>();

            mockRegistry.Setup(r => r.OnCapabilityRegistered(It.IsAny<Action<RegisteredCapability>>()))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.OnCapabilityUnregistered(It.IsAny<Action<string>>()))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.OnAvailabilityChanged(It.IsAny<Action<string, bool>>()))
                .Callback<Action<string, bool>>(handler => availabilityHandlers.Add(handler))
                .Returns(Mock.Of<IDisposable>());

            mockRegistry.Setup(r => r.GetAll()).Returns(new[] { capability });

            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            EndpointChangeEvent? capturedEvent = null;
            generator.OnEndpointChanged += (evt) => capturedEvent = evt;

            // Simulate availability change
            foreach (var handler in availabilityHandlers)
            {
                handler("test.endpoint", false);
            }

            Assert.NotNull(capturedEvent);
            Assert.Equal("updated", capturedEvent.ChangeType);
            Assert.Single(capturedEvent.Endpoints);
            Assert.False(capturedEvent.Endpoints[0].IsAvailable);
        }

        #endregion

        #region Refresh Tests

        [Fact]
        public void RefreshFromCapabilities_UpdatesEndpoints()
        {
            var mockRegistry = CreateMockRegistry(Array.Empty<RegisteredCapability>());
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var capabilities = new[]
            {
                CreateCapability("new.endpoint1", CapabilityCategory.Encryption),
                CreateCapability("new.endpoint2", CapabilityCategory.Compression)
            };

            generator.RefreshFromCapabilities(capabilities);

            var endpoints = generator.GetEndpoints();
            Assert.Equal(2, endpoints.Count);
        }

        [Fact]
        public void RefreshFromCapabilities_IdempotentOnDuplicates()
        {
            var mockRegistry = CreateMockRegistry(Array.Empty<RegisteredCapability>());
            using var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            var capability = CreateCapability("test.endpoint", CapabilityCategory.Encryption);

            generator.RefreshFromCapabilities(new[] { capability });
            generator.RefreshFromCapabilities(new[] { capability }); // Duplicate

            var endpoints = generator.GetEndpoints();
            Assert.Single(endpoints); // Should not duplicate
        }

        #endregion

        #region Disposal Tests

        [Fact]
        public void Dispose_CleansUpResources()
        {
            var mockRegistry = CreateMockRegistry(Array.Empty<RegisteredCapability>());
            var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            generator.Dispose();

            // After disposal, GetEndpoints should return empty
            var endpoints = generator.GetEndpoints();
            Assert.Empty(endpoints);
        }

        [Fact]
        public void Dispose_MultipleCalls_DoesNotThrow()
        {
            var mockRegistry = CreateMockRegistry(Array.Empty<RegisteredCapability>());
            var generator = new DynamicEndpointGenerator(mockRegistry.Object);

            generator.Dispose();
            generator.Dispose(); // Should not throw

            // Verify no exception was thrown
            Assert.True(true);
        }

        #endregion

        #region Helper Methods

        private static RegisteredCapability CreateCapability(
            string id,
            CapabilityCategory category,
            bool isAvailable = true,
            string pluginId = "TestPlugin")
        {
            return new RegisteredCapability
            {
                CapabilityId = id,
                DisplayName = id,
                Description = $"Test capability {id}",
                Category = category,
                SubCategory = null,
                PluginId = pluginId,
                PluginName = pluginId,
                PluginVersion = "1.0.0",
                Priority = 100,
                IsAvailable = isAvailable,
                Tags = Array.Empty<string>(),
                RegisteredAt = DateTimeOffset.UtcNow,
                ParameterSchema = null,
                Metadata = new Dictionary<string, object>()
            };
        }

        private static Mock<IPluginCapabilityRegistry> CreateMockRegistry(RegisteredCapability[] capabilities)
        {
            var mock = new Mock<IPluginCapabilityRegistry>();

            mock.Setup(r => r.GetAll()).Returns(capabilities);
            mock.Setup(r => r.OnCapabilityRegistered(It.IsAny<Action<RegisteredCapability>>()))
                .Returns(Mock.Of<IDisposable>());
            mock.Setup(r => r.OnCapabilityUnregistered(It.IsAny<Action<string>>()))
                .Returns(Mock.Of<IDisposable>());
            mock.Setup(r => r.OnAvailabilityChanged(It.IsAny<Action<string, bool>>()))
                .Returns(Mock.Of<IDisposable>());

            return mock;
        }

        #endregion
    }
}
