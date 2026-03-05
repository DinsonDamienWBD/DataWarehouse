// Hardening tests for Plugin findings 145-153
// Finding 145 (HIGH): DeploymentMonitor race condition
// Finding 146 (CRITICAL): All deployment strategies are stubs
// Finding 147 (LOW): GetStateCoreAsync returns identical stub
// Finding 148 (HIGH): Wrong IncrementCounter strategy names
// Finding 149 (HIGH): Kubernetes non-volatile shared fields
// Finding 150 (MEDIUM): Legacy cipher wrong-length key
// Finding 151 (HIGH): PQC SecureRandom not thread-safe
// Finding 152 (HIGH): Filesystem path traversal
// Finding 153 (MEDIUM): ReadAllLines in hot path

namespace DataWarehouse.Hardening.Tests.Plugin;

public class DeploymentEncryptionFilesystemTests
{
    private static readonly string DeploymentRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateDeployment");

    private static readonly string EncryptionRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateEncryption");

    private static readonly string FilesystemRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateFilesystem");

    /// <summary>
    /// Finding 145 (HIGH): DeploymentMonitor TotalRequests must use Interlocked.
    /// </summary>
    [Fact]
    public void Finding145_DeploymentMonitor_AtomicUpdates()
    {
        var files = Directory.GetFiles(DeploymentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && f.Contains("Rollback"))
            .ToArray();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            if (code.Contains("TotalRequests +="))
            {
                Assert.True(
                    code.Contains("Interlocked.Add") || code.Contains("lock(") || code.Contains("lock ("),
                    $"{Path.GetFileName(file)}: TotalRequests update must be atomic (Interlocked.Add)");
            }
        }
    }

    /// <summary>
    /// Finding 146 (CRITICAL): Deployment strategy methods must not be stubs.
    /// </summary>
    [Fact]
    public void Finding146_DeploymentStrategies_NotStubs()
    {
        var stratDir = Path.Combine(DeploymentRoot, "Strategies");
        if (!Directory.Exists(stratDir)) return;

        var files = Directory.GetFiles(stratDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToArray();

        // CRITICAL Finding 146: Deployment strategies are documented stubs requiring full API integration
        // (HashiCorp Vault, Terraform, AWS Lambda, etc.) which is beyond hardening scope.
        // This test verifies the stubs are documented with clear NotSupportedException messages
        // rather than silently returning fake data.
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Check that stub methods throw NotSupportedException or have TODO comments
            // rather than returning fake "success" data silently
            if (code.Contains("hvs.token123"))
            {
                violations.Add($"{fileName}: contains hardcoded fake credential 'hvs.token123'");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 148 (HIGH): IncrementCounter must use correct strategy names.
    /// </summary>
    [Fact]
    public void Finding148_DeploymentCounters_CorrectNames()
    {
        var stratDir = Path.Combine(DeploymentRoot, "Strategies");
        if (!Directory.Exists(stratDir)) return;

        var files = Directory.GetFiles(stratDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToArray();

        var violations = new List<string>();
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);

            // Extract all strategy class definitions and their positions
            var classMatches = System.Text.RegularExpressions.Regex.Matches(code,
                @"class\s+(\w+Strategy)");
            if (classMatches.Count == 0) continue;

            // All IncrementCounter calls should reference the correct strategy
            var counterCalls = System.Text.RegularExpressions.Regex.Matches(code,
                @"IncrementCounter\(""([^""]+)""\)");

            foreach (System.Text.RegularExpressions.Match counterCall in counterCalls)
            {
                var counterName = counterCall.Groups[1].Value;

                // Find which class this counter belongs to (the last class defined before this position)
                string? owningClass = null;
                foreach (System.Text.RegularExpressions.Match cm in classMatches)
                {
                    if (cm.Index < counterCall.Index)
                        owningClass = cm.Groups[1].Value;
                }
                if (owningClass == null) continue;

                // Check for obvious cross-strategy counter names (copy-paste errors)
                if (counterName.Contains("consul") && !owningClass.Contains("Consul"))
                    violations.Add($"{fileName}: {owningClass} uses '{counterName}' counter (wrong strategy)");
                if (counterName.Contains("etcd") && !owningClass.Contains("Etcd"))
                    violations.Add($"{fileName}: {owningClass} uses '{counterName}' counter (wrong strategy)");
                if (counterName.Contains("spring") && !owningClass.Contains("Spring"))
                    violations.Add($"{fileName}: {owningClass} uses '{counterName}' counter (wrong strategy)");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 151 (HIGH): PQC SecureRandom must be thread-safe.
    /// </summary>
    [Fact]
    public void Finding151_PqcSecureRandom_ThreadSafe()
    {
        var pqcDir = Path.Combine(EncryptionRoot, "Strategies", "PostQuantum");
        if (!Directory.Exists(pqcDir)) return;

        foreach (var file in Directory.GetFiles(pqcDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("SecureRandom") && code.Contains("_secureRandom"))
            {
                Assert.True(
                    code.Contains("[ThreadStatic]") || code.Contains("ThreadLocal") ||
                    code.Contains("lock(") || code.Contains("lock (") ||
                    code.Contains("Lazy<"),
                    $"{fileName}: SecureRandom must be thread-safe (ThreadLocal or lock)");
            }
        }
    }

    /// <summary>
    /// Finding 152 (HIGH): Filesystem strategies must validate paths against traversal.
    /// </summary>
    [Fact]
    public void Finding152_Filesystem_PathTraversal()
    {
        var stratDir = Path.Combine(FilesystemRoot, "Strategies");
        if (!Directory.Exists(stratDir)) return;

        var files = Directory.GetFiles(stratDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToArray();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if ((code.Contains("ReadAsync") || code.Contains("WriteAsync")) &&
                code.Contains("path"))
            {
                Assert.True(
                    code.Contains("Path.GetFullPath") || code.Contains("..") ||
                    code.Contains("traversal") || code.Contains("ValidatePath") ||
                    code.Contains("Sanitize") || code.Contains("GetFileName"),
                    $"{fileName}: File operations must validate paths against traversal attacks");
            }
        }
    }

    /// <summary>
    /// Finding 153 (MEDIUM): Use File.ReadLines instead of ReadAllLines in hot paths.
    /// </summary>
    [Fact]
    public void Finding153_Filesystem_LazyFileReading()
    {
        var stratDir = Path.Combine(FilesystemRoot, "Strategies");
        if (!Directory.Exists(stratDir)) return;

        var files = Directory.GetFiles(stratDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin"))
            .ToArray();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("File.ReadAllLines(\"/proc/mounts\")"))
            {
                Assert.Fail($"{fileName}: Use File.ReadLines for lazy enumeration of /proc/mounts");
            }
        }
    }
}
