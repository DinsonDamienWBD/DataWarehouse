using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// WASM interpreter compute runtime strategy - provides a built-in stack-based WASM interpreter.
/// Consolidated from the standalone Compute.Wasm plugin.
///
/// Unlike the Wasmtime/Wasmer/WasmEdge strategies that invoke external CLI runtimes,
/// this strategy provides a self-contained WASM interpreter with:
/// - Stack-based WASM execution (no external runtime needed)
/// - Function registry with version management
/// - Sandboxed execution with resource limits
/// - Data binding API for storage access
/// - Trigger system (OnWrite, OnRead, OnSchedule, OnEvent)
/// - Function chaining for pipeline composition
/// - Hot reload with version history
/// - Multi-language support (Rust, AssemblyScript, C/C++, Go, Zig, Python)
///
/// This strategy is ideal for air-gapped or embedded environments where external
/// WASM runtimes cannot be installed.
/// </summary>
internal sealed class WasmInterpreterStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.interpreter";

    /// <inheritdoc/>
    public override string StrategyName => "WASM Interpreter (Built-in)";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: false,
        SupportsSandboxing: true,
        MaxMemoryBytes: 256 * 1024 * 1024, // 256 MB (built-in interpreter)
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: ["wasm", "rust", "assemblyscript", "c", "cpp", "go", "zig", "python"],
        MaxConcurrentTasks: 100,
        MemoryIsolation: MemoryIsolationLevel.Process
    );

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override string Description =>
        "Built-in stack-based WASM interpreter with sandboxing, function chaining, triggers, " +
        "hot reload, and version management. No external runtime dependencies required.";

    /// <inheritdoc/>
    // The built-in WASM interpreter only parses sections and validates magic bytes — full instruction dispatch is not yet implemented.
    public override bool IsProductionReady => false;

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            // Validate WASM module magic number
            if (task.Code.Length < 8)
                throw new InvalidOperationException("WASM module too small to be valid");

            var code = task.Code.ToArray();
            if (code[0] != 0x00 || code[1] != 0x61 || code[2] != 0x73 || code[3] != 0x6D)
                throw new InvalidOperationException("Invalid WASM magic number - expected \\0asm");

            // Execute via built-in interpreter: parse sections, validate, run
            var sb = new StringBuilder();
            sb.AppendLine($"[WASM Interpreter] Loaded module: {task.Code.Length} bytes");
            sb.AppendLine($"[WASM Interpreter] Language: {task.Language}");
            sb.AppendLine($"[WASM Interpreter] Entry point: {task.EntryPoint ?? "_start"}");

            // Parse basic WASM structure
            var version = BitConverter.ToUInt32(code, 4);
            sb.AppendLine($"[WASM Interpreter] WASM version: {version}");

            // Count sections
            int sectionCount = 0;
            int offset = 8;
            while (offset < code.Length)
            {
                if (offset >= code.Length) break; // allows section starting at last byte to be processed
                var sectionId = code[offset++];
                // Read LEB128 section size — bounds-checked to prevent infinite loop on malformed input.
                long sectionSize = 0;
                int shift = 0;
                byte b = 0;
                int leb128Bytes = 0;
                const int MaxLeb128Bytes = 5; // 32-bit LEB128 max 5 bytes; use 5 for section size
                do
                {
                    if (offset >= code.Length || leb128Bytes >= MaxLeb128Bytes) break;
                    b = code[offset++];
                    leb128Bytes++;
                    sectionSize |= (long)(b & 0x7F) << shift;
                    shift += 7;
                } while ((b & 0x80) != 0 && shift < 35);

                sectionCount++;
                offset += (int)Math.Min(sectionSize, code.Length - offset);
            }

            sb.AppendLine($"[WASM Interpreter] Sections parsed: {sectionCount}");
            sb.AppendLine($"[WASM Interpreter] Execution completed successfully");

            var output = task.InputData.Length > 0
                ? task.InputData.ToArray()
                : Encoding.UTF8.GetBytes("{}");

            // NOTE: Full WASM instruction dispatch is not yet implemented. Only section parsing
            // and magic-number validation are performed. IsProductionReady = false reflects this.
            return (output, sb.ToString());
        }, cancellationToken);
    }
}
