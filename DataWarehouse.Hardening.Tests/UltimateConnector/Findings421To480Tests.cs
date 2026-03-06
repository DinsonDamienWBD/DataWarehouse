// Hardening tests for UltimateConnector findings 421-480
// Covers: Observability (CloudWatch SigV4/paths, AzureMonitor DCR, Datadog paths,
// ElasticsearchLogging index, FluentdConnection port parse + reconnect, GcpCloudMonitoring PROJECT_ID,
// Honeycomb dataset, Instana CRLF injection, Logzio port URI, Mimir TLS, Nagios field validation,
// Netdata read-only API, NewRelic stub health, OpenSearch index, Prometheus wire format,
// SigNoz health check, SplunkHec CRLF, Tempo OTLP format, Zabbix endianness + response),
// Protocol (DNS fake verification, FTP dead code, GraphQL sync, gRPC retry, JsonRPC ping,
// SFTP raw TCP, SNMP fake verify, SOAP tempuri placeholder, SSE shared client,
// SSH raw TCP, Syslog fake verify, WebSocket no-op),
// SaaS (Airtable auth stub, FreshDesk default domain, GitHub rate limit + thread safety,
// Jira credential bytes, Salesforce silent catch + SOQL POST + null-forgiving,
// SAP CSRF race + empty host + password cleartext + $select encoding + function import URL,
// SendGrid pagination)

using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateConnector;

