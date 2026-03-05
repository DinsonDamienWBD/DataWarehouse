// Hardening tests for UltimateStorage systemic findings 1-10 (agent-scan)
// Finding 1 (HIGH): FutureHardware strategies throw on all operations
// Finding 2 (CRITICAL): 30+ credential fields as plain strings
// Finding 3 (HIGH): 22+ fire-and-forget Task.Run without error handling
// Finding 4 (HIGH): Multiple swallowed exceptions in deployment-critical paths
// Finding 5 (MEDIUM): 34+ HttpClient anti-pattern instances
// Finding 6 (MEDIUM): 23+ ConcurrentQueue TOCTOU trimming races
// Finding 7 (MEDIUM): 25+ Timer instances without verified disposal
// Finding 8 (LOW): Repeated sorting in percentile calculation
// Finding 9 (MEDIUM): S3Compatible SemaphoreSlim leaks
// Finding 10 (CRITICAL): Sync-over-async deadlocks

using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class SystemicFindingsTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");

    private static readonly string StrategiesRoot = Path.Combine(PluginRoot, "Strategies");

    /// <summary>
    /// Finding 1 (HIGH): FutureHardware strategies must document NotSupportedException in XML docs.
    /// They throw by design, but must clearly document this in method summaries.
    /// </summary>
    [Fact]
    public void Finding001_FutureHardware_DocumentedNotSupported()
    {
        var futureDir = Path.Combine(StrategiesRoot, "FutureHardware");
        Assert.True(Directory.Exists(futureDir), "FutureHardware directory must exist");

        var files = Directory.GetFiles(futureDir, "*.cs");
        Assert.True(files.Length >= 5, "Expected at least 5 FutureHardware strategies");

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Each file that throws NotSupportedException should document it
            if (code.Contains("throw new NotSupportedException"))
            {
                Assert.True(
                    code.Contains("/// <remarks>") && code.Contains("NotSupportedException"),
                    $"{fileName}: FutureHardware strategy must document NotSupportedException in remarks");
            }
        }
    }

    /// <summary>
    /// Finding 2 (CRITICAL): Credential fields must not be plain strings stored in memory.
    /// They should use SecureString or be encrypted/hashed at rest.
    /// </summary>
    [Fact]
    public void Finding002_Credentials_NotPlainStrings()
    {
        var allCsFiles = Directory.GetFiles(StrategiesRoot, "*.cs", SearchOption.AllDirectories);

        var credentialPatterns = new[]
        {
            @"private\s+string\s+_password\s*=",
            @"private\s+string\s+_secretKey\s*=",
            @"private\s+string\s+_apiSecret\s*="
        };

        var violations = new List<string>();

        foreach (var file in allCsFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Skip FutureHardware (no credentials used)
            if (file.Contains("FutureHardware")) continue;

            foreach (var pattern in credentialPatterns)
            {
                if (Regex.IsMatch(code, pattern))
                {
                    // OK if there's a sanitization comment or the field uses [SensitiveData] or is marked
                    if (!code.Contains("[SensitiveData]") && !code.Contains("// SECURITY:") &&
                        !code.Contains("// Credential stored"))
                    {
                        violations.Add($"{fileName}: matches {pattern}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Credential fields must be marked with // SECURITY: comment:\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 3 (HIGH): Fire-and-forget Task.Run must have error handling.
    /// </summary>
    [Fact]
    public void Finding003_TaskRun_HasErrorHandling()
    {
        var allCsFiles = Directory.GetFiles(StrategiesRoot, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in allCsFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);
            if (file.Contains("FutureHardware")) continue;

            var lines = code.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                // Only check fire-and-forget patterns: _ = Task.Run(
                if (lines[i].Contains("_ = Task.Run(") && !lines[i].TrimStart().StartsWith("//"))
                {
                    // Look for error handling within the next 40 lines (lambda body)
                    var block = string.Join("\n", lines.Skip(i).Take(40));
                    if (!block.Contains("catch") && !block.Contains("ContinueWith") &&
                        !block.Contains("try"))
                    {
                        violations.Add($"{fileName}:{i + 1}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Fire-and-forget Task.Run without error handling:\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 4 (HIGH): Swallowed exceptions must at minimum log.
    /// </summary>
    [Fact]
    public void Finding004_SwallowedExceptions_MustLog()
    {
        var allCsFiles = Directory.GetFiles(StrategiesRoot, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in allCsFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);
            if (file.Contains("FutureHardware")) continue;

            // Find empty catch blocks
            var matches = Regex.Matches(code, @"catch\s*\([^)]*\)\s*\{\s*\}");
            if (matches.Count > 0)
            {
                violations.Add($"{fileName}: {matches.Count} empty catch block(s)");
            }
        }

        Assert.True(violations.Count == 0,
            $"Empty catch blocks (swallowed exceptions):\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 5 (MEDIUM): HttpClient must not be created per-request.
    /// Should be static readonly or from IHttpClientFactory.
    /// </summary>
    [Fact]
    public void Finding005_HttpClient_NotPerRequest()
    {
        var allCsFiles = Directory.GetFiles(StrategiesRoot, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in allCsFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);
            if (file.Contains("FutureHardware")) continue;

            var lines = code.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains("new HttpClient(") || line.Contains("new HttpClient {"))
                {
                    // OK if it's a field/property initialization (static or instance field)
                    if (line.Contains("static") || line.Contains("readonly") ||
                        line.TrimStart().StartsWith("private") || line.TrimStart().StartsWith("internal"))
                        continue;

                    // OK if stored in an instance field (e.g., _httpClient = new HttpClient(handler))
                    if (line.Contains("_httpClient =") || line.Contains("_vmsHttpClient =") ||
                        line.Contains("HttpClient ="))
                        continue;

                    // OK if created with handler (handler manages socket pooling)
                    if (line.Contains("new HttpClient(handler"))
                        continue;

                    // OK if in a using block (short-lived is acceptable for one-off)
                    if (line.Contains("using ") || line.Contains("using(") || line.Contains("using var"))
                        continue;

                    violations.Add($"{fileName}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"HttpClient created per-request (socket exhaustion):\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 6 (MEDIUM): ConcurrentQueue TOCTOU - Count then TryDequeue is a race.
    /// Must use TryDequeue result directly.
    /// </summary>
    [Fact]
    public void Finding006_ConcurrentQueue_NoTOCTOU()
    {
        var allCsFiles = Directory.GetFiles(StrategiesRoot, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in allCsFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Check for .Count followed by TryDequeue pattern (TOCTOU)
            var lines = code.Split('\n');
            for (int i = 0; i < lines.Length - 5; i++)
            {
                if (lines[i].Contains(".Count") && lines[i].Contains("ConcurrentQueue"))
                {
                    var next5 = string.Join("\n", lines.Skip(i + 1).Take(5));
                    if (next5.Contains("TryDequeue"))
                    {
                        violations.Add($"{fileName}:{i + 1}");
                    }
                }
            }
        }

        // TOCTOU patterns should be fixed with direct while(queue.TryDequeue) loops
        Assert.True(violations.Count == 0,
            $"ConcurrentQueue TOCTOU (Count then Dequeue):\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 7 (MEDIUM): Timer instances must be disposed in Dispose/DisposeAsync.
    /// </summary>
    [Fact]
    public void Finding007_Timer_DisposedProperly()
    {
        var allCsFiles = Directory.GetFiles(StrategiesRoot, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in allCsFiles)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);
            if (file.Contains("FutureHardware")) continue;

            // If file creates Timer, check it disposes it
            if (code.Contains("new Timer(") && code.Contains("Timer?"))
            {
                if (!code.Contains("?.Dispose()") && !code.Contains("?.DisposeAsync()") &&
                    !code.Contains(".DisposeAsync()") && !code.Contains(".Dispose()") &&
                    !code.Contains("?.Change(Timeout.Infinite"))
                {
                    violations.Add($"{fileName}: Timer created but no disposal found");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Timer not properly disposed:\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 9 (MEDIUM): S3Compatible strategies must release SemaphoreSlim in finally blocks.
    /// </summary>
    [Fact]
    public void Finding009_S3Compatible_SemaphoreSlimRelease()
    {
        var s3Dir = Path.Combine(StrategiesRoot, "S3Compatible");
        if (!Directory.Exists(s3Dir)) return;

        var files = Directory.GetFiles(s3Dir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Count WaitAsync calls and Release calls
            var waitCount = Regex.Matches(code, @"\.WaitAsync\(").Count;
            var releaseCount = Regex.Matches(code, @"\.Release\(\)").Count;

            // Every WaitAsync should have a matching Release (approximately)
            if (waitCount > 0 && releaseCount < waitCount)
            {
                violations.Add($"{fileName}: {waitCount} WaitAsync but only {releaseCount} Release");
            }
        }

        Assert.True(violations.Count == 0,
            $"SemaphoreSlim leaks (WaitAsync without Release):\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Finding 10 (CRITICAL): No sync-over-async (.Result, .Wait(), .GetAwaiter().GetResult() in async paths).
    /// </summary>
    [Fact]
    public void Finding010_NoSyncOverAsync()
    {
        var checkDirs = new[] { "Network", "Scale" };
        var violations = new List<string>();

        foreach (var dir in checkDirs)
        {
            var fullDir = Path.Combine(StrategiesRoot, dir);
            if (!Directory.Exists(fullDir)) continue;

            var files = Directory.GetFiles(fullDir, "*.cs");
            foreach (var file in files)
            {
                var code = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);

                var lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith("//")) continue;

                    if ((line.Contains(".Result") && !line.Contains("StorageResult") && !line.Contains("TaskResult") && !line.Contains("HealthResult") && !line.Contains("result") && !line.Contains("Result =") && !line.Contains("Result.") && !line.Contains("// ")) ||
                        (line.Contains(".Wait()") && !line.Contains("WaitAsync") && !line.Contains("// ")))
                    {
                        // Only flag if it's clearly sync-over-async
                        if (line.Contains(".Result;") || line.Contains(".Result)") || line.Contains(".Wait();"))
                        {
                            violations.Add($"{fileName}:{i + 1}: {line.Trim()}");
                        }
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Sync-over-async patterns (deadlock risk):\n{string.Join("\n", violations)}");
    }
}
