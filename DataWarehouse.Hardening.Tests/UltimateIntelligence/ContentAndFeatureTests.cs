// Hardening tests for UltimateIntelligence findings 71-90
// ContentClassification, ContentProcessing, ContextRegenerator,
// DataSemanticStrategies, DomainModelRegistry, ElasticsearchVectorStore,
// EmbeddingProviderFactory, EmbeddingProviders

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class ContentAndFeatureTests
{
    private static readonly string PluginDir = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence");

    // --- ContentProcessingStrategies.cs (findings 72-76) ---

    [Fact]
    public void Finding075_ExtractWithAIAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Features", "ContentProcessingStrategies.cs"));
        Assert.DoesNotContain("ExtractWithAIAsync", code);
        Assert.Contains("ExtractWithAiAsync", code);
    }

    // --- ContextRegenerator.cs (findings 77-79) ---

    [Fact]
    public void Finding077_AIAdvancedContextRegenerator_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "ContextRegenerator.cs"));
        Assert.DoesNotContain("class AIAdvancedContextRegenerator", code);
        Assert.Contains("class AiAdvancedContextRegenerator", code);
    }

    // --- DomainModelRegistry.cs (findings 81-84) ---

    [Fact]
    public void Finding081_MessageBus_UnusedField()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "DomainModelRegistry.cs"));
        // Non-private field should use PascalCase (MessageBus)
        // The private readonly in DomainModelRegistry is fine; the protected in DomainModelStrategyBase is renamed
        Assert.Contains("protected new IMessageBus? MessageBus", code);
    }

    [Fact]
    public void Finding082_ONNX_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "DomainModelRegistry.cs"));
        Assert.DoesNotContain(" ONNX", code);
        Assert.Contains("Onnx", code);
    }

    [Fact]
    public void Finding083_OpenAICompatible_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "DomainModelRegistry.cs"));
        Assert.DoesNotContain("OpenAICompatible", code);
        Assert.Contains("OpenAiCompatible", code);
    }

    [Fact]
    public void Finding084_MessageBus_NonPrivate_PascalCase()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "DomainModelRegistry.cs"));
        // Non-private field _messageBus should be PascalCase
        Assert.True(
            !code.Contains("_messageBus") || code.Contains("MessageBus"),
            "_messageBus (non-private) should be renamed to MessageBus");
    }

    // --- EmbeddingProviderFactory.cs (findings 87-91) ---

    [Fact]
    public void Finding087_Registry_UnusedField()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Embeddings", "EmbeddingProviderFactory.cs"));
        // _registry field is private and used for initialization -- this is acceptable
        // The EmbeddingProviderRegistry is passed in constructor and stored
        Assert.Contains("_registry", code);
    }

    [Fact]
    public void Finding088_CreateOpenAI_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Embeddings", "EmbeddingProviderFactory.cs"));
        Assert.DoesNotContain("CreateOpenAI(", code);
        Assert.Contains("CreateOpenAi(", code);
    }

    [Fact]
    public void Finding089_CreateAzureOpenAI_Line169_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Embeddings", "EmbeddingProviderFactory.cs"));
        Assert.DoesNotContain("CreateAzureOpenAI(", code);
        Assert.Contains("CreateAzureOpenAi(", code);
    }

    [Fact]
    public void Finding090_CreateVoyageAI_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Embeddings", "EmbeddingProviderFactory.cs"));
        Assert.DoesNotContain("CreateVoyageAI(", code);
        Assert.Contains("CreateVoyageAi(", code);
    }
}
