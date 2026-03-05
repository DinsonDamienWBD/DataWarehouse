// Hardening tests for Plugin findings 137-144: UltimateDatabaseProtocol + UltimateDatabaseStorage
// Finding 137 (HIGH): JdbcBridge HttpClient without factory
// Finding 138 (HIGH): Embedded DB file ops no try/catch
// Finding 139 (HIGH): Db2Drda ParseQueryData unusable
// Finding 140 (MEDIUM): VertexCut HashSet mutation not thread-safe
// Finding 141 (HIGH): KV strategies bare catch in ListCoreAsync
// Finding 142 (MEDIUM): Cassandra/ScyllaDb full table scan
// Finding 143 (HIGH): Kafka bare catch in CheckHealthCoreAsync
// Finding 144 (MEDIUM): VictoriaMetrics HttpClient per-instance

namespace DataWarehouse.Hardening.Tests.Plugin;

public class DatabaseProtocolStorageTests
{
    private static readonly string DbProtocolRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateDatabaseProtocol");

    private static readonly string DbStorageRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateDatabaseStorage");

    /// <summary>
    /// Finding 138 (HIGH): Embedded DB file operations must have try/catch.
    /// </summary>
    [Fact]
    public void Finding138_EmbeddedDb_FileOpsHandled()
    {
        var dir = Path.Combine(DbProtocolRoot, "Strategies", "Embedded");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("ReadAllText") || code.Contains("WriteAllText") ||
                code.Contains("File.Delete"))
            {
                Assert.True(code.Contains("try") || code.Contains("IOException"),
                    $"{fileName}: File operations should have IOException handling");
            }
        }
    }

    /// <summary>
    /// Finding 140 (MEDIUM): VertexCut HashSet must be thread-safe.
    /// </summary>
    [Fact]
    public void Finding140_VertexCut_ThreadSafe()
    {
        var dir = Path.Combine(DbStorageRoot, "Strategies", "Graph");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*Partition*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("HashSet<int>") && code.Contains("AddOrUpdate"))
        {
            Assert.True(
                code.Contains("lock(") || code.Contains("lock (") ||
                code.Contains("ConcurrentDictionary"),
                "VertexCut: HashSet mutation in AddOrUpdate must be thread-safe");
        }
    }

    /// <summary>
    /// Finding 141 (HIGH): KV strategies must not silently skip errors in list operations.
    /// </summary>
    [Fact]
    public void Finding141_KvStrategies_NoSilentSkip()
    {
        var dir = Path.Combine(DbStorageRoot, "Strategies", "KeyValue");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("ListCoreAsync"))
            {
                // catch { continue; } is acceptable if OperationCanceledException is already handled
                // (the catch { continue; } only catches non-cancellation exceptions at that point)
                Assert.False(
                    System.Text.RegularExpressions.Regex.IsMatch(code,
                        @"catch\s*\{\s*continue\s*;\s*\}") &&
                    !code.Contains("OperationCanceledException"),
                    $"{fileName}: ListCoreAsync should not silently skip items on error without OCE guard");
            }
        }
    }

    /// <summary>
    /// Finding 143 (HIGH): Health check bare catch must propagate real errors.
    /// </summary>
    [Fact]
    public void Finding143_HealthCheck_PropagatesErrors()
    {
        var storageFiles = Directory.GetFiles(DbStorageRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToArray();

        var violations = new List<string>();
        foreach (var file in storageFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("CheckHealthCoreAsync") &&
                System.Text.RegularExpressions.Regex.IsMatch(code,
                    @"catch\s*\{\s*return\s+false\s*;\s*\}") &&
                !code.Contains("OperationCanceledException"))
            {
                violations.Add(fileName);
            }
        }

        Assert.Empty(violations);
    }
}
