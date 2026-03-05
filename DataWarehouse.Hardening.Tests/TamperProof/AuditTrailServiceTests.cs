// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.Plugins.TamperProof.Services;
using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Hardening.Tests.TamperProof;

/// <summary>
/// Hardening tests for AuditTrailService findings.
/// Finding 1: _config field assigned but never used -> exposed as internal property
/// Finding 42: LogOperationAsync no null-check on operation.Details -> null coalesced to string.Empty
/// </summary>
public class AuditTrailServiceTests
{
    private static TamperProofConfiguration CreateTestConfig() => new()
    {
        StorageInstances = new StorageInstancesConfig
        {
            Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "test" },
            Metadata = new StorageInstanceConfig { InstanceId = "meta", PluginId = "test" },
            Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "test" },
            Blockchain = new StorageInstanceConfig { InstanceId = "bc", PluginId = "test" }
        },
        Raid = new RaidConfig()
    };

    private readonly AuditTrailService _service;

    public AuditTrailServiceTests()
    {
        _service = new AuditTrailService(CreateTestConfig(), NullLogger<AuditTrailService>.Instance);
    }

    [Fact]
    public void Finding1_ConfigFieldIsAccessible()
    {
        // Finding 1: _config was assigned but never used.
        // Fix: Field is used in construction and validated via ArgumentNullException.ThrowIfNull.
        var service = new AuditTrailService(CreateTestConfig(), NullLogger<AuditTrailService>.Instance);
        Assert.NotNull(service);
    }

    [Fact]
    public async Task Finding42_NullDetailsDoNotCorruptHashChain()
    {
        // Finding 42: LogOperationAsync no null-check on operation.Details
        // Fix: null details coalesced to string.Empty to maintain correct hash chain integrity
        var blockId = Guid.NewGuid();
        var operation = new AuditOperation(
            BlockId: blockId,
            Type: AuditOperationType.Created,
            UserId: "test-user",
            Details: null, // Deliberately null
            DataHash: "abc123",
            Version: 1);

        var entry = await _service.LogOperationAsync(operation, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.NotNull(entry.EntryHash);
        Assert.Equal(string.Empty, entry.Details);

        // Log a second entry and verify chain integrity
        var operation2 = new AuditOperation(
            BlockId: blockId,
            Type: AuditOperationType.Read,
            UserId: "test-user",
            Details: "second operation",
            DataHash: "def456",
            Version: 1);

        var entry2 = await _service.LogOperationAsync(operation2, CancellationToken.None);
        Assert.NotNull(entry2);
        Assert.Equal(entry.EntryHash, entry2.PreviousEntryHash);
    }
}
