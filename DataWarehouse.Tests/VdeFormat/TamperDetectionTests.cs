using DataWarehouse.SDK.VirtualDiskEngine.Identity;
using DataWarehouse.SDK.VirtualDiskEngine.Regions;
using Xunit;

namespace DataWarehouse.Tests.VdeFormat;

/// <summary>
/// Tests for tamper detection: TamperResponseExecutor (5 levels), TamperDetectionResult
/// aggregation, TamperResponsePolicy serialization, and TamperCheckResult structure.
/// </summary>
public class TamperDetectionTests
{
    // ── 1. TamperResponseExecutor Tests ────────────────────────────────────

    [Theory]
    [InlineData(TamperResponse.Log)]
    [InlineData(TamperResponse.Alert)]
    [InlineData(TamperResponse.ReadOnly)]
    [InlineData(TamperResponse.Quarantine)]
    [InlineData(TamperResponse.Reject)]
    public void Clean_Result_AllowsOpen_RegardlessOfLevel(TamperResponse level)
    {
        var checks = new[]
        {
            new TamperCheckResult(TamperDetectionResult.CheckNamespaceSignature, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckFormatFingerprint, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckHeaderIntegritySeal, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckMetadataChainHash, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckFileSizeSentinel, true, null),
        };
        var result = new TamperDetectionResult(checks);
        Assert.True(result.IsClean);

        var action = TamperResponseExecutor.Execute(result, level);
        Assert.True(action.AllowOpen);
        Assert.False(action.ReadOnlyMode);
        Assert.False(action.Quarantined);
    }

