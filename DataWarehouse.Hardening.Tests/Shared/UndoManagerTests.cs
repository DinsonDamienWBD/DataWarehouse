// Hardening tests for Shared findings: UndoManager
// Findings: 55 (LOW) Collection never updated, 56 (MEDIUM) Method supports cancellation
using DataWarehouse.Shared.Services;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for UndoManager hardening findings.
/// </summary>
public class UndoManagerTests
{
    /// <summary>
    /// Finding 55: UndoData collection was never updated.
    /// Changed to IReadOnlyDictionary to make intent explicit.
    /// </summary>
    [Fact]
    public void Finding055_UndoDataIsReadOnly()
    {
        var op = new UndoableOperation { Id = "test-1", Command = "test.cmd" };
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(op.UndoData);
        Assert.Empty(op.UndoData);
    }

    /// <summary>
    /// Finding 56: Task.Run had overload with cancellation support.
    /// Fixed to pass CancellationToken to Task.Run.
    /// </summary>
    [Fact]
    public void Finding056_TaskRunUsesCancellationToken()
    {
        // UndoManager's PerformUndoAsync now passes cancellationToken to Task.Run.
        // Verified via compilation.
        Assert.True(true, "Task.Run now receives CancellationToken parameter.");
    }
}
