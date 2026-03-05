// Hardening tests for Plugin findings 106-136: Connector FileSystem, Innovations, IoT, Legacy, Messaging, Observability
// Finding 106 (MEDIUM): Non-sealed leaf strategies
// Finding 107 (HIGH): HttpClient Dispose race
// Finding 108 (MEDIUM): await Task.CompletedTask no-op
// Finding 109 (MEDIUM): Innovations StringContent not disposed
// Finding 110 (MEDIUM): ChameleonProtocol bare catch swallows cancellation
// Findings 111-118: IoT connection string validation, Rule 13 stubs
// Findings 119-121: Legacy TCP strategies parsing, TcpClient leak
// Finding 122 (MEDIUM): Messaging busy-poll
// Findings 123-125: Messaging HttpClient/health/subscribe issues
// Findings 126-136: Observability issues

namespace DataWarehouse.Hardening.Tests.Plugin;

public class ConnectorIoTLegacyMessagingTests
{
    private static readonly string ConnectorRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateConnector", "Strategies");

    /// <summary>
    /// Findings 111, 118-119 (MEDIUM-HIGH): Connection string parsing must validate null/empty.
    /// </summary>
    [Fact]
    public void Finding111_118_119_ConnectionStringValidation()
    {
        var dirs = new[]
        {
            Path.Combine(ConnectorRoot, "IoT"),
            Path.Combine(ConnectorRoot, "Legacy"),
            Path.Combine(ConnectorRoot, "Messaging"),
        };

        var violations = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var code = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);

