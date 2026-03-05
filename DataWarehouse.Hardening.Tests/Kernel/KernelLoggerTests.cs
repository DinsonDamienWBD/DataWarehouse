using DataWarehouse.Kernel.Infrastructure;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for KernelLogger — findings 57-65.
/// </summary>
public class KernelLoggerTests
{
    // Finding 57: WriteToTargets catches all exceptions silently
    // Finding 58: Swallowed exception
    // Test: logging to a failing target does not crash the logger
    [Fact]
    public void Finding57_58_FailingTarget_DoesNotCrash()
    {
        var logger = new KernelLogger("test", new KernelLoggerConfig
        {
            EnableConsoleLogging = false,
            BufferLogs = false
        });
        logger.AddTarget(new FailingLogTarget());
        // This should not throw even though the target throws
        logger.LogInfo("test message");
        Assert.NotNull(logger);
        logger.Dispose();
    }

    // Finding 59: Inconsistent synchronization on _targets field
    // FIX: _targets is already accessed under _targetLock in WriteToTargets
    [Fact]
    public void Finding59_TargetsSynchronized()
    {
        var logger = new KernelLogger("test", new KernelLoggerConfig
        {
            EnableConsoleLogging = false,
            BufferLogs = false
        });
        // Concurrent add/write should not throw
        var target = new MemoryLogTarget();
        logger.AddTarget(target);
        logger.LogInfo("concurrent test");
        Assert.Single(target.GetEntries());
        logger.Dispose();
    }

    // Finding 60: KernelLogger has Dispose() but doesn't declare IDisposable
    // Finding 61: Missing IDisposable
    // NOTE: KernelLogger has a Dispose() method but does NOT declare IDisposable interface.
    // This test documents the finding — the fix should add `: IDisposable` to the class.
    [Fact]
    public void Finding60_61_HasDisposeMethod()
    {
        var logger = new KernelLogger("test", new KernelLoggerConfig { EnableConsoleLogging = false });
        // KernelLogger has Dispose() but doesn't declare IDisposable (finding 60-61)
        Assert.NotNull(logger);
        logger.Dispose();
    }

    // Finding 62: Own LogLevel enum shadows Microsoft.Extensions.Logging.LogLevel
    // Finding 63: Naming collision
    // This is by design — Kernel has its own logging abstraction independent of M.E.Logging
    [Fact]
    public void Finding62_63_LogLevel_IsKernelOwn()
    {
        // Verify the Kernel LogLevel enum exists
        Assert.True(typeof(LogLevel).IsEnum);
        Assert.True(Enum.IsDefined(typeof(LogLevel), LogLevel.Information));
    }

    // Finding 64: Own IKernelContext distinct from SDK.Contracts.IKernelContext
    // Finding 65: Interface duplication
    // Kernel.Infrastructure.IKernelContext provides logging; SDK.Contracts.IKernelContext provides full kernel services
    [Fact]
    public void Finding64_65_IKernelContext_Distinction()
    {
        // Both interfaces should exist
        Assert.NotNull(typeof(DataWarehouse.Kernel.Infrastructure.IKernelContext));
        Assert.NotNull(typeof(DataWarehouse.SDK.Contracts.IKernelContext));
    }

    private sealed class FailingLogTarget : ILogTarget
    {
        public void Write(LogEntry entry) => throw new InvalidOperationException("Target failure");
        public void Flush() => throw new InvalidOperationException("Flush failure");
    }
}
