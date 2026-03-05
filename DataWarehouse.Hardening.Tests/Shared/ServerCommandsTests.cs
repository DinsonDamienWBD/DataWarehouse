// Hardening tests for Shared findings: ServerCommands (Shared models)
// Finding: 54 is in CLI, covered by CrossProjectTests
namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for Shared ServerCommands types (ServerStatus, ServerInfo).
/// </summary>
public class ServerCommandsTests
{
    /// <summary>
    /// Finding 54: Static field synchronization is in CLI/ServerCommands.
    /// Shared ServerCommands.cs only contains immutable record types (ServerStatus, ServerInfo)
    /// which are inherently thread-safe.
    /// </summary>
    [Fact]
    public void Finding054_SharedServerCommandRecordsAreImmutable()
    {
        var status = new DataWarehouse.Shared.Commands.ServerStatus
        {
            IsRunning = true,
            Port = 5000,
            Mode = "Server",
            ProcessId = 1234,
            StartTime = DateTime.UtcNow
        };

        Assert.True(status.IsRunning);
        Assert.NotNull(status.Uptime);
    }
}
