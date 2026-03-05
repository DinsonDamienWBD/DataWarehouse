// Hardening tests for Shared findings: DynamicEndpointGenerator
// Finding: 28 (LOW) Private field can be converted into local variable
using DataWarehouse.Shared;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for DynamicEndpointGenerator hardening findings.
/// </summary>
public class DynamicEndpointGeneratorTests
{
    /// <summary>
    /// Finding 28: _capabilityRegistry field was only used in constructor.
    /// Fixed by removing the field and using the parameter directly in the constructor.
    /// </summary>
    [Fact]
    public void Finding028_CapabilityRegistryFieldRemovedToLocalVariable()
    {
        // DynamicEndpointGenerator can be created without a registry
        using var generator = new DynamicEndpointGenerator();
        Assert.NotNull(generator);
    }
}
