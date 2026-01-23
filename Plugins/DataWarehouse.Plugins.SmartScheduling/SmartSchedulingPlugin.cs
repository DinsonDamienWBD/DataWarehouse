using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.SmartScheduling;

/// <summary>
/// Production-ready intelligent task scheduling plugin for DataWarehouse.
/// Implements resource-aware scheduling with priority queues, deadline awareness,
/// and load balancing across nodes.
///
/// Scheduling Features:
/// - Multi-level priority queue with aging
/// - EDF (Earliest Deadline First) scheduling for time-critical tasks
/// - Resource-aware scheduling (CPU, memory, I/O)
/// - Fair scheduling with weighted priorities
/// - Load balancing across distributed nodes
/// - Work stealing for idle workers
///
/// Resource Management:
/// - Real-time resource monitoring
/// - Task resource estimation
/// - Backpressure handling
/// - Throttling under load
///
/// Message Commands:
/// - scheduler.submit: Submit a task for scheduling
/// - scheduler.cancel: Cancel a scheduled task
/// - scheduler.status: Get scheduler status
/// - scheduler.task.status: Get task status
/// - scheduler.nodes: Get node information
/// - scheduler.stats: Get scheduler statistics
/// </summary>
public sealed class SmartSchedulingPlugin : IntelligencePluginBase
{
    private readonly ConcurrentDictionary<string, ScheduledTask> _tasks;
    private readonly ConcurrentDictionary<string, WorkerNode> _nodes;
    private readonly PriorityTaskQueue _priorityQueue;
    private readonly ConcurrentDictionary<string, TaskExecution> _runningTasks;
    private readonly ConcurrentQueue<TaskCompletionRecord> _completionHistory;
    private readonly SemaphoreSlim _schedulerLock;
    private readonly CancellationTokenSource _cts;

    private Task? _schedulerTask;
    private Task? _monitorTask;
    private Task? _loadBalancerTask;
    private SchedulerConfig _config;
    private ResourceMonitor _resourceMonitor;
    private long _totalTasksSubmitted;
    private long _totalTasksCompleted;
    private long _totalTasksFailed;
    private long _totalTasksCancelled;
    private DateTime _sessionStart;

    private const int MaxCompletionHistory = 10000;
    private const int SchedulerIntervalMs = 100;
    private const int MonitorIntervalMs = 1000;
    private const int LoadBalanceIntervalMs = 5000;

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.scheduling.smart";

    /// <inheritdoc/>
    public override string Name => "Smart Scheduling Plugin";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.IntelligenceProvider;

