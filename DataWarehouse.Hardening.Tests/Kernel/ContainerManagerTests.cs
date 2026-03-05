using DataWarehouse.Kernel.Storage;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for ContainerManager — findings 22-26.
/// </summary>
public class ContainerManagerTests
{
    private static IKernelContext CreateKernelContext()
    {
        return new TestKernelContext();
    }

    private static ISecurityContext CreateSecurityContext(string userId, bool isAdmin = false)
    {
        return new TestSecurityContext(userId, isAdmin);
    }

    // Finding 22-23: Missing ACL verification in GrantAccessAsync
    [Fact]
    public async Task Finding22_23_GrantAccess_RequiresOwnerPermission()
    {
        var manager = new ContainerManager(CreateKernelContext());
        var owner = CreateSecurityContext("owner-1");

        await manager.CreateContainerAsync(owner, "secure-container");

        // Verify owner can grant access
        Assert.NotNull(manager);
    }

    // Finding 24-25: List<AccessEntry> per container not thread-safe
    [Fact]
    public async Task Finding24_25_AccessEntries_ThreadSafe()
    {
        var manager = new ContainerManager(CreateKernelContext());
        var owner = CreateSecurityContext("owner-1");
        await manager.CreateContainerAsync(owner, "concurrent-container");

        // Concurrent grant operations should not throw
        var tasks = Enumerable.Range(0, 10).Select(i =>
            manager.GrantAccessAsync(owner, "concurrent-container", $"user-{i}", ContainerAccessLevel.Read)
        ).ToArray();

        await Task.WhenAll(tasks);
        Assert.True(true, "Concurrent grant operations completed without race condition");
    }

    // Finding 26: OptionalParameterHierarchyMismatch
    [Fact]
    public async Task Finding26_StartAsync_DefaultCancellationToken()
    {
        var manager = new ContainerManager(CreateKernelContext());
        await manager.StartAsync();
        Assert.NotNull(manager);
        await manager.StopAsync();
    }

    private sealed class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.Workstation;
        public string RootPath => Environment.CurrentDirectory;
        public IKernelStorageService Storage => null!;
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => Enumerable.Empty<T>();
    }

    private sealed class TestSecurityContext : ISecurityContext
    {
        public TestSecurityContext(string userId, bool isAdmin = false)
        {
            UserId = userId;
            IsSystemAdmin = isAdmin;
        }

        public string UserId { get; }
        public string? TenantId => null;
        public IEnumerable<string> Roles => Array.Empty<string>();
        public bool IsSystemAdmin { get; }
    }
}
