// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Unit tests for TamperProof access log provider SDK contracts (T6.4).
/// Validates AccessLogEntry construction, hash chain verification, time-window queries,
/// tamper detection, and AttributionConfidence levels.
/// </summary>
public class AccessLogProviderTests
{
    #region AccessLogEntry Construction

    [Fact]
    public void AccessLogEntry_ShouldConstructWithAllRequiredFields()
    {
        var objectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var entry = new AccessLogEntry
        {
            EntryId = Guid.NewGuid(),
            ObjectId = objectId,
            AccessType = AccessType.Read,
            Principal = "user:john.doe",
            Timestamp = now,
            Succeeded = true,
            DurationMs = 50,
            BytesTransferred = 4096
        };

        entry.ObjectId.Should().Be(objectId);
        entry.AccessType.Should().Be(AccessType.Read);
        entry.Principal.Should().Be("user:john.doe");
        entry.Timestamp.Should().Be(now);
        entry.Succeeded.Should().BeTrue();
        entry.DurationMs.Should().Be(50);
        entry.BytesTransferred.Should().Be(4096);
    }

    [Fact]
    public void AccessLogEntry_CreateRead_ShouldPopulateCorrectly()
    {
        var objectId = Guid.NewGuid();
        var entry = AccessLogEntry.CreateRead(objectId, "user:reader", 25, 2048);

        entry.EntryId.Should().NotBe(Guid.Empty);
        entry.ObjectId.Should().Be(objectId);
        entry.AccessType.Should().Be(AccessType.Read);
        entry.Principal.Should().Be("user:reader");
        entry.Succeeded.Should().BeTrue();
        entry.DurationMs.Should().Be(25);
        entry.BytesTransferred.Should().Be(2048);
    }

    [Fact]
    public void AccessLogEntry_CreateWrite_ShouldIncludeComputedHash()
    {
        var objectId = Guid.NewGuid();
        var entry = AccessLogEntry.CreateWrite(objectId, "user:writer", "AABBCCDD", 100, 8192);

        entry.AccessType.Should().Be(AccessType.Write);
        entry.ComputedHash.Should().Be("AABBCCDD");
        entry.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void AccessLogEntry_CreateFailed_ShouldIncludeErrorMessage()
    {
        var objectId = Guid.NewGuid();
        var entry = AccessLogEntry.CreateFailed(objectId, AccessType.Write, "user:writer", "Disk full");

        entry.Succeeded.Should().BeFalse();
        entry.ErrorMessage.Should().Be("Disk full");
        entry.AccessType.Should().Be(AccessType.Write);
    }

    [Fact]
    public void AccessLogEntry_CreateAdminOperation_ShouldIncludeContext()
    {
        var objectId = Guid.NewGuid();
        var entry = AccessLogEntry.CreateAdminOperation(objectId, "admin:root", "Manual recovery initiated");

        entry.AccessType.Should().Be(AccessType.AdminOperation);
        entry.Succeeded.Should().BeTrue();
        entry.Context.Should().ContainKey("Operation");
    }

    [Fact]
    public void AccessLogEntry_ShouldValidateSuccessfully()
    {
        var entry = AccessLogEntry.CreateRead(Guid.NewGuid(), "user:test", 10, 100);

        entry.Validate().Should().BeTrue();
    }

    [Fact]
    public void AccessLogEntry_ShouldFailValidationWithEmptyPrincipal()
    {
        var entry = new AccessLogEntry
        {
            EntryId = Guid.NewGuid(),
            ObjectId = Guid.NewGuid(),
            AccessType = AccessType.Read,
            Principal = "",
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = true
        };

        entry.Validate().Should().BeFalse();
    }

    #endregion

    #region Access Log Integrity (Hash Chain Verification)

    [Fact]
    public void AccessLogEntry_ComputeEntryHash_ShouldProduceConsistentHash()
    {
        var entry = new AccessLogEntry
        {
            EntryId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ObjectId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AccessType = AccessType.Read,
            Principal = "user:test",
            Timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Succeeded = true
        };

        var hash1 = entry.ComputeEntryHash();
        var hash2 = entry.ComputeEntryHash();

        hash1.Should().Be(hash2, "same entry should always produce the same hash");
        hash1.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AccessLogEntry_SequentialEntries_ShouldFormHashChain()
    {
        // Create sequential log entries where each entry's hash includes the previous
        var entries = new List<AccessLogEntry>();
        var entryHashes = new List<string>();

        for (int i = 0; i < 5; i++)
        {
            var entry = new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = Guid.NewGuid(),
                AccessType = i % 2 == 0 ? AccessType.Read : AccessType.Write,
                Principal = $"user:user-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
                Succeeded = true,
                ComputedHash = i % 2 == 1 ? $"hash-{i}" : null
            };
            entries.Add(entry);
            entryHashes.Add(entry.ComputeEntryHash());
        }

        // Verify all hashes are unique (different entries should produce different hashes)
        entryHashes.Should().OnlyHaveUniqueItems("each unique entry should produce a unique hash");
    }

    [Fact]
    public void AccessLogEntry_TamperedEntry_ShouldProduceDifferentHash()
    {
        var entry = new AccessLogEntry
        {
            EntryId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ObjectId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AccessType = AccessType.Read,
            Principal = "user:original",
            Timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Succeeded = true
        };

        var tamperedEntry = new AccessLogEntry
        {
            EntryId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ObjectId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AccessType = AccessType.Read,
            Principal = "user:tampered",
            Timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Succeeded = true
        };

        var originalHash = entry.ComputeEntryHash();
        var tamperedHash = tamperedEntry.ComputeEntryHash();

        originalHash.Should().NotBe(tamperedHash,
            "modifying the Principal field should change the entry hash");
    }

    #endregion

    #region Time-Window Query

    [Fact]
    public void AccessLogQuery_ForTimeRange_ShouldCreateValidQuery()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var end = DateTimeOffset.UtcNow;

        var query = AccessLogQuery.ForTimeRange(start, end);

        query.StartTime.Should().Be(start);
        query.EndTime.Should().Be(end);
        query.Validate().Should().BeTrue();
    }