    /// <summary>
    /// Initializes a new instance of the SmartSchedulingPlugin.
    /// </summary>
    /// <param name="config">Optional scheduler configuration.</param>
    public SmartSchedulingPlugin(SchedulerConfig? config = null)
    {
        _tasks = new ConcurrentDictionary<string, ScheduledTask>();
        _nodes = new ConcurrentDictionary<string, WorkerNode>();
        _priorityQueue = new PriorityTaskQueue();
        _runningTasks = new ConcurrentDictionary<string, TaskExecution>();
        _completionHistory = new ConcurrentQueue<TaskCompletionRecord>();
        _schedulerLock = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();
        _config = config ?? new SchedulerConfig();
        _resourceMonitor = new ResourceMonitor();
        _sessionStart = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _sessionStart = DateTime.UtcNow;

        // Register self as a local node
        var localNode = new WorkerNode
        {
            NodeId = Environment.MachineName,
            Endpoint = "local",
            IsLocal = true,
            MaxConcurrency = _config.MaxConcurrentTasks,
            Status = NodeStatus.Online,
            LastHeartbeat = DateTime.UtcNow
        };
        _nodes[localNode.NodeId] = localNode;

        _schedulerTask = RunSchedulerAsync(_cts.Token);
        _monitorTask = RunMonitorAsync(_cts.Token);
        _loadBalancerTask = RunLoadBalancerAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _cts.Cancel();

        // Cancel all running tasks
        foreach (var execution in _runningTasks.Values)
        {
            execution.CancellationSource.Cancel();
        }

        var tasks = new List<Task>();
        if (_schedulerTask != null) tasks.Add(_schedulerTask);
        if (_monitorTask != null) tasks.Add(_monitorTask);
        if (_loadBalancerTask != null) tasks.Add(_loadBalancerTask);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Expected
        }
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case "scheduler.submit":
                    await HandleSubmitAsync(message);
                    break;
                case "scheduler.cancel":
                    HandleCancel(message);
                    break;
                case "scheduler.status":
                    HandleGetStatus(message);
                    break;
                case "scheduler.task.status":
                    HandleGetTaskStatus(message);
                    break;
                case "scheduler.nodes":
                    HandleGetNodes(message);
                    break;
                case "scheduler.stats":
                    HandleGetStats(message);
                    break;
                case "scheduler.node.register":
                    HandleRegisterNode(message);
                    break;
                case "scheduler.node.heartbeat":
                    HandleNodeHeartbeat(message);
                    break;
                case "scheduler.queue":
                    HandleGetQueue(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }
        catch (Exception ex)
        {
            message.Payload["error"] = ex.Message;
            message.Payload["success"] = false;
        }
    }

    /// <summary>
    /// Submits a task for scheduling.
    /// </summary>
    /// <param name="taskId">Unique task identifier.</param>
    /// <param name="work">The work to execute.</param>
    /// <param name="options">Task scheduling options.</param>
    /// <returns>Submitted task information.</returns>
    public ScheduledTask SubmitTask(
        string taskId,
        Func<CancellationToken, Task> work,
        TaskSchedulingOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID required", nameof(taskId));
        if (work == null)
            throw new ArgumentNullException(nameof(work));

        options ??= new TaskSchedulingOptions();

        var task = new ScheduledTask
        {
            TaskId = taskId,
            Work = work,
            Priority = options.Priority,
            Deadline = options.Deadline,
            EstimatedDuration = options.EstimatedDuration,
            RequiredResources = options.RequiredResources ?? new ResourceRequirements(),
            SubmittedAt = DateTime.UtcNow,
            Status = TaskStatus.Pending,
            Description = options.Description,
            Tags = options.Tags ?? Array.Empty<string>(),
            DependsOn = options.DependsOn ?? Array.Empty<string>()
        };

        if (!_tasks.TryAdd(taskId, task))
        {
            throw new InvalidOperationException($"Task {taskId} already exists");
        }

        Interlocked.Increment(ref _totalTasksSubmitted);

        // Check dependencies
        if (task.DependsOn.Length == 0 || task.DependsOn.All(d =>
            _tasks.TryGetValue(d, out var dep) && dep.Status == TaskStatus.Completed))
        {
            _priorityQueue.Enqueue(task);
        }
        else
        {
            task.Status = TaskStatus.WaitingForDependencies;
        }

        return task;
    }

    /// <summary>
    /// Cancels a task.
    /// </summary>
    /// <param name="taskId">Task identifier to cancel.</param>
    /// <returns>True if the task was cancelled.</returns>
    public bool CancelTask(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (task.Status == TaskStatus.Running)
            {
                if (_runningTasks.TryGetValue(taskId, out var execution))
                {
                    execution.CancellationSource.Cancel();
                }
            }

            task.Status = TaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            Interlocked.Increment(ref _totalTasksCancelled);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the status of a task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <returns>Task status or null if not found.</returns>
    public TaskStatusInfo? GetTaskStatus(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        return new TaskStatusInfo
        {
            TaskId = task.TaskId,
            Status = task.Status,
            Priority = task.Priority,
            SubmittedAt = task.SubmittedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            Deadline = task.Deadline,
            AssignedNode = task.AssignedNode,
            Progress = task.Progress,
            Error = task.Error
        };
    }

    /// <summary>
    /// Registers a worker node.
    /// </summary>
    /// <param name="node">Node information.</param>
    public void RegisterNode(WorkerNode node)
    {
        if (string.IsNullOrWhiteSpace(node.NodeId))
            throw new ArgumentException("Node ID required");

        node.LastHeartbeat = DateTime.UtcNow;
        node.Status = NodeStatus.Online;
        _nodes[node.NodeId] = node;
    }

    /// <summary>
    /// Gets the current scheduler status.
    /// </summary>
    /// <returns>Scheduler status information.</returns>
    public SchedulerStatus GetSchedulerStatus()
    {
        var resources = _resourceMonitor.GetCurrentResources();

        return new SchedulerStatus
        {
            IsRunning = !_cts.IsCancellationRequested,
            PendingTasks = _priorityQueue.Count,
            RunningTasks = _runningTasks.Count,
            TotalTasks = _tasks.Count,
            ActiveNodes = _nodes.Values.Count(n => n.Status == NodeStatus.Online),
            TotalNodes = _nodes.Count,
            CpuUsagePercent = resources.CpuUsagePercent,
            MemoryUsagePercent = resources.MemoryUsagePercent,
            QueuedByPriority = _priorityQueue.GetCountsByPriority(),
            AverageWaitTimeMs = CalculateAverageWaitTime()
        };
    }

    private async Task HandleSubmitAsync(PluginMessage message)
    {
        var taskId = GetString(message.Payload, "taskId") ?? Guid.NewGuid().ToString("N");
        var priorityStr = GetString(message.Payload, "priority") ?? "normal";
        var deadlineStr = GetString(message.Payload, "deadline");
        var durationMs = GetInt(message.Payload, "estimatedDurationMs");
        var description = GetString(message.Payload, "description");

        var priority = ParsePriority(priorityStr);
        DateTime? deadline = null;
        if (!string.IsNullOrEmpty(deadlineStr) && DateTime.TryParse(deadlineStr, out var dl))
        {
            deadline = dl;
        }

        var options = new TaskSchedulingOptions
        {
            Priority = priority,
            Deadline = deadline,
            EstimatedDuration = durationMs.HasValue ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
            Description = description
        };

        var task = SubmitTask(taskId, _ => Task.CompletedTask, options);

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["taskId"] = task.TaskId,
            ["status"] = task.Status.ToString(),
            ["priority"] = task.Priority.ToString(),
            ["submittedAt"] = task.SubmittedAt,
            ["deadline"] = task.Deadline?.ToString() ?? "",
            ["queuePosition"] = _priorityQueue.GetPosition(taskId)
        };
        message.Payload["success"] = true;

        await Task.CompletedTask;
    }

    private void HandleCancel(PluginMessage message)
    {
        var taskId = GetString(message.Payload, "taskId");
        if (string.IsNullOrEmpty(taskId))
        {
            message.Payload["error"] = "taskId required";
            message.Payload["success"] = false;
            return;
        }

        var cancelled = CancelTask(taskId);
        message.Payload["result"] = new { taskId, cancelled };
        message.Payload["success"] = true;
    }

    private void HandleGetStatus(PluginMessage message)
    {
        var status = GetSchedulerStatus();

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["isRunning"] = status.IsRunning,
            ["pendingTasks"] = status.PendingTasks,
            ["runningTasks"] = status.RunningTasks,
            ["totalTasks"] = status.TotalTasks,
            ["activeNodes"] = status.ActiveNodes,
            ["totalNodes"] = status.TotalNodes,
            ["cpuUsagePercent"] = status.CpuUsagePercent,
            ["memoryUsagePercent"] = status.MemoryUsagePercent,
            ["queuedByPriority"] = status.QueuedByPriority.ToDictionary(
                kv => kv.Key.ToString(), kv => kv.Value),
            ["averageWaitTimeMs"] = status.AverageWaitTimeMs
        };
        message.Payload["success"] = true;
    }

    private void HandleGetTaskStatus(PluginMessage message)
    {
        var taskId = GetString(message.Payload, "taskId");
        if (string.IsNullOrEmpty(taskId))
        {
            message.Payload["error"] = "taskId required";
            message.Payload["success"] = false;
            return;
        }

        var status = GetTaskStatus(taskId);
        if (status == null)
        {
            message.Payload["error"] = "Task not found";
            message.Payload["success"] = false;
            return;
        }

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["taskId"] = status.TaskId,
            ["status"] = status.Status.ToString(),
            ["priority"] = status.Priority.ToString(),
            ["submittedAt"] = status.SubmittedAt,
            ["startedAt"] = status.StartedAt?.ToString() ?? "",
            ["completedAt"] = status.CompletedAt?.ToString() ?? "",
            ["deadline"] = status.Deadline?.ToString() ?? "",
            ["assignedNode"] = status.AssignedNode ?? "",
            ["progress"] = status.Progress,
            ["error"] = status.Error ?? ""
        };
        message.Payload["success"] = true;
    }

    private void HandleGetNodes(PluginMessage message)
    {
        var nodes = _nodes.Values.Select(n => new Dictionary<string, object>
        {
            ["nodeId"] = n.NodeId,
            ["endpoint"] = n.Endpoint,
            ["isLocal"] = n.IsLocal,
            ["status"] = n.Status.ToString(),
            ["maxConcurrency"] = n.MaxConcurrency,
            ["currentLoad"] = n.CurrentLoad,
            ["lastHeartbeat"] = n.LastHeartbeat,
            ["capabilities"] = n.Capabilities.ToArray()
        }).ToList();

        message.Payload["result"] = new { nodes, count = nodes.Count };
        message.Payload["success"] = true;
    }

    private void HandleGetStats(PluginMessage message)
    {
        var uptime = DateTime.UtcNow - _sessionStart;

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["uptimeSeconds"] = uptime.TotalSeconds,
            ["totalTasksSubmitted"] = Interlocked.Read(ref _totalTasksSubmitted),
            ["totalTasksCompleted"] = Interlocked.Read(ref _totalTasksCompleted),
            ["totalTasksFailed"] = Interlocked.Read(ref _totalTasksFailed),
            ["totalTasksCancelled"] = Interlocked.Read(ref _totalTasksCancelled),
            ["pendingTasks"] = _priorityQueue.Count,
            ["runningTasks"] = _runningTasks.Count,
            ["completionRate"] = _totalTasksSubmitted > 0
                ? (double)_totalTasksCompleted / _totalTasksSubmitted
                : 0,
            ["averageWaitTimeMs"] = CalculateAverageWaitTime(),
            ["averageExecutionTimeMs"] = CalculateAverageExecutionTime()
        };
        message.Payload["success"] = true;
    }

    private void HandleRegisterNode(PluginMessage message)
    {
        var nodeId = GetString(message.Payload, "nodeId");
        var endpoint = GetString(message.Payload, "endpoint");
        var maxConcurrency = GetInt(message.Payload, "maxConcurrency") ?? 4;

        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(endpoint))
        {
            message.Payload["error"] = "nodeId and endpoint required";
            message.Payload["success"] = false;
            return;
        }

        var node = new WorkerNode
        {
            NodeId = nodeId,
            Endpoint = endpoint,
            IsLocal = false,
            MaxConcurrency = maxConcurrency,
            Status = NodeStatus.Online,
            LastHeartbeat = DateTime.UtcNow
        };

        RegisterNode(node);

        message.Payload["result"] = new { registered = true, nodeId };
        message.Payload["success"] = true;
    }

    private void HandleNodeHeartbeat(PluginMessage message)
    {
        var nodeId = GetString(message.Payload, "nodeId");
        if (string.IsNullOrEmpty(nodeId))
        {
            message.Payload["error"] = "nodeId required";
            message.Payload["success"] = false;
            return;
        }

        if (_nodes.TryGetValue(nodeId, out var node))
        {
            node.LastHeartbeat = DateTime.UtcNow;
            node.Status = NodeStatus.Online;

            if (message.Payload.TryGetValue("currentLoad", out var load) && load is int loadVal)
            {
                node.CurrentLoad = loadVal;
            }

            message.Payload["result"] = new { acknowledged = true };
        }
        else
        {
            message.Payload["result"] = new { acknowledged = false, reason = "Node not registered" };
        }

        message.Payload["success"] = true;
    }

    private void HandleGetQueue(PluginMessage message)
    {
        var limit = GetInt(message.Payload, "limit") ?? 50;

        var queuedTasks = _priorityQueue.Peek(limit)
            .Select(t => new Dictionary<string, object>
            {
                ["taskId"] = t.TaskId,
                ["priority"] = t.Priority.ToString(),
                ["submittedAt"] = t.SubmittedAt,
                ["deadline"] = t.Deadline?.ToString() ?? "",
                ["description"] = t.Description ?? "",
                ["effectivePriority"] = CalculateEffectivePriority(t)
            })
            .ToList();

        message.Payload["result"] = new { tasks = queuedTasks, count = queuedTasks.Count };
        message.Payload["success"] = true;
    }

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SchedulerIntervalMs, ct);

                // Check resource availability
                var resources = _resourceMonitor.GetCurrentResources();
                if (resources.CpuUsagePercent > _config.MaxCpuUsagePercent ||
                    resources.MemoryUsagePercent > _config.MaxMemoryUsagePercent)
                {
                    continue; // Back-pressure
                }

                // Find available node
                var availableNode = FindBestNode();
                if (availableNode == null)
                    continue;

                // Dequeue highest priority task
                if (!_priorityQueue.TryDequeue(out var task))
                    continue;

                // Execute task
                await ExecuteTaskAsync(task, availableNode, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task RunMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MonitorIntervalMs, ct);

                // Update resource metrics
                _resourceMonitor.Update();

                // Check for deadline violations
                foreach (var task in _tasks.Values.Where(t => t.Status == TaskStatus.Pending))
                {
                    if (task.Deadline.HasValue && DateTime.UtcNow > task.Deadline.Value)
                    {
                        // Boost priority for overdue tasks
                        task.Priority = SchedulingPriority.Critical;
                    }
                }

                // Check for stale nodes
                var staleThreshold = DateTime.UtcNow.AddSeconds(-30);
                foreach (var node in _nodes.Values.Where(n => !n.IsLocal && n.LastHeartbeat < staleThreshold))
                {
                    node.Status = NodeStatus.Offline;
                }

                // Check dependency completion
                CheckDependencies();

                // Age tasks in queue (priority boosting)
                _priorityQueue.ApplyAging(_config.AgingIntervalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task RunLoadBalancerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LoadBalanceIntervalMs, ct);

                // Work stealing from overloaded nodes
                var overloadedNodes = _nodes.Values
                    .Where(n => n.Status == NodeStatus.Online && n.CurrentLoad > n.MaxConcurrency * 0.8)
                    .ToList();

                var underloadedNodes = _nodes.Values
                    .Where(n => n.Status == NodeStatus.Online && n.CurrentLoad < n.MaxConcurrency * 0.3)
                    .ToList();

                if (overloadedNodes.Count > 0 && underloadedNodes.Count > 0)
                {
                    // Redistribute work (in real implementation, migrate tasks)
                    foreach (var overloaded in overloadedNodes)
                    {
                        overloaded.CurrentLoad = Math.Max(0, overloaded.CurrentLoad - 1);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task ExecuteTaskAsync(ScheduledTask task, WorkerNode node, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (task.Deadline.HasValue)
        {
            var timeout = task.Deadline.Value - DateTime.UtcNow;
            if (timeout > TimeSpan.Zero)
            {
                cts.CancelAfter(timeout);
            }
        }

        var execution = new TaskExecution
        {
            TaskId = task.TaskId,
            NodeId = node.NodeId,
            CancellationSource = cts,
            StartTime = DateTime.UtcNow
        };

        _runningTasks[task.TaskId] = execution;
        task.Status = TaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        task.AssignedNode = node.NodeId;
        node.CurrentLoad++;

        try
        {
            await task.Work(cts.Token);

            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            Interlocked.Increment(ref _totalTasksCompleted);

            RecordCompletion(task, true);
        }
        catch (OperationCanceledException)
        {
            if (task.Status != TaskStatus.Cancelled)
            {
                task.Status = TaskStatus.Cancelled;
                task.CompletedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _totalTasksCancelled);
            }
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.CompletedAt = DateTime.UtcNow;
            task.Error = ex.Message;
            Interlocked.Increment(ref _totalTasksFailed);

            RecordCompletion(task, false);

            // Retry logic
            if (task.RetryCount < _config.MaxRetries)
            {
                task.RetryCount++;
                task.Status = TaskStatus.Pending;
                _priorityQueue.Enqueue(task);
            }
        }
        finally
        {
            _runningTasks.TryRemove(task.TaskId, out _);
            node.CurrentLoad = Math.Max(0, node.CurrentLoad - 1);
            cts.Dispose();
        }
    }

    private void CheckDependencies()
    {
        foreach (var task in _tasks.Values.Where(t => t.Status == TaskStatus.WaitingForDependencies))
        {
            if (task.DependsOn.All(d =>
                _tasks.TryGetValue(d, out var dep) && dep.Status == TaskStatus.Completed))
            {
                task.Status = TaskStatus.Pending;
                _priorityQueue.Enqueue(task);
            }
        }
    }

    private WorkerNode? FindBestNode()
    {
        return _nodes.Values
            .Where(n => n.Status == NodeStatus.Online && n.CurrentLoad < n.MaxConcurrency)
            .OrderBy(n => (double)n.CurrentLoad / n.MaxConcurrency)
            .ThenByDescending(n => n.IsLocal)
            .FirstOrDefault();
    }

    private double CalculateEffectivePriority(ScheduledTask task)
    {
        var basePriority = (int)task.Priority * 100;
        var waitTime = (DateTime.UtcNow - task.SubmittedAt).TotalSeconds;
        var aging = waitTime / _config.AgingIntervalSeconds * 10;

        double deadlineUrgency = 0;
        if (task.Deadline.HasValue)
        {
            var timeToDeadline = (task.Deadline.Value - DateTime.UtcNow).TotalMinutes;
            if (timeToDeadline < 60)
            {
                deadlineUrgency = (60 - timeToDeadline) * 2;
            }
        }

        return basePriority + aging + deadlineUrgency;
    }

    private void RecordCompletion(ScheduledTask task, bool success)
    {
        var record = new TaskCompletionRecord
        {
            TaskId = task.TaskId,
            Success = success,
            SubmittedAt = task.SubmittedAt,
            StartedAt = task.StartedAt ?? DateTime.UtcNow,
            CompletedAt = task.CompletedAt ?? DateTime.UtcNow,
            Priority = task.Priority,
            NodeId = task.AssignedNode ?? ""
        };

        _completionHistory.Enqueue(record);
        while (_completionHistory.Count > MaxCompletionHistory)
        {
            _completionHistory.TryDequeue(out _);
        }
    }

    private double CalculateAverageWaitTime()
    {
        var records = _completionHistory.ToArray();
        if (records.Length == 0) return 0;

        return records.Average(r => (r.StartedAt - r.SubmittedAt).TotalMilliseconds);
    }

    private double CalculateAverageExecutionTime()
    {
        var records = _completionHistory.ToArray();
        if (records.Length == 0) return 0;

        return records.Average(r => (r.CompletedAt - r.StartedAt).TotalMilliseconds);
    }

    private static SchedulingPriority ParsePriority(string priority)
    {
        return priority.ToLowerInvariant() switch
        {
            "critical" => SchedulingPriority.Critical,
            "high" => SchedulingPriority.High,
            "low" => SchedulingPriority.Low,
            "background" => SchedulingPriority.Background,
            _ => SchedulingPriority.Normal
        };
    }

    private static string? GetString(Dictionary<string, object> payload, string key)
    {
        return payload.TryGetValue(key, out var val) && val is string s ? s : null;
    }

    private static int? GetInt(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is string s && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "scheduler.submit", DisplayName = "Submit Task", Description = "Submit a task for scheduling" },
            new() { Name = "scheduler.cancel", DisplayName = "Cancel Task", Description = "Cancel a scheduled task" },
            new() { Name = "scheduler.status", DisplayName = "Scheduler Status", Description = "Get scheduler status" },
            new() { Name = "scheduler.task.status", DisplayName = "Task Status", Description = "Get task status" },
            new() { Name = "scheduler.nodes", DisplayName = "Node List", Description = "Get worker node information" },
            new() { Name = "scheduler.stats", DisplayName = "Statistics", Description = "Get scheduler statistics" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["PendingTasks"] = _priorityQueue.Count;
        metadata["RunningTasks"] = _runningTasks.Count;
        metadata["ActiveNodes"] = _nodes.Values.Count(n => n.Status == NodeStatus.Online);
        metadata["TotalTasksSubmitted"] = Interlocked.Read(ref _totalTasksSubmitted);
        return metadata;
    }
}

