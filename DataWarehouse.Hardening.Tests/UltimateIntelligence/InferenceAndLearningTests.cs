// Hardening tests for UltimateIntelligence findings 119-163
// FutureInnovations, HuggingFace, HybridMemoryStore, HybridVectorStore,
// IAdvancedRegenerationStrategy, IIntelligenceStrategy, IndexManager,
// InferenceEngine, InstanceLearning, IntelligenceBusFeatures,
// IntelligenceGateway

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class InferenceAndLearningTests
{
    private static readonly string PluginDir = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence");

    // --- IAdvancedRegenerationStrategy.cs (finding 130) ---

    [Fact]
    public void Finding130_FloatEqualityComparison()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "Strategies", "Memory", "Regeneration", "IAdvancedRegenerationStrategy.cs"));
        // Should not compare floats with == directly
        Assert.True(
            code.Contains("Math.Abs") || code.Contains("epsilon") ||
            !code.Contains("== current"),
            "Float comparison should use epsilon or Math.Abs");
    }

    // --- IIntelligenceStrategy.cs (findings 131-132) ---

    [Fact]
    public void Finding131_AIProvider_EnumMember_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "IIntelligenceStrategy.cs"));
        // Already fixed: AiProvider exists
        Assert.Contains("AiProvider", code);
    }

    [Fact]
    public void Finding132_AllAIProvider_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "IIntelligenceStrategy.cs"));
        Assert.DoesNotContain("AllAIProvider", code);
        Assert.Contains("AllAiProvider", code);
    }

    // --- InferenceEngine.cs (findings 138-145) ---

    [Fact]
    public void Finding139_CPU_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "InferenceEngine.cs"));
        // CPU -> Cpu enum member
        Assert.Contains("Cpu,", code);
    }

    [Fact]
    public void Finding140_GPU_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "InferenceEngine.cs"));
        Assert.Contains("Gpu,", code);
    }

    [Fact]
    public void Finding141_NPU_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "InferenceEngine.cs"));
        Assert.Contains("Npu", code);
    }

    [Fact]
    public void Finding142_WASM_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "InferenceEngine.cs"));
        Assert.Contains("Wasm", code);
    }

    [Fact]
    public void Finding143_WasiNN_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "EdgeNative", "InferenceEngine.cs"));
        Assert.Contains("WasiNn", code);
    }

    // --- InstanceLearning.cs (findings 146-154) ---

    [Fact]
    public void Finding146_CNN_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "InstanceLearning.cs"));
        Assert.DoesNotContain(" CNN,", code);
        Assert.Contains("Cnn", code);
    }

    [Fact]
    public void Finding147_RNN_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "InstanceLearning.cs"));
        Assert.DoesNotContain(" RNN,", code);
        Assert.Contains("Rnn", code);
    }

    [Fact]
    public void Finding148_GNN_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "InstanceLearning.cs"));
        Assert.DoesNotContain(" GNN,", code);
        Assert.Contains("Gnn", code);
    }

    [Fact]
    public void Finding149_VAE_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "InstanceLearning.cs"));
        Assert.DoesNotContain(" VAE,", code);
        Assert.Contains("Vae", code);
    }

    [Fact]
    public void Finding150_MaxMemoryMB_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "DomainModels", "InstanceLearning.cs"));
        Assert.DoesNotContain("MaxMemoryMB", code);
        Assert.Contains("MaxMemoryMb", code);
    }

    // --- IntelligenceGateway.cs (findings 156-163) ---

    [Fact]
    public void Finding156_CLI_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "IntelligenceGateway.cs"));
        // Enum member should be Cli, not CLI
        Assert.Contains("Cli,", code);
    }

    [Fact]
    public void Finding157_GUI_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "IntelligenceGateway.cs"));
        // Enum member should be Gui, not GUI
        Assert.Contains("Gui,", code);
    }

    [Fact]
    public void Finding158_API_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(PluginDir, "IntelligenceGateway.cs"));
        // Enum member should be Api, not API
        Assert.Contains("Api,", code);
    }
}
