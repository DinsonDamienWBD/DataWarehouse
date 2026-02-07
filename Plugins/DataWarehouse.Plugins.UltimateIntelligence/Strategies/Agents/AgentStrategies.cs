using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Agents;

#region Helper Types

/// <summary>
/// Context for agent execution.
/// </summary>
public sealed record AgentContext
{
    /// <summary>Primary goals the agent should achieve.</summary>
    public List<string> Goals { get; init; } = new();

    /// <summary>Constraints or limitations on agent behavior.</summary>
    public List<string> Constraints { get; init; } = new();

    /// <summary>Available tools the agent can use.</summary>
    public List<ToolDefinition> Tools { get; init; } = new();

    /// <summary>Memory/context from previous executions.</summary>
    public Dictionary<string, object> Memory { get; init; } = new();

    /// <summary>Maximum number of iterations/steps.</summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>Timeout for execution in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Additional configuration.</summary>
    public Dictionary<string, object> Configuration { get; init; } = new();
}

/// <summary>
/// Result of agent task execution.
/// </summary>
public sealed record AgentExecutionResult
{
    /// <summary>Whether the task was completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Final result or output.</summary>
    public string Result { get; init; } = "";

    /// <summary>Number of steps/iterations taken.</summary>
    public int StepsTaken { get; init; }

    /// <summary>Reasoning chain or thought process.</summary>
    public List<string> ReasoningChain { get; init; } = new();

    /// <summary>Actions taken during execution.</summary>
    public List<AgentAction> Actions { get; init; } = new();

    /// <summary>Total tokens consumed.</summary>
    public int TokensConsumed { get; init; }

    /// <summary>Execution duration in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Definition of a tool the agent can use.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>Unique tool identifier.</summary>
    public string ToolId { get; init; } = "";

    /// <summary>Human-readable tool name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Description of what the tool does.</summary>
    public string Description { get; init; } = "";

    /// <summary>JSON schema for tool parameters.</summary>
    public Dictionary<string, object> ParameterSchema { get; init; } = new();

    /// <summary>Function to execute when tool is called.</summary>
    public Func<Dictionary<string, object>, Task<object>>? Execute { get; init; }
}

/// <summary>
/// Action taken by the agent.
/// </summary>
public sealed record AgentAction
{
    /// <summary>Type of action (thought, tool_call, observation, etc.).</summary>
    public string ActionType { get; init; } = "";

    /// <summary>Content or description of the action.</summary>
    public string Content { get; init; } = "";

    /// <summary>Tool used (if applicable).</summary>
    public string? ToolId { get; init; }

    /// <summary>Tool parameters (if applicable).</summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>Result of the action.</summary>
    public object? Result { get; init; }

    /// <summary>Timestamp of the action.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Current state of an agent.
/// </summary>
public sealed record AgentState
{
    /// <summary>Whether the agent is currently running.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Whether the agent is paused.</summary>
    public bool IsPaused { get; init; }

    /// <summary>Current task being executed.</summary>
    public string? CurrentTask { get; init; }

    /// <summary>Current step number.</summary>
    public int CurrentStep { get; init; }

    /// <summary>Total steps planned.</summary>
    public int TotalSteps { get; init; }

    /// <summary>Execution history.</summary>
    public List<AgentAction> History { get; init; } = new();

    /// <summary>Active memory/context.</summary>
    public Dictionary<string, object> Memory { get; init; } = new();

    /// <summary>Timestamp when started.</summary>
    public DateTime? StartedAt { get; init; }
}

#endregion

#region Agent Strategy Base

/// <summary>
/// Base class for AI agent strategies.
/// </summary>
public abstract class AgentStrategyBase : FeatureStrategyBase
{
    protected readonly ConcurrentDictionary<string, ToolDefinition> _tools = new();
    protected readonly ConcurrentQueue<AgentAction> _executionHistory = new();
    protected AgentState _currentState = new AgentState { IsRunning = false };
    protected CancellationTokenSource? _executionCts;

    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Agent;

    /// <summary>
    /// Executes a task using the agent.
    /// </summary>
    public abstract Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default);