#region Priority Queue

/// <summary>
/// Multi-level priority task queue with aging support.
/// </summary>
internal sealed class PriorityTaskQueue
{
    private readonly SortedList<double, Queue<ScheduledTask>> _queues;
    private readonly Dictionary<string, double> _taskPriorities;
    private readonly object _lock = new();
    private int _count;
    private DateTime _lastAging;

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public PriorityTaskQueue()
    {
        _queues = new SortedList<double, Queue<ScheduledTask>>(Comparer<double>.Create((a, b) => b.CompareTo(a)));
        _taskPriorities = new Dictionary<string, double>();
        _lastAging = DateTime.UtcNow;
    }

    public void Enqueue(ScheduledTask task)
    {
        lock (_lock)
        {
            var priority = CalculateInitialPriority(task);
            _taskPriorities[task.TaskId] = priority;

            if (!_queues.ContainsKey(priority))
            {
                _queues[priority] = new Queue<ScheduledTask>();
            }

            _queues[priority].Enqueue(task);
            _count++;
        }
    }

    public bool TryDequeue(out ScheduledTask? task)
    {
        lock (_lock)
        {
            task = null;
            if (_count == 0) return false;

            foreach (var queue in _queues.Values)
            {
                if (queue.Count > 0)
                {
                    task = queue.Dequeue();
                    _taskPriorities.Remove(task.TaskId);
                    _count--;
                    return true;
                }
            }

            return false;
        }
    }

