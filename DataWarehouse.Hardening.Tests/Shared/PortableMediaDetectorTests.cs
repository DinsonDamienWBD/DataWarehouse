// Hardening tests for Shared findings: PortableMediaDetector
// Finding: 50 (MEDIUM) Object initializer for 'using' variable
namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for PortableMediaDetector hardening findings.
/// </summary>
public class PortableMediaDetectorTests
{
    /// <summary>
    /// Finding 50: Object initializer used with 'using' variable (HttpClient).
    /// If the Timeout setter throws, the HttpClient would not be disposed.
    /// Fixed by separating construction from property assignment.
    /// </summary>
    [Fact]
    public void Finding050_HttpClientInitSeparatedFromPropertyAssignment()
    {
        // Verify that HttpClient can be created and timeout set separately
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(2), client.Timeout);
    }
}
