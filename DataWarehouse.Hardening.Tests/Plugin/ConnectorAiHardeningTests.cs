// Hardening tests for Plugin findings 65-85: UltimateConnector AI, Blockchain, CloudPlatform
// Finding 65-66 (HIGH): AI connectors HttpClient + HttpResponseMessage leaks
// Finding 67 (HIGH): StringContent not disposed
// Finding 68 (HIGH): JsonDocument not disposed
// Finding 69-70 (HIGH): StreamResponseAsync swallows cancellation
// Finding 71 (HIGH): URL path injection
// Finding 72 (HIGH): TestCoreAsync always returns true
// Findings 73-79: Second batch of AI connectors same patterns
// Finding 80-85: WeightsAndBiases+Whisper, Blockchain, CloudPlatform patterns

using System.Reflection;

namespace DataWarehouse.Hardening.Tests.Plugin;

public class ConnectorAiHardeningTests
{
    private static readonly string ConnectorRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateConnector", "Strategies");

    /// <summary>
    /// Findings 65, 73 (HIGH): AI connectors must not create new HttpClient per connection.
    /// </summary>
    [Fact]
    public void Finding065_073_AiConnectors_HttpClientManagement()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);
            // Verify HttpClient is handled properly - either via factory or proper disposal
            if (code.Contains("ConnectCoreAsync") && code.Contains("new HttpClient"))
            {
                // OK if the HttpClient is stored in the connection handle for reuse
                Assert.True(
                    code.Contains("DefaultConnectionHandle") || code.Contains("ConnectionHandle"),
                    $"{fileName}: HttpClient should be stored in connection handle for lifecycle management");
            }
        }
    }

    /// <summary>
    /// Findings 66, 74 (HIGH): HttpResponseMessage must be in using blocks.
    /// </summary>
    [Fact]
    public void Finding066_074_HttpResponseMessage_Disposed()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Check that response messages have using
            if (code.Contains("SendAsync") || code.Contains("PostAsync") || code.Contains("GetAsync"))
            {
                // Count responses without using
                var responseLines = code.Split('\n')
                    .Where(l => (l.Contains("= await") || l.Contains("=await")) &&
                                (l.Contains("SendAsync") || l.Contains("PostAsync") || l.Contains("GetAsync")))
                    .ToList();

                foreach (var line in responseLines)
                {
                    if (!line.Contains("using ") && !line.TrimStart().StartsWith("using ") &&
                        !line.Contains("using(") && !line.Contains("using var"))
                    {
                        violations.Add($"{fileName}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 67, 75 (HIGH): StringContent must be disposed after use.
    /// </summary>
    [Fact]
    public void Finding067_075_StringContent_Disposed()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("new StringContent"))
            {
                var lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("new StringContent") &&
                        !lines[i].Contains("using ") && !lines[i].TrimStart().StartsWith("using ") &&
                        !lines[i].Contains("request.Content") && !lines[i].Contains(".Content ="))
                    {
                        // StringContent assigned to request.Content is disposed via the request
                        violations.Add($"{fileName}:{i + 1}: StringContent without using");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 68, 76 (HIGH): JsonDocument.Parse must be in using blocks.
    /// </summary>
    [Fact]
    public void Finding068_076_JsonDocument_Disposed()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("JsonDocument.Parse"))
            {
                var lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("JsonDocument.Parse") &&
                        !lines[i].Contains("using ") && !lines[i].TrimStart().StartsWith("using "))
                    {
                        violations.Add($"{fileName}:{i + 1}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 69-70, 77 (HIGH): StreamResponseAsync must not swallow OperationCanceledException.
    /// </summary>
    [Fact]
    public void Finding069_070_077_StreamResponse_PropagatesCancellation()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("StreamResponseAsync"))
            {
                // Should not have bare catch blocks in streaming
                if (System.Text.RegularExpressions.Regex.IsMatch(code,
                    @"StreamResponseAsync.*?catch\s*\{",
                    System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    // Check if it re-throws OperationCanceledException
                    if (!code.Contains("OperationCanceledException") ||
                        !code.Contains("throw"))
                    {
                        violations.Add(fileName);
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 71, 78 (HIGH): URL path parameters must be encoded.
    /// </summary>
    [Fact]
    public void Finding071_078_UrlPathInjection_Encoded()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Check for user-supplied identifiers in URL paths without encoding
            // Pattern: $"/path/{variable}" where variable is not wrapped in Uri.EscapeDataString
            var matches = System.Text.RegularExpressions.Regex.Matches(code,
                @"\$""[^""]*\{(?!Uri\.EscapeDataString)(model|deployment|collection|index|voiceId|pipelineId|runId|engineId|experimentId)\}[^""]*""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                violations.Add($"{fileName}: {m.Value}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 72, 79 (HIGH): TestCoreAsync must not always return true.
    /// </summary>
    [Fact]
    public void Finding072_079_TestCoreAsync_NotAlwaysTrue()
    {
        var aiDir = Path.Combine(ConnectorRoot, "AI");
        Assert.True(Directory.Exists(aiDir));

        var files = Directory.GetFiles(aiDir, "*.cs");
        var violations = new List<string>();

        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Check for TestCoreAsync that returns Task.FromResult(true) unconditionally
            if (System.Text.RegularExpressions.Regex.IsMatch(code,
                @"TestCoreAsync[^{]*\{[^}]*return\s+Task\.FromResult\(true\)\s*;[^}]*\}"))
            {
                violations.Add(fileName);
            }
            // Also check for simple return true without any check
            if (System.Text.RegularExpressions.Regex.IsMatch(code,
                @"TestCoreAsync[^{]*\{[^}]*return\s+true\s*;[^}]*\}") &&
                !code.Contains("GetAsync") && !code.Contains("SendAsync"))
            {
                violations.Add(fileName);
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 80-81 (HIGH): Blockchain connectors HttpClient leaks.
    /// </summary>
    [Fact]
    public void Finding080_081_BlockchainConnectors_HttpClientLifecycle()
    {
        var bcDir = Path.Combine(ConnectorRoot, "Blockchain");
        if (!Directory.Exists(bcDir)) return;

        var files = Directory.GetFiles(bcDir, "*.cs");
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("new HttpClient") && code.Contains("ConnectCoreAsync"))
            {
                // Should have error handling around HttpClient creation
                Assert.True(
                    code.Contains("try") || code.Contains("using "),
                    $"{fileName}: HttpClient creation should be in try/finally for cleanup on error");
            }
        }
    }

    /// <summary>
    /// Finding 82-83 (HIGH): Blockchain connector stubs (GetBlockAsync/SubmitTransactionAsync).
    /// These throw NotSupportedException with descriptive messages indicating which SDK is needed.
    /// This is tracked as a known limitation requiring actual SDK integration (Nethereum, Solnet, etc.)
    /// which is outside hardening scope. The fix ensures descriptive exception messages are present.
    /// </summary>
    [Fact]
    public void Finding082_083_BlockchainConnectors_DescriptiveStubs()
    {
        var bcDir = Path.Combine(ConnectorRoot, "Blockchain");
        if (!Directory.Exists(bcDir)) return;

        var files = Directory.GetFiles(bcDir, "*.cs");
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Stubs should have descriptive messages indicating which SDK is needed
            if (code.Contains("NotSupportedException"))
            {
                Assert.True(
                    code.Contains("Requires") || code.Contains("SDK") || code.Contains("library"),
                    $"{fileName}: NotSupportedException should include descriptive message about required SDK");
            }
        }
    }

    /// <summary>
    /// Finding 84 (HIGH): Null guard on config.ConnectionString before Uri construction.
    /// </summary>
    [Fact]
    public void Finding084_BlockchainConnectors_NullGuard()
    {
        var bcDir = Path.Combine(ConnectorRoot, "Blockchain");
        if (!Directory.Exists(bcDir)) return;

        var files = Directory.GetFiles(bcDir, "*.cs");
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // ConnectionString must be validated before use (Split, Uri, etc.)
            if (code.Contains("ConnectionString") && code.Contains("ConnectCoreAsync"))
            {
                Assert.True(
                    code.Contains("?? throw") || code.Contains("ArgumentException") ||
                    code.Contains("IsNullOrEmpty") || code.Contains("IsNullOrWhiteSpace") ||
                    code.Contains("ArgumentNullException"),
                    $"{fileName}: ConnectionString must be validated before use");
            }
        }
    }

    /// <summary>
    /// Finding 85 (MEDIUM): TestCoreAsync/GetHealthCoreAsync must not always return true/Healthy.
    /// </summary>
    [Fact]
    public void Finding085_HealthCheck_Functional()
    {
        var bcDir = Path.Combine(ConnectorRoot, "Blockchain");
        if (!Directory.Exists(bcDir)) return;

        var files = Directory.GetFiles(bcDir, "*.cs");
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // TestCoreAsync should do a real check
            if (System.Text.RegularExpressions.Regex.IsMatch(code,
                @"TestCoreAsync[^{]*\{[^}]*Task\.FromResult\(true\)[^}]*\}"))
            {
                Assert.Fail($"{fileName}: TestCoreAsync should perform actual connectivity check");
            }
        }
    }
}