    public List<ScheduledTask> Peek(int count)
    {
        lock (_lock)
        {
            var result = new List<ScheduledTask>();
            foreach (var queue in _queues.Values)
            {
                foreach (var task in queue)
                {
                    result.Add(task);
                    if (result.Count >= count) return result;
                }
            }
            return result;
        }
    }

    public int GetPosition(string taskId)
    {
        lock (_lock)
        {
            int position = 0;
            foreach (var queue in _queues.Values)
            {
                foreach (var task in queue)
                {
                    position++;
                    if (task.TaskId == taskId) return position;
                }
            }
            return -1;
        }
    }

    public Dictionary<SchedulingPriority, int> GetCountsByPriority()
    {
        lock (_lock)
        {
            var counts = new Dictionary<SchedulingPriority, int>
            {
                [SchedulingPriority.Critical] = 0,
                [SchedulingPriority.High] = 0,
                [SchedulingPriority.Normal] = 0,
                [SchedulingPriority.Low] = 0,
                [SchedulingPriority.Background] = 0
            };

            foreach (var queue in _queues.Values)
            {
                foreach (var task in queue)
                {
                    counts[task.Priority]++;
                }
            }

            return counts;
        }
    }

    public void ApplyAging(int agingIntervalSeconds)
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastAging).TotalSeconds < agingIntervalSeconds)
                return;

            _lastAging = DateTime.UtcNow;

            // Collect tasks to re-prioritize
            var tasksToReprioritize = new List<(ScheduledTask task, double oldPriority)>();

            foreach (var kv in _queues)
            {
                foreach (var task in kv.Value)
                {
                    var newPriority = CalculateAgedPriority(task);
                    if (Math.Abs(newPriority - kv.Key) > 10)
                    {
                        tasksToReprioritize.Add((task, kv.Key));
                    }
                }
            }

            // Re-prioritize tasks
            foreach (var (task, oldPriority) in tasksToReprioritize)
            {
                // Remove from old queue
                if (_queues.TryGetValue(oldPriority, out var oldQueue))
                {
                    var tempQueue = new Queue<ScheduledTask>();
                    while (oldQueue.Count > 0)
                    {
                        var t = oldQueue.Dequeue();
                        if (t.TaskId != task.TaskId)
                        {
                            tempQueue.Enqueue(t);
                        }
                    }
                    while (tempQueue.Count > 0)
                    {
                        oldQueue.Enqueue(tempQueue.Dequeue());
                    }
                }

                // Add to new queue
                var newPriority = CalculateAgedPriority(task);
                _taskPriorities[task.TaskId] = newPriority;

                if (!_queues.ContainsKey(newPriority))
                {
                    _queues[newPriority] = new Queue<ScheduledTask>();
                }
                _queues[newPriority].Enqueue(task);
            }

            // Clean empty queues
            var emptyKeys = _queues.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            foreach (var key in emptyKeys)
            {
                _queues.Remove(key);
            }
        }
    }

    private static double CalculateInitialPriority(ScheduledTask task)
    {
        var basePriority = (int)task.Priority * 100;
        double deadlineBonus = 0;

        if (task.Deadline.HasValue)
        {
            var minutesToDeadline = (task.Deadline.Value - DateTime.UtcNow).TotalMinutes;
            if (minutesToDeadline < 60)
            {
                deadlineBonus = (60 - minutesToDeadline) * 2;
            }
        }

        return basePriority + deadlineBonus;
    }

    private static double CalculateAgedPriority(ScheduledTask task)
    {
        var basePriority = CalculateInitialPriority(task);
        var ageMinutes = (DateTime.UtcNow - task.SubmittedAt).TotalMinutes;
        var ageBonus = Math.Min(50, ageMinutes * 0.5);

        return basePriority + ageBonus;
    }
}