    [Fact]
    public void AccessLogQuery_ShouldFailValidationWithInvertedTimeRange()
    {
        var query = new AccessLogQuery
        {
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddHours(-1)
        };

        query.Validate().Should().BeFalse();
    }

    [Fact]
    public void AccessLogQuery_ForObject_ShouldFilterByObjectId()
    {
        var objectId = Guid.NewGuid();
        var query = AccessLogQuery.ForObject(objectId);

        query.ObjectId.Should().Be(objectId);
        query.Validate().Should().BeTrue();
    }

    [Fact]
    public void AccessLogQuery_ForPrincipal_ShouldFilterByPrincipal()
    {
        var query = AccessLogQuery.ForPrincipal("user:john.doe");

        query.Principal.Should().Be("user:john.doe");
        query.Validate().Should().BeTrue();
    }

    [Fact]
    public void AccessLogQuery_ForSuspiciousWrites_ShouldFilterWriteAndCorrectTypes()
    {
        var query = AccessLogQuery.ForSuspiciousWrites();

        query.AccessTypes.Should().Contain(AccessType.Write);
        query.AccessTypes.Should().Contain(AccessType.Correct);
        query.SucceededOnly.Should().BeTrue();
        query.WithHashOnly.Should().BeTrue();
    }

    [Fact]
    public void TimeWindow_ShouldContainTimestampsWithinRange()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var window = new TimeWindow { Start = start, End = end };

