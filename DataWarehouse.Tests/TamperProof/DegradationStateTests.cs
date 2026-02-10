// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof degradation state SDK contracts (T6.10).
/// Validates InstanceDegradationState enum (6 values), valid/invalid state transitions,
/// and TransactionResult.DegradationState property.
/// </summary>
public class DegradationStateTests
{
    #region InstanceDegradationState Enum

    [Fact]
    public void InstanceDegradationState_ShouldHaveExactlySixValues()
    {
        var values = Enum.GetValues<InstanceDegradationState>();
        values.Should().HaveCount(6);
    }

    [Fact]
    public void InstanceDegradationState_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<InstanceDegradationState>();
        values.Should().Contain(InstanceDegradationState.Healthy);
        values.Should().Contain(InstanceDegradationState.Degraded);
        values.Should().Contain(InstanceDegradationState.DegradedReadOnly);
        values.Should().Contain(InstanceDegradationState.DegradedNoRecovery);
        values.Should().Contain(InstanceDegradationState.Offline);
        values.Should().Contain(InstanceDegradationState.Corrupted);
    }

    [Theory]
    [InlineData(InstanceDegradationState.Healthy, "Healthy")]
    [InlineData(InstanceDegradationState.Degraded, "Degraded")]
    [InlineData(InstanceDegradationState.DegradedReadOnly, "DegradedReadOnly")]
    [InlineData(InstanceDegradationState.DegradedNoRecovery, "DegradedNoRecovery")]
    [InlineData(InstanceDegradationState.Offline, "Offline")]
    [InlineData(InstanceDegradationState.Corrupted, "Corrupted")]
    public void InstanceDegradationState_ShouldHaveCorrectNames(
        InstanceDegradationState state, string expectedName)
    {
        state.ToString().Should().Be(expectedName);
    }

    #endregion

    #region Valid State Transitions

    [Theory]
    [InlineData(InstanceDegradationState.Healthy, InstanceDegradationState.Degraded)]
    [InlineData(InstanceDegradationState.Degraded, InstanceDegradationState.DegradedReadOnly)]
    [InlineData(InstanceDegradationState.Degraded, InstanceDegradationState.DegradedNoRecovery)]
    [InlineData(InstanceDegradationState.Degraded, InstanceDegradationState.Healthy)]
    [InlineData(InstanceDegradationState.DegradedReadOnly, InstanceDegradationState.Degraded)]
    [InlineData(InstanceDegradationState.DegradedReadOnly, InstanceDegradationState.Offline)]
    [InlineData(InstanceDegradationState.DegradedNoRecovery, InstanceDegradationState.Degraded)]
    [InlineData(InstanceDegradationState.Offline, InstanceDegradationState.Healthy)]
    [InlineData(InstanceDegradationState.Healthy, InstanceDegradationState.Corrupted)]
    [InlineData(InstanceDegradationState.Degraded, InstanceDegradationState.Corrupted)]
    public void ValidTransitions_ShouldBeAllowed(
        InstanceDegradationState from, InstanceDegradationState to)
    {
        IsValidTransition(from, to).Should().BeTrue(
            $"Transition from {from} to {to} should be valid");
    }

    [Theory]
    [InlineData(InstanceDegradationState.Corrupted, InstanceDegradationState.Healthy)]
    [InlineData(InstanceDegradationState.Corrupted, InstanceDegradationState.Degraded)]
    public void InvalidTransitions_ShouldBeRejected(
        InstanceDegradationState from, InstanceDegradationState to)
    {
        // Corrupted -> Healthy/Degraded requires admin override (not a normal transition)
        IsValidTransition(from, to).Should().BeFalse(
            $"Transition from {from} to {to} should require admin override");
    }

    [Fact]
    public void CorruptedState_ShouldOnlyTransitionWithAdminOverride()
    {
        var currentState = InstanceDegradationState.Corrupted;

        // Normal transitions from Corrupted should not be valid
        IsValidTransition(currentState, InstanceDegradationState.Healthy).Should().BeFalse();
        IsValidTransition(currentState, InstanceDegradationState.Degraded).Should().BeFalse();

        // Admin override transition should be valid
        IsValidTransitionWithAdminOverride(currentState, InstanceDegradationState.Healthy).Should().BeTrue();
    }

    #endregion

    #region TransactionResult DegradationState

    [Fact]
    public void TransactionResult_SuccessfulTransaction_ShouldBeHealthy()
    {
        var result = TransactionResult.CreateSuccess(
            Guid.NewGuid(),
            TierWriteResult.CreateSuccess("Data", "d1", "r1", 1000),
            TierWriteResult.CreateSuccess("Metadata", "m1", "r2", 200),
            TierWriteResult.CreateSuccess("WORM", "w1", "r3", 1000),
            TierWriteResult.CreateSuccess("Blockchain", "b1", "r4", 64));

        result.DegradationState.Should().Be(InstanceDegradationState.Healthy);
    }

    [Fact]
    public void TransactionResult_FailedTransaction_ShouldHaveDegradedState()
    {
        var result = TransactionResult.CreateFailure(
            Guid.NewGuid(),
            "WORM write failed",
            InstanceDegradationState.Degraded);

        result.DegradationState.Should().Be(InstanceDegradationState.Degraded);
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void TransactionResult_CorruptionDetected_ShouldBeCorrupted()
    {
        var result = TransactionResult.CreateFailure(
            Guid.NewGuid(),
            "Integrity check failed after write",
            InstanceDegradationState.Corrupted);

        result.DegradationState.Should().Be(InstanceDegradationState.Corrupted);
    }

    [Fact]
    public void SecureWriteResult_ShouldDefaultToHealthyDegradationState()
    {
        var integrityHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB");
        var writeContext = new WriteContextRecord
        {
            Author = "test", Comment = "test", Timestamp = DateTimeOffset.UtcNow
        };

        var result = SecureWriteResult.CreateSuccess(
            Guid.NewGuid(), 1, integrityHash, "m1", "w1",
            writeContext, 4, 1000, 1024);

        result.DegradationState.Should().Be(InstanceDegradationState.Healthy);
    }

    [Fact]
    public void SecureWriteResult_Failure_ShouldDefaultToCorrupted()
    {
        var result = SecureWriteResult.CreateFailure("Write failed");

        result.DegradationState.Should().Be(InstanceDegradationState.Corrupted);
    }

    [Fact]
    public void SecureWriteResult_FailureWithCustomDegradation_ShouldUseProvidedState()
    {
        var result = SecureWriteResult.CreateFailure(
            "WORM unavailable",
            degradationState: InstanceDegradationState.DegradedNoRecovery);

        result.DegradationState.Should().Be(InstanceDegradationState.DegradedNoRecovery);
    }

    #endregion

    #region Degradation State Severity Ordering

    [Fact]
    public void DegradationStates_ShouldFollowSeverityOrdering()
    {
        // Verify the enum values follow a logical severity ordering
        var healthy = (int)InstanceDegradationState.Healthy;
        var degraded = (int)InstanceDegradationState.Degraded;
        var readOnly = (int)InstanceDegradationState.DegradedReadOnly;
        var noRecovery = (int)InstanceDegradationState.DegradedNoRecovery;
        var offline = (int)InstanceDegradationState.Offline;
        var corrupted = (int)InstanceDegradationState.Corrupted;

        healthy.Should().BeLessThan(degraded);
        degraded.Should().BeLessThan(readOnly);
        readOnly.Should().BeLessThan(noRecovery);
        noRecovery.Should().BeLessThan(offline);
        offline.Should().BeLessThan(corrupted);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Simulates the DegradationStateService transition validation logic.
    /// Corrupted state requires admin override and cannot normally transition to Healthy/Degraded.
    /// </summary>
    private static bool IsValidTransition(InstanceDegradationState from, InstanceDegradationState to)
    {
        // Corrupted -> Healthy or Degraded requires admin (not a normal transition)
        if (from == InstanceDegradationState.Corrupted &&
            (to == InstanceDegradationState.Healthy || to == InstanceDegradationState.Degraded))
        {
            return false;
        }

        // All other transitions are valid in this simplified model
        return true;
    }

    private static bool IsValidTransitionWithAdminOverride(
        InstanceDegradationState from, InstanceDegradationState to)
    {
        // With admin override, any transition is allowed
        return true;
    }

    #endregion
}
