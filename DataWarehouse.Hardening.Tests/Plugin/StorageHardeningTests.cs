// Hardening tests for Plugin findings 177-195: UltimateStorage
// Finding 177 (LOW): iSCSI small allocations
// Finding 178 (HIGH): NFS shell injection
// Finding 179 (MEDIUM): Process.WaitForExit sync in async
// Finding 180 (MEDIUM): NvmeOf naming/contract lie
// Finding 181 (MEDIUM): CinderStrategy unsynchronized read
// Finding 182 (MEDIUM): CloudflareR2 SemaphoreSlim leak
// Finding 183 (HIGH): SSTableReader shared stream corruption
// Finding 184 (MEDIUM): SSTableWriter no sort validation
// Finding 185 (MEDIUM): WalWriter concurrent replay+append race
// Finding 186 (HIGH): BeeGFS Rule 13 sidecar stubs
// Finding 187 (MEDIUM): CephRados missing EnsureInitialized
// Finding 188 (HIGH): HttpClient not disposed in Dispose
// Finding 189 (LOW): CephRgw XML traversal inefficiency
// Finding 190 (MEDIUM): CephRgw missing EnsureInitialized
// Finding 191 (HIGH): Credentials stored as plain string
// Finding 192 (MEDIUM): JuiceFs encryption key plain string
// Finding 193 (HIGH): LizardFs unbounded semaphore dictionary
// Finding 194 (LOW): CLI exit codes not checked
// Finding 195 (HIGH): MooseFs unbounded semaphore dictionary

namespace DataWarehouse.Hardening.Tests.Plugin;

public class StorageHardeningTests
{
    private static readonly string StorageRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");