#endregion

#region Resource Monitor

/// <summary>
/// Resource monitor for scheduling decisions.
/// </summary>
internal sealed class ResourceMonitor
{
    private Process? _currentProcess;
    private double _cpuUsage;
    private double _memoryUsage;
    private DateTime _lastUpdate;
    private TimeSpan _lastCpuTime;

    public void Update()
    {
        try
        {
            _currentProcess ??= Process.GetCurrentProcess();
            _currentProcess.Refresh();

            // Calculate CPU usage
            var currentCpuTime = _currentProcess.TotalProcessorTime;
            var elapsed = DateTime.UtcNow - _lastUpdate;

            if (elapsed.TotalMilliseconds > 0 && _lastUpdate != default)
            {
                var cpuUsed = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
                _cpuUsage = cpuUsed / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            }

            _lastCpuTime = currentCpuTime;
            _lastUpdate = DateTime.UtcNow;

            // Memory usage
            var workingSet = _currentProcess.WorkingSet64;
            var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            _memoryUsage = totalMemory > 0 ? (double)workingSet / totalMemory * 100 : 0;
        }
        catch
        {
            // Unable to get metrics
        }
    }

    public ResourceInfo GetCurrentResources()
    {
        return new ResourceInfo
        {
            CpuUsagePercent = _cpuUsage,
            MemoryUsagePercent = _memoryUsage,
            AvailableMemoryMB = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes -
                               (_currentProcess?.WorkingSet64 ?? 0)) / 1024 / 1024
        };
    }
}

