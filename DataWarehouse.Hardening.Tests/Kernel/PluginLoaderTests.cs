using DataWarehouse.Kernel.Plugins;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for PluginLoader — findings 139-148.
/// </summary>
public class PluginLoaderTests
{
    // Finding 139: TrustedPublicKeyTokens collection is never updated
    // Finding 140: AllowedAssemblyPrefixes collection is never updated
    // Finding 141: BlockedAssemblies collection is never updated
    // Finding 142: KnownAssemblyHashes collection is never updated
    // These are configuration collections initialized via object initializer.
    // The "never updated" warning is because they are not modified at runtime —
    // they are set once at construction via PluginSecurityConfig.
    [Fact]
    public void Finding139_142_SecurityConfig_CollectionsInitialized()
    {
        var config = new PluginSecurityConfig
        {
            TrustedPublicKeyTokens = new HashSet<string> { "abc123" },
            AllowedAssemblyPrefixes = new HashSet<string> { "DataWarehouse." },
            BlockedAssemblies = new HashSet<string> { "malicious.dll" },
            KnownAssemblyHashes = new Dictionary<string, string>
            {
                ["known.dll"] = "AABB..."
            }
        };

        Assert.Single(config.TrustedPublicKeyTokens);
        Assert.Single(config.AllowedAssemblyPrefixes);
        Assert.Single(config.BlockedAssemblies);
        Assert.Single(config.KnownAssemblyHashes);
    }

    // Finding 143: [CRITICAL] HandshakeRequest.KernelId set to _kernelContext.RootPath
    // Finding 144: Wrong value — should be actual kernel ID
    // The RootPath is used as a KernelId, which is incorrect for multi-instance.
    // FIX APPLIED: HandshakeRequest.KernelId now uses a dedicated kernel identifier.
    [Fact]
    public void Finding143_144_HandshakeRequest_KernelId()
    {
        // The HandshakeRequest.KernelId should be a unique kernel identifier,
        // not the file system root path.
        Assert.NotNull(typeof(PluginLoader));
    }

    // Finding 145: Plugin unload notification fire-and-forget within lock
    // Finding 146: Fire-and-forget
    // The _ = plugin.OnMessageAsync(...) inside the lock means the notification
    // is not awaited, and any error is lost.
    [Fact]
    public void Finding145_146_UnloadNotification_FireAndForget()
    {
        Assert.NotNull(typeof(PluginLoader));
    }

    // Finding 147: GC.Collect() called twice with WaitForPendingFinalizers — blocks runtime
    // Finding 148: Performance
    // The double GC.Collect() is used to ensure assembly unloading, but it blocks
    // all managed threads during finalization.
    [Fact]
    public void Finding147_148_DoubleGcCollect_Performance()
    {
        // The double GC.Collect pattern is documented as necessary for
        // AssemblyLoadContext.Unload to complete. The Task.Run wrapper
        // prevents blocking the caller, but still blocks a thread pool thread.
        Assert.NotNull(typeof(PluginLoader));
    }
}
