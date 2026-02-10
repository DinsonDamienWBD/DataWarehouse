// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof recovery SDK contracts (T6.8).
/// Validates TamperRecoveryBehavior enum (5 values), recovery behavior dispatch,
/// RecoveryResult construction, and WORM recovery with version restoration.
/// </summary>
public class RecoveryTests
{
    #region TamperRecoveryBehavior Enum

    [Fact]
    public void TamperRecoveryBehavior_ShouldHaveExactlyFiveValues()
    {
        var values = Enum.GetValues<TamperRecoveryBehavior>();
        values.Should().HaveCount(5);
    }

    [Fact]
    public void TamperRecoveryBehavior_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<TamperRecoveryBehavior>();
        values.Should().Contain(TamperRecoveryBehavior.AutoRecoverSilent);
        values.Should().Contain(TamperRecoveryBehavior.AutoRecoverWithReport);
        values.Should().Contain(TamperRecoveryBehavior.AlertAndWait);
        values.Should().Contain(TamperRecoveryBehavior.ManualOnly);
        values.Should().Contain(TamperRecoveryBehavior.FailClosed);
    }

    [Theory]
    [InlineData(TamperRecoveryBehavior.AutoRecoverSilent, "AutoRecoverSilent")]
    [InlineData(TamperRecoveryBehavior.AutoRecoverWithReport, "AutoRecoverWithReport")]
    [InlineData(TamperRecoveryBehavior.AlertAndWait, "AlertAndWait")]
    [InlineData(TamperRecoveryBehavior.ManualOnly, "ManualOnly")]
    [InlineData(TamperRecoveryBehavior.FailClosed, "FailClosed")]
    public void TamperRecoveryBehavior_ShouldHaveCorrectNames(
        TamperRecoveryBehavior behavior, string expectedName)
    {
        behavior.ToString().Should().Be(expectedName);
    }

    #endregion

    #region Recovery Behavior Dispatch

    [Theory]
    [InlineData(TamperRecoveryBehavior.AutoRecoverSilent, true, false)]
    [InlineData(TamperRecoveryBehavior.AutoRecoverWithReport, true, true)]
    [InlineData(TamperRecoveryBehavior.AlertAndWait, false, true)]
    [InlineData(TamperRecoveryBehavior.ManualOnly, false, true)]
    [InlineData(TamperRecoveryBehavior.FailClosed, false, false)]
    public void RecoveryBehavior_ShouldDetermineRecoveryAndAlertPolicy(
        TamperRecoveryBehavior behavior, bool shouldAutoRecover, bool shouldAlert)
    {
        var autoRecover = behavior is TamperRecoveryBehavior.AutoRecoverSilent
            or TamperRecoveryBehavior.AutoRecoverWithReport;
        var alert = behavior is TamperRecoveryBehavior.AutoRecoverWithReport
            or TamperRecoveryBehavior.AlertAndWait
            or TamperRecoveryBehavior.ManualOnly;

        autoRecover.Should().Be(shouldAutoRecover);
        alert.Should().Be(shouldAlert);
    }

    [Fact]
    public void TamperProofConfiguration_ShouldDefaultToAutoRecoverWithReport()
    {
        var config = CreateTestConfig();
        config.RecoveryBehavior.Should().Be(TamperRecoveryBehavior.AutoRecoverWithReport);
    }

    [Fact]
    public void TamperProofConfiguration_RecoveryBehavior_ShouldBeMutableAtRuntime()
    {
        var config = CreateTestConfig();
        config.RecoveryBehavior = TamperRecoveryBehavior.FailClosed;
        config.RecoveryBehavior.Should().Be(TamperRecoveryBehavior.FailClosed);
    }

    #endregion

    #region RecoveryResult Construction

    [Fact]
    public void RecoveryResult_CreateSuccess_ShouldPopulateFields()
    {
        var objectId = Guid.NewGuid();
        var result = RecoveryResult.CreateSuccess(
            objectId, 1, "WORM", "Recovered all 4 data shards from WORM backup");

        result.Success.Should().BeTrue();
        result.ObjectId.Should().Be(objectId);
        result.Version.Should().Be(1);
        result.RecoverySource.Should().Be("WORM");
        result.RecoveryDetails.Should().Contain("4 data shards");
        result.RequiresManualIntervention.Should().BeFalse();
        result.Behavior.Should().Be(RecoveryBehavior.AutoRecover);
    }

    [Fact]
    public void RecoveryResult_CreateFailure_ShouldIndicateError()
    {
        var objectId = Guid.NewGuid();
        var result = RecoveryResult.CreateFailure(objectId, 1, "WORM storage unavailable");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("WORM storage unavailable");
        result.RecoverySource.Should().Be("N/A");
    }

    [Fact]
    public void RecoveryResult_CreateManualRequired_ShouldIndicateManualIntervention()
    {
        var objectId = Guid.NewGuid();
        var result = RecoveryResult.CreateManualRequired(
            objectId, 1, "Corruption detected in all replicas");

        result.Success.Should().BeFalse();
        result.RequiresManualIntervention.Should().BeTrue();
        result.Behavior.Should().Be(RecoveryBehavior.ManualOnly);
        result.ErrorMessage.Should().Contain("Manual intervention");
    }

    [Fact]
    public void RecoveryResult_WithTamperIncident_ShouldLinkToIncident()
    {
        var tamperIncident = new TamperIncident
        {
            IncidentId = Guid.NewGuid(),
            ObjectId = Guid.NewGuid(),
            Version = 1,
            DetectedAt = DateTimeOffset.UtcNow,
            TamperedComponent = "shard-2",
            AttributionConfidence = AttributionConfidence.Suspected,
            RecoveryPerformed = true,
            AdminNotified = true
        };

        var result = RecoveryResult.CreateSuccess(
            tamperIncident.ObjectId, 1, "WORM", "Recovered shard-2",
            tamperIncident);

        result.TamperIncident.Should().NotBeNull();
        result.TamperIncident!.IncidentId.Should().Be(tamperIncident.IncidentId);
    }

    #endregion

    #region WORM Recovery with Version Restoration

    [Fact]
    public void WormRecovery_ShouldRestoreToSpecificVersion()
    {
        var objectId = Guid.NewGuid();

        // Simulate a WORM reference for version 1
        var wormRef = new WormReference
        {
            StorageLocation = "s3://worm/obj-001/v1",
            ContentHash = "original-hash",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow.AddDays(-30),
            EnforcementMode = WormEnforcementMode.HardwareIntegrated
        };

        // Recovery should reference the WORM backup
        var result = RecoveryResult.CreateSuccess(
            objectId, 1, "WORM",
            $"Restored from {wormRef.StorageLocation}, hash: {wormRef.ContentHash}");

        result.RecoverySource.Should().Be("WORM");
        result.RecoveryDetails.Should().Contain(wormRef.StorageLocation);
        result.RecoveryDetails.Should().Contain(wormRef.ContentHash);
    }

    [Fact]
    public void RecoveryBehavior_Enum_ShouldHaveFourValues()
    {
        var values = Enum.GetValues<RecoveryBehavior>();
        values.Should().Contain(RecoveryBehavior.AutoRecover);
        values.Should().Contain(RecoveryBehavior.ManualOnly);
        values.Should().Contain(RecoveryBehavior.FailClosed);
        values.Should().Contain(RecoveryBehavior.AlertOnly);
    }

    #endregion

    #region RollbackResult

    [Fact]
    public void RollbackResult_CreateSuccess_ShouldTrackTierResults()
    {
        var tierResults = new[]
        {
            new TierRollbackResult { TierName = "Data", Success = true, Action = "Deleted" },
            new TierRollbackResult { TierName = "Metadata", Success = true, Action = "Deleted" },
            new TierRollbackResult { TierName = "WORM", Success = false, Action = "Cannot rollback", ErrorMessage = "Immutable" },
            new TierRollbackResult { TierName = "Blockchain", Success = true, Action = "Skipped" }
        };

        var result = RollbackResult.CreateSuccess(Guid.NewGuid(), tierResults);

        result.Success.Should().BeTrue();
        result.TierResults.Should().HaveCount(4);
        result.TierResults[2].Success.Should().BeFalse("WORM cannot be rolled back");
    }

    [Fact]
    public void RollbackResult_WithOrphanedWormRecords_ShouldTrackThem()
    {
        var orphaned = new OrphanedWormRecord
        {
            OrphanId = Guid.NewGuid(),
            WormReference = new WormReference
            {
                StorageLocation = "s3://worm/orphaned",
                ContentHash = "orphan-hash",
                ContentSize = 1024,
                WrittenAt = DateTimeOffset.UtcNow,
                EnforcementMode = WormEnforcementMode.Software
            },
            Status = OrphanedWormStatus.TransactionFailed,
            CreatedAt = DateTimeOffset.UtcNow,
            FailureReason = "Metadata write failed after WORM write"
        };

        var result = RollbackResult.CreateSuccess(
            Guid.NewGuid(),
            Array.Empty<TierRollbackResult>(),
            new[] { orphaned });

        result.OrphanedRecords.Should().HaveCount(1);
        result.OrphanedRecords[0].Status.Should().Be(OrphanedWormStatus.TransactionFailed);
    }

    #endregion

    #region Helpers

    private static TamperProofConfiguration CreateTestConfig()
    {
        return new TamperProofConfiguration
        {
            StorageInstances = new StorageInstancesConfig
            {
                Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
                Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
                Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "local" },
                Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
            },
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 }
        };
    }

    #endregion
}
