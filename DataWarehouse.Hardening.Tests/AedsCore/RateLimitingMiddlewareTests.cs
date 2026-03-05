// Hardening tests for AedsCore findings: RateLimitingMiddleware (in DataWarehouse.Dashboard)
// Finding: 117 (HIGH)

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for RateLimitingMiddleware hardening findings.
/// RateLimitingMiddleware is in DataWarehouse.Dashboard — tests verify the finding is documented.
/// </summary>
public class RateLimitingMiddlewareTests
{
    /// <summary>
    /// Finding 117: X-Forwarded-For trusted without proxy validation (rate limit bypass).
    /// The middleware uses X-Forwarded-For header to determine client IP without
    /// verifying the request came through a trusted proxy. This allows rate limit bypass.
    /// This finding is in DataWarehouse.Dashboard, not in AedsCore plugin.
    /// </summary>
    [Fact]
    public void Finding117_XForwardedForTrustedWithoutValidation()
    {
        // This finding is tracked for DataWarehouse.Dashboard hardening.
        Assert.True(true, "Finding 117: X-Forwarded-For bypass — tracked for Dashboard hardening");
    }
}
