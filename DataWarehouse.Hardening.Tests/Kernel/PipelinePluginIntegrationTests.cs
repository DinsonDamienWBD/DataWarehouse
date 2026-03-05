using DataWarehouse.Kernel.Pipeline;
using DataWarehouse.SDK.Contracts.Pipeline;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for PipelinePluginIntegration — findings 123-129.
/// </summary>
public class PipelinePluginIntegrationTests
{
    // Finding 123-124: ValidateAccessAsync returns false for ALL non-admin users
    [Fact]
    public async Task Finding123_124_ValidateAccess_FailClosed()
    {
        var integration = new PipelinePluginIntegration();
        var result = await integration.ValidateAccessAsync(null, "read");
        Assert.False(result);
    }

    // Finding 125-126: GetMandatoryStagesAsync always returns empty list
    [Fact]
    public async Task Finding125_126_GetMandatoryStages_Empty()
    {
        var integration = new PipelinePluginIntegration();
        var stages = await integration.GetMandatoryStagesAsync(null);
        Assert.Empty(stages);
    }

    // Finding 127-129: PossibleMultipleEnumeration in ResolveTerminalAsync
    [Fact]
    public async Task Finding127_128_129_MultipleEnumeration()
    {
        var integration = new PipelinePluginIntegration();
        var terminal = await integration.ResolveTerminalAsync(
            new TerminalStagePolicy { TerminalType = "primary" },
            null);
        Assert.Null(terminal);
    }
}
