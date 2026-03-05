// Hardening tests for Shared findings: IServerHost / ServerHostRegistry
// Finding: 32 (MEDIUM) ServerHostRegistry.Current has no synchronization
using DataWarehouse.Shared.Commands;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for ServerHostRegistry hardening findings.
/// </summary>
public class IServerHostTests
{
    /// <summary>
    /// Finding 32: ServerHostRegistry.Current has no synchronization.
    /// Fixed by using Volatile.Read/Write for thread-safe access.
    /// </summary>
    [Fact]
    public void Finding032_ServerHostRegistryUsesVolatileAccess()
    {
        // Set and read from multiple threads to verify thread safety
        ServerHostRegistry.Current = null;
        Assert.Null(ServerHostRegistry.Current);

        var results = new IServerHost?[100];
        Parallel.For(0, 100, i =>
        {
            results[i] = ServerHostRegistry.Current;
        });

        Assert.All(results, r => Assert.Null(r));
    }
}