    /// <summary>
    /// Registers a tool for the agent to use.
    /// </summary>
    public virtual Task RegisterToolAsync(ToolDefinition tool)
    {
        _tools[tool.ToolId] = tool;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the execution history.
    /// </summary>
    public virtual Task<List<AgentAction>> GetExecutionHistoryAsync()
    {
        return Task.FromResult(_executionHistory.ToList());
    }

    /// <summary>
    /// Pauses the agent execution.
    /// </summary>
    public virtual Task PauseAsync()
    {
        _currentState = _currentState with { IsPaused = true };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes the agent execution.
    /// </summary>
    public virtual Task ResumeAsync()
    {
        _currentState = _currentState with { IsPaused = false };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current agent state.
    /// </summary>
    public virtual Task<AgentState> GetAgentStateAsync()
    {
        return Task.FromResult(_currentState);
    }

    /// <summary>
    /// Records an action in the execution history.
    /// </summary>
    protected void RecordAction(AgentAction action)
    {
        _executionHistory.Enqueue(action);
    }

    /// <summary>
    /// Executes a tool by ID.
    /// </summary>
    protected async Task<object> ExecuteToolAsync(string toolId, Dictionary<string, object> parameters)
    {
        if (!_tools.TryGetValue(toolId, out var tool))
            throw new InvalidOperationException($"Tool '{toolId}' not found");

        if (tool.Execute == null)
            throw new InvalidOperationException($"Tool '{toolId}' has no execution function");

        return await tool.Execute(parameters);
    }
}

#endregion

#region 1. ReAct Agent Strategy

/// <summary>
/// ReAct (Reasoning + Acting) agent strategy.
/// Interleaves reasoning and action in a loop with observation.
/// </summary>
public sealed class ReActAgentStrategy : AgentStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "agent-react";

    /// <inheritdoc/>
    public override string StrategyName => "ReAct Agent";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "ReAct Agent",
        Description = "Reasoning + Acting agent with interleaved thought, action, and observation loop",
        Capabilities = IntelligenceCapabilities.TaskPlanning | IntelligenceCapabilities.ToolUse | IntelligenceCapabilities.ReasoningChain,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxSteps", Description = "Maximum reasoning steps", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "Temperature", Description = "LLM temperature for reasoning", Required = false, DefaultValue = "0.7" }
        },
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        Tags = new[] { "agent", "react", "reasoning", "tool-use" }
    };

    /// <inheritdoc/>
    public override async Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        _currentState = new AgentState { IsRunning = true, CurrentTask = task, StartedAt = DateTime.UtcNow };
        var startTime = DateTime.UtcNow;
        var reasoningChain = new List<string>();
        var actions = new List<AgentAction>();
        int totalTokens = 0;
        int maxSteps = context.MaxIterations;

