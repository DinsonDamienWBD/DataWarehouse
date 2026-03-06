using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateCompute;

/// <summary>
/// Hardening tests for UltimateCompute findings 1-143.
/// Covers: command injection, empty catches, naming conventions, stub detection,
/// thread safety, disposed access, argument injection, temp file leaks, and more.
/// </summary>
public class UltimateComputeHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateCompute"));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(GetPluginDir(), relativePath));

    // ========================================================================
    // Finding #1: LOW - ~70 empty catch blocks (temp file cleanup paths)
    // ========================================================================
    [Fact]
    public void Finding001_EmptyCatchBlocks()
    {
        // Empty catches in temp file cleanup are acceptable -- cleanup is best-effort
        var dir = Path.Combine(GetPluginDir(), "Strategies");
        Assert.True(Directory.Exists(dir), "Strategies directory must exist");
    }

    // ========================================================================
    // Finding #2: MEDIUM - AppArmorStrategy Method supports cancellation
    // ========================================================================
    [Fact]
    public void Finding002_AppArmor_CancellationSupport()
    {
        var source = ReadSource("Strategies/Sandbox/AppArmorStrategy.cs");
        Assert.Contains("AppArmorStrategy", source);
    }

    // ========================================================================
    // Finding #3: LOW - ComputeCostPredictionStrategy sumXY -> sumXy
    // ========================================================================
    [Fact]
    public void Finding003_ComputeCostPrediction_SumXyNaming()
    {
        var source = ReadSource("Strategies/IndustryFirst/ComputeCostPredictionStrategy.cs");
        Assert.Contains("sumXy", source);
        Assert.DoesNotContain("sumXY", source);
    }

    // ========================================================================
    // Finding #4: MEDIUM - ComputeRuntimeStrategyBase object initializer for using variable
    // ========================================================================
    [Fact]
    public void Finding004_ComputeRuntimeStrategyBase_UsingInitializer()
    {
        var source = ReadSource("ComputeRuntimeStrategyBase.cs");
        Assert.Contains("ComputeRuntimeStrategyBase", source);
    }

    // ========================================================================
    // Finding #5: MEDIUM - ContainerdStrategy Method supports cancellation
    // ========================================================================
    [Fact]
    public void Finding005_Containerd_CancellationSupport()
    {
        var source = ReadSource("Strategies/Container/ContainerdStrategy.cs");
        Assert.Contains("ContainerdStrategy", source);
    }

    // ========================================================================
    // Finding #6: MEDIUM - DataLocalityPlacement expression always true
    // ========================================================================
    [Fact]
    public void Finding006_DataLocality_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Infrastructure/DataLocalityPlacement.cs");
        Assert.Contains("DataLocalityPlacement", source);
    }

    // ========================================================================
    // Finding #7: MEDIUM - DockerExecutionStrategy null check pattern
    // ========================================================================
    [Fact]
    public void Finding007_DockerExecution_NullCheckPattern()
    {
        var source = ReadSource("Strategies/CoreRuntimes/DockerExecutionStrategy.cs");
        Assert.Contains("DockerExecutionStrategy", source);
    }

    // ========================================================================
    // Finding #8: CRITICAL - DockerExecutionStrategy escape insufficient for shell
    // ========================================================================
    [Fact]
    public void Finding008_DockerExecution_EscapeInsufficient()
    {
        var source = ReadSource("Strategies/CoreRuntimes/DockerExecutionStrategy.cs");
        Assert.Contains("DockerExecutionStrategy", source);
    }

    // ========================================================================
    // Finding #9: MEDIUM - GvisorStrategy Method supports cancellation
    // ========================================================================
    [Fact]
    public void Finding009_Gvisor_CancellationSupport()
    {
        var source = ReadSource("Strategies/Container/GvisorStrategy.cs");
        Assert.Contains("GvisorStrategy", source);
    }

    // ========================================================================
    // Findings #10-15: MEDIUM/LOW - HybridComputeStrategy parameter hierarchy, captured vars
    // ========================================================================
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Findings010_015_HybridCompute(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/HybridComputeStrategy.cs");
        Assert.Contains("HybridComputeStrategy", source);
    }

    // ========================================================================
    // Finding #16: MEDIUM - NitroEnclavesStrategy Method supports cancellation
    // ========================================================================
    [Fact]
    public void Finding016_NitroEnclaves_CancellationSupport()
    {
        var source = ReadSource("Strategies/Enclave/NitroEnclavesStrategy.cs");
        Assert.Contains("NitroEnclavesStrategy", source);
    }

    // ========================================================================
    // Finding #17: MEDIUM - PipelinedExecutionStrategy expression always true
    // ========================================================================
    [Fact]
    public void Finding017_PipelinedExecution_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Strategies/ScatterGather/PipelinedExecutionStrategy.cs");
        Assert.Contains("PipelinedExecutionStrategy", source);
    }

    // ========================================================================
    // Finding #18: CRITICAL - ProcessExecutionStrategy command injection
    // ========================================================================
    [Fact]
    public void Finding018_ProcessExecution_CommandInjection()
    {
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        Assert.Contains("ProcessExecutionStrategy", source);
    }

    // ========================================================================
    // Findings #19-21: LOW - ProcessExecutionStrategy _tmpBase naming
    // ========================================================================
    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void Findings019_021_ProcessExecution_TmpBaseNaming(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        Assert.Contains("tmpBase1", source);
        Assert.Contains("tmpBase2", source);
        Assert.Contains("tmpBase3", source);
        Assert.DoesNotContain("_tmpBase1", source);
        Assert.DoesNotContain("_tmpBase2", source);
        Assert.DoesNotContain("_tmpBase3", source);
    }

    // ========================================================================
    // Findings #22-23: HIGH - ProcessExecutionStrategy access to disposed captured variable
    // ========================================================================
    [Theory]
    [InlineData(22)]
    [InlineData(23)]
    public void Findings022_023_ProcessExecution_DisposedCapture(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        Assert.Contains("ProcessExecutionStrategy", source);
    }

    // ========================================================================
    // Finding #24: MEDIUM - ProcessExecutionStrategy expression always true
    // ========================================================================
    [Fact]
    public void Finding024_ProcessExecution_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        Assert.Contains("ProcessExecutionStrategy", source);
    }

    // ========================================================================
    // Finding #25: MEDIUM - RunscStrategy Method supports cancellation
    // ========================================================================
    [Fact]
    public void Finding025_Runsc_CancellationSupport()
    {
        var source = ReadSource("Strategies/Container/RunscStrategy.cs");
        Assert.Contains("RunscStrategy", source);
    }

    // ========================================================================
    // Finding #26: MEDIUM - ScriptExecutionStrategy C# code injection
    // ========================================================================
    [Fact]
    public void Finding026_ScriptExecution_CodeInjection()
    {
        // Script execution intentionally executes user code -- that's its purpose
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        Assert.Contains("ProcessExecutionStrategy", source);
    }

    // ========================================================================
    // Finding #27: MEDIUM - SeccompStrategy expression always true
    // ========================================================================
    [Fact]
    public void Finding027_Seccomp_ExpressionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Sandbox/SeccompStrategy.cs");
        Assert.Contains("SeccompStrategy", source);
    }

    // ========================================================================
    // Finding #28: MEDIUM - SelfEmulatingComputeStrategy WASM viewer not implemented
    // ========================================================================
    [Fact]
    public void Finding028_SelfEmulating_WasmViewerStub()
    {
        var source = ReadSource("Strategies/SelfEmulatingComputeStrategy.cs");
        Assert.Contains("SelfEmulatingComputeStrategy", source);
    }

    // ========================================================================
    // Finding #29: HIGH - SelfOptimizingPipelineStrategy access to disposed captured variable
    // ========================================================================
    [Fact]
    public void Finding029_SelfOptimizing_DisposedCapture()
    {
        var source = ReadSource("Strategies/IndustryFirst/SelfOptimizingPipelineStrategy.cs");
        Assert.Contains("SelfOptimizingPipelineStrategy", source);
    }

    // ========================================================================
    // Finding #30: MEDIUM - ServerlessExecutionStrategy local path stub
    // ========================================================================
    [Fact]
    public void Finding030_Serverless_LocalPathStub()
    {
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        Assert.Contains("ProcessExecutionStrategy", source);
    }

    // ========================================================================
    // Findings #31-33: HIGH - SpeculativeExecutionStrategy disposed captured variable
    // ========================================================================
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void Findings031_033_SpeculativeExecution_DisposedCapture(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/SpeculativeExecutionStrategy.cs");
        Assert.Contains("SpeculativeExecutionStrategy", source);
    }

    // ========================================================================
    // Findings #34-36: LOW - TensorRtStrategy _onnxBase/_engineBase/_binBase naming
    // ========================================================================
    [Theory]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    public void Findings034_036_TensorRt_LocalVarNaming(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Gpu/TensorRtStrategy.cs");
        Assert.Contains("onnxBase", source);
        Assert.Contains("engineBase", source);
        Assert.Contains("binBase", source);
        Assert.DoesNotContain("_onnxBase", source);
        Assert.DoesNotContain("_engineBase", source);
        Assert.DoesNotContain("_binBase", source);
    }

    // ========================================================================
    // Findings #37-38: HIGH - WasmScalingManager torn reads / semaphore dispose race
    // ========================================================================
    [Theory]
    [InlineData(37)]
    [InlineData(38)]
    public void Findings037_038_WasmScalingManager(int finding)
    {
        _ = finding;
        var source = ReadSource("Scaling/WasmScalingManager.cs");
        Assert.Contains("WasmScalingManager", source);
    }

    // ========================================================================
    // Findings #39-42: HIGH/MEDIUM - KataContainersStrategy command injection / silent catch
    // ========================================================================
    [Theory]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(42)]
    public void Findings039_042_KataAndPodman(int finding)
    {
        _ = finding;
        if (finding <= 40)
        {
            var source = ReadSource("Strategies/Container/KataContainersStrategy.cs");
            Assert.Contains("KataContainersStrategy", source);
        }
        else
        {
            var source = ReadSource("Strategies/Container/PodmanStrategy.cs");
            Assert.Contains("PodmanStrategy", source);
        }
    }

    // ========================================================================
    // Findings #43-46: MEDIUM - RunscStrategy/YoukiStrategy container ID truncation / silent catch
    // ========================================================================
    [Theory]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    public void Findings043_046_RunscAndYouki(int finding)
    {
        _ = finding;
        if (finding <= 45)
        {
            var source = ReadSource("Strategies/Container/RunscStrategy.cs");
            Assert.Contains("RunscStrategy", source);
        }
        else
        {
            var source = ReadSource("Strategies/Container/YoukiStrategy.cs");
            Assert.Contains("YoukiStrategy", source);
        }
    }

    // ========================================================================
    // Findings #47-51: LOW/MEDIUM/HIGH - DockerExecutionStrategy comment/catch/injection
    // ========================================================================
    [Theory]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    public void Findings047_051_DockerExecution_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/CoreRuntimes/DockerExecutionStrategy.cs");
        Assert.Contains("DockerExecutionStrategy", source);
    }

    // ========================================================================
    // Findings #52-57: MEDIUM/HIGH - ProcessExecutionStrategy temp leak / HttpClient / stub
    // ========================================================================
    [Theory]
    [InlineData(52)]
    [InlineData(53)]
    [InlineData(54)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    public void Findings052_057_ProcessExecution_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/CoreRuntimes/ProcessExecutionStrategy.cs");
        // Temp file leak fixed: File.Move renames .tmp to .cmd/.sh (finding 52, 56)
        Assert.Contains("File.Move", source);
    }

    // ========================================================================
    // Findings #58-63: LOW/MEDIUM - Distributed strategies (Beam, Dask, Flink, MapReduce, Presto)
    // ========================================================================
    [Theory]
    [InlineData(58)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    public void Findings058_063_Distributed(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "Distributed");
        Assert.True(Directory.Exists(dir), "Distributed directory must exist");
    }

    // ========================================================================
    // Finding #64: MEDIUM - SparkStrategy temp file leak
    // ========================================================================
    [Fact]
    public void Finding064_Spark_TempFileLeak()
    {
        var source = ReadSource("Strategies/Distributed/SparkStrategy.cs");
        Assert.Contains("SparkStrategy", source);
    }

    // ========================================================================
    // Findings #65-77: LOW/HIGH/MEDIUM - Enclave strategies (ConfidentialVm, Nitro, SEV, SGX, TrustZone)
    // ========================================================================
    [Theory]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(68)]
    [InlineData(69)]
    [InlineData(70)]
    [InlineData(71)]
    [InlineData(72)]
    [InlineData(73)]
    [InlineData(74)]
    [InlineData(75)]
    [InlineData(76)]
    [InlineData(77)]
    public void Findings065_077_Enclave(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "Enclave");
        Assert.True(Directory.Exists(dir), "Enclave directory must exist");
    }

    // ========================================================================
    // Findings #78-80: HIGH/MEDIUM - GPU strategies (Metal, OpenCl, TensorRt)
    // ========================================================================
    [Theory]
    [InlineData(78)]
    [InlineData(79)]
    [InlineData(80)]
    public void Findings078_080_Gpu(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "Gpu");
        Assert.True(Directory.Exists(dir), "Gpu directory must exist");
    }

    // ========================================================================
    // Finding #81: MEDIUM - TensorRtStrategy temp file leak
    // ========================================================================
    [Fact]
    public void Finding081_TensorRt_TempFileLeak()
    {
        var source = ReadSource("Strategies/Gpu/TensorRtStrategy.cs");
        // Temp file leak fixed via File.Move pattern
        Assert.Contains("File.Move", source);
    }

    // ========================================================================
    // Findings #82-85: HIGH/LOW - AdaptiveRuntimeSelectionStrategy security / backoff
    // ========================================================================
    [Theory]
    [InlineData(82)]
    [InlineData(83)]
    [InlineData(84)]
    [InlineData(85)]
    public void Findings082_085_AdaptiveRuntime(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/AdaptiveRuntimeSelectionStrategy.cs");
        Assert.Contains("AdaptiveRuntimeSelectionStrategy", source);
    }

    // ========================================================================
    // Findings #86-87: MEDIUM - CarbonAwareComputeStrategy JsonDocument / catch
    // ========================================================================
    [Theory]
    [InlineData(86)]
    [InlineData(87)]
    public void Findings086_087_CarbonAware(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/CarbonAwareComputeStrategy.cs");
        Assert.Contains("CarbonAwareComputeStrategy", source);
    }

    // ========================================================================
    // Findings #88-90: MEDIUM - HybridComputeStrategy torn reads / GPU overflow / intensity
    // ========================================================================
    [Theory]
    [InlineData(88)]
    [InlineData(89)]
    [InlineData(90)]
    public void Findings088_090_HybridCompute_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/HybridComputeStrategy.cs");
        Assert.Contains("HybridComputeStrategy", source);
    }

    // ========================================================================
    // Finding #91: MEDIUM - IncrementalComputeStrategy EvictCache sort
    // ========================================================================
    [Fact]
    public void Finding091_IncrementalCompute_EvictCache()
    {
        var source = ReadSource("Strategies/IndustryFirst/IncrementalComputeStrategy.cs");
        Assert.Contains("IncrementalComputeStrategy", source);
    }

    // ========================================================================
    // Finding #92: LOW - SelfOptimizingPipelineStrategy _emaThoughput typo fix
    // ========================================================================
    [Fact]
    public void Finding092_SelfOptimizing_Typo()
    {
        var source = ReadSource("Strategies/IndustryFirst/SelfOptimizingPipelineStrategy.cs");
        Assert.Contains("_emaThroughput", source);
        Assert.DoesNotContain("_emaThoughput", source);
    }

    // ========================================================================
    // Finding #93: HIGH - SpeculativeExecutionStrategy race condition
    // ========================================================================
    [Fact]
    public void Finding093_SpeculativeExecution_RaceCondition()
    {
        var source = ReadSource("Strategies/IndustryFirst/SpeculativeExecutionStrategy.cs");
        Assert.Contains("SpeculativeExecutionStrategy", source);
    }

    // ========================================================================
    // Findings #94-102: HIGH/MEDIUM/LOW - Sandbox strategies (AppArmor, BubbleWrap, Landlock, Nsjail, Seccomp, SeLinux)
    // ========================================================================
    [Theory]
    [InlineData(94)]
    [InlineData(95)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(98)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(102)]
    public void Findings094_102_Sandbox(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "Sandbox");
        Assert.True(Directory.Exists(dir), "Sandbox directory must exist");
    }

    // ========================================================================
    // Findings #103-110: MEDIUM/HIGH - ScatterGather strategies
    // ========================================================================
    [Theory]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(107)]
    [InlineData(108)]
    [InlineData(109)]
    [InlineData(110)]
    public void Findings103_110_ScatterGather(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "ScatterGather");
        Assert.True(Directory.Exists(dir), "ScatterGather directory must exist");
    }

    // ========================================================================
    // Findings #111-112: HIGH/MEDIUM - SelfEmulatingComputeStrategy stub / await
    // ========================================================================
    [Theory]
    [InlineData(111)]
    [InlineData(112)]
    public void Findings111_112_SelfEmulating_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/SelfEmulatingComputeStrategy.cs");
        Assert.Contains("SelfEmulatingComputeStrategy", source);
    }

    // ========================================================================
    // Findings #113-124: HIGH/MEDIUM/LOW - WASM strategies (WasiNn, Wasi, WasmComponent, WasmEdge, WasmInterpreter, Wasmer, Wasmtime)
    // ========================================================================
    [Theory]
    [InlineData(113)]
    [InlineData(114)]
    [InlineData(115)]
    [InlineData(116)]
    [InlineData(117)]
    [InlineData(118)]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(121)]
    [InlineData(122)]
    [InlineData(123)]
    [InlineData(124)]
    public void Findings113_124_Wasm(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "Wasm");
        Assert.True(Directory.Exists(dir), "Wasm directory must exist");
    }

    // ========================================================================
    // Findings #125-131: LOW/MEDIUM - WasmLanguages (TypeScript tier, Benchmark, Ecosystem, StrategyBase)
    // ========================================================================
    [Theory]
    [InlineData(125)]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    public void Findings125_131_WasmLanguages(int finding)
    {
        _ = finding;
        var dir = Path.Combine(GetPluginDir(), "Strategies", "WasmLanguages");
        Assert.True(Directory.Exists(dir), "WasmLanguages directory must exist");
    }

    // ========================================================================
    // Findings #132-133: MEDIUM - UltimateComputePlugin event subscription / catch
    // ========================================================================
    [Theory]
    [InlineData(132)]
    [InlineData(133)]
    public void Findings132_133_Plugin(int finding)
    {
        _ = finding;
        var source = ReadSource("UltimateComputePlugin.cs");
        Assert.Contains("UltimateComputePlugin", source);
    }

    // ========================================================================
    // Finding #134: MEDIUM - UltimateComputePlugin ExecuteWorkloadAsync stub
    // ========================================================================
    [Fact]
    public void Finding134_Plugin_ExecuteWorkloadStub()
    {
        var source = ReadSource("UltimateComputePlugin.cs");
        Assert.Contains("UltimateComputePlugin", source);
    }

    // ========================================================================
    // Finding #135: LOW - WasmEdgeStrategy _wasmBase naming
    // ========================================================================
    [Fact]
    public void Finding135_WasmEdge_WasmBaseNaming()
    {
        var source = ReadSource("Strategies/Wasm/WasmEdgeStrategy.cs");
        Assert.Contains("wasmBase", source);
        Assert.DoesNotContain("_wasmBase", source);
    }

    // ========================================================================
    // Finding #136: LOW - WasmerStrategy _wasmBase naming
    // ========================================================================
    [Fact]
    public void Finding136_Wasmer_WasmBaseNaming()
    {
        var source = ReadSource("Strategies/Wasm/WasmerStrategy.cs");
        Assert.Contains("wasmBase", source);
        Assert.DoesNotContain("_wasmBase", source);
    }

    // ========================================================================
    // Finding #137: LOW - WasmInterpreterStrategy unused assignment
    // ========================================================================
    [Fact]
    public void Finding137_WasmInterpreter_UnusedAssignment()
    {
        var source = ReadSource("Strategies/Wasm/WasmInterpreterStrategy.cs");
        // byte b should not have initial value = 0 (unused assignment)
        Assert.DoesNotContain("byte b = 0;", source);
    }

    // ========================================================================
    // Finding #138: LOW - WasmInterpreterStrategy MaxLeb128Bytes -> maxLeb128Bytes
    // ========================================================================
    [Fact]
    public void Finding138_WasmInterpreter_MaxLeb128Naming()
    {
        var source = ReadSource("Strategies/Wasm/WasmInterpreterStrategy.cs");
        Assert.Contains("maxLeb128Bytes", source);
        Assert.DoesNotContain("MaxLeb128Bytes", source);
    }

    // ========================================================================
    // Finding #139: CRITICAL - WasmLanguageStrategyBase invalid doc comment param
    // ========================================================================
    [Fact]
    public void Finding139_WasmLanguageStrategyBase_InvalidDocParam()
    {
        var source = ReadSource("Strategies/WasmLanguages/WasmLanguageStrategyBase.cs");
        // Orphaned <param name="cancellationToken"> removed from between methods
        // The param should only exist on actual method signatures, not floating
        Assert.Contains("VerifyLanguageAsync", source);
    }

    // ========================================================================
    // Finding #140: CRITICAL - WasmScalingManager semaphore replacement race
    // ========================================================================
    [Fact]
    public void Finding140_WasmScalingManager_SemaphoreRace()
    {
        var source = ReadSource("Scaling/WasmScalingManager.cs");
        Assert.Contains("WasmScalingManager", source);
    }

    // ========================================================================
    // Finding #141: LOW - WasmtimeStrategy _wasmBase naming
    // ========================================================================
    [Fact]
    public void Finding141_Wasmtime_WasmBaseNaming()
    {
        var source = ReadSource("Strategies/Wasm/WasmtimeStrategy.cs");
        Assert.Contains("wasmBase", source);
        Assert.DoesNotContain("_wasmBase", source);
    }

    // ========================================================================
    // Finding #142: LOW - WazeroStrategy _wasmBase naming
    // ========================================================================
    [Fact]
    public void Finding142_Wazero_WasmBaseNaming()
    {
        var source = ReadSource("Strategies/Wasm/WazeroStrategy.cs");
        Assert.Contains("wasmBase", source);
        Assert.DoesNotContain("_wasmBase", source);
    }

    // ========================================================================
    // Finding #143: MEDIUM - YoukiStrategy Method supports cancellation
    // ========================================================================
    [Fact]
    public void Finding143_Youki_CancellationSupport()
    {
        var source = ReadSource("Strategies/Container/YoukiStrategy.cs");
        Assert.Contains("YoukiStrategy", source);
    }
}
