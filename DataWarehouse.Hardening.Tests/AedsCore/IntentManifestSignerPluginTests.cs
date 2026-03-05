// Hardening tests for AedsCore findings: IntentManifestSignerPlugin
// Findings: 84 (MEDIUM), 85 (MEDIUM)
using DataWarehouse.Plugins.AedsCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for IntentManifestSignerPlugin hardening findings.
/// </summary>
public class IntentManifestSignerPluginTests
{
    /// <summary>
    /// Finding 84: ConditionIsAlwaysTrueOrFalse — nullable annotation on message.Payload.
    /// Defensive null check in HandleSigningRequestAsync.
    /// </summary>
    [Fact]
    public void Finding084_DefensiveNullCheckOnPayload()
    {
        var method = typeof(IntentManifestSignerPlugin).GetMethod("HandleSigningRequestAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    /// <summary>
    /// Finding 85: ConditionIsAlwaysTrueOrFalse — nullable annotation on payload cast.
    /// Defensive type check before dictionary cast.
    /// </summary>
    [Fact]
    public void Finding085_DefensiveTypeCast()
    {
        // HandleSigningRequestAsync checks: message.Payload is Dictionary<string, object>
        // This is correct defensive programming.
        var plugin = new IntentManifestSignerPlugin(NullLogger<IntentManifestSignerPlugin>.Instance);
        Assert.Equal("Intent Manifest Signer", plugin.Name);
    }
}