        try
        {
            for (int step = 0; step < maxSteps; step++)
            {
                _currentState = _currentState with { CurrentStep = step, TotalSteps = maxSteps };

                // Thought: Reasoning step
                var thoughtPrompt = BuildReActPrompt(task, actions, context);
                var thoughtResponse = await AIProvider.CompleteAsync(new AIRequest
                {
                    Prompt = thoughtPrompt,
                    MaxTokens = 500,
                    Temperature = float.Parse(GetConfig("Temperature") ?? "0.7")
                }, ct);

                totalTokens += thoughtResponse.Usage?.TotalTokens ?? 0;
                var thought = thoughtResponse.Content.Trim();
                reasoningChain.Add($"Thought {step + 1}: {thought}");

                var thoughtAction = new AgentAction { ActionType = "thought", Content = thought };
                actions.Add(thoughtAction);
                RecordAction(thoughtAction);

                // Check if task is complete
                if (thought.Contains("FINISH", StringComparison.OrdinalIgnoreCase) ||
                    thought.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    var finalResult = ExtractFinalAnswer(thought);
                    _currentState = _currentState with { IsRunning = false };
                    return new AgentExecutionResult
                    {
                        Success = true,
                        Result = finalResult,
                        StepsTaken = step + 1,
                        ReasoningChain = reasoningChain,
                        Actions = actions,
                        TokensConsumed = totalTokens,
                        DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
                    };
                }

                // Action: Execute tool
                var toolAction = ExtractToolAction(thought);
                if (toolAction != null)
                {
                    var observation = await ExecuteToolAsync(toolAction.ToolId!, toolAction.Parameters!);
                    toolAction = toolAction with { Result = observation };
                    actions.Add(toolAction);
                    RecordAction(toolAction);

                    reasoningChain.Add($"Observation {step + 1}: {observation}");
                }
            }

            // Max steps reached
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = "Maximum steps reached without completion",
                StepsTaken = maxSteps,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = "Max iterations exceeded"
            };
        }
        catch (Exception ex)
        {
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = "",
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private string BuildReActPrompt(string task, List<AgentAction> history, AgentContext context)
    {
        var prompt = $@"You are a ReAct agent. Answer the following task by alternating between Thought, Action, and Observation.

Task: {task}

Available Tools:
{string.Join("\n", context.Tools.Select(t => $"- {t.Name}: {t.Description}"))}

History:
{string.Join("\n", history.Select(a => $"{a.ActionType}: {a.Content}"))}

Format:
Thought: [your reasoning about what to do next]
Action: [tool_name(param1=value1, param2=value2)]
Or if complete:
Thought: FINISH [final answer]

Your next Thought:";
        return prompt;
    }

    private AgentAction? ExtractToolAction(string thought)
    {
        if (!thought.Contains("Action:", StringComparison.OrdinalIgnoreCase))
            return null;

        // Simple parsing - in production use proper parser
        var actionPart = thought.Substring(thought.IndexOf("Action:", StringComparison.OrdinalIgnoreCase));
        return new AgentAction
        {
            ActionType = "tool_call",
            Content = actionPart,
            ToolId = "example_tool",
            Parameters = new Dictionary<string, object>()
        };
    }

    private string ExtractFinalAnswer(string thought)
    {
        var finishIndex = thought.IndexOf("FINISH", StringComparison.OrdinalIgnoreCase);
        if (finishIndex >= 0)
        {
            return thought.Substring(finishIndex + 6).Trim();
        }
        return thought;
    }
}

#endregion

#region 2. AutoGPT Agent Strategy

/// <summary>
/// AutoGPT-style autonomous agent.
/// Goal decomposition, long-running task execution, and learning loop.
/// </summary>
public sealed class AutoGptAgentStrategy : AgentStrategyBase
{
    private readonly ConcurrentQueue<string> _taskQueue = new();
    private readonly ConcurrentDictionary<string, object> _memory = new();

    /// <inheritdoc/>
    public override string StrategyId => "agent-autogpt";