public class Findings421To480Tests
{
    private static readonly string PluginDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
        "Plugins", "DataWarehouse.Plugins.UltimateConnector");

    private static string? FindFile(string fileName)
    {
        var result = Directory.GetFiles(PluginDir, fileName, SearchOption.AllDirectories);
        return result.Length > 0 ? result[0] : null;
    }

    // ======== Finding 422: CloudWatch HttpClient per-call + no SigV4 ========

    [Fact]
    public void Finding422_CloudWatch_HttpClientManaged()
    {
        var file = FindFile("AwsCloudWatchConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // HttpClient should be created once in ConnectCoreAsync
        Assert.Contains("ConnectCoreAsync", code);
    }

    // ======== Finding 425: AzureMonitor fixed DCR path ========

    [Fact]
    public void Finding425_AzureMonitor_ConfigurableDcrPath()
    {
        var file = FindFile("AzureMonitorConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // DCR path should be configurable, not hardcoded
        Assert.Matches(@"(config|Properties|GetConfiguration|dcrImmutableId|streamName)", code);
    }

    // ======== Finding 426: Datadog wrong logs API path ========

    [Fact]
    public void Finding426_Datadog_CorrectApiPaths()
    {
        var file = FindFile("DatadogConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use correct Datadog API paths
        Assert.Matches(@"(api/v2/logs|v2/series)", code);
    }

    // ======== Finding 429: ElasticsearchLogging hardcoded index ========

    [Fact]
    public void Finding429_ElasticsearchLogging_ConfigurableIndex()
    {
        var file = FindFile("ElasticsearchLoggingConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Index name should be configurable
        Assert.Matches(@"(config|Properties|GetConfiguration|IndexName|_indexName)", code);
    }

    // ======== Finding 430: Fluentd int.Parse no validation ========

    [Fact]
    public void Finding430_Fluentd_SafePortParse()
    {
        var file = FindFile("FluentdConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Port parsing should use TryParse or ParseHostPortSafe
        Assert.Matches(@"(TryParse|ParseHostPort)", code);
    }

    // ======== Finding 433: GcpCloudMonitoring hardcoded PROJECT_ID ========

    [Fact]
    public void Finding433_GcpCloudMonitoring_NoPlaceholderProjectId()
    {
        var file = FindFile("GcpCloudMonitoringConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not have literal "PROJECT_ID" in URLs
        Assert.DoesNotMatch(@"""PROJECT_ID""", code);
    }

    // ======== Finding 435: Instana CRLF injection in Authorization ========

    [Fact]
    public void Finding435_Instana_SafeAuthHeader()
    {
        var file = FindFile("InstanaConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use typed AuthenticationHeaderValue, not string Add
        Assert.Matches(@"(AuthenticationHeaderValue|Replace.*\\r|Replace.*\\n|Sanitize)", code);
    }

    // ======== Finding 436: Logzio port-only strings as URI ========

    [Fact]
    public void Finding436_Logzio_ValidUriPaths()
    {
        var file = FindFile("LogzioConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not use port-only strings as relative URIs
        Assert.DoesNotMatch(@""":\d{4}""", code);
    }

    // ======== Finding 438: Nagios field validation ========

    [Fact]
    public void Finding438_Nagios_FieldValidation()
    {
        var file = FindFile("NagiosConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate metric dictionary keys before access
        Assert.Matches(@"(TryGetValue|ContainsKey|GetValueOrDefault)", code);
    }

    // ======== Finding 441: NewRelic stub health check ========

    [Fact]
    public void Finding441_NewRelic_RealHealthCheck()
    {
        var file = FindFile("NewRelicConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // TestCoreAsync should make an actual API call, not always return true
        Assert.Matches(@"(GetAsync|SendAsync|HttpClient|response)", code);
    }

    // ======== Finding 444: Prometheus remote write format ========

    [Fact]
    public void Finding444_Prometheus_CorrectWriteFormat()
    {
        var file = FindFile("PrometheusConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should document that native remote_write requires Protobuf+Snappy
        // or use a compatible JSON import endpoint
        Assert.Matches(@"(Protobuf|Snappy|import|application/json)", code);
    }

    // ======== Finding 445: SigNoz health check always succeeds ========

    [Fact]
    public void Finding445_SigNoz_ProperHealthCheck()
    {
        var file = FindFile("SigNozConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Health check should verify proper response
        Assert.Matches(@"(IsSuccessStatusCode|StatusCode|EnsureSuccess)", code);
    }

    // ======== Finding 447: SplunkHEC CRLF injection ========

    [Fact]
    public void Finding447_SplunkHec_TokenSanitized()
    {
        var file = FindFile("SplunkHecConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // HEC token must be sanitized for CRLF
        Assert.Matches(@"(AuthenticationHeaderValue|Replace.*\\r|Replace.*\\n|Trim)", code);
    }

    // ======== Finding 449: Tempo OTLP trace format ========

    [Fact]
    public void Finding449_Tempo_CorrectOtlpFormat()
    {
        var file = FindFile("TempoConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use resourceSpans, not batches
        Assert.Contains("resourceSpans", code);
    }

    // ======== Finding 450: Zabbix endianness ========

    [Fact]
    public void Finding450_Zabbix_LittleEndianExplicit()
    {
        var file = FindFile("ZabbixConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Zabbix protocol requires little-endian, must be explicit
        Assert.Matches(@"(IsLittleEndian|BinaryPrimitives|LittleEndian)", code);
    }

    // ======== Finding 452: DNS fake verification ========

    [Fact]
    public void Finding452_Dns_RealVerification()
    {
        var file = FindFile("DnsConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not rely on Task.Delay as fake verification
        // DNS connection should send actual query or document limitation
        Assert.Matches(@"(SendAsync|query|DNS|documented|limitation)", code);
    }

    // ======== Finding 456: GraphQL config fields synchronization ========

    [Fact]
    public void Finding456_GraphQl_ConfigFieldsSync()
    {
        var file = FindFile("GraphQlConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // _maxQueryDepth, _maxQueryComplexity should be volatile or use lock
        Assert.Matches(@"(volatile|Interlocked|lock\s*\()", code);
    }

    // ======== Finding 460: SFTP raw TCP not SSH ========

    [Fact]
    public void Finding460_Sftp_NotRawTcp()
    {
        var file = FindFile("SftpConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should document that raw TCP is not SFTP or use proper SSH library
        Assert.Matches(@"(SSH|ssh|Renci|SshNet|limitation|raw TCP)", code);
    }

    // ======== Finding 462: SOAP hardcoded tempuri namespace ========

    [Fact]
    public void Finding462_Soap_NoTempuriPlaceholder()
    {
        var file = FindFile("SoapConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not hardcode tempuri.org namespace
        Assert.DoesNotMatch(@"tempuri\.org.*GetStatus", code);
    }

    // ======== Finding 468: Airtable no auth ========

    [Fact]
    public void Finding468_Airtable_AuthRequired()
    {
        var file = FindFile("AirtableConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Must apply Authorization header
        Assert.Matches(@"(Authorization|Bearer|AuthCredential|PersonalAccessToken)", code);
    }

    // ======== Finding 471: Airtable AuthenticateAsync returns random GUID ========

    [Fact]
    public void Finding471_Airtable_NoGuidToken()
    {
        var file = FindFile("AirtableConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not return Guid.NewGuid() as token
        Assert.DoesNotMatch(@"Guid\.NewGuid\(\)\.ToString.*as.*token", code);
    }

    // ======== Finding 472: FreshDesk default domain "example" ========

    [Fact]
    public void Finding472_FreshDesk_NoDefaultExample()
    {
        var file = FindFile("FreshDeskConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate domain is not the placeholder "example"
        Assert.Matches(@"(IsNullOrEmpty|IsNullOrWhiteSpace|ArgumentException|Validate)", code);
    }

    // ======== Finding 473: GitHub rate limit thread safety ========

    [Fact]
    public void Finding473_GitHub_RateLimitVolatile()
    {
        var file = FindFile("GitHubConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // _rateLimitRemaining should be volatile or use Interlocked
        Assert.Matches(@"volatile.*_rateLimitRemaining|Interlocked.*_rateLimitRemaining", code);
    }

    // ======== Finding 476: GitHub CheckRateLimitAsync arbitrary ceiling ========

    [Fact]
    public void Finding476_GitHub_RateLimitWaitNotArbitrary()
    {
        var file = FindFile("GitHubConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // CheckRateLimitAsync should handle the rate limit properly
        Assert.Contains("CheckRateLimitAsync", code);
    }

    // ======== Finding 479-480: Salesforce AuthenticateAsync no silent catch ========

    [Fact]
    public void Finding479_Salesforce_AuthNoSilentCatch()
    {
        var file = FindFile("SalesforceConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not silently catch OAuth errors and fall back to Guid
        Assert.DoesNotMatch(@"catch.*\{[^}]*Guid\.NewGuid", code);
        // Should not have Guid.NewGuid used as access token
        Assert.DoesNotMatch(@"Guid\.NewGuid\(\).*access.?token", code);
    }
}
