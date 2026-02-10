using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Streaming;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK streaming strategy infrastructure types:
    /// IStreamingStrategy, StreamingCapabilities, StreamMessage, PublishResult,
    /// ConsumerGroup, StreamOffset, StreamConfiguration, DeliveryGuarantee.
    /// </summary>
    public class SdkStreamingStrategyTests
    {
        #region IStreamingStrategy Interface Tests

        [Fact]
        public void IStreamingStrategy_DefinesStrategyIdProperty()
        {
            var prop = typeof(IStreamingStrategy).GetProperty("StrategyId");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IStreamingStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IStreamingStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(StreamingCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IStreamingStrategy_DefinesPublishAsyncMethod()
        {
            var method = typeof(IStreamingStrategy).GetMethod("PublishAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<PublishResult>), method!.ReturnType);
        }

        [Fact]
        public void IStreamingStrategy_DefinesSubscribeAsyncMethod()
        {
            var method = typeof(IStreamingStrategy).GetMethod("SubscribeAsync");
            Assert.NotNull(method);
        }

        [Fact]
        public void IStreamingStrategy_DefinesCreateStreamAsyncMethod()
        {
            var method = typeof(IStreamingStrategy).GetMethod("CreateStreamAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IStreamingStrategy_DefinesSupportedProtocolsProperty()
        {
            var prop = typeof(IStreamingStrategy).GetProperty("SupportedProtocols");
            Assert.NotNull(prop);
        }

        #endregion

        #region StreamingCapabilities Tests

        [Fact]
        public void StreamingCapabilities_Basic_HasBasicFeatures()
        {
            var caps = StreamingCapabilities.Basic;

            Assert.True(caps.SupportsOrdering);
            Assert.True(caps.SupportsPersistence);
            Assert.True(caps.SupportsAcknowledgment);
            Assert.Equal(DeliveryGuarantee.AtLeastOnce, caps.DefaultDeliveryGuarantee);
        }

        [Fact]
        public void StreamingCapabilities_Enterprise_HasAllFeatures()
        {
            var caps = StreamingCapabilities.Enterprise;

            Assert.True(caps.SupportsOrdering);
            Assert.True(caps.SupportsPartitioning);
            Assert.True(caps.SupportsExactlyOnce);
            Assert.True(caps.SupportsTransactions);
            Assert.True(caps.SupportsReplay);
            Assert.True(caps.SupportsPersistence);
            Assert.True(caps.SupportsConsumerGroups);
            Assert.True(caps.SupportsDeadLetterQueue);
            Assert.True(caps.SupportsHeaders);
            Assert.True(caps.SupportsCompression);
            Assert.True(caps.SupportsMessageFiltering);
            Assert.Equal(DeliveryGuarantee.ExactlyOnce, caps.DefaultDeliveryGuarantee);
        }

        [Fact]
        public void StreamingCapabilities_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = new StreamingCapabilities
            {
                SupportsOrdering = true,
                SupportsPartitioning = true,
                MaxMessageSize = 1024 * 1024,
                MaxRetention = TimeSpan.FromDays(7)
            };

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<StreamingCapabilities>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.SupportsOrdering, deserialized!.SupportsOrdering);
            Assert.Equal(original.SupportsPartitioning, deserialized.SupportsPartitioning);
            Assert.Equal(original.MaxMessageSize, deserialized.MaxMessageSize);
        }

        #endregion

        #region StreamMessage Tests

        [Fact]
        public void StreamMessage_Construction_SetsRequiredProperties()
        {
            var data = System.Text.Encoding.UTF8.GetBytes("{\"event\":\"test\"}");
            var message = new StreamMessage
            {
                Data = data,
                Key = "partition-key-1",
                MessageId = "msg-001",
                ContentType = "application/json"
            };

            Assert.Equal(data, message.Data);
            Assert.Equal("partition-key-1", message.Key);
            Assert.Equal("msg-001", message.MessageId);
            Assert.Equal("application/json", message.ContentType);
        }

        [Fact]
        public void StreamMessage_OptionalFieldsDefaultCorrectly()
        {
            var message = new StreamMessage { Data = new byte[] { 1, 2, 3 } };

            Assert.Null(message.MessageId);
            Assert.Null(message.Key);
            Assert.Null(message.Headers);
            Assert.Null(message.Partition);
            Assert.Null(message.Offset);
            Assert.Null(message.ContentType);
        }

        [Fact]
        public void StreamMessage_WithHeaders_SetsCorrectly()
        {
            var headers = new Dictionary<string, string>
            {
                ["correlation-id"] = "abc123",
                ["source"] = "producer-1"
            };

            var message = new StreamMessage
            {
                Data = new byte[] { 0 },
                Headers = headers
            };

            Assert.Equal(2, message.Headers!.Count);
            Assert.Equal("abc123", message.Headers["correlation-id"]);
        }

        #endregion

        #region PublishResult Tests

        [Fact]
        public void PublishResult_Construction_SetsRequiredProperties()
        {
            var result = new PublishResult
            {
                MessageId = "msg-100",
                Partition = 3,
                Offset = 42
            };

            Assert.Equal("msg-100", result.MessageId);
            Assert.Equal(3, result.Partition);
            Assert.Equal(42L, result.Offset);
            Assert.True(result.Success);
            Assert.Null(result.ErrorMessage);
        }

        #endregion

        #region ConsumerGroup Tests

        [Fact]
        public void ConsumerGroup_Construction_SetsRequiredProperties()
        {
            var group = new ConsumerGroup
            {
                GroupId = "my-consumer-group",
                ConsumerId = "instance-1"
            };

            Assert.Equal("my-consumer-group", group.GroupId);
            Assert.Equal("instance-1", group.ConsumerId);
        }

        #endregion

        #region StreamOffset Tests

        [Fact]
        public void StreamOffset_Beginning_ReturnsZeroOffset()
        {
            var offset = StreamOffset.Beginning();

            Assert.Equal(0, offset.Partition);
            Assert.Equal(0, offset.Offset);
        }

        [Fact]
        public void StreamOffset_End_ReturnsMaxOffset()
        {
            var offset = StreamOffset.End(2);

            Assert.Equal(2, offset.Partition);
            Assert.Equal(long.MaxValue, offset.Offset);
        }

        [Fact]
        public void StreamOffset_Construction_SetsProperties()
        {
            var offset = new StreamOffset
            {
                Partition = 5,
                Offset = 1000,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };

            Assert.Equal(5, offset.Partition);
            Assert.Equal(1000, offset.Offset);
            Assert.NotNull(offset.Timestamp);
        }

        #endregion

        #region StreamConfiguration Tests

        [Fact]
        public void StreamConfiguration_Construction_SetsProperties()
        {
            var config = new StreamConfiguration
            {
                PartitionCount = 12,
                ReplicationFactor = 3,
                RetentionPeriod = TimeSpan.FromDays(7),
                CompressionType = "lz4"
            };

            Assert.Equal(12, config.PartitionCount);
            Assert.Equal(3, config.ReplicationFactor);
            Assert.Equal(TimeSpan.FromDays(7), config.RetentionPeriod);
            Assert.Equal("lz4", config.CompressionType);
        }

        #endregion

        #region DeliveryGuarantee Enum Tests

        [Fact]
        public void DeliveryGuarantee_Has3Values()
        {
            var values = Enum.GetValues<DeliveryGuarantee>();
            Assert.Equal(3, values.Length);
            Assert.Contains(DeliveryGuarantee.AtMostOnce, values);
            Assert.Contains(DeliveryGuarantee.AtLeastOnce, values);
            Assert.Contains(DeliveryGuarantee.ExactlyOnce, values);
        }

        #endregion
    }
}