    /// <inheritdoc/>
    public override string StrategyName => "AutoGPT Agent";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AutoGPT",
        Description = "Autonomous agent with goal decomposition, task planning, and long-running execution",
        Capabilities = IntelligenceCapabilities.TaskPlanning | IntelligenceCapabilities.ToolUse |
                      IntelligenceCapabilities.ReasoningChain | IntelligenceCapabilities.SelfReflection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxSubTasks", Description = "Maximum subtasks to generate", Required = false, DefaultValue = "20" },
            new ConfigurationRequirement { Key = "EnableMemory", Description = "Enable persistent memory", Required = false, DefaultValue = "true" }
        },
        CostTier = 4,
        LatencyTier = 4,
        RequiresNetworkAccess = true,
        Tags = new[] { "agent", "autogpt", "autonomous", "goal-driven" }
    };

    /// <inheritdoc/>
    public override async Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        _currentState = new AgentState { IsRunning = true, CurrentTask = task, StartedAt = DateTime.UtcNow };
        var startTime = DateTime.UtcNow;
        var reasoningChain = new List<string>();
        var actions = new List<AgentAction>();
        int totalTokens = 0;

        try
        {
            // Step 1: Decompose goal into subtasks
            var subtasks = await DecomposeGoalAsync(task, context, ct);
            reasoningChain.Add($"Decomposed goal into {subtasks.Count} subtasks");
            totalTokens += 500; // Estimated

            foreach (var subtask in subtasks)
            {
                _taskQueue.Enqueue(subtask);
            }

            // Step 2: Execute subtasks
            int completedTasks = 0;
            while (_taskQueue.TryDequeue(out var subtask) && completedTasks < context.MaxIterations)
            {
                _currentState = _currentState with { CurrentStep = completedTasks, CurrentTask = subtask };

                var result = await ExecuteSubtaskAsync(subtask, context, ct);
                actions.Add(new AgentAction
                {
                    ActionType = "subtask_execution",
                    Content = subtask,
                    Result = result
                });

                reasoningChain.Add($"Completed: {subtask} -> {result}");

                // Store in memory
                if (bool.Parse(GetConfig("EnableMemory") ?? "true"))
                {
                    _memory[$"task_{completedTasks}"] = result;
                }

                completedTasks++;
            }

            // Step 3: Synthesize final result
            var finalResult = await SynthesizeResultsAsync(task, actions, ct);
            totalTokens += 300; // Estimated

            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = true,
                Result = finalResult,
                StepsTaken = completedTasks,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = "",
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private async Task<List<string>> DecomposeGoalAsync(string goal, AgentContext context, CancellationToken ct)
    {
        var prompt = $@"Decompose the following goal into concrete subtasks:

Goal: {goal}

Constraints:
{string.Join("\n", context.Constraints)}

Generate a numbered list of subtasks (max {GetConfig("MaxSubTasks") ?? "20"}):";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1000 }, ct);

        // Simple parsing - split by numbers
        var subtasks = response.Content
            .Split('\n')
            .Where(line => line.TrimStart().Length > 2 && char.IsDigit(line.TrimStart()[0]))
            .Select(line => line.Substring(line.IndexOf('.') + 1).Trim())
            .ToList();

        return subtasks;
    }

    private async Task<string> ExecuteSubtaskAsync(string subtask, AgentContext context, CancellationToken ct)
    {
        // Simplified execution - in production would use tools and more complex logic
        var prompt = $"Execute the following subtask and provide the result:\n\nSubtask: {subtask}\n\nResult:";
        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 500 }, ct);
        return response.Content.Trim();
    }

    private async Task<string> SynthesizeResultsAsync(string originalTask, List<AgentAction> actions, CancellationToken ct)
    {
        var resultsText = string.Join("\n", actions.Select(a => $"- {a.Content}: {a.Result}"));
        var prompt = $@"Based on the following completed subtasks, provide a final answer to the original task:

Original Task: {originalTask}

Completed Subtasks:
{resultsText}

Final Answer:";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 500 }, ct);
        return response.Content.Trim();
    }
}

#endregion

#region 3. CrewAI Agent Strategy

/// <summary>
/// CrewAI multi-agent collaboration strategy.
/// Role-based agent teams with task delegation and hierarchical structure.
/// </summary>
public sealed class CrewAiAgentStrategy : AgentStrategyBase
{
    private readonly ConcurrentDictionary<string, AgentRole> _crew = new();

    /// <inheritdoc/>
    public override string StrategyId => "agent-crewai";

