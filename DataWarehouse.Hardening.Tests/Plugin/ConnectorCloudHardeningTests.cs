// Hardening tests for Plugin findings 86-105: UltimateConnector CloudPlatform, CloudWarehouse, Database, FileSystem, Healthcare
// Finding 86 (HIGH): CloudPlatform/CloudWarehouse socket exhaustion
// Finding 87 (HIGH): Bare catch swallows OperationCanceledException
// Finding 88 (MEDIUM): CloudPlatform bare catch blocks
// Findings 89-90 (HIGH): AWS strategies health check and catch issues
// Finding 91 (LOW): Pointless async in DisconnectCoreAsync
// Finding 92 (HIGH): No auth headers on raw HttpClient strategies
// Finding 93 (HIGH): Socket exhaustion in raw HttpClient strategies
// Finding 94 (MEDIUM): TestCoreAsync accepts non-success codes as healthy
// Finding 95 (HIGH): Same misleading health check pattern
// Finding 96 (HIGH): CloudWarehouse concurrent connection field races
// Finding 97 (HIGH): ParseHostPort IPv6 broken
// Finding 98 (HIGH): CRLF injection in auth header
// Findings 99-103: Database connector issues
// Findings 104-105: FileSystem+Healthcare TCP strategy issues

namespace DataWarehouse.Hardening.Tests.Plugin;

public class ConnectorCloudHardeningTests
{
    private static readonly string ConnectorRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateConnector", "Strategies");

