using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Pipeline;

/// <summary>
/// Tests for pipeline orchestrator contracts (replaces PipelineOrchestratorTests).
/// Validates IPipelineOrchestrator interface and related types.
/// </summary>
[Trait("Category", "Unit")]
public class PipelineContractTests
{
    [Fact]
    public void IPipelineOrchestrator_ShouldExist()
    {
        var type = typeof(IPipelineOrchestrator);
        type.Should().NotBeNull();
        type.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void PipelineConfiguration_ShouldHaveDefaultValues()
    {
        var config = new PipelineConfiguration();
        config.WriteStages.Should().NotBeNull();
        config.WriteStages.Should().BeEmpty();
        config.Name.Should().Be("Default");
    }

    [Fact]
    public void PipelineConfiguration_CreateDefault_ShouldHaveTwoStages()
    {
        var config = PipelineConfiguration.CreateDefault();

        config.IsDefault.Should().BeTrue();
        config.WriteStages.Should().HaveCount(2);
        config.WriteStages[0].StageType.Should().Be("Compression");
        config.WriteStages[1].StageType.Should().Be("Encryption");
    }

    [Fact]
    public void PipelineContext_ShouldBeDisposable()
    {
        typeof(PipelineContext).GetInterfaces().Should().Contain(typeof(IDisposable));
    }

    [Fact]
    public void PipelineContext_ShouldSupportParameters()
    {
        using var context = new PipelineContext();
        context.Parameters["key1"] = "value1";

        context.Parameters.Should().ContainKey("key1");
        context.Parameters["key1"].Should().Be("value1");
    }

    [Fact]
    public void PipelineValidationResult_ShouldTrackErrors()
    {
        var result = new PipelineValidationResult
        {
            IsValid = false,
            Errors = ["Stage 'encrypt' requires encryption plugin"]
        };

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
    }

    [Fact]
    public void PipelineStageConfig_ShouldHaveDefaultEnabled()
    {
        var stage = new PipelineStageConfig();
        stage.Enabled.Should().BeTrue();
        stage.Parameters.Should().NotBeNull();
    }
}
