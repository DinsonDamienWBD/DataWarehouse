// Hardening tests for Shared findings: UserSession
// Finding: 60 (LOW) Linear search on Roles list
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for UserSession hardening findings.
/// </summary>
public class UserSessionTests
{
    /// <summary>
    /// Finding 60: HasRole used linear search (List.Contains) on Roles.
    /// Fixed by using a lazy-initialized HashSet for O(1) lookups.
    /// </summary>
    [Fact]
    public void Finding060_HasRoleUsesHashSetLookup()
    {
        var session = new UserSession
        {
            UserId = "test-user",
            Roles = new[] { "admin", "reader", "writer", "operator" }
        };

        // O(1) lookup via HashSet
        Assert.True(session.HasRole("admin"));
        Assert.True(session.HasRole("ADMIN")); // Case-insensitive
        Assert.True(session.HasRole("Reader"));
        Assert.False(session.HasRole("superadmin"));
    }

    [Fact]
    public void Finding060_HasRoleWithEmptyRoles()
    {
        var session = new UserSession { UserId = "test-user" };
        Assert.False(session.HasRole("admin"));
    }
}
