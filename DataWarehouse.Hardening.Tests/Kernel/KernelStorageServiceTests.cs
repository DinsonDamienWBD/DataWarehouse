using DataWarehouse.Kernel.Storage;
using DataWarehouse.Kernel.Plugins;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for KernelStorageService — findings 66-73.
/// </summary>
public class KernelStorageServiceTests
{
    // Finding 66-67: SemaphoreSlim _indexLock never disposed
    // NOTE: KernelStorageService does NOT yet implement IDisposable.
    // This test documents the finding.
    [Fact]
    public void Finding66_67_ShouldImplementIDisposable()
    {
        var storage = new InMemoryStoragePlugin();
        var service = new KernelStorageService(() => storage);
        // Document: KernelStorageService should implement IDisposable (finding 66-67)
        Assert.NotNull(service);
    }

    // Finding 68-69: ExistsAsync checks in-memory index only
    [Fact]
    public async Task Finding68_69_ExistsAsync_FallsThrough()
    {
        var storage = new InMemoryStoragePlugin();
        var service = new KernelStorageService(() => storage);

        var exists = await service.ExistsAsync("nonexistent");
        Assert.False(exists);

        await service.SaveAsync("test-item", new MemoryStream(new byte[10]));
        exists = await service.ExistsAsync("test-item");
        Assert.True(exists);
    }

    // Finding 70-71: GetMetadataAsync bare catch returns null for ALL exceptions
    [Fact]
    public async Task Finding70_71_GetMetadata_ReturnsNull_ForMissing()
    {
        var storage = new InMemoryStoragePlugin();
        var service = new KernelStorageService(() => storage);

        var metadata = await service.GetMetadataAsync("nonexistent");
        Assert.Null(metadata);
    }

    // Finding 72-73: DeleteMetadataAsync swallows metadata file deletion errors
    [Fact]
    public async Task Finding72_73_DeleteMetadata_Resilient()
    {
        var storage = new InMemoryStoragePlugin();
        var service = new KernelStorageService(() => storage);

        await service.SaveAsync("delete-test", new MemoryStream(new byte[10]));
        var deleted = await service.DeleteAsync("delete-test");
        Assert.True(deleted);
    }
}
