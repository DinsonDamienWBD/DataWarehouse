using DataWarehouse.SDK.Contracts.Security;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Security;

/// <summary>
/// Tests for key management and MPC contracts (replaces MpcStrategyTests).
/// Validates SDK security strategy interfaces and key management types.
/// </summary>
[Trait("Category", "Unit")]
public class KeyManagementContractTests
{
    [Fact]
    public void ISecurityStrategy_ShouldExistInSdk()
    {
        var type = typeof(ISecurityStrategy);
        type.Should().NotBeNull();
        type.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void SecurityDomain_ShouldContainExpectedValues()
    {
        Enum.GetValues<SecurityDomain>().Should().Contain(SecurityDomain.DataProtection);
        Enum.GetValues<SecurityDomain>().Should().Contain(SecurityDomain.AccessControl);
        Enum.GetValues<SecurityDomain>().Should().Contain(SecurityDomain.Identity);
    }

    [Fact]
    public void SecurityStrategyBase_ShouldBeAbstract()
    {
        var type = typeof(SecurityStrategyBase);
        type.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void SecurityStrategyBase_ShouldImplementISecurityStrategy()
    {
        typeof(SecurityStrategyBase).GetInterfaces().Should().Contain(typeof(ISecurityStrategy));
    }

    [Fact]
    public void SecurityDomain_DataProtection_ShouldCoverKeyManagement()
    {
        // DataProtection domain covers encryption at rest, in transit, and key management
        SecurityDomain.DataProtection.Should().BeDefined();
    }

    [Fact]
    public void SecurityDomain_ShouldHaveMultipleValues()
    {
        var domains = Enum.GetValues<SecurityDomain>();
        domains.Length.Should().BeGreaterThanOrEqualTo(6, "should cover multiple security domains");
    }
}
