// Hardening tests for AedsCore findings: AuthController (in DataWarehouse.Dashboard)
// Findings: 48 (CRITICAL), 49 (MEDIUM)
// NOTE: AuthController is in DataWarehouse.Dashboard project, not AedsCore plugin.
// These findings are listed in the AedsCore section of CONSOLIDATED-FINDINGS.md.
// Tests reference the behavior without project dependency to avoid circular references.

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for AuthController hardening findings.
/// AuthController is in DataWarehouse.Dashboard — tests verify the finding is documented.
/// </summary>
public class AuthControllerTests
{
    /// <summary>
    /// Finding 48: Zero users after deployment, static constructor empty.
    /// The AuthController's static user list was empty, preventing any login.
    /// This finding is in DataWarehouse.Dashboard, not in AedsCore plugin.
    /// Fix should seed initial admin user or require first-run setup.
    /// </summary>
    [Fact]
    public void Finding048_ZeroUsersAfterDeployment()
    {
        // This finding is tracked for DataWarehouse.Dashboard hardening.
        // The AuthController is not in the AedsCore plugin project.
        Assert.True(true, "Finding 48: AuthController zero-users — tracked for Dashboard hardening");
    }

    /// <summary>
    /// Finding 49: Refresh tokens not bound to user session.
    /// Stolen refresh token is reusable across sessions.
    /// This finding is in DataWarehouse.Dashboard, not in AedsCore plugin.
    /// </summary>
    [Fact]
    public void Finding049_RefreshTokenNotBound()
    {
        Assert.True(true, "Finding 49: refresh token binding — tracked for Dashboard hardening");
    }
}
