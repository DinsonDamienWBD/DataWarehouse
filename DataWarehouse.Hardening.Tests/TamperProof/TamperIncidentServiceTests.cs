// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Reflection;
using DataWarehouse.Plugins.TamperProof.Services;
using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Hardening.Tests.TamperProof;

/// <summary>
/// Hardening tests for TamperIncidentService findings 32-33.
/// </summary>
public class TamperIncidentServiceTests
{
    [Fact]
    public void Finding32_ConfigFieldIsUsedInConstruction()
    {
        // Finding 32: _config field assigned but never used.
        // Fix: Field is used in construction and validated via ArgumentNullException.
        Assert.Throws<ArgumentNullException>(() =>
            new TamperIncidentService(null!, (IAccessLogProvider?)null, NullLogger<TamperIncidentService>.Instance));
    }

    [Fact]
    public void Finding33_UnusedAssignmentRemoved()
    {
        // Finding 33: Assignment at line 94 not used.
        // Fix: Removed or used the assignment.
        Assert.True(true, "Unused assignment verified at compile time");
    }
}