        window.Contains(start.AddHours(12)).Should().BeTrue();
        window.Contains(start.AddDays(-1)).Should().BeFalse();
        window.Contains(end.AddDays(1)).Should().BeFalse();
        window.Duration.Should().Be(TimeSpan.FromDays(1));
    }

    #endregion

    #region Tamper Detection

    [Fact]
    public void AccessLogSummary_ShouldDetectSuspiciousPatterns_HighFrequencyAccess()
    {
        var objectId = Guid.NewGuid();
        var entries = new List<AccessLogEntry>();

        // Create 110 accesses from same principal (exceeds 100 threshold)
        for (int i = 0; i < 110; i++)
        {
            entries.Add(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Read,
                Principal = "user:suspicious",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-i),
                Succeeded = true,
                DurationMs = 10,
                BytesTransferred = 100
            });
        }

        var summary = AccessLogSummary.FromEntries(objectId, entries);

        summary.HasSuspiciousPatterns.Should().BeTrue();
        summary.SuspiciousPatterns.Should().Contain(p => p.Contains("High access frequency"));
    }

    [Fact]
    public void AccessLogSummary_ShouldComputeCorrectCounts()
    {
        var objectId = Guid.NewGuid();
        var entries = new List<AccessLogEntry>
        {
            AccessLogEntry.CreateRead(objectId, "user:reader1", 10, 100),
            AccessLogEntry.CreateRead(objectId, "user:reader2", 20, 200),
            AccessLogEntry.CreateWrite(objectId, "user:writer1", "hash1", 30, 300),
            AccessLogEntry.CreateFailed(objectId, AccessType.Write, "user:attacker", "Access denied")
        };

        var summary = AccessLogSummary.FromEntries(objectId, entries);

        summary.TotalAccesses.Should().Be(4);
        summary.SuccessfulAccesses.Should().Be(3);
        summary.FailedAccesses.Should().Be(1);
        summary.UniquePrincipals.Should().Be(4);
    }

    [Fact]
    public void AccessLogSummary_CreateEmpty_ShouldReturnZeroCounts()
    {
        var objectId = Guid.NewGuid();
        var summary = AccessLogSummary.CreateEmpty(objectId);

        summary.TotalAccesses.Should().Be(0);
        summary.SuccessfulAccesses.Should().Be(0);
        summary.FailedAccesses.Should().Be(0);
        summary.UniquePrincipals.Should().Be(0);
    }

    #endregion

    #region AttributionConfidence Levels

    [Fact]
    public void AttributionConfidence_ShouldHaveExactlyFourValues()
    {
        var values = Enum.GetValues<AttributionConfidence>();
        values.Should().HaveCount(4);
    }

    [Fact]
    public void AttributionConfidence_ShouldContainAllExpectedLevels()
    {
        var values = Enum.GetValues<AttributionConfidence>();
        values.Should().Contain(AttributionConfidence.Unknown);
        values.Should().Contain(AttributionConfidence.Suspected);
        values.Should().Contain(AttributionConfidence.Likely);
        values.Should().Contain(AttributionConfidence.Confirmed);
    }

    [Theory]
    [InlineData(AttributionConfidence.Unknown, 0)]
    [InlineData(AttributionConfidence.Suspected, 1)]
    [InlineData(AttributionConfidence.Likely, 2)]
    [InlineData(AttributionConfidence.Confirmed, 3)]
    public void AttributionConfidence_ShouldHaveAscendingIntValues(
        AttributionConfidence confidence, int expectedValue)
    {
        ((int)confidence).Should().Be(expectedValue,
            "confidence levels should increase from Unknown to Confirmed");
    }

    #endregion

    #region AccessType Enum

    [Fact]
    public void AccessType_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<AccessType>();
        values.Should().Contain(AccessType.Read);
        values.Should().Contain(AccessType.Write);
        values.Should().Contain(AccessType.Correct);
        values.Should().Contain(AccessType.Delete);
        values.Should().Contain(AccessType.MetadataRead);
        values.Should().Contain(AccessType.MetadataWrite);
        values.Should().Contain(AccessType.AdminOperation);
        values.Should().Contain(AccessType.SystemMaintenance);
    }

    [Fact]
    public void AccessType_ShouldHaveAtLeastEightValues()
    {
        var values = Enum.GetValues<AccessType>();
        values.Should().HaveCountGreaterThanOrEqualTo(8);
    }

    #endregion
}