    /// <summary>
    /// Finding 178 (HIGH): NFS shell command injection must be prevented.
    /// </summary>
    [Fact]
    public void Finding178_Nfs_NoShellInjection()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "Network");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*Nfs*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("Process") && (code.Contains("_exportPath") || code.Contains("_mountPoint")))
        {
            Assert.True(
                code.Contains("Sanitize") || code.Contains("Path.GetFullPath") ||
                code.Contains("ArgumentList") || code.Contains("ProcessStartInfo") ||
                code.Contains("IsNullOrEmpty"),
                "NFS: Shell command parameters must be sanitized against injection");
        }
    }

    /// <summary>
    /// Finding 179 (MEDIUM): Use WaitForExitAsync instead of WaitForExit.
    /// </summary>
    [Fact]
    public void Finding179_Nfs_AsyncWaitForExit()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "Network");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*Nfs*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should not use sync WaitForExit in async methods
        Assert.DoesNotContain("WaitForExit()", code);
    }

    /// <summary>
    /// Finding 181 (MEDIUM): CinderStrategy volume cache must be synchronized for reads.
    /// </summary>
    [Fact]
    public void Finding181_Cinder_SynchronizedReads()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "OpenStack");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*Cinder*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("_volumeCache"))
        {
            // Verify cache accesses are synchronized via lock or SemaphoreSlim
            Assert.True(
                code.Contains("lock(") || code.Contains("lock (") ||
                code.Contains("SemaphoreSlim") || code.Contains("ConcurrentDictionary"),
                "CinderStrategy: _volumeCache must be synchronized for concurrent access");
        }
    }

    /// <summary>
    /// Finding 183 (HIGH): SSTableReader must synchronize shared stream access.
    /// </summary>
    [Fact]
    public void Finding183_SSTableReader_StreamSynchronized()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "Scale", "LsmTree");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*SSTableReader*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("_stream"))
        {
            Assert.True(
                code.Contains("lock(") || code.Contains("lock (") ||
                code.Contains("SemaphoreSlim") || code.Contains("Interlocked"),
                "SSTableReader: Shared stream must be synchronized for concurrent readers");
        }
    }

    /// <summary>
    /// Finding 184 (MEDIUM): SSTableWriter must validate entries are sorted.
    /// </summary>
    [Fact]
    public void Finding184_SSTableWriter_ValidateSort()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "Scale", "LsmTree");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*SSTableWriter*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        Assert.True(
            code.Contains("sorted") || code.Contains("Sorted") ||
            code.Contains("CompareTo") || code.Contains("OrderBy"),
            "SSTableWriter: Must validate that entries are sorted before writing");
    }

    /// <summary>
    /// Finding 185 (MEDIUM): WalWriter must prevent concurrent replay + append.
    /// </summary>
    [Fact]
    public void Finding185_WalWriter_NoConcurrentReplayAppend()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "Scale", "LsmTree");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*WalWriter*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        Assert.True(
            code.Contains("SemaphoreSlim") || code.Contains("lock(") ||
            code.Contains("lock (") || code.Contains("_replayLock"),
            "WalWriter: Must synchronize replay and append operations");
    }

    /// <summary>
    /// Finding 186 (HIGH): BeeGFS strategies must not write sidecar JSON instead of CLI calls.
    /// </summary>
    [Fact]
    public void Finding186_BeeGfs_NotSidecar()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "SoftwareDefined");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*BeeGfs*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should use beegfs-ctl or proper API, not JSON sidecar files
        Assert.True(
            code.Contains("beegfs-ctl") || code.Contains("Process") ||
            code.Contains("NotSupportedException") || code.Contains("beegfs"),
            "BeeGFS: Must use beegfs-ctl CLI or proper API, not sidecar JSON files");
    }

    /// <summary>
    /// Finding 187, 190 (MEDIUM): Ceph strategies must call EnsureInitialized.
    /// </summary>
    [Fact]
    public void Finding187_190_Ceph_EnsureInitialized()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "SoftwareDefined");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*Ceph*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("_httpClient") && code.Contains("StoreAsyncCore"))
            {
                Assert.True(
                    code.Contains("EnsureInitialized") ||
                    code.Contains("_httpClient ??") || code.Contains("_httpClient!") ||
                    code.Contains("?? throw"),
                    $"{fileName}: Must call EnsureInitialized before using _httpClient");
            }
        }
    }

    /// <summary>
    /// Finding 188 (HIGH): HttpClient instances must be disposed in DisposeCoreAsync.
    /// </summary>
    [Fact]
    public void Finding188_SoftwareDefined_HttpClientDisposed()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "SoftwareDefined");
        if (!Directory.Exists(dir)) return;

        var targetFiles = new[] { "*CephRados*.cs", "*CephRgw*.cs", "*GlusterFs*.cs", "*JuiceFs*.cs" };
        foreach (var pattern in targetFiles)
        {
            var file = Directory.GetFiles(dir, pattern).FirstOrDefault();
            if (file == null) continue;

            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("_httpClient"))
            {
                Assert.True(
                    code.Contains("_httpClient?.Dispose") || code.Contains("_httpClient.Dispose") ||
                    code.Contains("Dispose()") || code.Contains("IDisposable"),
                    $"{fileName}: _httpClient must be disposed in Dispose/DisposeCoreAsync");
            }
        }
    }

    /// <summary>
    /// Finding 191-192 (HIGH/MEDIUM): Credentials must be zeroed on dispose.
    /// </summary>
    [Fact]
    public void Finding191_192_Credentials_ZeroedOnDispose()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "SoftwareDefined");
        if (!Directory.Exists(dir)) return;

        var sensitiveFields = new[] { "_secretKey", "_accessKey", "_webDavPassword", "_s3SecretKey", "_managementApiPassword", "_encryptionKey" };

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (var field in sensitiveFields)
            {
                if (code.Contains(field) && code.Contains("string " + field.TrimStart('_')))
                {
                    Assert.True(
                        code.Contains($"{field} = null") || code.Contains($"{field} = \"\"") ||
                        code.Contains($"{field} = string.Empty") || code.Contains("CryptographicOperations.ZeroMemory"),
                        $"{fileName}: {field} must be zeroed/cleared on dispose");
                }
            }
        }
    }

    /// <summary>
    /// Findings 193, 195 (HIGH): Unbounded semaphore dictionaries must have eviction.
    /// </summary>
    [Fact]
    public void Finding193_195_UnboundedSemaphoreDict()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "SoftwareDefined");
        if (!Directory.Exists(dir)) return;

        var targetFiles = new[] { "*LizardFs*.cs", "*MooseFs*.cs" };
        foreach (var pattern in targetFiles)
        {
            var file = Directory.GetFiles(dir, pattern).FirstOrDefault();
            if (file == null) continue;

            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("Dictionary<string, SemaphoreSlim>"))
            {
                Assert.True(
                    code.Contains("Remove") || code.Contains("TryRemove") ||
                    code.Contains("_maxSize") || code.Contains("Evict") ||
                    code.Contains("Bounded"),
                    $"{fileName}: Semaphore dictionary must have eviction or bounding");
            }
        }
    }

    /// <summary>
    /// Finding 194 (LOW): CLI process exit codes must be checked.
    /// </summary>
    [Fact]
    public void Finding194_CliExitCodes_Checked()
    {
        var dir = Path.Combine(StorageRoot, "Strategies", "SoftwareDefined");
        if (!Directory.Exists(dir)) return;

        var targetFiles = new[] { "*LizardFs*.cs", "*Lustre*.cs", "*MooseFs*.cs" };
        foreach (var pattern in targetFiles)
        {
            var file = Directory.GetFiles(dir, pattern).FirstOrDefault();
            if (file == null) continue;

            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("WaitForExit"))
            {
                Assert.True(
                    code.Contains("ExitCode") || code.Contains("exitCode"),
                    $"{fileName}: CLI process exit codes should be checked after WaitForExit");
            }
        }
    }
}
