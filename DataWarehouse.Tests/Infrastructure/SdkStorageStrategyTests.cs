using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK storage strategy infrastructure types:
    /// IStorageStrategy, StorageCapabilities, StorageTier, StorageObjectMetadata,
    /// ConsistencyModel, and StorageHealthInfo.
    /// </summary>
    public class SdkStorageStrategyTests
    {
        #region IStorageStrategy Interface Tests

        [Fact]
        public void IStorageStrategy_DefinesStrategyIdProperty()
        {
            var prop = typeof(IStorageStrategy).GetProperty("StrategyId");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IStorageStrategy_DefinesNameProperty()
        {
            var prop = typeof(IStorageStrategy).GetProperty("Name");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IStorageStrategy_DefinesTierProperty()
        {
            var prop = typeof(IStorageStrategy).GetProperty("Tier");
            Assert.NotNull(prop);
            Assert.Equal(typeof(StorageTier), prop!.PropertyType);
        }

        [Fact]
        public void IStorageStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IStorageStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(StorageCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IStorageStrategy_DefinesStoreAsyncMethod()
        {
            var method = typeof(IStorageStrategy).GetMethod("StoreAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<StorageObjectMetadata>), method!.ReturnType);
        }

        [Fact]
        public void IStorageStrategy_DefinesRetrieveAsyncMethod()
        {
            var method = typeof(IStorageStrategy).GetMethod("RetrieveAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<System.IO.Stream>), method!.ReturnType);
        }

        [Fact]
        public void IStorageStrategy_DefinesDeleteAsyncMethod()
        {
            var method = typeof(IStorageStrategy).GetMethod("DeleteAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IStorageStrategy_DefinesExistsAsyncMethod()
        {
            var method = typeof(IStorageStrategy).GetMethod("ExistsAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<bool>), method!.ReturnType);
        }

        [Fact]
        public void IStorageStrategy_DefinesGetHealthAsyncMethod()
        {
            var method = typeof(IStorageStrategy).GetMethod("GetHealthAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<StorageHealthInfo>), method!.ReturnType);
        }

        #endregion

        #region StorageCapabilities Tests

        [Fact]
        public void StorageCapabilities_Default_HasBasicFeatures()
        {
            var caps = StorageCapabilities.Default;

            Assert.True(caps.SupportsMetadata);
            Assert.True(caps.SupportsStreaming);
            Assert.Equal(ConsistencyModel.Eventual, caps.ConsistencyModel);
            Assert.False(caps.SupportsVersioning);
            Assert.False(caps.SupportsLocking);
            Assert.False(caps.SupportsTiering);
        }

        [Fact]
        public void StorageCapabilities_CustomConstruction_SetsAllFlags()
        {
            var caps = new StorageCapabilities
            {
                SupportsVersioning = true,
                SupportsMetadata = true,
                SupportsLocking = true,
                SupportsTiering = true,
                SupportsEncryption = true,
                SupportsCompression = true,
                SupportsStreaming = true,
                SupportsMultipart = true,
                MaxObjectSize = 5L * 1024 * 1024 * 1024,
                MaxObjects = 1_000_000,
                ConsistencyModel = ConsistencyModel.Strong
            };

            Assert.True(caps.SupportsVersioning);
            Assert.True(caps.SupportsLocking);
            Assert.True(caps.SupportsTiering);
            Assert.True(caps.SupportsEncryption);
            Assert.True(caps.SupportsCompression);
            Assert.True(caps.SupportsMultipart);
            Assert.Equal(5L * 1024 * 1024 * 1024, caps.MaxObjectSize);
            Assert.Equal(1_000_000, caps.MaxObjects);
            Assert.Equal(ConsistencyModel.Strong, caps.ConsistencyModel);
        }

        [Fact]
        public void StorageCapabilities_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = new StorageCapabilities
            {
                SupportsVersioning = true,
                SupportsEncryption = true,
                SupportsTiering = true,
                ConsistencyModel = ConsistencyModel.ReadAfterWrite,
                MaxObjectSize = 10 * 1024 * 1024
            };

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<StorageCapabilities>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.SupportsVersioning, deserialized!.SupportsVersioning);
            Assert.Equal(original.SupportsEncryption, deserialized.SupportsEncryption);
            Assert.Equal(original.SupportsTiering, deserialized.SupportsTiering);
            Assert.Equal(original.ConsistencyModel, deserialized.ConsistencyModel);
        }

        #endregion

        #region StorageTier Enum Tests

        [Fact]
        public void StorageTier_Has6Values()
        {
            var values = Enum.GetValues<StorageTier>();
            Assert.Equal(6, values.Length);
        }

        [Fact]
        public void StorageTier_ContainsExpectedTiers()
        {
            var values = Enum.GetValues<StorageTier>();
            Assert.Contains(StorageTier.Hot, values);
            Assert.Contains(StorageTier.Warm, values);
            Assert.Contains(StorageTier.Cold, values);
            Assert.Contains(StorageTier.Archive, values);
            Assert.Contains(StorageTier.RamDisk, values);
            Assert.Contains(StorageTier.Tape, values);
        }

        #endregion

        #region ConsistencyModel Enum Tests

        [Fact]
        public void ConsistencyModel_Has3Values()
        {
            var values = Enum.GetValues<ConsistencyModel>();
            Assert.Equal(3, values.Length);
            Assert.Contains(ConsistencyModel.Eventual, values);
            Assert.Contains(ConsistencyModel.Strong, values);
            Assert.Contains(ConsistencyModel.ReadAfterWrite, values);
        }

        #endregion

        #region StorageObjectMetadata Tests

        [Fact]
        public void StorageObjectMetadata_Construction_SetsProperties()
        {
            var now = DateTime.UtcNow;
            var metadata = new StorageObjectMetadata
            {
                Key = "container/blob-001.dat",
                Size = 1024 * 1024,
                Created = now,
                Modified = now,
                ETag = "\"abc123\"",
                ContentType = "application/octet-stream"
            };

            Assert.Equal("container/blob-001.dat", metadata.Key);
            Assert.Equal(1024 * 1024, metadata.Size);
            Assert.Equal("\"abc123\"", metadata.ETag);
            Assert.Equal("application/octet-stream", metadata.ContentType);
        }

        [Fact]
        public void StorageObjectMetadata_DefaultKey_IsEmptyString()
        {
            var metadata = new StorageObjectMetadata();
            Assert.Equal(string.Empty, metadata.Key);
        }

        #endregion
    }
}
