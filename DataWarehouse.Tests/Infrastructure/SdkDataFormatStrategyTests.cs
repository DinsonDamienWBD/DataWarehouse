using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK data format strategy infrastructure types:
    /// IDataFormatStrategy, DataFormatCapabilities, DomainFamily, and FormatInfo.
    /// </summary>
    public class SdkDataFormatStrategyTests
    {
        #region IDataFormatStrategy Interface Tests

        [Fact]
        public void IDataFormatStrategy_DefinesStrategyIdProperty()
        {
            var prop = typeof(IDataFormatStrategy).GetProperty("StrategyId");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesDisplayNameProperty()
        {
            var prop = typeof(IDataFormatStrategy).GetProperty("DisplayName");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IDataFormatStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(DataFormatCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesDetectFormatAsyncMethod()
        {
            var method = typeof(IDataFormatStrategy).GetMethod("DetectFormatAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<bool>), method!.ReturnType);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesParseAsyncMethod()
        {
            var method = typeof(IDataFormatStrategy).GetMethod("ParseAsync");
            Assert.NotNull(method);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesSerializeAsyncMethod()
        {
            var method = typeof(IDataFormatStrategy).GetMethod("SerializeAsync");
            Assert.NotNull(method);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesConvertToAsyncMethod()
        {
            var method = typeof(IDataFormatStrategy).GetMethod("ConvertToAsync");
            Assert.NotNull(method);
        }

        [Fact]
        public void IDataFormatStrategy_DefinesValidateAsyncMethod()
        {
            var method = typeof(IDataFormatStrategy).GetMethod("ValidateAsync");
            Assert.NotNull(method);
        }

        #endregion

        #region DataFormatCapabilities Tests

        [Fact]
        public void DataFormatCapabilities_Full_HasAllFeaturesEnabled()
        {
            var caps = DataFormatCapabilities.Full;

            Assert.True(caps.Bidirectional);
            Assert.True(caps.Streaming);
            Assert.True(caps.SchemaAware);
            Assert.True(caps.CompressionAware);
            Assert.True(caps.RandomAccess);
            Assert.True(caps.SelfDescribing);
            Assert.True(caps.SupportsHierarchicalData);
            Assert.True(caps.SupportsBinaryData);
        }

        [Fact]
        public void DataFormatCapabilities_Basic_HasMinimalFeatures()
        {
            var caps = DataFormatCapabilities.Basic;

            Assert.True(caps.Bidirectional);
            Assert.False(caps.Streaming);
            Assert.False(caps.SchemaAware);
            Assert.False(caps.CompressionAware);
            Assert.False(caps.RandomAccess);
            Assert.False(caps.SelfDescribing);
        }

        [Fact]
        public void DataFormatCapabilities_Construction_AllowsCustomValues()
        {
            var caps = new DataFormatCapabilities
            {
                Bidirectional = true,
                Streaming = true,
                SchemaAware = false,
                CompressionAware = true
            };

            Assert.True(caps.Bidirectional);
            Assert.True(caps.Streaming);
            Assert.False(caps.SchemaAware);
            Assert.True(caps.CompressionAware);
        }

        [Fact]
        public void DataFormatCapabilities_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = DataFormatCapabilities.Full;

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<DataFormatCapabilities>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.Bidirectional, deserialized!.Bidirectional);
            Assert.Equal(original.Streaming, deserialized.Streaming);
            Assert.Equal(original.SchemaAware, deserialized.SchemaAware);
            Assert.Equal(original.RandomAccess, deserialized.RandomAccess);
        }

        #endregion

        #region DomainFamily Enum Tests

        [Fact]
        public void DomainFamily_ContainsExpectedValues()
        {
            var values = Enum.GetValues<DomainFamily>();
            Assert.True(values.Length >= 5);
            Assert.Contains(DomainFamily.General, values);
            Assert.Contains(DomainFamily.Analytics, values);
            Assert.Contains(DomainFamily.Scientific, values);
            Assert.Contains(DomainFamily.Geospatial, values);
            Assert.Contains(DomainFamily.Healthcare, values);
        }

        #endregion
    }
}
