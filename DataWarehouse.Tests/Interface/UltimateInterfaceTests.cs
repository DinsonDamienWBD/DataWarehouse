using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Interface;
using FluentAssertions;
using Xunit;
using SdkHttpMethod = DataWarehouse.SDK.Contracts.Interface.HttpMethod;

namespace DataWarehouse.Tests.Interface;

/// <summary>
/// Tests for UltimateInterface strategy contracts.
/// Uses SDK-level reflection testing since interface strategies are internal.
/// </summary>
[Trait("Category", "Unit")]
public class UltimateInterfaceTests
{
    [Fact]
    public void InterfaceStrategyBase_ShouldBeAbstract()
    {
        typeof(InterfaceStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void InterfaceStrategyBase_ShouldDefineRequiredProperties()
    {
        var type = typeof(InterfaceStrategyBase);
        type.GetProperty("Protocol").Should().NotBeNull();
        type.GetProperty("Capabilities").Should().NotBeNull();
    }

    [Fact]
    public void InterfaceStrategyBase_ShouldDefineHandleRequestAsync()
    {
        var type = typeof(InterfaceStrategyBase);
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        methods.Should().Contain(m => m.Name == "HandleRequestAsync");
    }

    [Fact]
    public void InterfaceRequest_ShouldHaveRequiredProperties()
    {
        var type = typeof(InterfaceRequest);
        type.GetProperty("Path").Should().NotBeNull();
        type.GetProperty("Method").Should().NotBeNull();
    }

    [Fact]
    public void InterfaceResponse_ShouldHaveStatusCode()
    {
        var type = typeof(InterfaceResponse);
        type.GetProperty("StatusCode").Should().NotBeNull();
    }

    [Fact]
    public void InterfaceProtocol_ShouldContainRestAndGraphQL()
    {
        Enum.GetNames<InterfaceProtocol>().Should().Contain("REST");
        Enum.GetNames<InterfaceProtocol>().Should().Contain("GraphQL");
    }

    [Fact]
    public void InterfaceProtocol_ShouldHaveMultipleValues()
    {
        var values = Enum.GetValues<InterfaceProtocol>();
        values.Length.Should().BeGreaterThanOrEqualTo(10,
            "should support many interface protocols");
    }

    [Fact]
    public void InterfaceRequest_ShouldBeConstructible()
    {
        var request = new InterfaceRequest(
            Method: SdkHttpMethod.GET,
            Path: "/api/test",
            Headers: new Dictionary<string, string>(),
            Body: ReadOnlyMemory<byte>.Empty,
            QueryParameters: new Dictionary<string, string>(),
            Protocol: InterfaceProtocol.REST);
        request.Should().NotBeNull();
        request.Path.Should().Be("/api/test");
    }

    [Fact]
    public void InterfaceResponse_ShouldBeConstructible()
    {
        var response = new InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>(),
            Body: ReadOnlyMemory<byte>.Empty);
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(200);
    }

    [Fact]
    public void IInterfaceStrategy_ShouldExist()
    {
        var type = typeof(IInterfaceStrategy);
        type.Should().NotBeNull();
        type.IsInterface.Should().BeTrue();
    }
}