    [Fact]
    public void Tampered_Log_AllowsOpen_WithLoggedMessage()
    {
        var result = CreateTamperedResult("HeaderIntegritySeal");
        var action = TamperResponseExecutor.Execute(result, TamperResponse.Log);

        Assert.True(action.AllowOpen);
        Assert.False(action.ReadOnlyMode);
        Assert.False(action.Quarantined);
        Assert.Contains("logged", action.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tampered_Alert_AllowsOpen_WithAlertMessage()
    {
        var result = CreateTamperedResult("FormatFingerprint");
        var action = TamperResponseExecutor.Execute(result, TamperResponse.Alert);

        Assert.True(action.AllowOpen);
        Assert.False(action.ReadOnlyMode);
        Assert.False(action.Quarantined);
        Assert.Contains("ALERT", action.Message);
    }

    [Fact]
    public void Tampered_ReadOnly_AllowsOpen_InReadOnlyMode()
    {
        var result = CreateTamperedResult("MetadataChainHash");
        var action = TamperResponseExecutor.Execute(result, TamperResponse.ReadOnly);

        Assert.True(action.AllowOpen);
        Assert.True(action.ReadOnlyMode);
        Assert.False(action.Quarantined);
    }

    [Fact]
    public void Tampered_Quarantine_DeniesOpen_SetsQuarantined()
    {
        var result = CreateTamperedResult("FileSizeSentinel");
        var action = TamperResponseExecutor.Execute(result, TamperResponse.Quarantine);

        Assert.False(action.AllowOpen);
        Assert.False(action.ReadOnlyMode);
        Assert.True(action.Quarantined);
    }

    [Fact]
    public void Tampered_Reject_ThrowsVdeTamperDetectedException()
    {
        var result = CreateTamperedResult("NamespaceSignature");
        Assert.Throws<VdeTamperDetectedException>(() =>
            TamperResponseExecutor.Execute(result, TamperResponse.Reject));
    }

    [Fact]
    public void Tampered_InvalidLevel_ThrowsArgumentOutOfRange()
    {
        var result = CreateTamperedResult(TamperDetectionResult.CheckFormatFingerprint);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TamperResponseExecutor.Execute(result, (TamperResponse)99));
    }

    [Fact]
    public void NullResult_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TamperResponseExecutor.Execute(null!, TamperResponse.Log));
    }

    [Fact]
    public void Clean_Result_AppliedLevel_MatchesConfigured()
    {
        var result = CreateCleanResult();
        var action = TamperResponseExecutor.Execute(result, TamperResponse.ReadOnly);
        Assert.Equal(TamperResponse.ReadOnly, action.AppliedLevel);
    }

    [Fact]
    public void Tampered_Log_AppliedLevel_IsLog()
    {
        var result = CreateTamperedResult("Test");
        var action = TamperResponseExecutor.Execute(result, TamperResponse.Log);
        Assert.Equal(TamperResponse.Log, action.AppliedLevel);
    }

    // ── 2. TamperDetectionResult Tests ─────────────────────────────────────

    [Fact]
    public void AllChecksPass_IsClean_True()
    {
        var result = CreateCleanResult();
        Assert.True(result.IsClean);
        Assert.Empty(result.FailedChecks);
        Assert.Contains("All 5 integrity checks passed", result.Summary);
    }

    [Fact]
    public void OneCheckFails_IsClean_False_FailedCount1()
    {
        var checks = new[]
        {
            new TamperCheckResult(TamperDetectionResult.CheckNamespaceSignature, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckFormatFingerprint, false, "Fingerprint mismatch"),
            new TamperCheckResult(TamperDetectionResult.CheckHeaderIntegritySeal, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckMetadataChainHash, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckFileSizeSentinel, true, null),
        };
        var result = new TamperDetectionResult(checks);
        Assert.False(result.IsClean);
        Assert.Single(result.FailedChecks);
        Assert.Equal("FormatFingerprint", result.FailedChecks[0].CheckName);
    }

    [Fact]
    public void AllChecksFail_FailedCount5()
    {
        var checks = new[]
        {
            new TamperCheckResult(TamperDetectionResult.CheckNamespaceSignature, false, "Bad"),
            new TamperCheckResult(TamperDetectionResult.CheckFormatFingerprint, false, "Bad"),
            new TamperCheckResult(TamperDetectionResult.CheckHeaderIntegritySeal, false, "Bad"),
            new TamperCheckResult(TamperDetectionResult.CheckMetadataChainHash, false, "Bad"),
            new TamperCheckResult(TamperDetectionResult.CheckFileSizeSentinel, false, "Bad"),
        };
        var result = new TamperDetectionResult(checks);
        Assert.False(result.IsClean);
        Assert.Equal(5, result.FailedChecks.Count);
    }

    [Fact]
    public void Summary_IncludesNameOfFailedChecks()
    {
        var checks = new[]
        {
            new TamperCheckResult(TamperDetectionResult.CheckNamespaceSignature, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckHeaderIntegritySeal, false, "Seal broken"),
            new TamperCheckResult(TamperDetectionResult.CheckFileSizeSentinel, false, "Size wrong"),
        };
        var result = new TamperDetectionResult(checks);
        Assert.Contains("HeaderIntegritySeal", result.Summary);
        Assert.Contains("FileSizeSentinel", result.Summary);
    }

    [Fact]
    public void CheckNames_MatchConstants()
    {
        Assert.Equal("NamespaceSignature", TamperDetectionResult.CheckNamespaceSignature);
        Assert.Equal("FormatFingerprint", TamperDetectionResult.CheckFormatFingerprint);
        Assert.Equal("HeaderIntegritySeal", TamperDetectionResult.CheckHeaderIntegritySeal);
        Assert.Equal("MetadataChainHash", TamperDetectionResult.CheckMetadataChainHash);
        Assert.Equal("FileSizeSentinel", TamperDetectionResult.CheckFileSizeSentinel);
    }

    [Fact]
    public void NullChecksArray_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TamperDetectionResult(null!));
    }

    [Fact]
    public void EmptyChecksArray_IsClean_True()
    {
        var result = new TamperDetectionResult(Array.Empty<TamperCheckResult>());
        Assert.True(result.IsClean);
    }

    // ── 3. TamperResponsePolicy Tests ──────────────────────────────────────

    [Theory]
    [InlineData(TamperResponse.Log, 0)]
    [InlineData(TamperResponse.Alert, 1)]
    [InlineData(TamperResponse.ReadOnly, 2)]
    [InlineData(TamperResponse.Quarantine, 3)]
    [InlineData(TamperResponse.Reject, 4)]
    public void SerializeDeserialize_RoundTrip(TamperResponse level, byte expectedByte)
    {
        var policy = new TamperResponsePolicy { Level = level };
        var data = policy.Serialize();
        Assert.Single(data);
        Assert.Equal(expectedByte, data[0]);

        var deserialized = TamperResponsePolicy.Deserialize(data);
        Assert.Equal(level, deserialized.Level);
    }

    [Fact]
    public void ToPolicyDefinition_ProducesPolicyType0x0074()
    {
        var policy = new TamperResponsePolicy { Level = TamperResponse.ReadOnly };
        var pd = policy.ToPolicyDefinition();
        Assert.Equal(0x0074, pd.PolicyType);
        Assert.NotEqual(Guid.Empty, pd.PolicyId);
    }

    [Fact]
    public void FromPolicyDefinition_RoundTrips()
    {
        var original = new TamperResponsePolicy { Level = TamperResponse.Quarantine };
        var pd = original.ToPolicyDefinition();
        var restored = TamperResponsePolicy.FromPolicyDefinition(pd);
        Assert.Equal(TamperResponse.Quarantine, restored.Level);
    }

    [Fact]
    public void FromPolicyDefinition_WrongType_Throws()
    {
        var pd = new PolicyDefinition(
            Guid.NewGuid(), 0x9999, 1,
            DateTimeOffset.UtcNow.Ticks, DateTimeOffset.UtcNow.Ticks,
            new byte[] { 0x00 });
        Assert.Throws<ArgumentException>(() => TamperResponsePolicy.FromPolicyDefinition(pd));
    }

    [Fact]
    public void Deserialize_NullData_Throws()
    {
        Assert.Throws<ArgumentException>(() => TamperResponsePolicy.Deserialize(null!));
    }

    [Fact]
    public void Deserialize_EmptyData_Throws()
    {
        Assert.Throws<ArgumentException>(() => TamperResponsePolicy.Deserialize(Array.Empty<byte>()));
    }

    [Fact]
    public void Default_IsReject()
    {
        var defaultPolicy = TamperResponsePolicy.Default;
        Assert.Equal(TamperResponse.Reject, defaultPolicy.Level);
    }

    [Fact]
    public void PolicyTypeId_Is0x0074()
    {
        Assert.Equal((ushort)0x0074, TamperResponsePolicy.PolicyTypeId);
    }

    // ── 4. TamperCheckResult Tests ─────────────────────────────────────────

    [Fact]
    public void TamperCheckResult_Passed_FailureReasonNull()
    {
        var check = new TamperCheckResult("Test", true, null);
        Assert.True(check.Passed);
        Assert.Null(check.FailureReason);
    }

    [Fact]
    public void TamperCheckResult_Failed_HasFailureReason()
    {
        var check = new TamperCheckResult("Test", false, "Data corrupted");
        Assert.False(check.Passed);
        Assert.Equal("Data corrupted", check.FailureReason);
    }

    [Fact]
    public void TamperCheckResult_RecordStruct_EqualityWorks()
    {
        var a = new TamperCheckResult("Check1", true, null);
        var b = new TamperCheckResult("Check1", true, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TamperCheckResult_RecordStruct_InequalityWorks()
    {
        var a = new TamperCheckResult("Check1", true, null);
        var b = new TamperCheckResult("Check2", true, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TamperCheckResult_CheckName_PropertyAccess()
    {
        var check = new TamperCheckResult("NamespaceSignature", false, "Invalid");
        Assert.Equal("NamespaceSignature", check.CheckName);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static TamperDetectionResult CreateCleanResult()
    {
        var checks = new[]
        {
            new TamperCheckResult(TamperDetectionResult.CheckNamespaceSignature, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckFormatFingerprint, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckHeaderIntegritySeal, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckMetadataChainHash, true, null),
            new TamperCheckResult(TamperDetectionResult.CheckFileSizeSentinel, true, null),
        };
        return new TamperDetectionResult(checks);
    }

    private static TamperDetectionResult CreateTamperedResult(string failedCheckName)
    {
        var checks = new[]
        {
            new TamperCheckResult(TamperDetectionResult.CheckNamespaceSignature,
                failedCheckName != TamperDetectionResult.CheckNamespaceSignature, failedCheckName == TamperDetectionResult.CheckNamespaceSignature ? "Failed" : null),
            new TamperCheckResult(TamperDetectionResult.CheckFormatFingerprint,
                failedCheckName != TamperDetectionResult.CheckFormatFingerprint, failedCheckName == TamperDetectionResult.CheckFormatFingerprint ? "Failed" : null),
            new TamperCheckResult(TamperDetectionResult.CheckHeaderIntegritySeal,
                failedCheckName != TamperDetectionResult.CheckHeaderIntegritySeal, failedCheckName == TamperDetectionResult.CheckHeaderIntegritySeal ? "Failed" : null),
            new TamperCheckResult(TamperDetectionResult.CheckMetadataChainHash,
                failedCheckName != TamperDetectionResult.CheckMetadataChainHash, failedCheckName == TamperDetectionResult.CheckMetadataChainHash ? "Failed" : null),
            new TamperCheckResult(TamperDetectionResult.CheckFileSizeSentinel,
                failedCheckName != TamperDetectionResult.CheckFileSizeSentinel, failedCheckName == TamperDetectionResult.CheckFileSizeSentinel ? "Failed" : null),
        };
        return new TamperDetectionResult(checks);
    }
}