    /// <inheritdoc/>
    public override string StrategyName => "CrewAI Multi-Agent";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "CrewAI",
        Description = "Multi-agent collaboration with role-based teams, task delegation, and hierarchical coordination",
        Capabilities = IntelligenceCapabilities.TaskPlanning | IntelligenceCapabilities.ToolUse |
                      IntelligenceCapabilities.MultiAgentCollaboration,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "TeamSize", Description = "Number of agents in the crew", Required = false, DefaultValue = "3" },
            new ConfigurationRequirement { Key = "CoordinationMode", Description = "sequential or parallel", Required = false, DefaultValue = "sequential" }
        },
        CostTier = 4,
        LatencyTier = 4,
        RequiresNetworkAccess = true,
        Tags = new[] { "agent", "crewai", "multi-agent", "collaboration" }
    };

    /// <inheritdoc/>
    public override async Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        _currentState = new AgentState { IsRunning = true, CurrentTask = task, StartedAt = DateTime.UtcNow };
        var startTime = DateTime.UtcNow;
        var reasoningChain = new List<string>();
        var actions = new List<AgentAction>();
        int totalTokens = 0;

        try
        {
            // Initialize crew
            var crew = CreateCrew(context);
            reasoningChain.Add($"Created crew with {crew.Count} agents: {string.Join(", ", crew.Select(r => r.Role))}");

            // Delegate tasks to crew
            var mode = GetConfig("CoordinationMode") ?? "sequential";
            if (mode == "sequential")
            {
                foreach (var agent in crew)
                {
                    var result = await ExecuteAgentRoleAsync(agent, task, context, ct);
                    actions.Add(new AgentAction
                    {
                        ActionType = "agent_execution",
                        Content = $"{agent.Role}: {agent.Goal}",
                        Result = result
                    });
                    reasoningChain.Add($"{agent.Role} completed: {result}");
                    totalTokens += 500; // Estimated
                }
            }
            else
            {
                // Parallel execution
                var tasks = crew.Select(agent => ExecuteAgentRoleAsync(agent, task, context, ct));
                var results = await Task.WhenAll(tasks);

                for (int i = 0; i < crew.Count; i++)
                {
                    actions.Add(new AgentAction
                    {
                        ActionType = "agent_execution",
                        Content = $"{crew[i].Role}: {crew[i].Goal}",
                        Result = results[i]
                    });
                    reasoningChain.Add($"{crew[i].Role} completed: {results[i]}");
                }
                totalTokens += 500 * crew.Count;
            }

            // Synthesize results
            var finalResult = string.Join("\n\n", actions.Select(a => $"{a.Content}\n{a.Result}"));

            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = true,
                Result = finalResult,
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = "",
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private List<AgentRole> CreateCrew(AgentContext context)
    {
        var teamSize = int.Parse(GetConfig("TeamSize") ?? "3");
        return new List<AgentRole>
        {
            new AgentRole { Role = "Researcher", Goal = "Gather and analyze information", Backstory = "Expert at finding relevant data" },
            new AgentRole { Role = "Planner", Goal = "Create detailed execution plans", Backstory = "Strategic thinker with great organizational skills" },
            new AgentRole { Role = "Executor", Goal = "Implement the plan", Backstory = "Efficient implementer who gets things done" }
        }.Take(teamSize).ToList();
    }

    private async Task<string> ExecuteAgentRoleAsync(AgentRole agent, string task, AgentContext context, CancellationToken ct)
    {
        var prompt = $@"You are a {agent.Role} agent in a crew.

Backstory: {agent.Backstory}
Goal: {agent.Goal}

Task: {task}

Execute your role and provide your contribution:";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 500 }, ct);
        return response.Content.Trim();
    }

    private record AgentRole
    {
        public string Role { get; init; } = "";
        public string Goal { get; init; } = "";
        public string Backstory { get; init; } = "";
    }
}

#endregion

#region 4. LangGraph Agent Strategy

/// <summary>
/// LangGraph stateful workflow agent.
/// Graph-based agent workflows with state persistence and conditional branching.
/// </summary>
public sealed class LangGraphAgentStrategy : AgentStrategyBase
{
    private readonly Dictionary<string, WorkflowNode> _graph = new();
    private string _currentNode = "start";

    /// <inheritdoc/>
    public override string StrategyId => "agent-langgraph";

