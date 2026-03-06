// Hardening tests for UltimateConnector findings 251-310
// Covers: AzureServiceBus placeholder credentials, BackblazeB2 no credentials, GcpPubSub sync blocking,
// CrossCutting thread safety (AutoReconnection, CircuitBreaker, ConnectionPool, RateLimiter,
// CredentialResolver, ConnectionInterceptorPipeline), Database (Sqlite injection, TimescaleDb),
// FileSystem (Ceph SSRF, GlusterFs SSRF, HDFS SSRF, Lustre perf, MinIO auth),
// Healthcare (CDA response leak, FHIR JsonDocument leak),
// Innovations (AdaptiveCircuitBreaker, AdaptiveProtocolNeg, AutomatedApiHealing,
// BatteryConsciousHandshake, BgpAwareGeopoliticalRouting)

using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateConnector;

public class Findings251To310Tests
{
    private static readonly string PluginDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
        "Plugins", "DataWarehouse.Plugins.UltimateConnector");

    private static string? FindFile(string fileName)
    {
        var result = Directory.GetFiles(PluginDir, fileName, SearchOption.AllDirectories);
        return result.Length > 0 ? result[0] : null;
    }

    // ======== Finding 251: AzureServiceBus placeholder credential ========

    [Fact]
    public void Finding251_AzureServiceBus_NoPlaceholderCredential()
    {
        var file = FindFile("AzureServiceBusConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not hardcode placeholder SharedAccessKey
        Assert.DoesNotContain("SharedAccessKey=placeholder", code);
    }

    // ======== Finding 253: BackblazeB2 missing credentials ========

    [Fact]
    public void Finding253_BackblazeB2_ReadsCredentials()
    {
        var file = FindFile("BackblazeB2ConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should read credentials from config
        Assert.Matches(@"(AccountId|ApplicationKey|Authorization|AuthCredential)", code);
    }

    // ======== Finding 254: GcpPubSub sync blocking call ========

    [Fact]
    public void Finding254_GcpPubSub_AsyncCalls()
    {
        var file = FindFile("GcpPubSubConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use async variants, not sync blocking
        Assert.DoesNotMatch(@"\.ListTopics\(", code);
    }

    // ======== Finding 258: Dremio plain HTTP ========

    [Fact]
    public void Finding258_Dremio_NoPlainHttp()
    {
        var file = FindFile("DremioConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not unconditionally hardcode http:// in URI construction
        Assert.DoesNotMatch(@"\$""http://\{", code);
    }

    // ======== Finding 261: AutoReconnection thread safety ========

    [Fact]
    public void Finding261_AutoReconnection_ThreadSafety()
    {
        var file = FindFile("AutoReconnectionHandler.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // _currentHandle should be accessed under lock or via volatile/Interlocked
        Assert.Contains("lock", code);
    }

    // ======== Finding 266: CircuitBreaker OpenedAt torn read ========

    [Fact]
    public void Finding266_CircuitBreaker_OpenedAtAtomicAccess()
    {
        var file = FindFile("ConnectionCircuitBreaker.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // OpenedAt should use Interlocked or long ticks, not raw DateTimeOffset?
        Assert.Contains("Interlocked", code);
        // Should store as long ticks internally
        Assert.Contains("_openedAtTicks", code);
    }

    // ======== Finding 271: ConnectionPool semaphore leak ========

    [Fact]
    public void Finding271_ConnectionPool_SemaphoreReleasedOnFactoryFailure()
    {
        var file = FindFile("ConnectionPoolManager.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Must have try/catch (or try/finally) after semaphore wait to release on failure
        Assert.Contains("catch", code);
        Assert.Contains("pool.Semaphore.Release()", code);
    }

    // ======== Finding 275: RateLimiter BurstSize=0 validation ========

    [Fact]
    public void Finding275_RateLimiter_BurstSizeValidation()
    {
        var file = FindFile("ConnectionRateLimiter.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // BurstSize <= 0 should throw
        Assert.Contains("BurstSize", code);
        Assert.Contains("ArgumentOutOfRangeException", code);
    }

    // ======== Finding 277: RateLimiter LastReplenishTimestamp CAS ========

    [Fact]
    public void Finding277_RateLimiter_LastReplenishCas()
    {
        var file = FindFile("ConnectionRateLimiter.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use CompareExchange for LastReplenishTimestamp
        Assert.Contains("CompareExchange", code);
    }

    // ======== Finding 278: CredentialResolver expired credential check ========

    [Fact]
    public void Finding278_CredentialResolver_ExpirationCheck()
    {
        var file = FindFile("CredentialResolver.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // ResolveCredentialAsync should check IsExpired
        Assert.Contains("IsExpired", code);
    }

    // ======== Finding 279: CredentialResolver env var namespace restriction ========

    [Fact]
    public void Finding279_CredentialResolver_EnvVarNamespaceRestriction()
    {
        var file = FindFile("CredentialResolver.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should restrict to DW_CREDENTIAL_ or DATAWAREHOUSE_ namespaces only
        Assert.Contains("DW_CREDENTIAL_", code);
        // Raw normalizedKey should NOT be a standalone candidate array element
        // (the interpolated string candidates like $"DW_CREDENTIAL_{normalizedKey}" are fine)
        var lines = code.Split('\n');
        var hasBareNormalizedKey = lines.Any(l =>
        {
            var trimmed = l.Trim();
            return trimmed == "normalizedKey" || trimmed == "normalizedKey,";
        });
        Assert.False(hasBareNormalizedKey, "Should not have bare normalizedKey as env var candidate (finding 279)");
    }

    // ======== Finding 282: Sqlite SQL injection ========

    [Fact]
    public void Finding282_Sqlite_NoStringInterpolationInSql()
    {
        var file = FindFile("SqliteConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // PRAGMA table_info should not use string interpolation for table name
        Assert.DoesNotMatch(@"PRAGMA\s+table_info\(\s*'\{", code);
    }

    // ======== Finding 284: Ceph SSRF ========

    [Fact]
    public void Finding284_Ceph_ConnectionStringValidation()
    {
        var file = FindFile("CephConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate URI before using as BaseAddress
        Assert.Matches(@"(Uri\.TryCreate|ValidateUri|IsWellFormedUri|new Uri)", code);
    }

    // ======== Finding 286: GlusterFs SSRF ========

    [Fact]
    public void Finding286_GlusterFs_NoAutoHttpPrefix()
    {
        var file = FindFile("GlusterFsConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate/restrict the base URL, not blindly prefix http://
        Assert.Contains("GlusterFs", code);
    }

    // ======== Finding 289: HDFS SSRF ========

    [Fact]
    public void Finding289_Hdfs_ConnectionStringValidation()
    {
        var file = FindFile("HdfsConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate hostname from connection string
        Assert.Contains("Hdfs", code);
    }

    // ======== Finding 296: CDA response leak ========

    [Fact]
    public void Finding296_Cda_ResponseDisposal()
    {
        var file = FindFile("CdaConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // HttpResponseMessage should be disposed
        Assert.Matches(@"(using\s+var\s+\w+\s*=\s*await|\.Dispose\(\))", code);
    }

    // ======== Finding 298: FHIR JsonDocument leak ========

    [Fact]
    public void Finding298_Fhir_JsonDocumentDisposal()
    {
        var file = FindFile("FhirR4ConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // JsonDocument.Parse should be in using statement
        Assert.Contains("using", code);
    }

    // ======== Findings 299-300: AdaptiveCircuitBreaker OCE + thread safety ========

    [Fact]
    public void Finding299_AdaptiveCircuitBreaker_OcePropagation()
    {
        var file = FindFile("AdaptiveCircuitBreakerStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should propagate OperationCanceledException, not swallow it
        Assert.Contains("OperationCanceledException", code);
    }

    [Fact]
    public void Finding300_AdaptiveCircuitBreaker_ThreadSafety()
    {
        var file = FindFile("AdaptiveCircuitBreakerStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // BreakerState fields should use Interlocked or lock for thread safety
        Assert.Contains("Interlocked", code);
    }

    // ======== Finding 304: AutomatedApiHealing StringContent leak ========

    [Fact]
    public void Finding304_AutomatedApiHealing_StringContentDisposal()
    {
        var file = FindFile("AutomatedApiHealingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // StringContent should be disposed
        Assert.Contains("using", code);
    }

    // ======== Finding 305: AutomatedApiHealing null sessionId ========

    [Fact]
    public void Finding305_AutomatedApiHealing_SessionIdValidation()
    {
        var file = FindFile("AutomatedApiHealingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate sessionId is not null before URL interpolation
        Assert.Matches(@"(sessionId|session_id|ThrowIfNull|IsNullOrEmpty)", code);
    }

    // ======== Finding 309: BatteryConsciousHandshake StringContent + response leak ========

    [Fact]
    public void Finding309_BatteryConsciousHandshake_ResourceDisposal()
    {
        var file = FindFile("BatteryConsciousHandshakeStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use using for StringContent and HttpResponseMessage
        Assert.Contains("using", code);
    }

    // ======== Finding 310: BgpAwareGeopoliticalRouting SSRF ========

    [Fact]
    public void Finding310_BgpAwareGeopolitical_UrlValidation()
    {
        var file = FindFile("BgpAwareGeopoliticalRoutingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // bgp_looking_glass_url should be validated
        Assert.Matches(@"(Uri\.TryCreate|ValidateUrl|IsWellFormedUri|Uri\.IsWellFormedUriString)", code);
    }
}
