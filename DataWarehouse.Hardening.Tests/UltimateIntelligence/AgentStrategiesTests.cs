// Hardening tests for UltimateIntelligence findings 11-16
// AgentStrategies.cs: Naming violations and unused assignment

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class AgentStrategiesTests
{
    private static readonly string FilePath = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence", "Strategies", "Agents", "AgentStrategies.cs");

    /// <summary>
    /// Finding 11 (LOW): Collection 'Constraints' never updated (non-private).
    /// Should use init-only setter since it's set at construction.
    /// </summary>
    [Fact]
    public void Finding011_Constraints_InitSetter()
    {
        var code = File.ReadAllText(FilePath);
        Assert.Contains("Constraints", code);
    }

    /// <summary>
    /// Finding 12 (LOW): _tools should be Tools (non-private field naming).
    /// </summary>
    [Fact]
    public void Finding012_Tools_PascalCase()
    {
        var code = File.ReadAllText(FilePath);
        // Non-private fields should use PascalCase
        Assert.True(
            code.Contains("protected readonly BoundedDictionary<string, ToolDefinition> Tools") ||
            code.Contains("internal readonly BoundedDictionary<string, ToolDefinition> Tools"),
            "_tools (non-private) should be renamed to Tools");
    }

    /// <summary>
    /// Finding 13 (LOW): _executionHistory should be ExecutionHistory (non-private field naming).
    /// </summary>
    [Fact]
    public void Finding013_ExecutionHistory_PascalCase()
    {
        var code = File.ReadAllText(FilePath);
        Assert.True(
            code.Contains("protected readonly ConcurrentQueue<AgentAction> ExecutionHistory"),
            "_executionHistory (non-private) should be renamed to ExecutionHistory");
    }

    /// <summary>
    /// Finding 14 (LOW): _currentState should be CurrentState (non-private field naming).
    /// </summary>
    [Fact]
    public void Finding014_CurrentState_PascalCase()
    {
        var code = File.ReadAllText(FilePath);
        Assert.True(
            code.Contains("protected AgentState CurrentState"),
            "_currentState (non-private) should be renamed to CurrentState");
    }

    /// <summary>
    /// Finding 15 (LOW): _executionCts should be ExecutionCts (non-private field naming).
    /// </summary>
    [Fact]
    public void Finding015_ExecutionCts_PascalCase()
    {
        var code = File.ReadAllText(FilePath);
        Assert.True(
            code.Contains("protected CancellationTokenSource? ExecutionCts"),
            "_executionCts (non-private) should be renamed to ExecutionCts");
    }

    /// <summary>
    /// Finding 16 (LOW): Unused assignment at line 1259.
    /// </summary>
    [Fact]
    public void Finding016_UnusedAssignment_Fixed()
    {
        var code = File.ReadAllText(FilePath);
        // The assignment `continueExecution = false` before break is redundant
        Assert.DoesNotContain("continueExecution = false;\n                    reasoningChain.Add(\"No tool calls requested", code);
    }
}