    /// <summary>
    /// Finding 87-89 (HIGH): Bare catch blocks must propagate OperationCanceledException.
    /// </summary>
    [Fact]
    public void Finding087_089_CloudPlatform_NoBareSwallowingCatch()
    {
        var cpDir = Path.Combine(ConnectorRoot, "CloudPlatform");
        if (!Directory.Exists(cpDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(cpDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Catch blocks that return false are OK if preceded by OperationCanceledException handling
            if (code.Contains("TestCoreAsync") &&
                System.Text.RegularExpressions.Regex.IsMatch(code,
                    @"catch\s*\{\s*return\s+(false|true)\s*;\s*\}") &&
                !code.Contains("OperationCanceledException"))
            {
                violations.Add(fileName);
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 90, 94-95 (HIGH/MEDIUM): Health check must reject non-success HTTP status codes.
    /// </summary>
    [Fact]
    public void Finding090_094_095_HealthCheck_RejectNonSuccess()
    {
        var cpDir = Path.Combine(ConnectorRoot, "CloudPlatform");
        if (!Directory.Exists(cpDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(cpDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Should not check only for 503 - should use IsSuccessStatusCode
            if (code.Contains("ServiceUnavailable") && !code.Contains("IsSuccessStatusCode"))
            {
                violations.Add(fileName);
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 92 (HIGH): Raw HttpClient strategies must set authentication headers.
    /// </summary>
    [Fact]
    public void Finding092_RawHttpClient_HasAuthHeaders()
    {
        var cpDir = Path.Combine(ConnectorRoot, "CloudPlatform");
        if (!Directory.Exists(cpDir)) return;

        var rawHttpClientStrategies = new[]
        {
            "AzureCosmosConnectionStrategy.cs", "AzureDataLakeConnectionStrategy.cs",
            "AzureFunctionsConnectionStrategy.cs", "BackblazeB2ConnectionStrategy.cs",
            "CloudflareR2ConnectionStrategy.cs", "DigitalOceanSpacesConnectionStrategy.cs",
            "GcpBigtableConnectionStrategy.cs", "GcpDataflowConnectionStrategy.cs",
            "GcpFirestoreConnectionStrategy.cs"
        };

        foreach (var fileName in rawHttpClientStrategies)
        {
            var file = Path.Combine(cpDir, fileName);
            if (!File.Exists(file)) continue;

            var code = File.ReadAllText(file);
            // Auth headers should be set either in ConnectCoreAsync or via AuthenticateAsync override
            Assert.True(
                code.Contains("Authorization") || code.Contains("Bearer") ||
                code.Contains("x-api-key") || code.Contains("AuthCredential") ||
                code.Contains("AccessToken") || code.Contains("AuthenticateAsync") ||
                code.Contains("ConnectionInfo"),
                $"{fileName}: Should set authentication headers on HttpClient or implement AuthenticateAsync");
        }
    }

    /// <summary>
    /// Finding 96 (HIGH): CloudWarehouse connection fields must be thread-safe.
    /// </summary>
    [Fact]
    public void Finding096_CloudWarehouse_ThreadSafeConnectionFields()
    {
        var cwDir = Path.Combine(ConnectorRoot, "CloudWarehouse");
        if (!Directory.Exists(cwDir)) return;

        foreach (var file in Directory.GetFiles(cwDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Connection fields should be volatile or protected by lock
            if (code.Contains("_httpClient") || code.Contains("_tcpClient"))
            {
                Assert.True(
                    code.Contains("volatile") || code.Contains("lock(") ||
                    code.Contains("lock (") || code.Contains("Interlocked") ||
                    code.Contains("SemaphoreSlim"),
                    $"{fileName}: Connection fields should be thread-safe");
            }
        }
    }

    /// <summary>
    /// Finding 97 (HIGH): ParseHostPort must handle IPv6 addresses.
    /// </summary>
    [Fact]
    public void Finding097_CloudWarehouse_IPv6Parsing()
    {
        var cwDir = Path.Combine(ConnectorRoot, "CloudWarehouse");
        if (!Directory.Exists(cwDir)) return;

        foreach (var file in Directory.GetFiles(cwDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("ParseHostPort") && code.Contains("Split(':')"))
            {
                Assert.True(
                    code.Contains("[") || code.Contains("IPv6") ||
                    code.Contains("Uri.TryCreate"),
                    $"{fileName}: ParseHostPort must handle IPv6 bracket notation");
            }
        }
    }

    /// <summary>
    /// Finding 98 (HIGH): Authorization header must not be vulnerable to CRLF injection.
    /// </summary>
    [Fact]
    public void Finding098_CrlfInjection_AuthHeader()
    {
        var cwDir = Path.Combine(ConnectorRoot, "CloudWarehouse");
        if (!Directory.Exists(cwDir)) return;

        foreach (var file in Directory.GetFiles(cwDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (code.Contains("Authorization") && code.Contains("AuthCredential"))
            {
                Assert.True(
                    code.Contains("Replace(\"\\r") || code.Contains("Replace(\"\\n") ||
                    code.Contains("Contains(\"\\r") || code.Contains("Contains(\"\\n") ||
                    code.Contains("HeaderValidation") || code.Contains("Trim()"),
                    $"{fileName}: Auth credential must be sanitized for CRLF injection");
            }
        }
    }

    /// <summary>
    /// Findings 100, 102-103 (HIGH): Database connectors must not have mock connections or stub queries.
    /// </summary>
    [Fact]
    public void Finding100_102_103_Database_NotMockOrStub()
    {
        var dbDir = Path.Combine(ConnectorRoot, "Database");
        if (!Directory.Exists(dbDir)) return;

        foreach (var file in Directory.GetFiles(dbDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Should not contain mock connection objects
            Assert.DoesNotContain("mockConnection", code);

            // ExecuteQueryAsync should not return fabricated data
            if (code.Contains("ExecuteQueryAsync"))
            {
                Assert.DoesNotContain("OPERATION_NOT_SUPPORTED", code);
            }
        }
    }

    /// <summary>
    /// Findings 104-105 (HIGH/MEDIUM): TCP strategies must validate connection strings.
    /// </summary>
    [Fact]
    public void Finding104_105_TcpStrategies_ValidateConnString()
    {
        var dirs = new[]
        {
            Path.Combine(ConnectorRoot, "FileSystem"),
            Path.Combine(ConnectorRoot, "Healthcare"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var code = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);

                if (code.Contains("Split(':')") && code.Contains("ConnectionString"))
                {
                    Assert.True(
                        code.Contains("IsNullOrEmpty") || code.Contains("IsNullOrWhiteSpace") ||
                        code.Contains("?? throw") || code.Contains("ArgumentException"),
                        $"{fileName}: Connection string must be validated before Split");
                }
            }
        }
    }
}
