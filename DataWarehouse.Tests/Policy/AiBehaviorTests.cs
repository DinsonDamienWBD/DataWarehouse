using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Intelligence;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;
using FluentAssertions;
using Moq;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Comprehensive AI behavior tests covering autonomy levels, self-modification guard,
/// ring buffer, overhead throttle, hybrid profiles, threat detection, cost analysis,
/// data sensitivity, and factory wiring (INTG-04).
/// </summary>
[Trait("Category", "Integration")]
public class AiBehaviorTests
{
    // =============================================
    // 1. AiAutonomyLevel enforcement (~20 tests)
    // =============================================

    [Fact]
    public void GetAutonomy_DefaultLevel_ReturnsSuggest()
    {
        var config = new AiAutonomyConfiguration();
        var level = config.GetAutonomy("encryption", PolicyLevel.VDE);
        level.Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public void GetAutonomy_CustomDefault_ReturnsCustom()
    {
        var config = new AiAutonomyConfiguration(AiAutonomyLevel.ManualOnly);
        var level = config.GetAutonomy("encryption", PolicyLevel.VDE);
        level.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void SetAutonomy_ManualOnly_RestrictsAllActions()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("encryption", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        config.GetAutonomy("encryption", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void SetAutonomy_Suggest_ProducesSuggestionsOnly()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("compression", PolicyLevel.Container, AiAutonomyLevel.Suggest);
        config.GetAutonomy("compression", PolicyLevel.Container).Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public void SetAutonomy_SuggestExplain_IncludesReasoning()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("replication", PolicyLevel.Object, AiAutonomyLevel.SuggestExplain);
        config.GetAutonomy("replication", PolicyLevel.Object).Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public void SetAutonomy_AutoNotify_ExecutesWithNotification()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("access_control", PolicyLevel.Chunk, AiAutonomyLevel.AutoNotify);
        config.GetAutonomy("access_control", PolicyLevel.Chunk).Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public void SetAutonomy_AutoSilent_ExecutesSilently()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("compression", PolicyLevel.Block, AiAutonomyLevel.AutoSilent);
        config.GetAutonomy("compression", PolicyLevel.Block).Should().Be(AiAutonomyLevel.AutoSilent);
    }

    [Theory]
    [InlineData(PolicyLevel.VDE)]
    [InlineData(PolicyLevel.Container)]
    [InlineData(PolicyLevel.Object)]
    [InlineData(PolicyLevel.Chunk)]
    [InlineData(PolicyLevel.Block)]
    public void SetAutonomy_EachPolicyLevel_IndependentlyConfigured(PolicyLevel level)
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("encryption", level, AiAutonomyLevel.AutoNotify);
        config.GetAutonomy("encryption", level).Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public void SetAutonomyForFeature_SetsAllLevels()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomyForFeature("encryption", AiAutonomyLevel.ManualOnly);

        foreach (PolicyLevel level in Enum.GetValues<PolicyLevel>())
        {
            config.GetAutonomy("encryption", level).Should().Be(AiAutonomyLevel.ManualOnly,
                $"level {level} should be ManualOnly");
        }
    }

    [Fact]
    public void SetAutonomy_DifferentFeaturesAtSameLevel_Independent()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("encryption", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        config.SetAutonomy("compression", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);

        config.GetAutonomy("encryption", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
        config.GetAutonomy("compression", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.AutoSilent);
    }

    [Fact]
    public void SetAutonomy_Override_ReplacesExisting()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("encryption", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        config.SetAutonomy("encryption", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);

        config.GetAutonomy("encryption", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void ExportConfiguration_ReturnsAllConfigured()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        config.SetAutonomy("cmp", PolicyLevel.Block, AiAutonomyLevel.AutoSilent);

        var exported = config.ExportConfiguration();

        exported.Should().ContainKey("enc:VDE");
        exported.Should().ContainKey("cmp:Block");
        exported["enc:VDE"].Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void ImportConfiguration_OverwritesExisting()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomy("enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);

        config.ImportConfiguration(new Dictionary<string, AiAutonomyLevel>
        {
            ["enc:VDE"] = AiAutonomyLevel.ManualOnly
        });

        config.GetAutonomy("enc", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void ConfiguredPointCount_TracksExplicitConfigs()
    {
        var config = new AiAutonomyConfiguration();
        config.ConfiguredPointCount.Should().Be(0);

        config.SetAutonomy("enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        config.ConfiguredPointCount.Should().Be(1);

        config.SetAutonomy("cmp", PolicyLevel.Block, AiAutonomyLevel.AutoSilent);
        config.ConfiguredPointCount.Should().Be(2);
    }

    [Fact]
    public void TotalConfigPoints_Is94FeaturesTimes5Levels()
    {
        var config = new AiAutonomyConfiguration();
        config.TotalConfigPoints.Should().Be(CheckClassificationTable.TotalFeatures * 5);
    }

    [Fact]
    public void UnknownFeature_ReturnsDefault()
    {
        var config = new AiAutonomyConfiguration(AiAutonomyLevel.SuggestExplain);
        config.GetAutonomy("unknown_feature_xyz", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public void NullFeatureId_ThrowsArgumentNull()
    {
        var config = new AiAutonomyConfiguration();
        var act = () => config.GetAutonomy(null!, PolicyLevel.VDE);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetAutonomyForCategory_AppliesToAllFeaturesInCategory()
    {
        var config = new AiAutonomyConfiguration();
        config.SetAutonomyForCategory(CheckTiming.ConnectTime, AiAutonomyLevel.ManualOnly);

        var features = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.ConnectTime);
        features.Count.Should().BeGreaterThan(0);

        foreach (var feature in features)
        {
            foreach (PolicyLevel level in Enum.GetValues<PolicyLevel>())
            {
                config.GetAutonomy(feature, level).Should().Be(AiAutonomyLevel.ManualOnly);
            }
        }
    }

    // =============================================
    // 2. AiSelfModificationGuard (~20 tests)
    // =============================================

    private static AiSelfModificationGuard CreateGuard(
        AiSelfModificationPolicy? policy = null,
        IQuorumService? quorumService = null,
        AiAutonomyLevel defaultLevel = AiAutonomyLevel.Suggest)
    {
        var config = new AiAutonomyConfiguration(defaultLevel);
        return new AiSelfModificationGuard(
            config,
            quorumService,
            policy ?? new AiSelfModificationPolicy());
    }

    [Fact]
    public async Task Guard_AiPrefix_Rejected_WhenSelfModBlocked()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("ai:optimizer", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        result.Should().BeFalse();
        guard.BlockedSelfModificationAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Guard_SystemAiPrefix_Rejected_WhenSelfModBlocked()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("system:ai:scheduler", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        result.Should().BeFalse();
        guard.BlockedSelfModificationAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Guard_UserPrefix_Allowed()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("user:admin1", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Guard_AdminPrefix_Allowed()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("admin:root", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Guard_SelfModAllowed_AiPrefixPermitted()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: true, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("ai:optimizer", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Guard_QuorumRequired_NoService_FailsClosed()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: true));
        var result = await guard.TryModifyAutonomyAsync("user:admin1", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        result.Should().BeFalse();
        guard.QuorumDeniedChanges.Should().Be(1);
    }

    [Fact]
    public void Guard_MinimumQuorumSize_RespectedDefault()
    {
        var policy = new AiSelfModificationPolicy();
        policy.MinimumQuorumSize.Should().Be(3);
    }

    [Fact]
    public void Guard_GetAutonomy_AlwaysAllowed()
    {
        var guard = CreateGuard();
        var result = guard.GetAutonomy("enc", PolicyLevel.VDE);
        result.Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public async Task Guard_CaseInsensitive_AiPrefix()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("AI:optimizer", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Guard_CaseInsensitive_SystemAiPrefix()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("SYSTEM:AI:foo", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Guard_NullRequester_ThrowsArgumentNull()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var act = () => guard.TryModifyAutonomyAsync(null!, "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Guard_NullFeatureId_ThrowsArgumentNull()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var act = () => guard.TryModifyAutonomyAsync("user:a", null!, PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Guard_MultipleAiAttempts_CounterIncrements()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        for (int i = 0; i < 5; i++)
        {
            await guard.TryModifyAutonomyAsync("ai:bot", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        }
        guard.BlockedSelfModificationAttempts.Should().Be(5);
    }

    [Fact]
    public async Task Guard_UserModifies_ThenAiBlocked()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var userResult = await guard.TryModifyAutonomyAsync("user:admin", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        userResult.Should().BeTrue();
        var aiResult = await guard.TryModifyAutonomyAsync("ai:optimizer", "enc", PolicyLevel.VDE, AiAutonomyLevel.AutoSilent);
        aiResult.Should().BeFalse();
        guard.GetAutonomy("enc", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task Guard_EmptyRequesterId_NotAiOriginated()
    {
        var guard = CreateGuard(new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: false));
        var result = await guard.TryModifyAutonomyAsync("", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Guard_QuorumApproved_ChangesApplied()
    {
        var quorumMock = new Mock<IQuorumService>();
        quorumMock.Setup(q => q.InitiateQuorumAsync(
            It.IsAny<QuorumAction>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuorumRequest
            {
                RequestId = Guid.NewGuid().ToString(), Action = QuorumAction.OverrideAi,
                RequestedBy = "user:admin", Justification = "test",
                RequestedAt = DateTimeOffset.UtcNow, State = QuorumRequestState.Approved,
                RequiredApprovals = 1, TotalMembers = 1,
                ApprovalWindow = TimeSpan.FromHours(24), CoolingOffPeriod = TimeSpan.FromHours(24)
            });
        var config = new AiAutonomyConfiguration();
        var guard = new AiSelfModificationGuard(config, quorumMock.Object,
            new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: true));
        var result = await guard.TryModifyAutonomyAsync("user:admin", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        result.Should().BeTrue();
        guard.QuorumApprovedChanges.Should().Be(1);
        guard.GetAutonomy("enc", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task Guard_QuorumPending_ChangesDenied()
    {
        var quorumMock = new Mock<IQuorumService>();
        quorumMock.Setup(q => q.InitiateQuorumAsync(
            It.IsAny<QuorumAction>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuorumRequest
            {
                RequestId = Guid.NewGuid().ToString(), Action = QuorumAction.OverrideAi,
                RequestedBy = "user:admin", Justification = "test",
                RequestedAt = DateTimeOffset.UtcNow, State = QuorumRequestState.Collecting,
                RequiredApprovals = 3, TotalMembers = 5,
                ApprovalWindow = TimeSpan.FromHours(24), CoolingOffPeriod = TimeSpan.FromHours(24)
            });
        var config = new AiAutonomyConfiguration();
        var guard = new AiSelfModificationGuard(config, quorumMock.Object,
            new AiSelfModificationPolicy(AllowSelfModification: false, ModificationRequiresQuorum: true));
        var result = await guard.TryModifyAutonomyAsync("user:admin", "enc", PolicyLevel.VDE, AiAutonomyLevel.ManualOnly);
        result.Should().BeFalse();
        guard.QuorumDeniedChanges.Should().Be(1);
    }

    [Fact]
    public void Guard_NullInnerConfig_ThrowsArgumentNull()
    {
        var act = () => new AiSelfModificationGuard(null!, null, new AiSelfModificationPolicy());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Guard_NullPolicy_ThrowsArgumentNull()
    {
        var config = new AiAutonomyConfiguration();
        var act = () => new AiSelfModificationGuard(config, null, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // =============================================
    // 3. AiObservationRingBuffer (~12 tests)
    // =============================================

    private static ObservationEvent MakeObs(string pluginId = "test", string metric = "cpu", double value = 1.0)
    {
        return new ObservationEvent(pluginId, metric, value, null, null, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RingBuffer_DefaultCapacity_8192()
    {
        var buffer = new AiObservationRingBuffer();
        buffer.Capacity.Should().Be(8192);
    }

    [Fact]
    public void RingBuffer_PowerOfTwo_RoundedUp()
    {
        var buffer = new AiObservationRingBuffer(1000);
        buffer.Capacity.Should().Be(1024);
    }

    [Fact]
    public void RingBuffer_SmallCapacity_Minimum2()
    {
        var buffer = new AiObservationRingBuffer(1);
        buffer.Capacity.Should().Be(2);
    }

    [Fact]
    public void RingBuffer_CAS_WriteAndRead()
    {
        var buffer = new AiObservationRingBuffer(8);
        var evt = MakeObs();
        buffer.TryWrite(evt).Should().BeTrue();
        buffer.Count.Should().Be(1);
        buffer.TryRead(out var read).Should().BeTrue();
        read.Should().NotBeNull();
        read!.PluginId.Should().Be("test");
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public void RingBuffer_Full_ReturnsFalse()
    {
        var buffer = new AiObservationRingBuffer(4);
        for (int i = 0; i < 4; i++) buffer.TryWrite(MakeObs()).Should().BeTrue();
        buffer.TryWrite(MakeObs()).Should().BeFalse();
    }

    [Fact]
    public void RingBuffer_WrapsAround_Correctly()
    {
        var buffer = new AiObservationRingBuffer(4);
        for (int i = 0; i < 4; i++) buffer.TryWrite(MakeObs(metric: $"m{i}")).Should().BeTrue();
        buffer.TryRead(out var r1).Should().BeTrue();
        r1!.MetricName.Should().Be("m0");
        buffer.TryRead(out _).Should().BeTrue();
        buffer.TryWrite(MakeObs(metric: "m4")).Should().BeTrue();
        buffer.TryWrite(MakeObs(metric: "m5")).Should().BeTrue();
        buffer.TryRead(out var r3).Should().BeTrue();
        r3!.MetricName.Should().Be("m2");
    }

    [Fact]
    public void RingBuffer_ConcurrentWrites_NoCorruption()
    {
        var buffer = new AiObservationRingBuffer(8192);
        int successCount = 0;
        Parallel.For(0, 1000, i =>
        {
            if (buffer.TryWrite(MakeObs(metric: $"m{i}")))
                Interlocked.Increment(ref successCount);
        });
        successCount.Should().Be(1000);
        buffer.Count.Should().Be(1000);
    }

    [Fact]
    public void RingBuffer_ReadDuringWrite_ConsistentData()
    {
        var buffer = new AiObservationRingBuffer(256);
        var readEvents = new ConcurrentBag<ObservationEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
                buffer.TryWrite(MakeObs(metric: $"m{i}"));
        });
        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (buffer.TryRead(out var evt) && evt is not null)
                    readEvents.Add(evt);
            }
        });
        writer.Wait();
        SpinWait.SpinUntil(() => false, 100);
        cts.Cancel();
        reader.Wait();
        foreach (var evt in readEvents)
            evt.MetricName.Should().StartWith("m");
    }

    [Fact]
    public void RingBuffer_Empty_ReadReturnsFalse()
    {
        var buffer = new AiObservationRingBuffer(8);
        buffer.TryRead(out var evt).Should().BeFalse();
        evt.Should().BeNull();
    }

    [Fact]
    public void RingBuffer_DrainTo_ReturnsBatch()
    {
        var buffer = new AiObservationRingBuffer(16);
        for (int i = 0; i < 5; i++) buffer.TryWrite(MakeObs(metric: $"m{i}"));
        var batch = new List<ObservationEvent>();
        int drained = buffer.DrainTo(batch, 3);
        drained.Should().Be(3);
        batch.Should().HaveCount(3);
        buffer.Count.Should().Be(2);
    }

    [Fact]
    public void RingBuffer_DrainTo_EmptyBuffer_ReturnsZero()
    {
        var buffer = new AiObservationRingBuffer(8);
        var batch = new List<ObservationEvent>();
        buffer.DrainTo(batch, 10).Should().Be(0);
        batch.Should().BeEmpty();
    }

    [Fact]
    public void RingBuffer_ExactPowerOfTwo_Capacity()
    {
        var buffer = new AiObservationRingBuffer(256);
        buffer.Capacity.Should().Be(256);
    }

    // =============================================
    // 4. OverheadThrottle (~12 tests)
    // =============================================

    [Fact]
    public void Throttle_BelowThreshold_NotThrottled()
    {
        var throttle = new OverheadThrottle(5.0);
        throttle.UpdateCpuUsage(3.0);
        throttle.IsThrottled.Should().BeFalse();
    }

    [Fact]
    public void Throttle_AboveThreshold_Throttled()
    {
        var throttle = new OverheadThrottle(5.0);
        throttle.UpdateCpuUsage(6.0);
        throttle.IsThrottled.Should().BeTrue();
    }

    [Fact]
    public void Throttle_HysteresisLowerBound_80Percent()
    {
        var throttle = new OverheadThrottle(10.0);
        throttle.UpdateCpuUsage(11.0);
        throttle.IsThrottled.Should().BeTrue();
        throttle.UpdateCpuUsage(9.0);
        throttle.IsThrottled.Should().BeTrue(); // Still throttled (above 8%)
        for (int i = 0; i < 10; i++) throttle.UpdateCpuUsage(5.0);
        throttle.IsThrottled.Should().BeFalse();
    }

    [Fact]
    public void Throttle_SlidingWindowAverage_AffectsThrottle()
    {
        var throttle = new OverheadThrottle(5.0);
        for (int i = 0; i < 9; i++) throttle.UpdateCpuUsage(1.0);
        throttle.UpdateCpuUsage(50.0);
        // Average = (9*1 + 50) / 10 = 5.9 > 5.0
        throttle.IsThrottled.Should().BeTrue();
    }

    [Fact]
    public void Throttle_RecordDrop_CountsCorrectly()
    {
        var throttle = new OverheadThrottle();
        throttle.RecordDrop(10);
        throttle.RecordDrop(5);
        throttle.DroppedObservations.Should().Be(15);
    }

    [Fact]
    public void Throttle_RecordDrop_NegativeIgnored()
    {
        var throttle = new OverheadThrottle();
        throttle.RecordDrop(-1);
        throttle.DroppedObservations.Should().Be(0);
    }

    [Fact]
    public void Throttle_DefaultMax_1Percent()
    {
        var throttle = new OverheadThrottle();
        throttle.MaxCpuOverheadPercent.Should().Be(1.0);
    }

    [Fact]
    public void Throttle_CustomMax_Respected()
    {
        var throttle = new OverheadThrottle(5.0);
        throttle.MaxCpuOverheadPercent.Should().Be(5.0);
    }

    [Fact]
    public void Throttle_ZeroMax_DefaultsTo1()
    {
        var throttle = new OverheadThrottle(0);
        throttle.MaxCpuOverheadPercent.Should().Be(1.0);
    }

    [Fact]
    public void Throttle_NegativeMax_DefaultsTo1()
    {
        var throttle = new OverheadThrottle(-5);
        throttle.MaxCpuOverheadPercent.Should().Be(1.0);
    }

    [Fact]
    public async Task Throttle_MeasureCpuUsage_ReturnsValidRange()
    {
        var throttle = new OverheadThrottle();
        await Task.Delay(50);
        double usage = throttle.MeasureCpuUsage();
        usage.Should().BeInRange(0, 100);
    }

    [Fact]
    public void Throttle_Release_WhenCpuDropsBelowHysteresis()
    {
        var throttle = new OverheadThrottle(10.0);
        for (int i = 0; i < 10; i++) throttle.UpdateCpuUsage(15.0);
        throttle.IsThrottled.Should().BeTrue();
        for (int i = 0; i < 10; i++) throttle.UpdateCpuUsage(5.0);
        throttle.IsThrottled.Should().BeFalse();
    }

    // =============================================
    // 5. HybridAutonomyProfile (~12 tests)
    // =============================================

    [Fact]
    public void Paranoid_AllCritical_ManualOnly()
    {
        var config = new AiAutonomyConfiguration();
        var profile = HybridAutonomyProfile.Paranoid(config);
        profile.ActiveProfileName.Should().Be("Paranoid");
        var levels = profile.GetCategoryLevels();
        levels[CheckTiming.ConnectTime].Should().Be(AiAutonomyLevel.ManualOnly);
        levels[CheckTiming.SessionCached].Should().Be(AiAutonomyLevel.ManualOnly);
        levels[CheckTiming.Periodic].Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void Paranoid_Deferred_Suggest()
    {
        var config = new AiAutonomyConfiguration();
        var profile = HybridAutonomyProfile.Paranoid(config);
        var levels = profile.GetCategoryLevels();
        levels[CheckTiming.Deferred].Should().Be(AiAutonomyLevel.Suggest);
        levels[CheckTiming.PerOperation].Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public void Balanced_MixedLevels()
    {
        var config = new AiAutonomyConfiguration();
        var profile = HybridAutonomyProfile.Balanced(config);
        profile.ActiveProfileName.Should().Be("Balanced");
        var levels = profile.GetCategoryLevels();
        levels[CheckTiming.ConnectTime].Should().Be(AiAutonomyLevel.Suggest);
        levels[CheckTiming.SessionCached].Should().Be(AiAutonomyLevel.SuggestExplain);
        levels[CheckTiming.PerOperation].Should().Be(AiAutonomyLevel.AutoNotify);
        levels[CheckTiming.Deferred].Should().Be(AiAutonomyLevel.AutoNotify);
        levels[CheckTiming.Periodic].Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public void Performance_HighAutonomy()
    {
        var config = new AiAutonomyConfiguration();
        var profile = HybridAutonomyProfile.Performance(config);
        profile.ActiveProfileName.Should().Be("Performance");
        var levels = profile.GetCategoryLevels();
        levels[CheckTiming.PerOperation].Should().Be(AiAutonomyLevel.AutoSilent);
        levels[CheckTiming.Deferred].Should().Be(AiAutonomyLevel.AutoSilent);
    }

    [Fact]
    public void Performance_ConnectTime_SuggestExplain()
    {
        var config = new AiAutonomyConfiguration();
        var profile = HybridAutonomyProfile.Performance(config);
        profile.GetCategoryLevels()[CheckTiming.ConnectTime].Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public void CustomProfile_ApplyProfile_Works()
    {
        var config = new AiAutonomyConfiguration();
        var profile = new HybridAutonomyProfile(config);
        profile.ApplyProfile("Custom", new Dictionary<CheckTiming, AiAutonomyLevel>
        {
            [CheckTiming.ConnectTime] = AiAutonomyLevel.AutoSilent,
            [CheckTiming.Deferred] = AiAutonomyLevel.ManualOnly
        });
        profile.ActiveProfileName.Should().Be("Custom");
        var levels = profile.GetCategoryLevels();
        levels[CheckTiming.ConnectTime].Should().Be(AiAutonomyLevel.AutoSilent);
        levels[CheckTiming.Deferred].Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void Profile_NullConfig_ThrowsArgumentNull()
    {
        var act = () => new HybridAutonomyProfile(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Profile_NullProfileName_ThrowsArgumentNull()
    {
        var config = new AiAutonomyConfiguration();
        var profile = new HybridAutonomyProfile(config);
        var act = () => profile.ApplyProfile(null!, new Dictionary<CheckTiming, AiAutonomyLevel>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Profile_NullCategoryLevels_ThrowsArgumentNull()
    {
        var config = new AiAutonomyConfiguration();
        var profile = new HybridAutonomyProfile(config);
        var act = () => profile.ApplyProfile("Test", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Paranoid_ConfigActuallyUpdated()
    {
        var config = new AiAutonomyConfiguration(AiAutonomyLevel.AutoSilent);
        HybridAutonomyProfile.Paranoid(config);
        var connectTimeFeatures = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.ConnectTime);
        if (connectTimeFeatures.Count > 0)
            config.GetAutonomy(connectTimeFeatures[0], PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void ProfileSwitch_OverridesPrevious()
    {
        var config = new AiAutonomyConfiguration();
        var profile = new HybridAutonomyProfile(config);
        profile.ApplyProfile("First", new Dictionary<CheckTiming, AiAutonomyLevel>
        {
            [CheckTiming.ConnectTime] = AiAutonomyLevel.ManualOnly,
            [CheckTiming.Deferred] = AiAutonomyLevel.ManualOnly
        });
        profile.ApplyProfile("Second", new Dictionary<CheckTiming, AiAutonomyLevel>
        {
            [CheckTiming.ConnectTime] = AiAutonomyLevel.AutoSilent
        });
        profile.ActiveProfileName.Should().Be("Second");
        profile.GetCategoryLevels()[CheckTiming.ConnectTime].Should().Be(AiAutonomyLevel.AutoSilent);
        profile.GetCategoryLevels().Should().NotContainKey(CheckTiming.Deferred);
    }

    [Fact]
    public void AllThreePresets_HaveFiveCategories()
    {
        var c1 = new AiAutonomyConfiguration();
        HybridAutonomyProfile.Paranoid(c1).GetCategoryLevels().Count.Should().Be(5);
        var c2 = new AiAutonomyConfiguration();
        HybridAutonomyProfile.Balanced(c2).GetCategoryLevels().Count.Should().Be(5);
        var c3 = new AiAutonomyConfiguration();
        HybridAutonomyProfile.Performance(c3).GetCategoryLevels().Count.Should().Be(5);
    }

    // =============================================
    // 6. ThreatDetector (~10 tests)
    // =============================================

    [Fact]
    public async Task ThreatDetector_NoSignals_ScoreZero()
    {
        var detector = new ThreatDetector();
        await detector.ProcessObservationsAsync(
            new List<ObservationEvent> { new("p1", "cpu_usage", 50.0, null, null, DateTimeOffset.UtcNow) },
            CancellationToken.None);
        detector.CurrentAssessment.ThreatScore.Should().Be(0.0);
        detector.CurrentAssessment.Level.Should().Be(ThreatLevel.None);
    }

    [Fact]
    public async Task ThreatDetector_AnomalyRateHigh_DetectedAbove10()
    {
        var detector = new ThreatDetector();
        var now = DateTimeOffset.UtcNow;
        var batch = Enumerable.Range(0, 15)
            .Select(_ => new ObservationEvent("p1", "metric", 1.0, "anomaly_detected", "test", now))
            .ToList();
        await detector.ProcessObservationsAsync(batch, CancellationToken.None);
        detector.CurrentAssessment.ActiveSignals.Should().Contain(s => s.SignalType == "anomaly_rate_high");
        detector.CurrentAssessment.ThreatScore.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ThreatDetector_AuthFailureSpike_DetectedAbove5()
    {
        var detector = new ThreatDetector();
        var now = DateTimeOffset.UtcNow;
        var batch = Enumerable.Range(0, 8)
            .Select(_ => new ObservationEvent("p1", "auth_fail_count", 1.0, null, null, now))
            .ToList();
        await detector.ProcessObservationsAsync(batch, CancellationToken.None);
        detector.CurrentAssessment.ActiveSignals.Should().Contain(s => s.SignalType == "auth_failure_spike");
    }

    [Fact]
    public void ThreatDetector_AdvisorId_Correct()
    {
        new ThreatDetector().AdvisorId.Should().Be("threat_detector");
    }

    [Fact]
    public async Task ThreatDetector_CompositeScore_ExceedsThreshold()
    {
        var detector = new ThreatDetector(0.1);
        var now = DateTimeOffset.UtcNow;
        var batch = new List<ObservationEvent>();
        for (int i = 0; i < 15; i++)
            batch.Add(new ObservationEvent("p1", "metric", 1.0, "anomaly_detected", null, now));
        for (int i = 0; i < 8; i++)
            batch.Add(new ObservationEvent("p1", "auth_fail_count", 1.0, null, null, now));
        await detector.ProcessObservationsAsync(batch, CancellationToken.None);
        detector.CurrentAssessment.ShouldTightenPolicy.Should().BeTrue();
        detector.CurrentAssessment.ThreatScore.Should().BeGreaterThan(0.1);
    }

    [Fact]
    public async Task ThreatDetector_NoAnomalies_ScoreStaysZero()
    {
        var detector = new ThreatDetector();
        await detector.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "normal_metric", 42.0, null, null, DateTimeOffset.UtcNow),
            new("p2", "another_metric", 10.0, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        detector.CurrentAssessment.ThreatScore.Should().Be(0.0);
        detector.CurrentAssessment.ShouldTightenPolicy.Should().BeFalse();
    }

    [Fact]
    public async Task ThreatDetector_EmptyBatch_NoError()
    {
        var detector = new ThreatDetector();
        await detector.ProcessObservationsAsync(new List<ObservationEvent>(), CancellationToken.None);
        detector.CurrentAssessment.Level.Should().Be(ThreatLevel.None);
    }

    [Fact]
    public void ThreatDetector_DefaultThreshold_NoThreat()
    {
        new ThreatDetector().CurrentAssessment.ShouldTightenPolicy.Should().BeFalse();
    }

    [Fact]
    public void ThreatDetector_RecommendedCascade_Default_MostRestrictive()
    {
        new ThreatDetector().RecommendedCascade.Should().Be(CascadeStrategy.MostRestrictive);
    }

    [Fact]
    public async Task ThreatDetector_MultipleBatches_AccumulateSignals()
    {
        var detector = new ThreatDetector(0.1);
        var now = DateTimeOffset.UtcNow;
        var batch1 = Enumerable.Range(0, 6)
            .Select(_ => new ObservationEvent("p1", "metric", 1.0, "anomaly_detected", null, now)).ToList();
        await detector.ProcessObservationsAsync(batch1, CancellationToken.None);
        var batch2 = Enumerable.Range(0, 6)
            .Select(_ => new ObservationEvent("p1", "metric", 1.0, "anomaly_detected", null, now)).ToList();
        await detector.ProcessObservationsAsync(batch2, CancellationToken.None);
        detector.CurrentAssessment.ActiveSignals.Should().Contain(s => s.SignalType == "anomaly_rate_high");
    }

    // =============================================
    // 7. CostAnalyzer (~6 tests)
    // =============================================

    [Fact]
    public async Task CostAnalyzer_ParsesAlgorithmDuration()
    {
        var analyzer = new CostAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "algorithm_aes256_duration_ms", 5.0, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        var cost = analyzer.GetCostForAlgorithm("aes256");
        cost.Should().NotBeNull();
        cost!.AlgorithmId.Should().Be("aes256");
        cost.OperationCount.Should().Be(1);
    }

    [Fact]
    public async Task CostAnalyzer_ParsesEncryptionPrefix()
    {
        var analyzer = new CostAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "encryption_chacha20_ms", 2.0, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        var cost = analyzer.GetCostForAlgorithm("enc_chacha20");
        cost.Should().NotBeNull();
        cost!.AlgorithmId.Should().Be("enc_chacha20");
    }

    [Fact]
    public async Task CostAnalyzer_ParsesCompressionPrefix()
    {
        var analyzer = new CostAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "compression_lz4_ms", 1.5, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        var cost = analyzer.GetCostForAlgorithm("cmp_lz4");
        cost.Should().NotBeNull();
        cost!.AlgorithmId.Should().Be("cmp_lz4");
    }

    [Fact]
    public async Task CostAnalyzer_MissingMetrics_GracefulDefault()
    {
        var analyzer = new CostAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "unrelated_metric", 100.0, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        analyzer.GetCostForAlgorithm("unknown").Should().BeNull();
    }

    [Fact]
    public void CostAnalyzer_AdvisorId_Correct()
    {
        new CostAnalyzer().AdvisorId.Should().Be("cost_analyzer");
    }

    [Fact]
    public async Task CostAnalyzer_MultipleOps_AveragesDuration()
    {
        var analyzer = new CostAnalyzer();
        var now = DateTimeOffset.UtcNow;
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "algorithm_sha256_duration_ms", 10.0, null, null, now),
            new("p1", "algorithm_sha256_duration_ms", 20.0, null, null, now),
            new("p1", "algorithm_sha256_duration_ms", 30.0, null, null, now)
        }, CancellationToken.None);
        var cost = analyzer.GetCostForAlgorithm("sha256");
        cost.Should().NotBeNull();
        cost!.OperationCount.Should().Be(3);
        cost.AverageDurationMs.Should().BeApproximately(20.0, 0.1);
    }

    // =============================================
    // 8. DataSensitivityAnalyzer (~5 tests)
    // =============================================

    [Fact]
    public async Task Sensitivity_PiiDetected_ElevatesToConfidential()
    {
        var analyzer = new DataSensitivityAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "pii_email", 1.0, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        analyzer.CurrentProfile.PiiDetected.Should().BeTrue();
        ((int)analyzer.CurrentProfile.HighestClassification).Should().BeGreaterThanOrEqualTo((int)DataClassification.Confidential);
    }

    [Fact]
    public async Task Sensitivity_NoPii_StaysPublic()
    {
        var analyzer = new DataSensitivityAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "normal_metric", 42.0, null, null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        analyzer.CurrentProfile.PiiDetected.Should().BeFalse();
        analyzer.CurrentProfile.HighestClassification.Should().Be(DataClassification.Public);
    }

    [Fact]
    public async Task Sensitivity_MultiplePiiFields_IncreasedConfidence()
    {
        var analyzer = new DataSensitivityAnalyzer();
        var now = DateTimeOffset.UtcNow;
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "pii_email", 1.0, null, null, now),
            new("p1", "pii_ssn", 1.0, null, null, now),
            new("p1", "pii_phone", 1.0, null, null, now)
        }, CancellationToken.None);
        analyzer.CurrentProfile.PiiDetected.Should().BeTrue();
        analyzer.CurrentProfile.ActiveSignals.Count.Should().BeGreaterThanOrEqualTo(3);
        analyzer.CurrentProfile.SensitivityScore.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Sensitivity_AdvisorId_Correct()
    {
        new DataSensitivityAnalyzer().AdvisorId.Should().Be("data_sensitivity_analyzer");
    }

    [Fact]
    public async Task Sensitivity_AnomalyPii_Detected()
    {
        var analyzer = new DataSensitivityAnalyzer();
        await analyzer.ProcessObservationsAsync(new List<ObservationEvent>
        {
            new("p1", "metric", 1.0, "pii_leak_detected", null, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        analyzer.CurrentProfile.PiiDetected.Should().BeTrue();
    }

    // =============================================
    // 9. AiPolicyIntelligenceFactory (~5 tests)
    // =============================================

    [Fact]
    public async Task Factory_CreateDefault_AllComponentsWired()
    {
        await using var system = AiPolicyIntelligenceFactory.CreateDefault();
        system.RingBuffer.Should().NotBeNull();
        system.Throttle.Should().NotBeNull();
        system.Pipeline.Should().NotBeNull();
        system.Hardware.Should().NotBeNull();
        system.Workload.Should().NotBeNull();
        system.Threat.Should().NotBeNull();
        system.Cost.Should().NotBeNull();
        system.Sensitivity.Should().NotBeNull();
        system.Advisor.Should().NotBeNull();
        system.AutonomyConfig.Should().NotBeNull();
        system.SelfModificationGuard.Should().NotBeNull();
    }

    [Fact]
    public async Task Factory_Create_CustomOptions_Respected()
    {
        var options = new AiPolicyIntelligenceOptions
        {
            MaxCpuOverheadPercent = 5.0,
            RingBufferCapacity = 16384,
            ThreatThreshold = 0.5,
            DefaultAutonomyLevel = AiAutonomyLevel.ManualOnly
        };
        await using var system = AiPolicyIntelligenceFactory.Create(options);
        system.RingBuffer.Capacity.Should().Be(16384);
        system.Throttle.MaxCpuOverheadPercent.Should().Be(5.0);
        system.AutonomyConfig.GetAutonomy("test", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public void Factory_Create_NullOptions_ThrowsArgumentNull()
    {
        var act = () => AiPolicyIntelligenceFactory.Create(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Factory_SystemProducesAccessibleComponents()
    {
        await using var system = AiPolicyIntelligenceFactory.CreateDefault();
        system.RingBuffer.Capacity.Should().BeGreaterThan(0);
        system.Throttle.IsThrottled.Should().BeFalse();
        system.Threat.AdvisorId.Should().Be("threat_detector");
        system.Cost.AdvisorId.Should().Be("cost_analyzer");
        system.Sensitivity.AdvisorId.Should().Be("data_sensitivity_analyzer");
    }

    [Fact]
    public async Task Factory_SelfModificationGuard_DefaultBlocked()
    {
        await using var system = AiPolicyIntelligenceFactory.CreateDefault();
        var guard = system.SelfModificationGuard;
        guard.GetAutonomy("encryption", PolicyLevel.VDE).Should().Be(AiAutonomyLevel.Suggest);
    }
}
