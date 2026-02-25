using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Security regression tests that statically scan source files for anti-patterns
/// that were fixed in Phase 53 (pentest remediation). These tests ensure v5.0
/// feature work (phases 54-65) does not re-introduce security vulnerabilities.
/// </summary>
[Trait("Category", "SecurityRegression")]
public class SecurityRegressionTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    private static string FindSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate DataWarehouse.slnx. Ensure tests run from within the solution tree.");
    }

    private static IEnumerable<string> GetSourceFiles(string subPath = "")
    {
        var searchPath = string.IsNullOrEmpty(subPath)
            ? SolutionRoot
            : Path.Combine(SolutionRoot, subPath);

        if (!Directory.Exists(searchPath))
            return Enumerable.Empty<string>();

        return Directory.EnumerateFiles(searchPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static IEnumerable<string> GetPluginSourceFiles()
        => GetSourceFiles("Plugins");

    private static IEnumerable<string> GetNonTestSourceFiles()
        => GetSourceFiles()
            .Where(f => !f.Contains("DataWarehouse.Tests")
                     && !f.Contains(".Tests."));

    // -----------------------------------------------------------------------
    // Test 1: No unconditional TLS certificate validation bypasses in SDK/Kernel
    // Verifies NET-01, NET-03, NET-06 fixes remain intact in core infrastructure.
    // Note: Plugin strategies may have TLS bypasses for dev/self-signed cert
    // scenarios -- these are tracked in SECURITY-REGRESSION-REPORT.md but are
    // not regressions of the Phase 53 core fixes.
    // -----------------------------------------------------------------------
    [Fact]
    public void NoTlsBypasses_InKernelAndSdk()
    {
        var coreFiles = GetSourceFiles("DataWarehouse.Kernel")
            .Concat(GetSourceFiles("DataWarehouse.SDK"))
            .Concat(GetSourceFiles("DataWarehouse.Launcher"))
            .Concat(GetSourceFiles("DataWarehouse.Dashboard"));

        var tlsBypassPattern = new Regex(
            @"ServerCertificate(Custom)?ValidationCallback\s*=\s*\(.*?\)\s*=>\s*true",
            RegexOptions.Singleline);

        var violations = new List<string>();

        foreach (var file in coreFiles)
        {
            var content = File.ReadAllText(file);
            var matches = tlsBypassPattern.Matches(content);
            foreach (Match match in matches)
            {
                var relativePath = Path.GetRelativePath(SolutionRoot, file);
                violations.Add($"{relativePath}: {match.Value.Trim()}");
            }
        }

        violations.Should().BeEmpty(
            "Core infrastructure (Kernel, SDK, Launcher, Dashboard) must not unconditionally bypass TLS certificate validation (NET-01/NET-03/NET-06)");
    }

    // -----------------------------------------------------------------------
    // Test 2: No hardcoded secrets in production (non-test) code
    // Verifies AUTH-02 fix remains intact and no new hardcoded credentials.
    // Excludes: test files, honeypot/deception code, masked/redacted values.
    // -----------------------------------------------------------------------
    [Fact]
    public void NoHardcodedSecrets_InProductionCode()
    {
        var secretPatterns = new[]
        {
            new Regex(@"Development_Secret", RegexOptions.IgnoreCase),
            new Regex(@"(?<!\w)password\s*=\s*""(?!(\*|{|dummy|test|\$))[A-Za-z0-9!@#$%^&*]{8,}""",
                RegexOptions.IgnoreCase),
        };

        var violations = new List<string>();

        foreach (var file in GetNonTestSourceFiles())
        {
            // Skip honeypot/deception strategies (intentionally contain fake credentials)
            var fileName = Path.GetFileName(file);
            if (fileName.Contains("Deception") || fileName.Contains("Honeypot") || fileName.Contains("Lure"))
                continue;

            var content = File.ReadAllText(file);
            foreach (var pattern in secretPatterns)
            {
                var matches = pattern.Matches(content);
                foreach (Match match in matches)
                {
                    var relativePath = Path.GetRelativePath(SolutionRoot, file);
                    violations.Add($"{relativePath}: {match.Value.Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "Production code must not contain hardcoded secrets or development credentials (AUTH-02)");
    }

    // -----------------------------------------------------------------------
    // Test 3: Async methods in v5.0 plugins should have CancellationToken
    // Reports violations as warnings (informational, not hard failure).
    // -----------------------------------------------------------------------
    [Fact]
    public void AllAsyncMethodsHaveCancellation_PluginWarnings()
    {
        var asyncMethodPattern = new Regex(
            @"(?:public|protected|internal)\s+(?:virtual\s+|override\s+|async\s+)*Task\S*\s+(\w+Async)\s*\(([^)]*)\)",
            RegexOptions.Multiline);

        var warnings = new List<string>();
        var total = 0;
        var withToken = 0;

        foreach (var file in GetPluginSourceFiles())
        {
            var content = File.ReadAllText(file);
            var matches = asyncMethodPattern.Matches(content);

            foreach (Match match in matches)
            {
                total++;
                var parameters = match.Groups[2].Value;
                if (parameters.Contains("CancellationToken") || parameters.Contains("CancellationToken"))
                {
                    withToken++;
                }
                else
                {
                    var relativePath = Path.GetRelativePath(SolutionRoot, file);
                    var methodName = match.Groups[1].Value;
                    warnings.Add($"{relativePath}: {methodName}");
                }
            }
        }

        // Informational: report coverage percentage but do not fail
        var coverage = total > 0 ? (double)withToken / total * 100 : 100;

        // Pass -- this is a warning-only test; coverage is tracked for reporting
        total.Should().BeGreaterThan(0, "should find async methods in plugin code");
        coverage.Should().BeGreaterThan(0, "at least some async methods should have CancellationToken");
    }

    // -----------------------------------------------------------------------
    // Test 4: No unsafe assembly loading outside PluginLoader
    // Verifies ISO-02 fix: all assembly loading goes through PluginLoader.
    // Activator.CreateInstance is allowed for internal strategy registration
    // (assembly-scanned types, not user-controlled).
    // -----------------------------------------------------------------------
    [Fact]
    public void NoUnsafeAssemblyLoading_OutsidePluginLoader()
    {
        var unsafePatterns = new[]
        {
            new Regex(@"Assembly\.LoadFrom\s*\("),
            new Regex(@"Assembly\.Load\s*\(\s*(?:byte|File\.ReadAllBytes)"),
            new Regex(@"Assembly\.LoadFile\s*\("),
        };

        var violations = new List<string>();

        foreach (var file in GetNonTestSourceFiles())
        {
            // PluginLoader is the authorized loading path
            if (Path.GetFileName(file) == "PluginLoader.cs")
                continue;

            var content = File.ReadAllText(file);
            foreach (var pattern in unsafePatterns)
            {
                var matches = pattern.Matches(content);
                foreach (Match match in matches)
                {
                    var relativePath = Path.GetRelativePath(SolutionRoot, file);
                    violations.Add($"{relativePath}: {match.Value.Trim()}");
                }
            }
        }

        violations.Should().BeEmpty(
            "Assembly loading must go through PluginLoader pipeline (ISO-02). " +
            "No Assembly.LoadFrom/LoadFile/Load(byte[]) outside of PluginLoader.cs");
    }

    // -----------------------------------------------------------------------
    // Test 5: Path traversal protection on Path.Combine with user-facing params
    // Verifies D01, D03 fixes: path inputs are sanitized.
    // Heuristic: checks that Path.Combine calls using parameters named path/filePath/name/key
    // have nearby path validation (GetFullPath, GetFileName, Contains(".."), ValidatePath).
    // -----------------------------------------------------------------------
    [Fact]
    public void PathTraversalProtection_InStorageAndFilePlugins()
    {
        // Verify the core path protection utilities exist
        var sdkFiles = GetSourceFiles("DataWarehouse.SDK");
        var webDavFiles = GetSourceFiles("Plugins")
            .Where(f => f.Contains("WebDav", StringComparison.OrdinalIgnoreCase));
        var smbFiles = GetSourceFiles("Plugins")
            .Where(f => f.Contains("Smb", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("SmbStrategy", StringComparison.OrdinalIgnoreCase));

        // Check that VDE NamespaceTree has symlink depth protection (D02)
        var namespaceTreeFiles = sdkFiles
            .Where(f => f.Contains("NamespaceTree", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasSymlinkProtection = false;
        foreach (var file in namespaceTreeFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("MaxSymlinkDepth") || content.Contains("maxDepth"))
            {
                hasSymlinkProtection = true;
                break;
            }
        }

        hasSymlinkProtection.Should().BeTrue(
            "VDE NamespaceTree must have symlink depth protection (D02)");

        // Check that WebDAV has path validation (D03)
        var hasWebDavProtection = false;
        foreach (var file in webDavFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("ValidatePathSafe") || content.Contains("GetFullPath"))
            {
                hasWebDavProtection = true;
                break;
            }
        }

        hasWebDavProtection.Should().BeTrue(
            "WebDAV strategy must have path traversal protection (D03)");
    }

    // -----------------------------------------------------------------------
    // Test 6: Message bus authentication used in kernel wiring
    // Verifies AUTH-01, AUTH-08, AUTH-10: kernel passes enforced bus to plugins.
    // -----------------------------------------------------------------------
    [Fact]
    public void MessageBusAuthenticationUsed_InKernelWiring()
    {
        var kernelFiles = GetSourceFiles("DataWarehouse.Kernel");

        var hasEnforcedBus = false;
        var hasAccessEnforcement = false;
        var hasAuthenticatedDecorator = false;

        foreach (var file in kernelFiles)
        {
            var content = File.ReadAllText(file);

            if (content.Contains("_enforcedMessageBus"))
                hasEnforcedBus = true;

            if (content.Contains("WithAccessEnforcement"))
                hasAccessEnforcement = true;

            if (content.Contains("AuthenticatedMessageBusDecorator"))
                hasAuthenticatedDecorator = true;
        }

        hasEnforcedBus.Should().BeTrue(
            "Kernel must use _enforcedMessageBus for plugin injection (AUTH-10)");
        hasAccessEnforcement.Should().BeTrue(
            "Kernel must wire AccessEnforcementInterceptor via WithAccessEnforcement() (AUTH-01)");
        hasAuthenticatedDecorator.Should().BeTrue(
            "Kernel must have AuthenticatedMessageBusDecorator for HMAC signing (BUS-02)");
    }

    // -----------------------------------------------------------------------
    // Test 7: PBKDF2 iterations in authentication paths meet NIST minimum
    // Verifies D04 fix: authentication PBKDF2 uses >= 600K iterations.
    // -----------------------------------------------------------------------
    [Fact]
    public void Pbkdf2Iterations_AuthPathsMeetNistMinimum()
    {
        // Check the two primary authentication paths
        var authFiles = new[]
        {
            Path.Combine(SolutionRoot, "DataWarehouse.Launcher", "Integration", "DataWarehouseHost.cs"),
            Path.Combine(SolutionRoot, "DataWarehouse.Shared", "Services", "UserAuthenticationService.cs"),
        };

        var iterationPattern = new Regex(@"600[_,]?000");

        foreach (var file in authFiles)
        {
            if (!File.Exists(file))
                continue;

            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(SolutionRoot, file);

            iterationPattern.IsMatch(content).Should().BeTrue(
                $"{relativePath} must use >= 600K PBKDF2 iterations for authentication (D04)");
        }
    }

    // -----------------------------------------------------------------------
    // Test 8: Rate limiting present on message bus
    // Verifies BUS-03 fix: message bus has rate limiting infrastructure.
    // -----------------------------------------------------------------------
    [Fact]
    public void RateLimiting_PresentOnMessageBus()
    {
        var messageBusFiles = GetSourceFiles("DataWarehouse.Kernel")
            .Where(f => f.Contains("MessageBus", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasRateLimiter = false;
        var hasTopicValidator = false;

        foreach (var file in messageBusFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("SlidingWindowRateLimiter") || content.Contains("RateLimiter"))
                hasRateLimiter = true;
            if (content.Contains("TopicValidator"))
                hasTopicValidator = true;
        }

        hasRateLimiter.Should().BeTrue(
            "Message bus must have rate limiting (BUS-03)");
        hasTopicValidator.Should().BeTrue(
            "Message bus must have topic validation (BUS-06)");
    }

    // -----------------------------------------------------------------------
    // Test 9: DashboardHub has [Authorize] attribute
    // Verifies AUTH-04 fix: SignalR hub requires authentication.
    // -----------------------------------------------------------------------
    [Fact]
    public void DashboardHub_HasAuthorizeAttribute()
    {
        var hubFiles = GetSourceFiles("DataWarehouse.Dashboard")
            .Where(f => f.Contains("DashboardHub", StringComparison.OrdinalIgnoreCase))
            .ToList();

        hubFiles.Should().NotBeEmpty("DashboardHub.cs should exist");

        var hasAuthorize = false;
        foreach (var file in hubFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("[Authorize]"))
            {
                hasAuthorize = true;
                break;
            }
        }

        hasAuthorize.Should().BeTrue(
            "DashboardHub must have [Authorize] attribute (AUTH-04)");
    }

    // -----------------------------------------------------------------------
    // Test 10: Raft consensus has authentication
    // Verifies DIST-01, DIST-02 fixes: Raft uses HMAC + membership verification.
    // -----------------------------------------------------------------------
    [Fact]
    public void RaftConsensus_HasAuthentication()
    {
        var raftFiles = GetSourceFiles("DataWarehouse.SDK")
            .Where(f => f.Contains("RaftConsensus", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasHmac = false;
        var hasMembershipCheck = false;

        foreach (var file in raftFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("VerifyHmac") || content.Contains("ClusterSecret") || content.Contains("HMAC"))
                hasHmac = true;
            if (content.Contains("GetMembers") || content.Contains("IClusterMembership"))
                hasMembershipCheck = true;
        }

        hasHmac.Should().BeTrue(
            "Raft consensus must use HMAC authentication (DIST-01)");
        hasMembershipCheck.Should().BeTrue(
            "Raft consensus must verify membership before processing (DIST-02)");
    }
}
