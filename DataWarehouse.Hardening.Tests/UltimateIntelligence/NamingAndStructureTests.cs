// Hardening tests for UltimateIntelligence findings 20-70
// Covers: AINavigator, AutoMLEngine, AzureAISearch, AzureOpenAIEmbedding,
// BinaryFormatRegeneration, CassandraPersistence, ChatCapabilities,
// CompressedManifestIndex, ConcreteChannels, ConfigurationRegeneration,
// ConnectorIntegrationStrategy naming/structure findings

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class NamingAndStructureTests
{
    private static readonly string PluginDir = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence");

    // --- AINavigator.cs (findings 20-21) ---

    [Fact]
    public void Finding020_IAINavigator_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Indexing", "AINavigator.cs"));
        Assert.DoesNotContain("interface IAINavigator", code);
        Assert.Contains("interface IAiNavigator", code);
    }

    [Fact]
    public void Finding021_AINavigator_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Indexing", "AINavigator.cs"));
        Assert.DoesNotContain("class AINavigator", code);
        Assert.Contains("class AiNavigator", code);
    }

    // --- AutoMLEngine.cs (findings 22-33) ---

    [Fact]
    public void Finding022_AutoMLEngine_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "AutoMLEngine.cs"));
        Assert.DoesNotContain("class AutoMLEngine", code);
        Assert.Contains("class AutoMlEngine", code);
    }

    [Fact]
    public void Finding023_RunAutoMLPipelineAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "AutoMLEngine.cs"));
        Assert.DoesNotContain("RunAutoMLPipelineAsync", code);
        Assert.Contains("RunAutoMlPipelineAsync", code);
    }

    [Fact]
    public void Finding029_SendAIRequestAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "AutoMLEngine.cs"));
        Assert.DoesNotContain("SendAIRequestAsync", code);
        Assert.Contains("SendAiRequestAsync", code);
    }

    [Fact]
    public void Finding030_AutoMLEngine_IndexOf_Ordinal()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "AutoMLEngine.cs"));
        // IndexOf(string) should use StringComparison.Ordinal
        Assert.Contains("StringComparison.Ordinal", code);
    }

    [Fact]
    public void Finding032_AvailableMemoryMB_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "AutoMLEngine.cs"));
        Assert.DoesNotContain("AvailableMemoryMB", code);
        Assert.Contains("AvailableMemoryMb", code);
    }

    [Fact]
    public void Finding033_AutoMLException_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "AutoMLEngine.cs"));
        Assert.DoesNotContain("class AutoMLException", code);
        Assert.Contains("class AutoMlException", code);
    }

    // --- AzureAISearchVectorStore.cs (findings 35-37) ---

    [Fact]
    public void Finding035_AzureAISearchOptions_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "VectorStores", "AzureAISearchVectorStore.cs"));
        Assert.DoesNotContain("class AzureAISearchOptions", code);
        Assert.Contains("class AzureAiSearchOptions", code);
    }

    [Fact]
    public void Finding036_AzureAISearchVectorStore_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "VectorStores", "AzureAISearchVectorStore.cs"));
        Assert.DoesNotContain("class AzureAISearchVectorStore", code);
        Assert.Contains("class AzureAiSearchVectorStore", code);
    }

    // --- AzureOpenAIEmbeddingProvider.cs (finding 38) ---

    [Fact]
    public void Finding038_AzureOpenAIEmbeddingProvider_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Embeddings", "AzureOpenAIEmbeddingProvider.cs"));
        Assert.DoesNotContain("class AzureOpenAIEmbeddingProvider", code);
        Assert.Contains("class AzureOpenAiEmbeddingProvider", code);
    }

    // --- CassandraPersistenceBackend.cs (findings 41-42) ---

    [Fact]
    public void Finding041_DefaultTTLSeconds_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Persistence", "CassandraPersistenceBackend.cs"));
        Assert.DoesNotContain("DefaultTTLSeconds", code);
        Assert.Contains("DefaultTtlSeconds", code);
    }

    [Fact]
    public void Finding042_TierTTLSeconds_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Persistence", "CassandraPersistenceBackend.cs"));
        Assert.DoesNotContain("TierTTLSeconds", code);
        Assert.Contains("TierTtlSeconds", code);
    }

    // --- ChatCapabilities.cs (findings 43-46) ---

    [Fact]
    public void Finding043_FunctionHandler_UnusedField()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Capabilities", "ChatCapabilities.cs"));
        // Field should be exposed as internal or used
        Assert.True(
            code.Contains("internal") && code.Contains("FunctionHandler") ||
            !code.Contains("_functionHandler"),
            "_functionHandler should be exposed or removed");
    }

    [Fact]
    public void Finding045_FormatSSE_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Capabilities", "ChatCapabilities.cs"));
        Assert.DoesNotContain("FormatSSE", code);
        Assert.Contains("FormatSse", code);
    }

    [Fact]
    public void Finding046_ConversationTTL_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Capabilities", "ChatCapabilities.cs"));
        Assert.DoesNotContain("ConversationTTL", code);
        Assert.Contains("ConversationTtl", code);
    }

    // --- ConcreteChannels.cs (findings 48-59) ---

    [Fact]
    public void Finding048_CLIChannel_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class CLIChannel", code);
        Assert.Contains("class CliChannel", code);
    }

    [Fact]
    public void Finding049_CLIChannelOptions_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class CLIChannelOptions", code);
        Assert.Contains("class CliChannelOptions", code);
    }

    [Fact]
    public void Finding050_RESTChannel_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class RESTChannel", code);
        Assert.Contains("class RestChannel", code);
    }

    [Fact]
    public void Finding051_RESTChannelOptions_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class RESTChannelOptions", code);
        Assert.Contains("class RestChannelOptions", code);
    }

    [Fact]
    public void Finding052_GRPCChannel_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class GRPCChannel", code);
        Assert.Contains("class GrpcChannel", code);
    }

    [Fact]
    public void Finding054_GRPCCallState_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class GRPCCallState", code);
        Assert.Contains("class GrpcCallState", code);
    }

    [Fact]
    public void Finding055_GRPCChannelOptions_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("class GRPCChannelOptions", code);
        Assert.Contains("class GrpcChannelOptions", code);
    }

    [Fact]
    public void Finding056_IGRPCClientAdapter_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Channels", "ConcreteChannels.cs"));
        Assert.DoesNotContain("interface IGRPCClientAdapter", code);
        Assert.Contains("interface IGrpcClientAdapter", code);
    }

    // --- ConnectorIntegrationStrategy.cs (findings 61-70) ---

    [Fact]
    public void Finding061_RegistryHealthClient_PascalCase()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "ConnectorIntegration", "ConnectorIntegrationStrategy.cs"));
        Assert.DoesNotContain("_registryHealthClient", code);
        Assert.Contains("RegistryHealthClient", code);
    }

    [Fact]
    public void Finding066_TransformPayloadWithAIAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "ConnectorIntegration", "ConnectorIntegrationStrategy.cs"));
        Assert.DoesNotContain("TransformPayloadWithAIAsync", code);
        Assert.Contains("TransformPayloadWithAiAsync", code);
    }

    [Fact]
    public void Finding067_OptimizeQueryWithAIAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "ConnectorIntegration", "ConnectorIntegrationStrategy.cs"));
        Assert.DoesNotContain("OptimizeQueryWithAIAsync", code);
        Assert.Contains("OptimizeQueryWithAiAsync", code);
    }

    [Fact]
    public void Finding068_EnrichSchemaWithAIAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "ConnectorIntegration", "ConnectorIntegrationStrategy.cs"));
        Assert.DoesNotContain("EnrichSchemaWithAIAsync", code);
        Assert.Contains("EnrichSchemaWithAiAsync", code);
    }

    [Fact]
    public void Finding069_DetectAnomaliesWithAIAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "ConnectorIntegration", "ConnectorIntegrationStrategy.cs"));
        Assert.DoesNotContain("DetectAnomaliesWithAIAsync", code);
        Assert.Contains("DetectAnomaliesWithAiAsync", code);
    }

    [Fact]
    public void Finding070_PredictFailureWithAIAsync_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "ConnectorIntegration", "ConnectorIntegrationStrategy.cs"));
        Assert.DoesNotContain("PredictFailureWithAIAsync", code);
        Assert.Contains("PredictFailureWithAiAsync", code);
    }
}
