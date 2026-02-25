using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure;

/// <summary>
/// Tests for SDK resilience infrastructure types (replaces CircuitBreakerPolicyTests,
/// TokenBucketRateLimiterTests, and SdkInfrastructureTests).
/// Uses SDK contract-level testing via reflection.
/// </summary>
[Trait("Category", "Unit")]
public class SdkResilienceTests
{
    [Fact]
    public void MessageBusBase_ShouldDefineAllRequiredMethods()
    {
        var type = typeof(MessageBusBase);
        type.Should().NotBeNull();
        type.IsAbstract.Should().BeTrue();

        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        methods.Should().Contain(m => m.Name == "PublishAsync");
        methods.Should().Contain(m => m.Name == "SendAsync");
        methods.Should().Contain(m => m.Name == "Subscribe");
        methods.Should().Contain(m => m.Name == "Unsubscribe");
        methods.Should().Contain(m => m.Name == "GetActiveTopics");
    }

    [Fact]
    public void MessageResponse_Ok_ShouldCreateSuccessResponse()
    {
        var response = MessageResponse.Ok("payload");

        response.Success.Should().BeTrue();
        response.Payload.Should().Be("payload");
        response.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void MessageResponse_Error_ShouldCreateErrorResponse()
    {
        var response = MessageResponse.Error("something failed", "ERR_001");

        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Be("something failed");
        response.ErrorCode.Should().Be("ERR_001");
    }

    [Fact]
    public void StorageResult_ShouldTrackSuccessAndDuration()
    {
        var result = new StorageResult
        {
            Success = true,
            BytesWritten = 1024,
            Duration = TimeSpan.FromMilliseconds(50),
            ProvidersUsed = ["memory"]
        };

        result.Success.Should().BeTrue();
        result.BytesWritten.Should().Be(1024);
        result.ProvidersUsed.Should().ContainSingle("memory");
    }

    [Fact]
    public void StorageResult_ShouldTrackErrors()
    {
        var result = new StorageResult
        {
            Success = false,
            Error = "Storage full"
        };

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Storage full");
    }

    [Fact]
    public void PluginCategory_ShouldContainExpectedValues()
    {
        Enum.GetValues<PluginCategory>().Should().Contain(PluginCategory.StorageProvider);
        Enum.GetValues<PluginCategory>().Should().Contain(PluginCategory.SecurityProvider);
    }

    [Fact]
    public void CapabilityCategory_ShouldContainStorageAndSecurity()
    {
        Enum.GetValues<SDK.Contracts.CapabilityCategory>().Should().Contain(SDK.Contracts.CapabilityCategory.Storage);
        Enum.GetValues<SDK.Contracts.CapabilityCategory>().Should().Contain(SDK.Contracts.CapabilityCategory.Security);
    }

    [Fact]
    public void MessageTopics_ShouldDefineStandardTopics()
    {
        MessageTopics.SystemStartup.Should().Be("system.startup");
        MessageTopics.SystemShutdown.Should().Be("system.shutdown");
        MessageTopics.StorageSave.Should().Be("storage.save");
        MessageTopics.StorageLoad.Should().Be("storage.load");
        MessageTopics.PluginLoaded.Should().Be("plugin.loaded");
    }

    [Fact]
    public void MessageBusStatistics_ShouldHaveDefaultValues()
    {
        var stats = new MessageBusStatistics();
        stats.TotalMessagesPublished.Should().Be(0);
        stats.TotalMessagesDelivered.Should().Be(0);
        stats.ActiveSubscriptions.Should().Be(0);
        stats.MessagesByTopic.Should().NotBeNull();
    }
}