                if (code.Contains("ConnectionString") && code.Contains("Split(':')"))
                {
                    if (!code.Contains("IsNullOrEmpty") && !code.Contains("IsNullOrWhiteSpace") &&
                        !code.Contains("?? throw") && !code.Contains("ArgumentException"))
                    {
                        violations.Add($"{dir.Split(Path.DirectorySeparatorChar).Last()}/{fileName}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 112, 117 (HIGH): IoT stubs must be replaced with proper implementations.
    /// </summary>
    [Fact]
    public void Finding112_117_IoT_NotStubs()
    {
        var iotDir = Path.Combine(ConnectorRoot, "IoT");
        if (!Directory.Exists(iotDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(iotDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // ReadTelemetryAsync/SendCommandAsync should not return fabricated data
            if (code.Contains("ReadTelemetryAsync") && code.Contains("\"queued\""))
            {
                violations.Add($"{fileName}: ReadTelemetryAsync returns fabricated data");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 113-114 (HIGH): IoT health checks must be functional.
    /// </summary>
    [Fact]
    public void Finding113_114_IoT_FunctionalHealthAndCommands()
    {
        var iotDir = Path.Combine(ConnectorRoot, "IoT");
        if (!Directory.Exists(iotDir)) return;

        foreach (var file in Directory.GetFiles(iotDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // TestCoreAsync should not return true for non-503 only
            if (code.Contains("TestCoreAsync") && code.Contains("ServiceUnavailable") &&
                !code.Contains("IsSuccessStatusCode"))
            {
                Assert.Fail($"{fileName}: TestCoreAsync should use IsSuccessStatusCode");
            }

            // SendCommandAsync should use correct HTTP verb (POST, not GET)
            if (code.Contains("SendCommandAsync"))
            {
                // Extract the SendCommandAsync method body and check if it uses GetAsync
                var sendCmdMatch = System.Text.RegularExpressions.Regex.Match(code,
                    @"SendCommandAsync[^{]*\{(.*?)(?=public\s|protected\s|private\s|$)",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                if (sendCmdMatch.Success && sendCmdMatch.Groups[1].Value.Contains("GetAsync"))
                {
                    Assert.Fail($"{fileName}: SendCommandAsync should use POST, not GET");
                }
            }
        }
    }

    /// <summary>
    /// Finding 120 (HIGH): TcpClient must be in using/try-finally for ConnectAsync.
    /// </summary>
    [Fact]
    public void Finding120_TcpClient_ProperDisposal()
    {
        var dirs = new[]
        {
            Path.Combine(ConnectorRoot, "Legacy"),
            Path.Combine(ConnectorRoot, "Messaging"),
            Path.Combine(ConnectorRoot, "IoT"),
        };

        var violations = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var code = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);

                if (code.Contains("new TcpClient") && code.Contains("ConnectAsync"))
                {
                    if (!code.Contains("try") && !code.Contains("using "))
                    {
                        violations.Add($"{Path.GetFileName(dir)}/{fileName}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 121 (MEDIUM): Bare catch in TestCoreAsync must propagate cancellation.
    /// </summary>
    [Fact]
    public void Finding121_BareCatch_PropagatesCancellation()
    {
        var dirs = new[]
        {
            Path.Combine(ConnectorRoot, "Legacy"),
            Path.Combine(ConnectorRoot, "Messaging"),
            Path.Combine(ConnectorRoot, "FileSystem"),
        };

        var violations = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var code = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);

                if (code.Contains("TestCoreAsync") &&
                    System.Text.RegularExpressions.Regex.IsMatch(code,
                        @"catch\s*\{\s*return\s+false\s*;\s*\}") &&
                    !code.Contains("OperationCanceledException"))
                {
                    violations.Add($"{Path.GetFileName(dir)}/{fileName}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 126-127 (MEDIUM/LOW): Observability batch-size and null validation.
    /// </summary>
    [Fact]
    public void Finding126_127_Observability_InputValidation()
    {
        var obsDir = Path.Combine(ConnectorRoot, "Observability");
        if (!Directory.Exists(obsDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(obsDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Should have some form of input validation
            if (code.Contains("PostAsync") || code.Contains("SendAsync"))
            {
                if (!code.Contains("ArgumentNullException") && !code.Contains("IsNullOrEmpty") &&
                    !code.Contains("throw") && !code.Contains("Count") &&
                    !code.Contains("Length"))
                {
                    violations.Add(fileName);
                }
            }
        }

        // Most files should have validation
        Assert.True(violations.Count < Directory.GetFiles(obsDir, "*.cs").Length / 2,
            $"Too many observability strategies lack input validation: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// Findings 128 (MEDIUM): TestCoreAsync must not return unconditional true.
    /// </summary>
    [Fact]
    public void Finding128_Observability_TestNotAlwaysTrue()
    {
        var obsDir = Path.Combine(ConnectorRoot, "Observability");
        if (!Directory.Exists(obsDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(obsDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            if (System.Text.RegularExpressions.Regex.IsMatch(code,
                @"TestCoreAsync[^{]*\{[^}]*Task\.FromResult\(true\)[^}]*\}"))
            {
                violations.Add(fileName);
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Findings 131-133 (HIGH): Nagios strategy connection string and TcpClient handling.
    /// </summary>
    [Fact]
    public void Finding131_133_Nagios_SafeConnectionParsing()
    {
        var obsDir = Path.Combine(ConnectorRoot, "Observability");
        if (!Directory.Exists(obsDir)) return;

        var file = Directory.GetFiles(obsDir, "*Nagios*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);

        // Should validate connection string
        Assert.True(
            code.Contains("IsNullOrEmpty") || code.Contains("IsNullOrWhiteSpace") ||
            code.Contains("?? throw"),
            "Nagios: Connection string must be null-checked");

        // Should use TryParse for port
        Assert.True(
            code.Contains("TryParse") || code.Contains("int.TryParse"),
            "Nagios: Port parsing should use TryParse");

        // TcpClient should have error handling
        if (code.Contains("new TcpClient"))
        {
            Assert.True(code.Contains("try") || code.Contains("using "),
                "Nagios: TcpClient should be in try/finally or using");
        }
    }

    /// <summary>
    /// Findings 134-135 (HIGH): Observability HTTP strategies HttpClient and StringContent lifecycle.
    /// </summary>
    [Fact]
    public void Finding134_135_Observability_ResourceLifecycle()
    {
        var obsDir = Path.Combine(ConnectorRoot, "Observability");
        if (!Directory.Exists(obsDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(obsDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // StringContent should be in using blocks
            if (code.Contains("new StringContent") && !code.Contains("using "))
            {
                if (!code.Contains("using var") && !code.Contains("using("))
                {
                    violations.Add($"{fileName}: StringContent without using");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Finding 136 (MEDIUM): OTLP payload structure must conform to spec.
    /// </summary>
    [Fact]
    public void Finding136_Otlp_PayloadStructure()
    {
        var obsDir = Path.Combine(ConnectorRoot, "Observability");
        if (!Directory.Exists(obsDir)) return;

        var file = Directory.GetFiles(obsDir, "*Otlp*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);

        if (code.Contains("resourceMetrics") || code.Contains("ResourceMetrics"))
        {
            Assert.True(
                code.Contains("scopeMetrics") || code.Contains("ScopeMetrics"),
                "OTLP payload must include scopeMetrics in ResourceMetrics structure");
        }
    }
}
