// Hardening tests for Shared findings: RaidCommands
// Finding: 53 (LOW) StripeSizeKB naming
using DataWarehouse.Shared.Commands;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for RaidCommands hardening findings.
/// </summary>
public class RaidCommandsTests
{
    /// <summary>
    /// Finding 53: StripeSizeKB -> StripeSizeKb property rename.
    /// </summary>
    [Fact]
    public void Finding053_StripeSizeKbPropertyRenamed()
    {
        var info = new RaidConfigInfo
        {
            Id = "raid-1",
            Name = "TestArray",
            Level = "5",
            Status = "Healthy",
            StripeSizeKb = 64
        };

        Assert.Equal(64, info.StripeSizeKb);
    }
}
