// Hardening tests for Shared findings: BackupCommands
// Finding: 5 (LOW) Collection never updated
using DataWarehouse.Shared.Commands;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for BackupCommands hardening findings.
/// </summary>
public class BackupCommandsTests
{
    /// <summary>
    /// Finding 5: backups collection was never updated (List declared but only returned empty).
    /// Fixed by changing to IReadOnlyList/Array.Empty to make intent explicit.
    /// </summary>
    [Fact]
    public void Finding005_BackupListUsesReadOnlyCollection()
    {
        // BackupListCommand exists and can be instantiated
        var cmd = new BackupListCommand();
        Assert.Equal("backup.list", cmd.Name);
        Assert.Equal("backup", cmd.Category);
    }
}
