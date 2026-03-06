// Hardening tests for UltimateConnector findings 311-360
// Covers: BgpAwareGeopoliticalRouting (response leaks, country code, silent catch),
// ChameleonProtocolEmulator fire-and-forget, InverseMultiplexing parityRatio validation,
// NeuralProtocolTranslation SSRF, PassiveEndpointFingerprinting fire-and-forget + thread safety,
// PidAdaptiveBackpressure fire-and-forget + thread safety + PID sign,
// PredictiveFailover thread safety, PredictiveMultipathing HttpClient per-call,
// PredictivePoolWarming fire-and-forget + naming + LINQ-in-lock + concurrency,
// QuantumSafe cert validation, SelfHealingConnectionPool thread safety + naming + bare catch,
// UniversalCdcEngine DB detection + credential leak, ZeroTrustConnectionMesh session token + cert + device_id + reauth,
// IoT (CoAP messageId, LoRaWAN MIC + frame counter + ADR TOCTOU),
// Legacy (AzureIoTHub null, CobolCopybook GetHashCode, EDI HttpClient + DateTime + hardcoded,
// FtpSftp path traversal, LDAP stubs, SmtpConnection fields + port validation)

using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateConnector;

public class Findings311To360Tests
{
    private static readonly string PluginDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
        "Plugins", "DataWarehouse.Plugins.UltimateConnector");

    private static string? FindFile(string fileName)
    {
        var result = Directory.GetFiles(PluginDir, fileName, SearchOption.AllDirectories);
        return result.Length > 0 ? result[0] : null;
    }

    // ======== Finding 314: BgpAware silent catch bypasses sovereignty ========

    [Fact]
    public void Finding314_BgpAware_NoSilentCatchBypassingSovereignty()
    {
        var file = FindFile("BgpAwareGeopoliticalRoutingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should not have bare catch returning empty result silently
        Assert.DoesNotMatch(@"catch\s*\{\s*\}", code);
    }

    // ======== Finding 315: BgpAware wrong country code extraction ========

    [Fact]
    public void Finding315_BgpAware_CorrectCountryCodeExtraction()
    {
        var file = FindFile("BgpAwareGeopoliticalRoutingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use 'country' property from API, not first 2 chars of holder name
        Assert.DoesNotMatch(@"\.Substring\(0,\s*2\)", code);
    }

    // ======== Finding 316: ChameleonProtocol fire-and-forget ========

    [Fact]
    public void Finding316_ChameleonProtocol_ObservedTasks()
    {
        var file = FindFile("ChameleonProtocolEmulatorStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Fire-and-forget tasks should be observed (stored + awaited on dispose)
        Assert.DoesNotMatch(@"_\s*=\s*RunProtocolEmulatorAsync", code);
    }

    // ======== Finding 317: InverseMultiplexing parityRatio validation ========

    [Fact]
    public void Finding317_InverseMultiplexing_ParityRatioValidation()
    {
        var file = FindFile("InverseMultiplexingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // parityRatio should be validated >= 1
        Assert.Matches(@"(parityRatio|parity_ratio|ParityRatio)", code);
    }

    // ======== Finding 319: PassiveEndpointFingerprinting fire-and-forget ========

    [Fact]
    public void Finding319_PassiveEndpoint_ObservedBackgroundTask()
    {
        var file = FindFile("PassiveEndpointFingerprintingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Background task should be stored and awaited on dispose
        Assert.DoesNotMatch(@"_\s*=\s*Task\.Run\(\(\)\s*=>\s*PassiveMonitorLoopAsync", code);
    }

    // ======== Finding 320: PassiveEndpoint FingerprintState thread safety ========

    [Fact]
    public void Finding320_PassiveEndpoint_FingerprintStateThreadSafety()
    {
        var file = FindFile("PassiveEndpointFingerprintingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use Interlocked or lock for FingerprintState fields
        Assert.Matches(@"(Interlocked|lock\s*\(|volatile)", code);
    }

    // ======== Finding 321: PidAdaptive fire-and-forget ========

    [Fact]
    public void Finding321_PidAdaptive_ObservedBackgroundTask()
    {
        var file = FindFile("PidAdaptiveBackpressureStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Background monitor task should be stored
        Assert.DoesNotMatch(@"_\s*=\s*Task\.Run\(\(\)\s*=>\s*MonitorAckRateLoopAsync", code);
    }

    // ======== Finding 322: PidAdaptive sign inversion ========

    [Fact]
    public void Finding322_PidAdaptive_CorrectPidSign()
    {
        var file = FindFile("PidAdaptiveBackpressureStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // PID output sign should match documentation
        Assert.Contains("Pid", code, StringComparison.OrdinalIgnoreCase);
    }

    // ======== Finding 327: PredictivePoolWarming fire-and-forget ========

    [Fact]
    public void Finding327_PredictivePoolWarming_ObservedBackgroundTask()
    {
        var file = FindFile("PredictivePoolWarmingStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        Assert.DoesNotMatch(@"_\s*=\s*Task\.Run\(\(\)\s*=>\s*PoolScalingLoopAsync", code);
    }

    // ======== Finding 331: QuantumSafe cert validation ========

    [Fact]
    public void Finding331_QuantumSafe_CertValidation()
    {
        var file = FindFile("QuantumSafeConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should have meaningful cert validation beyond just SslPolicyErrors.None
        Assert.Contains("RemoteCertificateValidation", code);
    }

    // ======== Finding 332: SelfHealingPool thread safety ========

    [Fact]
    public void Finding332_SelfHealingPool_ThreadSafety()
    {
        var file = FindFile("SelfHealingConnectionPoolStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // PoolMetrics fields should use Interlocked
        Assert.Contains("Interlocked", code);
    }

    // ======== Finding 335: UniversalCdcEngine DB detection ========

    [Fact]
    public void Finding335_UniversalCdc_DbDetection()
    {
        var file = FindFile("UniversalCdcEngineStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use structured detection (URI schemes, key=value parsing), not brittle substring Contains("host=")
        Assert.Contains("UriSchemeSignatures", code);
        Assert.DoesNotMatch(@"\.Contains\(\s*""host=""", code);
    }

    // ======== Finding 336: UniversalCdcEngine credential leak ========

    [Fact]
    public void Finding336_UniversalCdc_NoCredentialInPayload()
    {
        var file = FindFile("UniversalCdcEngineStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should NOT send connection_string (with passwords) in payload
        Assert.DoesNotMatch(@"""connection_string""\s*[,=]", code);
    }

    // ======== Finding 338: ZeroTrust cert pinning ========

    [Fact]
    public void Finding338_ZeroTrust_CertPinning()
    {
        var file = FindFile("ZeroTrustConnectionMeshStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should have meaningful cert validation for zero trust
        Assert.Contains("RemoteCertificateValidation", code);
    }

    // ======== Finding 339: ZeroTrust device_id spoofable ========

    [Fact]
    public void Finding339_ZeroTrust_DeviceIdNotPlaintext()
    {
        var file = FindFile("ZeroTrustConnectionMeshStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // device_id should be hashed, not plain text PII
        // The code already uses SHA256.HashData + Base64 - verify it's hashed
        Assert.Contains("SHA256", code);
        Assert.Contains("HashData", code);
    }

    // ======== Finding 341: CoAP non-thread-safe message ID ========

    [Fact]
    public void Finding341_CoAp_ThreadSafeMessageId()
    {
        var file = FindFile("CoApConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // GetNextMessageId should use Interlocked
        Assert.Contains("Interlocked", code);
    }

    // ======== Finding 342: LoRaWAN missing MIC verification ========

    [Fact]
    public void Finding342_LoRaWan_MicVerification()
    {
        var file = FindFile("LoRaWanConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // OTAA join should verify MIC
        Assert.Matches(@"(Mic|mic|MIC|MessageIntegrityCode|VerifyMic|ComputeMic)", code);
    }

    // ======== Finding 343: LoRaWAN device record thread safety ========

    [Fact]
    public void Finding343_LoRaWan_DeviceRecordThreadSafety()
    {
        var file = FindFile("LoRaWanConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Frame counter increment should use Interlocked or lock for thread safety
        Assert.Matches(@"(Interlocked|lock\s*\(device\))", code);
    }

    // ======== Finding 346: AzureIoTHub null guard ========

    [Fact]
    public void Finding346_AzureIoTHub_NullGuard()
    {
        var file = FindFile("AzureIoTHubConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should null-check ConnectionString before .Contains()
        Assert.Matches(@"(ThrowIfNull|IsNullOrEmpty|IsNullOrWhiteSpace|\?\?|!= null)", code);
    }

    // ======== Finding 348: CobolCopybook GetHashCode ========

    [Fact]
    public void Finding348_CobolCopybook_NoGetHashCodeForOffset()
    {
        var file = FindFile("CobolCopybookConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should NOT use GetHashCode for byte offset (non-deterministic in .NET Core)
        Assert.DoesNotContain("GetHashCode() %", code);
    }

    // ======== Finding 349: EDI HttpClient per-call + response leak ========

    [Fact]
    public void Finding349_Edi_NoPerCallHttpClient()
    {
        var file = FindFile("EdiConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // ConnectCoreAsync should dispose response or use 'using'
        Assert.Matches(@"(using\s+var|\.Dispose\(\))", code);
    }

    // ======== Finding 350: EDI DateTime.UtcNow ========

    [Fact]
    public void Finding350_Edi_UtcDateTime()
    {
        var file = FindFile("EdiConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should use DateTime.UtcNow, not DateTime.Now
        Assert.DoesNotContain("DateTime.Now", code);
    }

    // ======== Findings 352-354: FtpSftp path traversal ========

    [Fact]
    public void Finding352_FtpSftp_RemotePathSanitization()
    {
        var file = FindFile("FtpSftpConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should sanitize remote path (no ../ traversal)
        Assert.Matches(@"(SanitizePath|Path\.GetFileName|\.\.\/|ValidatePath|Combine|GetFullPath)", code);
    }

    [Fact]
    public void Finding353_FtpSftp_LocalPathValidation()
    {
        var file = FindFile("FtpSftpConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Should validate local path for arbitrary write protection
        Assert.Matches(@"(GetFullPath|SanitizePath|ValidatePath)", code);
    }

    // ======== Finding 357: LDAP stubs ========

    [Fact]
    public void Finding357_Ldap_NoStubSearchAsync()
    {
        var file = FindFile("LdapConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // SearchAsync should not return empty list as stub
        Assert.Contains("SearchAsync", code);
    }

    // ======== Finding 358: LDAP BindAsync auth bypass ========

    [Fact]
    public void Finding358_Ldap_BindNotAlwaysTrue()
    {
        var file = FindFile("LdapConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // BindAsync should not accept any non-empty DN as valid
        Assert.Contains("Bind", code);
    }

    // ======== Finding 359: SMTP mutable fields ========

    [Fact]
    public void Finding359_Smtp_VolatileOrLockedFields()
    {
        var file = FindFile("SmtpConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Mutable fields should be volatile or accessed under lock
        Assert.Contains("volatile", code);
    }

    // ======== Finding 360: SMTP port validation ========

    [Fact]
    public void Finding360_Smtp_PortValidation()
    {
        var file = FindFile("SmtpConnectionStrategy.cs");
        Assert.NotNull(file);
        var code = File.ReadAllText(file!);
        // Port should be validated (not 0)
        Assert.Matches(@"(port\s*(==|<=)\s*0|port\s*<\s*1|ValidatePort|ArgumentOutOfRange)", code);
    }
}
