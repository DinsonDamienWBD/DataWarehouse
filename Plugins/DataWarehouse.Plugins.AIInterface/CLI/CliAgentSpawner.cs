// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AIInterface.CLI;

/// <summary>
/// Spawns and manages AI agents for complex CLI tasks.
/// Enables autonomous execution of multi-step operations like file organization,
/// storage optimization, and compliance auditing.
/// </summary>
/// <remarks>
/// <para>
/// The CliAgentSpawner handles complex tasks that require reasoning and multi-step execution:
/// <list type="bullet">
/// <item>"Organize my files" - AI agent analyzes structure and restructures intelligently</item>
/// <item>"Optimize storage costs" - AI agent runs tiering analysis and recommends changes</item>
/// <item>"Check for compliance issues" - AI agent runs comprehensive audit</item>
/// <item>"Clean up duplicate files" - AI agent identifies and handles duplicates</item>
/// </list>
/// </para>
/// <para>
/// Agents use the ReAct (Reasoning + Acting) pattern:
/// 1. Observe the current state
/// 2. Think about what action to take
/// 3. Act by executing a tool/command
/// 4. Repeat until goal is achieved
/// </para>
/// </remarks>
public sealed class CliAgentSpawner : IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, AgentExecution> _activeAgents = new();
    private readonly Dictionary<string, AgentTaskDefinition> _taskDefinitions;
    private bool _disposed;

    /// <summary>
    /// Gets the number of currently active agents.
    /// </summary>
    public int ActiveAgentCount => _activeAgents.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliAgentSpawner"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence communication.</param>
    public CliAgentSpawner(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
        _taskDefinitions = BuildTaskDefinitions();
    }

    /// <summary>
    /// Spawns an AI agent to handle a complex task.
    /// </summary>
    /// <param name="input">The natural language task description.</param>
    /// <param name="session">The CLI session for context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The agent execution result.</returns>
    public async Task<CliResponse> SpawnAgentAsync(
        string input,
        CliSession session,
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            return new CliResponse
            {
                Success = false,
                Message = "Agent mode requires a configured message bus."
            };
        }

        // Parse the task from input
        var task = ParseAgentTask(input);
        if (task == null)
        {
            return new CliResponse
            {
                Success = false,
                Message = "Could not understand the agent task.",
                Suggestions = GetAvailableAgentTasks()
            };
        }

        // Check if we already have an agent for this task
        var agentId = $"{session.SessionId}:{task.TaskType}";
        if (_activeAgents.ContainsKey(agentId))
        {
            return new CliResponse
            {
                Success = false,
                Message = $"An agent is already running for '{task.TaskType}'. Use 'agent status' to check progress."
            };
        }

        // Create agent execution
        var execution = new AgentExecution
        {
            AgentId = agentId,
            TaskType = task.TaskType,
            TaskDescription = task.Description,
            Parameters = task.Parameters,
            StartedAt = DateTime.UtcNow,
            Status = AgentStatus.Running,
            Steps = new List<AgentStep>()
        };

        _activeAgents[agentId] = execution;

        try
        {
            // Execute the agent task
            var result = await ExecuteAgentTaskAsync(execution, session, ct);

            // Update execution status
            execution.CompletedAt = DateTime.UtcNow;
            execution.Status = result.Success ? AgentStatus.Completed : AgentStatus.Failed;
            execution.FinalResult = result.Message;

            return result;
        }
        catch (OperationCanceledException)
        {
            execution.Status = AgentStatus.Cancelled;
            execution.CompletedAt = DateTime.UtcNow;

            return new CliResponse
            {
                Success = false,
                Message = $"Agent task '{task.TaskType}' was cancelled."
            };
        }
        catch (Exception ex)
        {
            execution.Status = AgentStatus.Failed;
            execution.CompletedAt = DateTime.UtcNow;
            execution.FinalResult = ex.Message;

            return new CliResponse
            {
                Success = false,
                Message = $"Agent task failed: {ex.Message}"
            };
        }
        finally
        {
            // Keep completed agents for a while for status queries
            var capturedAgentId = agentId;
            _ = Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None)
                .ContinueWith(_ => _activeAgents.TryRemove(capturedAgentId, out var _ignored), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Gets the status of active agents.
    /// </summary>
    /// <param name="sessionId">Optional session ID to filter by.</param>
    /// <returns>List of agent statuses.</returns>
    public IReadOnlyList<AgentStatusInfo> GetAgentStatuses(string? sessionId = null)
    {
        var agents = _activeAgents.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(sessionId))
        {
            agents = agents.Where(a => a.AgentId.StartsWith($"{sessionId}:"));
        }

        return agents.Select(a => new AgentStatusInfo
        {
            AgentId = a.AgentId,
            TaskType = a.TaskType,
            TaskDescription = a.TaskDescription,
            Status = a.Status,
            StartedAt = a.StartedAt,
            CompletedAt = a.CompletedAt,
            StepsCompleted = a.Steps.Count,
            CurrentStep = a.Steps.LastOrDefault()?.Action ?? "Initializing"
        }).ToList().AsReadOnly();
    }

    /// <summary>
    /// Cancels an active agent.
    /// </summary>
    /// <param name="agentId">The agent ID to cancel.</param>
    /// <returns>True if the agent was cancelled.</returns>
    public bool CancelAgent(string agentId)
    {
        if (_activeAgents.TryGetValue(agentId, out var execution))
        {
            execution.CancellationSource?.Cancel();
            execution.Status = AgentStatus.Cancelled;
            return true;
        }
        return false;
    }

    private AgentTask? ParseAgentTask(string input)
    {
        var lower = input.ToLowerInvariant().Trim();

        // Remove "agent" prefix if present
        if (lower.StartsWith("agent "))
        {
            lower = lower.Substring(6).Trim();
        }

        // Match against known task patterns
        foreach (var definition in _taskDefinitions.Values)
        {
            foreach (var pattern in definition.TriggerPatterns)
            {
                if (lower.Contains(pattern))
                {
                    var parameters = ExtractTaskParameters(lower, definition);
                    return new AgentTask
                    {
                        TaskType = definition.TaskType,
                        Description = definition.Description,
                        Parameters = parameters
                    };
                }
            }
        }

        // Check for custom/generic task
        if (lower.StartsWith("do ") || lower.StartsWith("run ") || lower.StartsWith("execute "))
        {
            return new AgentTask
            {
                TaskType = "custom",
                Description = input,
                Parameters = new Dictionary<string, object> { ["task"] = input }
            };
        }

        return null;
    }

    private Dictionary<string, object> ExtractTaskParameters(string input, AgentTaskDefinition definition)
    {
        var parameters = new Dictionary<string, object>();

        // Extract scope (e.g., "organize my documents" -> scope = "documents")
        if (input.Contains("documents") || input.Contains("docs"))
            parameters["scope"] = "documents";
        else if (input.Contains("images") || input.Contains("photos"))
            parameters["scope"] = "images";
        else if (input.Contains("everything") || input.Contains("all"))
            parameters["scope"] = "all";

        // Extract path hints
        var pathMatch = System.Text.RegularExpressions.Regex.Match(input, @"in\s+[""']?([^""']+)[""']?");
        if (pathMatch.Success)
        {
            parameters["path"] = pathMatch.Groups[1].Value;
        }

        // Extract compliance framework
        if (input.Contains("hipaa"))
            parameters["framework"] = "HIPAA";
        else if (input.Contains("gdpr"))
            parameters["framework"] = "GDPR";
        else if (input.Contains("pci") || input.Contains("pci-dss"))
            parameters["framework"] = "PCI-DSS";
        else if (input.Contains("sox"))
            parameters["framework"] = "SOX";

        return parameters;
    }

    private async Task<CliResponse> ExecuteAgentTaskAsync(
        AgentExecution execution,
        CliSession session,
        CancellationToken ct)
    {
        execution.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = execution.CancellationSource.Token;

        // Get the task definition
        if (!_taskDefinitions.TryGetValue(execution.TaskType, out var definition))
        {
            return new CliResponse
            {
                Success = false,
                Message = $"Unknown task type: {execution.TaskType}"
            };
        }

        // Build agent context
        var agentContext = new Dictionary<string, object>
        {
            ["taskType"] = execution.TaskType,
            ["taskDescription"] = execution.TaskDescription,
            ["parameters"] = execution.Parameters,
            ["sessionContext"] = session.CurrentContext,
            ["tools"] = definition.AvailableTools,
            ["constraints"] = definition.Constraints
        };

        // Request agent execution from Intelligence
        var payload = new Dictionary<string, object>
        {
            ["task"] = execution.TaskDescription,
            ["agentType"] = "react",
            ["context"] = agentContext,
            ["maxSteps"] = definition.MaxSteps,
            ["timeout"] = definition.TimeoutSeconds
        };

        try
        {
            var response = await _messageBus!.RequestAsync(
                IntelligenceTopics.AgentExecute,
                payload,
                linkedToken);

            if (response == null)
            {
                return new CliResponse
                {
                    Success = false,
                    Message = "No response from Intelligence agent."
                };
            }

            // Parse agent response
            var success = response.TryGetValue("success", out var successObj) && successObj is true;
            var result = response.TryGetValue("result", out var resultObj) ? resultObj?.ToString() : null;
            var stepsTaken = response.TryGetValue("stepsTaken", out var stepsObj) && stepsObj is int steps ? steps : 0;

            // Extract reasoning chain if available
            if (response.TryGetValue("reasoningChain", out var chainObj) && chainObj is IEnumerable<object> chain)
            {
                foreach (var step in chain)
                {
                    if (step is string stepStr)
                    {
                        execution.Steps.Add(new AgentStep
                        {
                            Action = stepStr,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }
            }

            var message = new System.Text.StringBuilder();
            message.AppendLine(success ? "Agent task completed successfully." : "Agent task completed with issues.");
            message.AppendLine();
            message.AppendLine($"Task: {execution.TaskDescription}");
            message.AppendLine($"Steps taken: {stepsTaken}");

            if (!string.IsNullOrEmpty(result))
            {
                message.AppendLine();
                message.AppendLine("Result:");
                message.AppendLine(result);
            }

            // Extract recommendations if available
            string[]? suggestions = null;
            if (response.TryGetValue("recommendations", out var recsObj))
            {
                if (recsObj is string[] recs)
                    suggestions = recs;
                else if (recsObj is List<string> recsList)
                    suggestions = recsList.ToArray();
            }

            return new CliResponse
            {
                Success = success,
                Message = message.ToString(),
                Data = response,
                Suggestions = suggestions
            };
        }
        catch (Exception ex)
        {
            return new CliResponse
            {
                Success = false,
                Message = $"Agent execution failed: {ex.Message}"
            };
        }
    }

    private string[] GetAvailableAgentTasks()
    {
        return _taskDefinitions.Values
            .Select(d => $"agent {d.TriggerPatterns.First()} - {d.Description}")
            .ToArray();
    }

    private Dictionary<string, AgentTaskDefinition> BuildTaskDefinitions()
    {
        return new Dictionary<string, AgentTaskDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["organize"] = new AgentTaskDefinition
            {
                TaskType = "organize",
                Description = "Analyze and restructure files intelligently",
                TriggerPatterns = new[] { "organize", "reorganize", "structure", "sort files", "arrange" },
                AvailableTools = new[] { "list_files", "move_file", "create_folder", "get_metadata", "classify_content" },
                Constraints = new Dictionary<string, object>
                {
                    ["requireConfirmation"] = true,
                    ["dryRunFirst"] = true
                },
                MaxSteps = 50,
                TimeoutSeconds = 300
            },
            ["optimize"] = new AgentTaskDefinition
            {
                TaskType = "optimize",
                Description = "Analyze storage and recommend cost optimizations",
                TriggerPatterns = new[] { "optimize storage", "optimize cost", "reduce storage", "tiering analysis", "save space" },
                AvailableTools = new[] { "analyze_storage", "get_access_patterns", "recommend_tier", "calculate_costs", "compress_recommendation" },
                Constraints = new Dictionary<string, object>
                {
                    ["readOnly"] = true
                },
                MaxSteps = 30,
                TimeoutSeconds = 180
            },
            ["compliance"] = new AgentTaskDefinition
            {
                TaskType = "compliance",
                Description = "Run comprehensive compliance audit",
                TriggerPatterns = new[] { "check compliance", "compliance audit", "check for compliance", "audit compliance", "security audit" },
                AvailableTools = new[] { "scan_pii", "check_encryption", "verify_access_controls", "check_retention", "generate_report" },
                Constraints = new Dictionary<string, object>
                {
                    ["readOnly"] = true
                },
                MaxSteps = 100,
                TimeoutSeconds = 600
            },
            ["cleanup"] = new AgentTaskDefinition
            {
                TaskType = "cleanup",
                Description = "Find and handle duplicate or unused files",
                TriggerPatterns = new[] { "cleanup", "clean up", "find duplicates", "remove duplicates", "find unused" },
                AvailableTools = new[] { "find_duplicates", "find_unused", "get_file_size", "delete_file", "archive_file" },
                Constraints = new Dictionary<string, object>
                {
                    ["requireConfirmation"] = true,
                    ["dryRunFirst"] = true
                },
                MaxSteps = 50,
                TimeoutSeconds = 300
            },
            ["backup"] = new AgentTaskDefinition
            {
                TaskType = "backup",
                Description = "Analyze and optimize backup strategy",
                TriggerPatterns = new[] { "backup analysis", "analyze backups", "backup strategy", "verify backups" },
                AvailableTools = new[] { "list_backups", "verify_backup", "check_retention", "recommend_schedule", "calculate_rpo" },
                Constraints = new Dictionary<string, object>
                {
                    ["readOnly"] = true
                },
                MaxSteps = 30,
                TimeoutSeconds = 180
            },
            ["security"] = new AgentTaskDefinition
            {
                TaskType = "security",
                Description = "Run security assessment and recommendations",
                TriggerPatterns = new[] { "security check", "security scan", "vulnerability scan", "check security", "security assessment" },
                AvailableTools = new[] { "check_encryption", "verify_access_controls", "scan_vulnerabilities", "check_permissions", "audit_keys" },
                Constraints = new Dictionary<string, object>
                {
                    ["readOnly"] = true
                },
                MaxSteps = 50,
                TimeoutSeconds = 300
            },
            ["metadata"] = new AgentTaskDefinition
            {
                TaskType = "metadata",
                Description = "Enrich files with AI-generated metadata",
                TriggerPatterns = new[] { "enrich metadata", "add metadata", "tag files", "classify files", "auto-tag" },
                AvailableTools = new[] { "extract_content", "classify_content", "extract_entities", "generate_summary", "add_tags" },
                Constraints = new Dictionary<string, object>
                {
                    ["batchSize"] = 100
                },
                MaxSteps = 100,
                TimeoutSeconds = 600
            },
            ["custom"] = new AgentTaskDefinition
            {
                TaskType = "custom",
                Description = "Execute a custom task with AI reasoning",
                TriggerPatterns = new[] { "do", "run", "execute" },
                AvailableTools = new[] { "list_files", "get_metadata", "search", "classify_content", "generate_report" },
                Constraints = new Dictionary<string, object>
                {
                    ["readOnly"] = true,
                    ["requireApproval"] = true
                },
                MaxSteps = 20,
                TimeoutSeconds = 120
            }
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var execution in _activeAgents.Values)
        {
            execution.CancellationSource?.Cancel();
            execution.CancellationSource?.Dispose();
        }

        _activeAgents.Clear();
    }
}

/// <summary>
/// Parsed agent task.
/// </summary>
internal sealed class AgentTask
{
    public string TaskType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Agent task definition.
/// </summary>
internal sealed class AgentTaskDefinition
{
    public string TaskType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string[] TriggerPatterns { get; init; } = Array.Empty<string>();
    public string[] AvailableTools { get; init; } = Array.Empty<string>();
    public Dictionary<string, object> Constraints { get; init; } = new();
    public int MaxSteps { get; init; } = 50;
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// Active agent execution state.
/// </summary>
internal sealed class AgentExecution
{
    public string AgentId { get; init; } = string.Empty;
    public string TaskType { get; init; } = string.Empty;
    public string TaskDescription { get; init; } = string.Empty;
    public Dictionary<string, object> Parameters { get; init; } = new();
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public AgentStatus Status { get; set; }
    public List<AgentStep> Steps { get; init; } = new();
    public string? FinalResult { get; set; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// A single step in agent execution.
/// </summary>
internal sealed class AgentStep
{
    public string Action { get; init; } = string.Empty;
    public string? Observation { get; init; }
    public string? Thought { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Agent execution status.
/// </summary>
public enum AgentStatus
{
    /// <summary>Agent is running.</summary>
    Running,

    /// <summary>Agent completed successfully.</summary>
    Completed,

    /// <summary>Agent failed.</summary>
    Failed,

    /// <summary>Agent was cancelled.</summary>
    Cancelled,

    /// <summary>Agent is waiting for user input.</summary>
    WaitingForInput
}

/// <summary>
/// Agent status information for display.
/// </summary>
public sealed class AgentStatusInfo
{
    /// <summary>Gets or sets the agent ID.</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the task type.</summary>
    public string TaskType { get; init; } = string.Empty;

    /// <summary>Gets or sets the task description.</summary>
    public string TaskDescription { get; init; } = string.Empty;

    /// <summary>Gets or sets the current status.</summary>
    public AgentStatus Status { get; init; }

    /// <summary>Gets or sets when the agent started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when the agent completed.</summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>Gets or sets the number of steps completed.</summary>
    public int StepsCompleted { get; init; }

    /// <summary>Gets or sets the current step description.</summary>
    public string CurrentStep { get; init; } = string.Empty;
}