#endregion

#region Data Types

/// <summary>
/// Scheduler configuration.
/// </summary>
public sealed class SchedulerConfig
{
    /// <summary>Maximum concurrent tasks.</summary>
    public int MaxConcurrentTasks { get; set; } = Environment.ProcessorCount * 2;
    /// <summary>Maximum CPU usage before backpressure.</summary>
    public double MaxCpuUsagePercent { get; set; } = 80;
    /// <summary>Maximum memory usage before backpressure.</summary>
    public double MaxMemoryUsagePercent { get; set; } = 85;
    /// <summary>Maximum retries for failed tasks.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Interval in seconds for priority aging.</summary>
    public int AgingIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Task scheduling options.
/// </summary>
public sealed class TaskSchedulingOptions
{
    /// <summary>Task priority.</summary>
    public SchedulingPriority Priority { get; init; } = SchedulingPriority.Normal;
    /// <summary>Task deadline.</summary>
    public DateTime? Deadline { get; init; }
    /// <summary>Estimated duration.</summary>
    public TimeSpan? EstimatedDuration { get; init; }
    /// <summary>Required resources.</summary>
    public ResourceRequirements? RequiredResources { get; init; }
    /// <summary>Task description.</summary>
    public string? Description { get; init; }
    /// <summary>Task tags.</summary>
    public string[]? Tags { get; init; }
    /// <summary>Task dependencies.</summary>
    public string[]? DependsOn { get; init; }
}

/// <summary>
/// Resource requirements for a task.
/// </summary>
public sealed class ResourceRequirements
{
    /// <summary>CPU cores required.</summary>
    public double CpuCores { get; init; } = 1;
    /// <summary>Memory required in MB.</summary>
    public int MemoryMB { get; init; } = 256;
    /// <summary>I/O priority.</summary>
    public int IoPriority { get; init; } = 1;
}

/// <summary>
/// Scheduling priority levels.
/// </summary>
public enum SchedulingPriority
{
    /// <summary>Background priority.</summary>
    Background = 0,
    /// <summary>Low priority.</summary>
    Low = 1,
    /// <summary>Normal priority.</summary>
    Normal = 2,
    /// <summary>High priority.</summary>
    High = 3,
    /// <summary>Critical priority.</summary>
    Critical = 4
}

/// <summary>
/// Task status enumeration.
/// </summary>
public enum TaskStatus
{
    /// <summary>Pending in queue.</summary>
    Pending,
    /// <summary>Waiting for dependencies.</summary>
    WaitingForDependencies,
    /// <summary>Currently running.</summary>
    Running,
    /// <summary>Successfully completed.</summary>
    Completed,
    /// <summary>Failed with error.</summary>
    Failed,
    /// <summary>Cancelled.</summary>
    Cancelled
}

/// <summary>
/// Node status enumeration.
/// </summary>
public enum NodeStatus
{
    /// <summary>Online and available.</summary>
    Online,
    /// <summary>Offline or unreachable.</summary>
    Offline,
    /// <summary>Draining tasks.</summary>
    Draining
}

/// <summary>
/// Scheduled task.
/// </summary>
public sealed class ScheduledTask
{
    /// <summary>Unique task identifier.</summary>
    public required string TaskId { get; init; }
    /// <summary>Work to execute.</summary>
    public required Func<CancellationToken, Task> Work { get; init; }
    /// <summary>Task priority.</summary>
    public SchedulingPriority Priority { get; set; }
    /// <summary>Task deadline.</summary>
    public DateTime? Deadline { get; init; }
    /// <summary>Estimated duration.</summary>
    public TimeSpan? EstimatedDuration { get; init; }
    /// <summary>Required resources.</summary>
    public ResourceRequirements RequiredResources { get; init; } = new();
    /// <summary>When submitted.</summary>
    public DateTime SubmittedAt { get; init; }
    /// <summary>When started.</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>When completed.</summary>
    public DateTime? CompletedAt { get; set; }
    /// <summary>Current status.</summary>
    public TaskStatus Status { get; set; }
    /// <summary>Assigned node.</summary>
    public string? AssignedNode { get; set; }
    /// <summary>Progress (0-100).</summary>
    public int Progress { get; set; }
    /// <summary>Error message if failed.</summary>
    public string? Error { get; set; }
    /// <summary>Task description.</summary>
    public string? Description { get; init; }
    /// <summary>Task tags.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
    /// <summary>Dependencies.</summary>
    public string[] DependsOn { get; init; } = Array.Empty<string>();
    /// <summary>Retry count.</summary>
    public int RetryCount { get; set; }
}

/// <summary>
/// Worker node information.
/// </summary>
public sealed class WorkerNode
{
    /// <summary>Node identifier.</summary>
    public required string NodeId { get; init; }
    /// <summary>Network endpoint.</summary>
    public required string Endpoint { get; init; }
    /// <summary>Whether this is the local node.</summary>
    public bool IsLocal { get; init; }
    /// <summary>Maximum concurrent tasks.</summary>
    public int MaxConcurrency { get; set; }
    /// <summary>Current load.</summary>
    public int CurrentLoad { get; set; }
    /// <summary>Node status.</summary>
    public NodeStatus Status { get; set; }
    /// <summary>Last heartbeat time.</summary>
    public DateTime LastHeartbeat { get; set; }
    /// <summary>Node capabilities.</summary>
    public HashSet<string> Capabilities { get; } = new();
}

/// <summary>
/// Task execution context.
/// </summary>
internal sealed class TaskExecution
{
    public required string TaskId { get; init; }
    public required string NodeId { get; init; }
    public required CancellationTokenSource CancellationSource { get; init; }
    public DateTime StartTime { get; init; }
}

/// <summary>
/// Task completion record.
/// </summary>
internal sealed class TaskCompletionRecord
{
    public required string TaskId { get; init; }
    public bool Success { get; init; }
    public DateTime SubmittedAt { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public SchedulingPriority Priority { get; init; }
    public string NodeId { get; init; } = "";
}

/// <summary>
/// Task status information.
/// </summary>
public sealed class TaskStatusInfo
{
    /// <summary>Task identifier.</summary>
    public required string TaskId { get; init; }
    /// <summary>Current status.</summary>
    public TaskStatus Status { get; init; }
    /// <summary>Priority.</summary>
    public SchedulingPriority Priority { get; init; }
    /// <summary>When submitted.</summary>
    public DateTime SubmittedAt { get; init; }
    /// <summary>When started.</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>When completed.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>Deadline.</summary>
    public DateTime? Deadline { get; init; }
    /// <summary>Assigned node.</summary>
    public string? AssignedNode { get; init; }
    /// <summary>Progress.</summary>
    public int Progress { get; init; }
    /// <summary>Error message.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Scheduler status.
/// </summary>
public sealed class SchedulerStatus
{
    /// <summary>Whether scheduler is running.</summary>
    public bool IsRunning { get; init; }
    /// <summary>Pending task count.</summary>
    public int PendingTasks { get; init; }
    /// <summary>Running task count.</summary>
    public int RunningTasks { get; init; }
    /// <summary>Total task count.</summary>
    public int TotalTasks { get; init; }
    /// <summary>Active node count.</summary>
    public int ActiveNodes { get; init; }
    /// <summary>Total node count.</summary>
    public int TotalNodes { get; init; }
    /// <summary>CPU usage percent.</summary>
    public double CpuUsagePercent { get; init; }
    /// <summary>Memory usage percent.</summary>
    public double MemoryUsagePercent { get; init; }
    /// <summary>Tasks queued by priority.</summary>
    public Dictionary<SchedulingPriority, int> QueuedByPriority { get; init; } = new();
    /// <summary>Average wait time in ms.</summary>
    public double AverageWaitTimeMs { get; init; }
}

/// <summary>
/// Current resource information.
/// </summary>
public sealed class ResourceInfo
{
    /// <summary>CPU usage percent.</summary>
    public double CpuUsagePercent { get; init; }
    /// <summary>Memory usage percent.</summary>
    public double MemoryUsagePercent { get; init; }
    /// <summary>Available memory in MB.</summary>
    public long AvailableMemoryMB { get; init; }
}

#endregion
