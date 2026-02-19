using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataGovernance.Moonshots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DataWarehouse.Tests.Moonshots;

/// <summary>
/// Integration tests for the moonshot pipeline orchestration system.
/// Validates full pipeline execution, stage ordering, feature-flag skipping,
/// fail-open semantics, and context data flow between stages.
/// </summary>
public sealed class MoonshotPipelineTests
{
    private readonly MoonshotRegistryImpl _registry = new();
    private readonly MoonshotConfiguration _config;
    private readonly ILogger<MoonshotOrchestrator> _logger = NullLogger<MoonshotOrchestrator>.Instance;

    public MoonshotPipelineTests()
    {
        _config = MoonshotConfigurationDefaults.CreateProductionDefaults();
    }

    /// <summary>
    /// Helper: creates a mock message bus that returns success for all SendAsync calls.
    /// </summary>
    private static Mock<IMessageBus> CreateSuccessMessageBus()
    {
        var busMock = new Mock<IMessageBus>();
        busMock.Setup(b => b.SendAsync(It.IsAny<string>(), It.IsAny<PluginMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageResponse { Success = true, Payload = new Dictionary<string, object> { ["result"] = "ok" } });
        busMock.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<PluginMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return busMock;
    }

    /// <summary>
    /// Helper: creates a recording stage that tracks execution order and can optionally write context properties.
    /// </summary>
    private sealed class RecordingStage : IMoonshotPipelineStage
    {
        private readonly List<MoonshotId> _executionOrder;
        private readonly string? _contextKey;
        private readonly object? _contextValue;
        private readonly bool _shouldThrow;

        public MoonshotId Id { get; }

        public RecordingStage(
            MoonshotId id,
            List<MoonshotId> executionOrder,
            string? contextKey = null,
            object? contextValue = null,
            bool shouldThrow = false)
        {
            Id = id;
            _executionOrder = executionOrder;
            _contextKey = contextKey;
            _contextValue = contextValue;
            _shouldThrow = shouldThrow;
        }

        public Task<bool> CanExecuteAsync(MoonshotPipelineContext context, CancellationToken ct)
            => Task.FromResult(true);

        public Task<MoonshotStageResult> ExecuteAsync(MoonshotPipelineContext context, CancellationToken ct)
        {
            if (_shouldThrow)
                throw new InvalidOperationException($"Stage {Id} simulated failure");

            _executionOrder.Add(Id);

            if (_contextKey is not null && _contextValue is not null)
                context.SetProperty(_contextKey, _contextValue);

            return Task.FromResult(new MoonshotStageResult(
                Stage: Id,
                Success: true,
                Duration: TimeSpan.FromMilliseconds(1)));
        }
    }

    [Fact]
    public async Task MoonshotPipeline_ExecuteDefaultPipeline_RunsAllStagesInOrder()
    {
        // Arrange
        var busMock = CreateSuccessMessageBus();
        var executionOrder = new List<MoonshotId>();
        var orchestrator = new MoonshotOrchestrator(_registry, _config, _logger);

        var expectedOrder = new[]
        {
            MoonshotId.DataConsciousness, MoonshotId.UniversalTags,
            MoonshotId.CompliancePassports, MoonshotId.SovereigntyMesh,
            MoonshotId.ZeroGravityStorage, MoonshotId.CryptoTimeLocks,
            MoonshotId.SemanticSync, MoonshotId.ChaosVaccination,
            MoonshotId.CarbonAwareLifecycle, MoonshotId.UniversalFabric
        };

        foreach (var id in expectedOrder)
        {
            orchestrator.RegisterStage(new RecordingStage(id, executionOrder));
        }

        var context = new MoonshotPipelineContext
        {
            ObjectId = "test-obj-001",
            MessageBus = busMock.Object,
            Logger = NullLogger.Instance
        };

        // Act
        var result = await orchestrator.ExecuteDefaultPipelineAsync(context, CancellationToken.None);

        // Assert
        Assert.True(result.AllSucceeded);
        Assert.Equal(10, result.StageResults.Count);
        Assert.Equal(expectedOrder, executionOrder);
    }

    [Fact]
    public async Task MoonshotPipeline_DisabledMoonshot_SkipsStage()
    {
        // Arrange: create config with DataConsciousness disabled
        var moonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>();
        foreach (var kvp in _config.Moonshots)
        {
            if (kvp.Key == MoonshotId.DataConsciousness)
                moonshots[kvp.Key] = kvp.Value with { Enabled = false };
            else
                moonshots[kvp.Key] = kvp.Value;
        }

        var disabledConfig = new MoonshotConfiguration
        {
            Moonshots = new System.Collections.ObjectModel.ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(moonshots),
            Level = ConfigHierarchyLevel.Instance
        };

        var busMock = CreateSuccessMessageBus();
        var executionOrder = new List<MoonshotId>();
        var orchestrator = new MoonshotOrchestrator(_registry, disabledConfig, _logger);

        foreach (var id in Enum.GetValues<MoonshotId>())
        {
            orchestrator.RegisterStage(new RecordingStage(id, executionOrder));
        }

        var context = new MoonshotPipelineContext
        {
            ObjectId = "test-obj-002",
            MessageBus = busMock.Object,
            Logger = NullLogger.Instance
        };

        // Act
        var result = await orchestrator.ExecuteDefaultPipelineAsync(context, CancellationToken.None);

        // Assert
        Assert.True(result.AllSucceeded);
        Assert.Equal(9, result.StageResults.Count);
        Assert.DoesNotContain(MoonshotId.DataConsciousness, executionOrder);
    }

    [Fact]
    public async Task MoonshotPipeline_StageFailure_ContinuesRemaining()
    {
        // Arrange: consciousness stage throws, rest succeed
        var busMock = CreateSuccessMessageBus();
        var executionOrder = new List<MoonshotId>();
        var orchestrator = new MoonshotOrchestrator(_registry, _config, _logger);

        orchestrator.RegisterStage(new RecordingStage(
            MoonshotId.DataConsciousness, executionOrder, shouldThrow: true));

        foreach (var id in Enum.GetValues<MoonshotId>())
        {
            if (id != MoonshotId.DataConsciousness)
                orchestrator.RegisterStage(new RecordingStage(id, executionOrder));
        }

        var context = new MoonshotPipelineContext
        {
            ObjectId = "test-obj-003",
            MessageBus = busMock.Object,
            Logger = NullLogger.Instance
        };

        // Act
        var result = await orchestrator.ExecuteDefaultPipelineAsync(context, CancellationToken.None);

        // Assert: fail-open -- remaining 9 stages execute
        Assert.False(result.AllSucceeded);
        var consciousnessResult = result.StageResults.FirstOrDefault(r => r.Stage == MoonshotId.DataConsciousness);
        Assert.NotNull(consciousnessResult);
        Assert.False(consciousnessResult.Success);
        Assert.Contains("simulated failure", consciousnessResult.Error);

        // The other 9 stages still executed
        Assert.Equal(9, executionOrder.Count);
        Assert.DoesNotContain(MoonshotId.DataConsciousness, executionOrder);
    }

    [Fact]
    public async Task MoonshotPipeline_ContextFlowsData_BetweenStages()
    {
        // Arrange: ConsciousnessStage writes a score, TagsStage reads it
        var busMock = CreateSuccessMessageBus();
        var executionOrder = new List<MoonshotId>();
        var orchestrator = new MoonshotOrchestrator(_registry, _config, _logger);
        int? receivedScore = null;

        // Stage that writes ConsciousnessScore to context
        orchestrator.RegisterStage(new RecordingStage(
            MoonshotId.DataConsciousness, executionOrder,
            contextKey: MoonshotContextKeys.ConsciousnessScore,
            contextValue: 85));

        // Custom stage that reads ConsciousnessScore from context
        var readingStage = new ContextReadingStage(
            MoonshotId.UniversalTags,
            executionOrder,
            MoonshotContextKeys.ConsciousnessScore,
            score => receivedScore = score);
        orchestrator.RegisterStage(readingStage);

        // Fill remaining stages
        foreach (var id in Enum.GetValues<MoonshotId>())
        {
            if (id != MoonshotId.DataConsciousness && id != MoonshotId.UniversalTags)
                orchestrator.RegisterStage(new RecordingStage(id, executionOrder));
        }

        var context = new MoonshotPipelineContext
        {
            ObjectId = "test-obj-004",
            MessageBus = busMock.Object,
            Logger = NullLogger.Instance
        };

        // Act
        await orchestrator.ExecuteDefaultPipelineAsync(context, CancellationToken.None);

        // Assert: tags stage received the score written by consciousness stage
        Assert.Equal(85, receivedScore);
    }

    /// <summary>
    /// Stage that reads a property from context during execution for verification.
    /// </summary>
    private sealed class ContextReadingStage : IMoonshotPipelineStage
    {
        private readonly List<MoonshotId> _executionOrder;
        private readonly string _contextKey;
        private readonly Action<int?> _onRead;

        public MoonshotId Id { get; }

        public ContextReadingStage(
            MoonshotId id,
            List<MoonshotId> executionOrder,
            string contextKey,
            Action<int?> onRead)
        {
            Id = id;
            _executionOrder = executionOrder;
            _contextKey = contextKey;
            _onRead = onRead;
        }

        public Task<bool> CanExecuteAsync(MoonshotPipelineContext context, CancellationToken ct)
            => Task.FromResult(true);

        public Task<MoonshotStageResult> ExecuteAsync(MoonshotPipelineContext context, CancellationToken ct)
        {
            _executionOrder.Add(Id);
            var score = context.GetProperty<int>(_contextKey);
            _onRead(score);

            return Task.FromResult(new MoonshotStageResult(
                Stage: Id, Success: true, Duration: TimeSpan.FromMilliseconds(1)));
        }
    }

    [Theory]
    [InlineData(MoonshotId.UniversalTags)]
    [InlineData(MoonshotId.DataConsciousness)]
    [InlineData(MoonshotId.CompliancePassports)]
    [InlineData(MoonshotId.SovereigntyMesh)]
    [InlineData(MoonshotId.ZeroGravityStorage)]
    [InlineData(MoonshotId.CryptoTimeLocks)]
    [InlineData(MoonshotId.SemanticSync)]
    [InlineData(MoonshotId.ChaosVaccination)]
    [InlineData(MoonshotId.CarbonAwareLifecycle)]
    [InlineData(MoonshotId.UniversalFabric)]
    public async Task MoonshotStage_Executes_ReturnsResult(MoonshotId id)
    {
        // Arrange: create the actual stage implementation and a mock bus
        var busMock = CreateSuccessMessageBus();
        var stage = CreateStage(id);
        var context = new MoonshotPipelineContext
        {
            ObjectId = $"test-{id}",
            MessageBus = busMock.Object,
            Logger = NullLogger.Instance
        };

        // For SovereigntyMesh, it requires a CompliancePassport in context to pass CanExecuteAsync
        if (id == MoonshotId.SovereigntyMesh)
            context.SetProperty(MoonshotContextKeys.CompliancePassport, new Dictionary<string, object> { ["status"] = "issued" });

        // Act
        var canExecute = await stage.CanExecuteAsync(context, CancellationToken.None);
        Assert.True(canExecute, $"Stage {id} CanExecuteAsync should return true when preconditions are met");

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(id, result.Stage);
        Assert.True(result.Success, $"Stage {id} should succeed with a mock bus returning success");
    }

    /// <summary>
    /// Creates the actual pipeline stage instance for the given moonshot.
    /// </summary>
    private static IMoonshotPipelineStage CreateStage(MoonshotId id) => id switch
    {
        MoonshotId.UniversalTags => new UniversalTagsStage(),
        MoonshotId.DataConsciousness => new DataConsciousnessStage(),
        MoonshotId.CompliancePassports => new CompliancePassportsStage(),
        MoonshotId.SovereigntyMesh => new SovereigntyMeshStage(),
        MoonshotId.ZeroGravityStorage => new ZeroGravityStorageStage(),
        MoonshotId.CryptoTimeLocks => new CryptoTimeLocksStage(),
        MoonshotId.SemanticSync => new SemanticSyncStage(),
        MoonshotId.ChaosVaccination => new ChaosVaccinationStage(),
        MoonshotId.CarbonAwareLifecycle => new CarbonAwareLifecycleStage(),
        MoonshotId.UniversalFabric => new UniversalFabricStage(),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
}