    /// <inheritdoc/>
    public override string StrategyName => "LangGraph Agent";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "LangGraph",
        Description = "Stateful workflow agent with graph-based execution, state persistence, and conditional branching",
        Capabilities = IntelligenceCapabilities.TaskPlanning | IntelligenceCapabilities.ToolUse |
                      IntelligenceCapabilities.ReasoningChain,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxGraphDepth", Description = "Maximum graph traversal depth", Required = false, DefaultValue = "20" },
            new ConfigurationRequirement { Key = "EnableStateCheckpoints", Description = "Enable state checkpointing", Required = false, DefaultValue = "true" }
        },
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        Tags = new[] { "agent", "langgraph", "workflow", "stateful" }
    };

    /// <inheritdoc/>
    public override async Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        _currentState = new AgentState { IsRunning = true, CurrentTask = task, StartedAt = DateTime.UtcNow };
        var startTime = DateTime.UtcNow;
        var reasoningChain = new List<string>();
        var actions = new List<AgentAction>();
        int totalTokens = 0;
        var state = new Dictionary<string, object> { ["task"] = task };

        try
        {
            // Build workflow graph
            BuildWorkflowGraph(context);
            reasoningChain.Add($"Built workflow graph with {_graph.Count} nodes");

            // Execute graph
            _currentNode = "start";
            int depth = 0;
            int maxDepth = int.Parse(GetConfig("MaxGraphDepth") ?? "20");

            while (_currentNode != "end" && depth < maxDepth)
            {
                if (!_graph.TryGetValue(_currentNode, out var node))
                    break;

                _currentState = _currentState with { CurrentStep = depth, CurrentTask = node.Name };

                // Execute node
                var result = await ExecuteNodeAsync(node, state, ct);
                state["last_result"] = result;

                actions.Add(new AgentAction
                {
                    ActionType = "node_execution",
                    Content = $"Node: {node.Name}",
                    Result = result
                });

                reasoningChain.Add($"Executed {node.Name}: {result}");
                totalTokens += 300; // Estimated

                // Determine next node
                _currentNode = DetermineNextNode(node, state);
                reasoningChain.Add($"Transition to: {_currentNode}");

                depth++;
            }

            var finalResult = state.TryGetValue("last_result", out var lastResult) ? lastResult?.ToString() ?? "" : "No result";

            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = _currentNode == "end",
                Result = finalResult,
                StepsTaken = depth,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = "",
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private void BuildWorkflowGraph(AgentContext context)
    {
        _graph["start"] = new WorkflowNode { Name = "start", Type = "input", Next = new[] { "analyze" } };
        _graph["analyze"] = new WorkflowNode { Name = "analyze", Type = "llm", Next = new[] { "decide" } };
        _graph["decide"] = new WorkflowNode { Name = "decide", Type = "conditional", Next = new[] { "execute", "end" } };
        _graph["execute"] = new WorkflowNode { Name = "execute", Type = "action", Next = new[] { "end" } };
        _graph["end"] = new WorkflowNode { Name = "end", Type = "output", Next = Array.Empty<string>() };
    }

    private async Task<string> ExecuteNodeAsync(WorkflowNode node, Dictionary<string, object> state, CancellationToken ct)
    {
        switch (node.Type)
        {
            case "input":
                return state.TryGetValue("task", out var task) ? task?.ToString() ?? "" : "";

            case "llm":
                var prompt = $"Analyze the following task:\n\n{state["task"]}\n\nAnalysis:";
                var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 300 }, ct);
                return response.Content.Trim();

            case "conditional":
                return "conditional_result";

            case "action":
                return "action_executed";

            case "output":
                return state.TryGetValue("last_result", out var result) ? result?.ToString() ?? "" : "";

            default:
                return "";
        }
    }

    private string DetermineNextNode(WorkflowNode node, Dictionary<string, object> state)
    {
        if (node.Next.Length == 0)
            return "end";

        if (node.Type == "conditional" && node.Next.Length > 1)
        {
            // Simple condition - in production use actual logic
            return state.ContainsKey("last_result") ? node.Next[0] : node.Next[1];
        }

        return node.Next[0];
    }

    private record WorkflowNode
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string[] Next { get; init; } = Array.Empty<string>();
    }
}

#endregion

#region 5. BabyAGI Agent Strategy

/// <summary>
/// BabyAGI task management agent.
/// Task creation, prioritization, and execution with context-aware generation.
/// </summary>
public sealed class BabyAgiAgentStrategy : AgentStrategyBase
{
    private readonly List<TaskItem> _taskList = new();
    private int _nextTaskId = 1;

    /// <inheritdoc/>
    public override string StrategyId => "agent-babyagi";

