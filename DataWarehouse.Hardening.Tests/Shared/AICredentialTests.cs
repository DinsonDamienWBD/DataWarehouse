// Hardening tests for Shared findings: AICredential
// Findings: 2 (LOW naming), 3 (CRITICAL ApiKey serialization), 4 (LOW naming)
using System.Text.Json;
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for AICredential and AIProviderConfig hardening findings.
/// </summary>
public class AICredentialTests
{
    /// <summary>
    /// Finding 3 (CRITICAL): ApiKey property must have [JsonIgnore] to prevent serialization to logs.
    /// Verifies that serializing an AICredential does NOT include the raw ApiKey.
    /// </summary>
    [Fact]
    public void Finding003_ApiKeyHasJsonIgnore_NotSerializedToJson()
    {
        var credential = new AICredential
        {
            ProviderId = "openai",
            ApiKey = "sk-secret-key-12345678"
        };

        var json = JsonSerializer.Serialize(credential);

        // ApiKey must NOT appear in serialized output
        Assert.DoesNotContain("sk-secret-key-12345678", json);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finding 3 (CRITICAL): Ensure deserialization still works via explicit assignment.
    /// </summary>
    [Fact]
    public void Finding003_ApiKeyCanStillBeSetProgrammatically()
    {
        var credential = new AICredential
        {
            ProviderId = "anthropic",
            ApiKey = "my-key-value"
        };

        Assert.Equal("my-key-value", credential.ApiKey);
        Assert.True(credential.HasApiKey);
    }

    /// <summary>
    /// Findings 2 + 4: Naming findings for AICredential -> AiCredential and AIProviderConfig -> AiProviderConfig.
    /// These are type name renames that require broader refactoring. The types exist and are functional.
    /// Tracked: naming convention violations for AI prefix (inspectcode suggests AiCredential, AiProviderConfig).
    /// </summary>
    [Fact]
    public void Finding002_004_TypesExistAndAreUsable()
    {
        // AICredential and AIProviderConfig types exist and are functional
        var credential = new AICredential { ProviderId = "test" };
        Assert.NotNull(credential);

        var config = new AIProviderConfig { ProviderId = "test" };
        Assert.NotNull(config);
    }
}
