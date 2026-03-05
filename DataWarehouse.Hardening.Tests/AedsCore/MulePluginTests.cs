// Hardening tests for AedsCore findings: MulePlugin
// Finding: 18 (HIGH)
using DataWarehouse.Plugins.AedsCore.Extensions;

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for MulePlugin hardening findings.
/// </summary>
public class MulePluginTests
{
    /// <summary>
    /// Finding 18: USB index deserialized manifest IDs used directly in file paths — path traversal.
    /// FIX: Manifest IDs are validated to only allow alphanumeric, dash, underscore, dot.
    /// IDs with path traversal characters (../, \, etc.) are rejected.
    /// </summary>
    [Fact]
    public async Task Finding018_PathTraversalPrevented()
    {
        var plugin = new MulePlugin();
        var tempPath = Path.Combine(Path.GetTempPath(), $"mule-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempPath);

            // Valid manifest IDs should work
            await plugin.ExportManifestsAsync(
                new[] { "valid-id-001", "another.id.002" },
                tempPath);

            // Path traversal attempts should throw
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                plugin.ExportManifestsAsync(
                    new[] { "../../../etc/passwd" },
                    tempPath));

            Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }
}