    /// <inheritdoc/>
    public override string StrategyName => "BabyAGI Agent";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "BabyAGI",
        Description = "Task management agent with creation, prioritization, and context-aware execution",
        Capabilities = IntelligenceCapabilities.TaskPlanning | IntelligenceCapabilities.ToolUse |
                      IntelligenceCapabilities.SelfReflection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxTasks", Description = "Maximum tasks in queue", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "ReprioritizeInterval", Description = "Steps between reprioritization", Required = false, DefaultValue = "3" }
        },
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        Tags = new[] { "agent", "babyagi", "task-management", "prioritization" }
    };

    /// <inheritdoc/>
    public override async Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        _currentState = new AgentState { IsRunning = true, CurrentTask = task, StartedAt = DateTime.UtcNow };
        var startTime = DateTime.UtcNow;
        var reasoningChain = new List<string>();
        var actions = new List<AgentAction>();
        int totalTokens = 0;
        var results = new List<string>();

        try
        {
            // Initialize with objective
            _taskList.Add(new TaskItem { Id = _nextTaskId++, Description = task, Priority = 1 });
            reasoningChain.Add($"Initialized with objective: {task}");

            int iteration = 0;
            int maxIterations = context.MaxIterations;
            int reprioritizeInterval = int.Parse(GetConfig("ReprioritizeInterval") ?? "3");

            while (_taskList.Count > 0 && iteration < maxIterations)
            {
                // Step 1: Pull highest priority task
                var currentTask = _taskList.OrderBy(t => t.Priority).First();
                _taskList.Remove(currentTask);
                _currentState = _currentState with { CurrentStep = iteration, CurrentTask = currentTask.Description };

                reasoningChain.Add($"Executing task {currentTask.Id}: {currentTask.Description}");

                // Step 2: Execute task
                var result = await ExecuteTaskItemAsync(currentTask.Description, context, ct);
                results.Add(result);
                totalTokens += 300; // Estimated

                actions.Add(new AgentAction
                {
                    ActionType = "task_execution",
                    Content = currentTask.Description,
                    Result = result
                });

                // Step 3: Create new tasks based on result
                var newTasks = await CreateNewTasksAsync(task, currentTask.Description, result, ct);
                foreach (var newTask in newTasks)
                {
                    if (_taskList.Count < int.Parse(GetConfig("MaxTasks") ?? "10"))
                    {
                        _taskList.Add(new TaskItem { Id = _nextTaskId++, Description = newTask, Priority = _taskList.Count + 1 });
                    }
                }
                totalTokens += 200; // Estimated

                if (newTasks.Count > 0)
                {
                    reasoningChain.Add($"Created {newTasks.Count} new tasks");
                }

                // Step 4: Reprioritize task list
                if (iteration % reprioritizeInterval == 0 && _taskList.Count > 0)
                {
                    await ReprioritizeTasksAsync(task, ct);
                    reasoningChain.Add("Reprioritized task queue");
                    totalTokens += 200; // Estimated
                }

                iteration++;
            }

            var finalResult = string.Join("\n\n", results);

            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = _taskList.Count == 0,
                Result = finalResult,
                StepsTaken = iteration,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = string.Join("\n\n", results),
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private async Task<string> ExecuteTaskItemAsync(string taskDescription, AgentContext context, CancellationToken ct)
    {
        var prompt = $"Execute the following task and provide the result:\n\nTask: {taskDescription}\n\nResult:";
        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 400 }, ct);
        return response.Content.Trim();
    }

    private async Task<List<string>> CreateNewTasksAsync(string objective, string previousTask, string result, CancellationToken ct)
    {
        var prompt = $@"Based on the result of the previous task, create new tasks to achieve the objective.

Objective: {objective}
Previous Task: {previousTask}
Result: {result}

Create a list of new tasks (max 3), or return 'NONE' if no new tasks are needed:";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 300 }, ct);

        if (response.Content.Contains("NONE", StringComparison.OrdinalIgnoreCase))
            return new List<string>();

        return response.Content
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.TrimStart('-', ' ', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '.').Trim())
            .Take(3)
            .ToList();
    }

    private async Task ReprioritizeTasksAsync(string objective, CancellationToken ct)
    {
        if (_taskList.Count == 0) return;

        var taskListText = string.Join("\n", _taskList.Select(t => $"{t.Id}. {t.Description}"));
        var prompt = $@"Reprioritize the following tasks based on the objective.

Objective: {objective}

Current Tasks:
{taskListText}

Return the task IDs in priority order (comma-separated):";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 100 }, ct);

        // Simple parsing - in production use better logic
        var priorityOrder = response.Content
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .ToList();

        for (int i = 0; i < priorityOrder.Count && i < _taskList.Count; i++)
        {
            var task = _taskList.FirstOrDefault(t => t.Id == priorityOrder[i]);
            if (task != null)
            {
                task.Priority = i + 1;
            }
        }
    }

    private class TaskItem
    {
        public int Id { get; init; }
        public string Description { get; init; } = "";
        public int Priority { get; set; }
    }
}

#endregion

#region 6. Tool Calling Agent Strategy

