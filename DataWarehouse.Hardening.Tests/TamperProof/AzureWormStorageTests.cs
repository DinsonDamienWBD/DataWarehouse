// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.Plugins.TamperProof.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Hardening.Tests.TamperProof;

/// <summary>
/// Hardening tests for AzureWormStorage finding 58.
/// </summary>
public class AzureWormStorageTests
{
    [Fact]
    public async Task Finding58_AzureWormStorageSimulationDoesNotAutoVerify()
    {
        // Finding 58 (HIGH): Entire class is simulation using in-memory Dictionary.
        // VerifyImmutabilityConfigurationAsync previously set _immutabilityVerified = true
        // unconditionally after Task.Delay(10).
        // Fix: Now sets _immutabilityVerified = false, logs WARNING about simulation mode,
        // and returns false. Production requires real Azure.Storage.Blobs SDK.
        var config = new AzureBlobWormConfiguration(
            ConnectionString: "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=dGVzdA==;EndpointSuffix=core.windows.net",
            ContainerName: "test-container");
        var storage = new AzureWormStorage(config, NullLogger<AzureWormStorage>.Instance);

        var verified = await storage.VerifyImmutabilityConfigurationAsync();
        Assert.False(verified, "Simulation should return false for immutability verification");
    }
}
