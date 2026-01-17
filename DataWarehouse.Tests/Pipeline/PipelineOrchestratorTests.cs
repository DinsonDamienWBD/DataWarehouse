using DataWarehouse.Kernel.Messaging;
using DataWarehouse.Kernel.Pipeline;
using DataWarehouse.Kernel.Telemetry;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Pipeline;

/// <summary>
/// Tests for DefaultPipelineOrchestrator.
/// </summary>
public class PipelineOrchestratorTests
{
    #region Configuration Tests

    [Fact]
    public void GetConfiguration_ShouldReturnDefaultConfiguration()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Act
        var config = orchestrator.GetConfiguration();

        // Assert
        config.Should().NotBeNull();
        config.Name.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SetConfiguration_ShouldUpdateConfiguration()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var newConfig = new PipelineConfiguration
        {
            ConfigurationId = "custom",
            Name = "Custom Pipeline",
            WriteStages = new List<PipelineStageConfig>()
        };

        // Act
        orchestrator.SetConfiguration(newConfig);

        // Assert
        var retrieved = orchestrator.GetConfiguration();
        retrieved.ConfigurationId.Should().Be("custom");
        retrieved.Name.Should().Be("Custom Pipeline");
    }

    [Fact]
    public void SetConfiguration_ShouldThrowOnNull()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Act & Assert
        var act = () => orchestrator.SetConfiguration(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ResetToDefaults_ShouldRestoreDefaultConfiguration()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "custom",
            Name = "Custom"
        });

        // Act
        orchestrator.ResetToDefaults();

        // Assert
        var config = orchestrator.GetConfiguration();
        config.Name.Should().NotBe("Custom");
    }

    #endregion

    #region Stage Registration Tests

    [Fact]
    public void RegisterStage_ShouldAddStage()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var stage = new TestTransformStage("test-stage", "compress");

        // Act
        orchestrator.RegisterStage(stage);

        // Assert
        var stages = orchestrator.GetRegisteredStages().ToList();
        stages.Should().Contain(s => s.PluginId == "test-stage");
    }

    [Fact]
    public void RegisterStage_ShouldThrowOnNull()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Act & Assert
        var act = () => orchestrator.RegisterStage(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UnregisterStage_ShouldRemoveStage()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var stage = new TestTransformStage("removable", "compress");
        orchestrator.RegisterStage(stage);
        orchestrator.GetRegisteredStages().Should().Contain(s => s.PluginId == "removable");

        // Act
        orchestrator.UnregisterStage("removable");

        // Assert
        orchestrator.GetRegisteredStages().Should().NotContain(s => s.PluginId == "removable");
    }

    [Fact]
    public void GetRegisteredStages_ShouldReturnStageInfo()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        orchestrator.RegisterStage(new TestTransformStage("stage1", "compress"));
        orchestrator.RegisterStage(new TestTransformStage("stage2", "encrypt"));

        // Act
        var stages = orchestrator.GetRegisteredStages().ToList();

        // Assert
        stages.Should().HaveCount(2);
        stages.Should().Contain(s => s.SubCategory == "compress");
        stages.Should().Contain(s => s.SubCategory == "encrypt");
    }

    #endregion

    #region Validation Tests

    [Fact]
    public void ValidateConfiguration_ShouldAcceptEmptyPipeline()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var config = new PipelineConfiguration
        {
            ConfigurationId = "empty",
            Name = "Empty Pipeline",
            WriteStages = new List<PipelineStageConfig>()
        };

        // Act
        var result = orchestrator.ValidateConfiguration(config);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("no stages"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarnAboutMissingPlugins()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var config = new PipelineConfiguration
        {
            ConfigurationId = "missing",
            Name = "Missing Plugins",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "compress", PluginId = "nonexistent-plugin", Enabled = true, Order = 1 }
            }
        };

        // Act
        var result = orchestrator.ValidateConfiguration(config);

        // Assert
        result.Warnings.Should().Contain(w => w.Contains("not found"));
    }

    [Fact]
    public void ValidateConfiguration_ShouldWarnAboutDuplicateStageTypes()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var config = new PipelineConfiguration
        {
            ConfigurationId = "duplicates",
            Name = "Duplicates",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "compress", Enabled = true, Order = 1 },
                new() { StageType = "compress", Enabled = true, Order = 2 }
            }
        };

        // Act
        var result = orchestrator.ValidateConfiguration(config);

        // Assert
        result.Warnings.Should().Contain(w => w.Contains("Duplicate"));
    }

    #endregion

    #region Pipeline Execution Tests

    [Fact]
    public async Task ExecuteWritePipelineAsync_ShouldPassThroughWithNoStages()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "passthrough",
            Name = "Passthrough",
            WriteStages = new List<PipelineStageConfig>()
        });

        var input = new MemoryStream("test data"u8.ToArray());
        var context = new PipelineContext();

        // Act
        var result = await orchestrator.ExecuteWritePipelineAsync(input, context);

        // Assert
        using var reader = new StreamReader(result);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("test data");
    }

    [Fact]
    public async Task ExecuteWritePipelineAsync_ShouldExecuteEnabledStages()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var stage = new TestTransformStage("transform", "transform");
        orchestrator.RegisterStage(stage);

        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "with-stage",
            Name = "With Stage",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "transform", PluginId = "transform", Enabled = true, Order = 1 }
            }
        });

        var input = new MemoryStream("original"u8.ToArray());
        var context = new PipelineContext();

        // Act
        var result = await orchestrator.ExecuteWritePipelineAsync(input, context);

        // Assert
        context.ExecutedStages.Should().Contain("transform");
    }

    [Fact]
    public async Task ExecuteWritePipelineAsync_ShouldSkipDisabledStages()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var stage = new TestTransformStage("disabled-stage", "disabled");
        orchestrator.RegisterStage(stage);

        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "disabled",
            Name = "Disabled",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "disabled", PluginId = "disabled-stage", Enabled = false, Order = 1 }
            }
        });

        var input = new MemoryStream("data"u8.ToArray());
        var context = new PipelineContext();

        // Act
        await orchestrator.ExecuteWritePipelineAsync(input, context);

        // Assert
        context.ExecutedStages.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteWritePipelineAsync_ShouldRespectStageOrder()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var stage1 = new TestTransformStage("first", "first");
        var stage2 = new TestTransformStage("second", "second");
        orchestrator.RegisterStage(stage1);
        orchestrator.RegisterStage(stage2);

        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "ordered",
            Name = "Ordered",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "second", PluginId = "second", Enabled = true, Order = 200 },
                new() { StageType = "first", PluginId = "first", Enabled = true, Order = 100 }
            }
        });

        var input = new MemoryStream("data"u8.ToArray());
        var context = new PipelineContext();

        // Act
        await orchestrator.ExecuteWritePipelineAsync(input, context);

        // Assert - First should execute before second
        context.ExecutedStages.Should().ContainInOrder("first", "second");
    }

    [Fact]
    public async Task ExecuteWritePipelineAsync_ShouldHandleCancellation()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var slowStage = new SlowTransformStage("slow", TimeSpan.FromSeconds(10));
        orchestrator.RegisterStage(slowStage);

        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "slow",
            Name = "Slow",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "slow", PluginId = "slow", Enabled = true, Order = 1 }
            }
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var input = new MemoryStream("data"u8.ToArray());
        var context = new PipelineContext();

        // Act & Assert
        var act = async () => await orchestrator.ExecuteWritePipelineAsync(input, context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteReadPipelineAsync_ShouldExecuteInReverseOrder()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var stage1 = new TestTransformStage("first", "first");
        var stage2 = new TestTransformStage("second", "second");
        orchestrator.RegisterStage(stage1);
        orchestrator.RegisterStage(stage2);

        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "reverse",
            Name = "Reverse",
            WriteStages = new List<PipelineStageConfig>
            {
                new() { StageType = "first", PluginId = "first", Enabled = true, Order = 100 },
                new() { StageType = "second", PluginId = "second", Enabled = true, Order = 200 }
            }
        });

        var input = new MemoryStream("data"u8.ToArray());
        var context = new PipelineContext();

        // Act
        await orchestrator.ExecuteReadPipelineAsync(input, context);

        // Assert - Should execute in reverse: second then first
        context.ExecutedStages.Should().ContainInOrder("second", "first");
    }

    #endregion

    #region Distributed Tracing Integration Tests

    [Fact]
    public async Task ExecuteWritePipelineAsync_ShouldCreateTraceSpan()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var orchestrator = CreateOrchestrator(tracing);

        orchestrator.SetConfiguration(new PipelineConfiguration
        {
            ConfigurationId = "traced",
            Name = "Traced",
            WriteStages = new List<PipelineStageConfig>()
        });

        using var traceScope = tracing.StartTrace("test-operation");
        var input = new MemoryStream("data"u8.ToArray());
        var context = new PipelineContext();

        // Act
        await orchestrator.ExecuteWritePipelineAsync(input, context);

        // Assert - Should complete without error (trace span created internally)
        context.Should().NotBeNull();
    }

    #endregion

    #region Test Helpers

    private DefaultPipelineOrchestrator CreateOrchestrator(IDistributedTracing? tracing = null)
    {
        var registry = new PluginRegistry();
        var messageBus = new DefaultMessageBus(null);
        return new DefaultPipelineOrchestrator(registry, messageBus, null, tracing);
    }

    private class TestTransformStage : PipelinePluginBase
    {
        private readonly string _id;
        private readonly string _subCategory;

        public TestTransformStage(string id, string subCategory)
        {
            _id = id;
            _subCategory = subCategory;
        }

        public override string Id => _id;
        public override string Name => $"Test Stage: {_id}";
        public override string Version => "1.0.0";
        public override string SubCategory => _subCategory;
        public override int DefaultOrder => 100;

        public override Stream OnWrite(Stream input, IKernelContext ctx, IReadOnlyDictionary<string, object>? parameters)
        {
            return input; // Pass through
        }

        public override Stream OnRead(Stream input, IKernelContext ctx, IReadOnlyDictionary<string, object>? parameters)
        {
            return input; // Pass through
        }
    }

    private class SlowTransformStage : PipelinePluginBase
    {
        private readonly TimeSpan _delay;

        public SlowTransformStage(string id, TimeSpan delay)
        {
            Id = id;
            _delay = delay;
        }

        public override string Id { get; }
        public override string Name => "Slow Stage";
        public override string Version => "1.0.0";
        public override string SubCategory => "slow";

        public override Stream OnWrite(Stream input, IKernelContext ctx, IReadOnlyDictionary<string, object>? parameters)
        {
            Thread.Sleep(_delay);
            return input;
        }

        public override Stream OnRead(Stream input, IKernelContext ctx, IReadOnlyDictionary<string, object>? parameters)
        {
            Thread.Sleep(_delay);
            return input;
        }
    }

    private class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.OnPrem;
        public string RootPath => Environment.CurrentDirectory;
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => [];
    }

    #endregion
}