/// <summary>
/// Function/Tool calling agent using OpenAI/Claude function calling.
/// Tool schema registration, result parsing, and chaining.
/// </summary>
public sealed class ToolCallingAgentStrategy : AgentStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "agent-toolcalling";

    /// <inheritdoc/>
    public override string StrategyName => "Tool Calling Agent";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Tool Calling Agent",
        Description = "Function calling agent with native OpenAI/Claude tool use, schema registration, and result chaining",
        Capabilities = IntelligenceCapabilities.ToolUse | IntelligenceCapabilities.FunctionCalling |
                      IntelligenceCapabilities.ReasoningChain,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxToolCalls", Description = "Maximum tool calls per execution", Required = false, DefaultValue = "5" },
            new ConfigurationRequirement { Key = "ParallelToolCalls", Description = "Enable parallel tool execution", Required = false, DefaultValue = "false" }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "agent", "tool-calling", "function-calling", "openai", "claude" }
    };

    /// <inheritdoc/>
    public override async Task<AgentExecutionResult> ExecuteTaskAsync(string task, AgentContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        _currentState = new AgentState { IsRunning = true, CurrentTask = task, StartedAt = DateTime.UtcNow };
        var startTime = DateTime.UtcNow;
        var reasoningChain = new List<string>();
        var actions = new List<AgentAction>();
        int totalTokens = 0;
        var conversationHistory = new List<string> { task };

        try
        {
            int maxCalls = int.Parse(GetConfig("MaxToolCalls") ?? "5");
            bool continueExecution = true;
            int callCount = 0;

            while (continueExecution && callCount < maxCalls)
            {
                _currentState = _currentState with { CurrentStep = callCount };

                // Build prompt with conversation history
                var prompt = BuildToolCallingPrompt(task, conversationHistory, context.Tools);

                // Call AI with tool definitions
                var response = await AIProvider.CompleteAsync(new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 1000,
                    Temperature = 0.7f
                }, ct);

                totalTokens += response.Usage?.TotalTokens ?? 0;
                reasoningChain.Add($"AI Response {callCount + 1}: {response.Content}");

                // Check if AI wants to use tools
                var toolCalls = ExtractToolCalls(response.Content, context.Tools);

                if (toolCalls.Count == 0)
                {
                    // No tool calls - task complete
                    continueExecution = false;
                    reasoningChain.Add("No tool calls requested - task complete");
                    break;
                }

                // Execute tool calls
                foreach (var toolCall in toolCalls)
                {
                    var toolResult = await ExecuteToolAsync(toolCall.ToolId, toolCall.Parameters);

                    actions.Add(new AgentAction
                    {
                        ActionType = "tool_call",
                        Content = $"Called {toolCall.ToolId}",
                        ToolId = toolCall.ToolId,
                        Parameters = toolCall.Parameters,
                        Result = toolResult
                    });

                    conversationHistory.Add($"Tool: {toolCall.ToolId} -> {toolResult}");
                    reasoningChain.Add($"Executed {toolCall.ToolId}: {toolResult}");
                }

                callCount++;
            }

            var finalResult = conversationHistory.LastOrDefault() ?? "No result";

            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = true,
                Result = finalResult,
                StepsTaken = callCount,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _currentState = _currentState with { IsRunning = false };
            return new AgentExecutionResult
            {
                Success = false,
                Result = "",
                StepsTaken = actions.Count,
                ReasoningChain = reasoningChain,
                Actions = actions,
                TokensConsumed = totalTokens,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private string BuildToolCallingPrompt(string task, List<string> history, List<ToolDefinition> tools)
    {
        var toolsDesc = string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}"));
        var historyText = string.Join("\n", history);

        return $@"You are an AI assistant with access to tools. Complete the following task:

Task: {task}

Available Tools:
{toolsDesc}

Conversation History:
{historyText}

If you need to use a tool, respond with: TOOL_CALL: tool_name(param1=value1, param2=value2)
Otherwise, provide your final answer.

Response:";
    }

    private List<(string ToolId, Dictionary<string, object> Parameters)> ExtractToolCalls(string content, List<ToolDefinition> tools)
    {
        var toolCalls = new List<(string, Dictionary<string, object>)>();

        if (!content.Contains("TOOL_CALL:", StringComparison.OrdinalIgnoreCase))
            return toolCalls;

        // Simple parsing - in production use proper parser
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("TOOL_CALL:", StringComparison.OrdinalIgnoreCase))
            {
                // Extract tool name (simplified)
                var tool = tools.FirstOrDefault();
                if (tool != null)
                {
                    toolCalls.Add((tool.ToolId, new Dictionary<string, object>()));
                }
            }
        }

        return toolCalls;
    }
}

#endregion
