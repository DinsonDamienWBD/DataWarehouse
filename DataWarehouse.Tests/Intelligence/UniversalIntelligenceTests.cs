using DataWarehouse.Plugins.UltimateIntelligence;
using DataWarehouse.Plugins.UltimateIntelligence.Strategies.VectorStores;
using DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;
using DataWarehouse.SDK.AI;
using FluentAssertions;
using Xunit;
using PluginIntelligenceCapabilities = DataWarehouse.Plugins.UltimateIntelligence.IntelligenceCapabilities;

namespace DataWarehouse.Tests.Intelligence;

/// <summary>
/// Tests for UltimateIntelligence strategy implementations.
/// Validates AI provider contracts, vector store instantiation, and strategy metadata.
/// </summary>
[Trait("Category", "Unit")]
public class UniversalIntelligenceTests
{
    #region AI Provider Strategy Contract Tests

    [Fact]
    public void AIProviderStrategyBase_ShouldBeAbstract()
    {
        typeof(AIProviderStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void AIProviderStrategyBase_ShouldDefineRequiredMethods()
    {
        var type = typeof(AIProviderStrategyBase);
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        methods.Should().Contain(m => m.Name == "GetStrategyCapability");
    }

    [Fact]
    public void OpenAiProviderStrategy_ShouldHaveCorrectMetadata()
    {
        var strategy = new OpenAiProviderStrategy();
        strategy.StrategyId.Should().Contain("openai");
        strategy.StrategyName.Should().NotBeNullOrEmpty();
        strategy.Info.Should().NotBeNull();
        strategy.Info.ProviderName.Should().Contain("OpenAI");
    }

    [Fact]
    public void ClaudeProviderStrategy_ShouldHaveCorrectMetadata()
    {
        var strategy = new ClaudeProviderStrategy();
        strategy.StrategyId.Should().Contain("claude");
        strategy.StrategyName.Should().NotBeNullOrEmpty();
        strategy.Info.Should().NotBeNull();
    }

    [Fact]
    public void OllamaProviderStrategy_ShouldSupportOfflineMode()
    {
        var strategy = new OllamaProviderStrategy();
        strategy.Info.SupportsOfflineMode.Should().BeTrue(
            "Ollama runs locally and doesn't require network access");
    }

    [Fact]
    public void HuggingFaceProviderStrategy_ShouldHaveProviderInfo()
    {
        var strategy = new HuggingFaceProviderStrategy();
        strategy.Info.Should().NotBeNull();
        strategy.Info.ProviderName.Should().NotBeNullOrEmpty();
        strategy.Info.ConfigurationRequirements.Should().NotBeEmpty();
    }

    #endregion

    #region Vector Store Strategy Tests

    [Fact]
    public void VectorStoreStrategyBase_ShouldBeAbstract()
    {
        typeof(VectorStoreStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void PineconeVectorStrategy_ShouldHaveCorrectMetadata()
    {
        var strategy = new PineconeVectorStrategy();
        strategy.StrategyId.Should().Be("vector-pinecone");
        strategy.StrategyName.Should().Contain("Pinecone");
        strategy.Info.RequiresNetworkAccess.Should().BeTrue();
    }

    [Fact]
    public void PineconeVectorStrategy_ShouldRequireConfiguration()
    {
        var strategy = new PineconeVectorStrategy();
        strategy.Info.ConfigurationRequirements.Should().NotBeEmpty();
        strategy.Info.ConfigurationRequirements
            .Should().Contain(r => r.Key == "ApiKey" && r.Required);
    }

    [Fact]
    public void WeaviateVectorStrategy_ShouldHaveCorrectMetadata()
    {
        var strategy = new WeaviateVectorStrategy();
        strategy.StrategyId.Should().Contain("weaviate");
        strategy.StrategyName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void IntelligenceCapabilities_ShouldHaveVectorStoreFlag()
    {
        PluginIntelligenceCapabilities.AllVectorStore
            .Should().NotBe(PluginIntelligenceCapabilities.None);
    }

    #endregion
}
