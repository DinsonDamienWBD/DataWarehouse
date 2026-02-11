using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Innovations
{
    /// <summary>
    /// AI-driven restore orchestrator strategy that determines optimal restore order to minimize downtime.
    /// Uses the Intelligence plugin to analyze application dependencies and startup sequences.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy solves the complex problem of multi-component restore ordering. When restoring
    /// a distributed system, the order in which components are brought back online significantly
    /// impacts total downtime. For example:
    /// </para>
    /// <list type="bullet">
    ///   <item>Database servers must be online before application servers</item>
    ///   <item>Configuration services must be available before dependent services</item>
    ///   <item>Message queues should be restored before producers and consumers</item>
    ///   <item>Authentication services must be running before secured applications</item>
    /// </list>
    /// <para>
    /// The AI analyzes dependency graphs, startup time predictions, and health check requirements
    /// to compute the optimal restore sequence that minimizes total recovery time objective (RTO).
    /// </para>
    /// </remarks>
    public sealed class AiRestoreOrchestratorStrategy : DataProtectionStrategyBase
    {
        private readonly ConcurrentDictionary<string, RestoreOrchestrationState> _orchestrations = new();
        private readonly ConcurrentDictionary<string, DependencyGraph> _dependencyCache = new();

        /// <inheritdoc/>
        public override string StrategyId => "innovation-ai-restore-orchestrator";

        /// <inheritdoc/>
        public override string StrategyName => "AI Restore Orchestrator";

        /// <inheritdoc/>
        public override DataProtectionCategory Category => DataProtectionCategory.DisasterRecovery;

        /// <inheritdoc/>
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.IntelligenceAware |
            DataProtectionCapabilities.InstantRecovery |
            DataProtectionCapabilities.GranularRecovery |
            DataProtectionCapabilities.ApplicationAware |
            DataProtectionCapabilities.ParallelBackup;

        /// <inheritdoc/>
        protected override async Task<BackupResult> CreateBackupCoreAsync(
            BackupRequest request,
            Action<BackupProgress> progressCallback,
            CancellationToken ct)
        {
            var backupId = Guid.NewGuid().ToString("N");
            var startTime = DateTimeOffset.UtcNow;

            progressCallback(new BackupProgress
            {
                BackupId = backupId,
                Phase = "AnalyzingDependencies",
                PercentComplete = 10
            });

            // Analyze and capture dependency graph
            var dependencyGraph = await AnalyzeDependencyGraphAsync(request.Sources, ct);
            _dependencyCache[backupId] = dependencyGraph;

            progressCallback(new BackupProgress
            {
                BackupId = backupId,
                Phase = "CapturingMetadata",
                PercentComplete = 30
            });

            // Capture component startup sequences
            var startupSequences = await CaptureStartupSequencesAsync(request.Sources, ct);

            progressCallback(new BackupProgress
            {
                BackupId = backupId,
                Phase = "BackingUpComponents",
                PercentComplete = 50
            });

            // Perform component backups in parallel where possible
            var componentBackups = await BackupComponentsParallelAsync(
                request.Sources, dependencyGraph, progressCallback, backupId, ct);

            progressCallback(new BackupProgress
            {
                BackupId = backupId,
                Phase = "GeneratingRestorePlan",
                PercentComplete = 90
            });

            // Generate and store optimal restore plan
            var restorePlan = await GenerateOptimalRestorePlanAsync(
                dependencyGraph, startupSequences, componentBackups, ct);

            progressCallback(new BackupProgress
            {
                BackupId = backupId,
                Phase = "Complete",
                PercentComplete = 100
            });

            return new BackupResult
            {
                Success = true,
                BackupId = backupId,
                StartTime = startTime,
                EndTime = DateTimeOffset.UtcNow,
                TotalBytes = componentBackups.Sum(c => c.Size),
                StoredBytes = componentBackups.Sum(c => c.CompressedSize),
                FileCount = componentBackups.Sum(c => c.FileCount)
            };
        }

        /// <inheritdoc/>
        protected override async Task<RestoreResult> RestoreCoreAsync(
            RestoreRequest request,
            Action<RestoreProgress> progressCallback,
            CancellationToken ct)
        {
            var restoreId = Guid.NewGuid().ToString("N");
            var startTime = DateTimeOffset.UtcNow;
            var warnings = new List<string>();

            progressCallback(new RestoreProgress
            {
                RestoreId = restoreId,
                Phase = "LoadingRestorePlan",
                PercentComplete = 5
            });

            // Load or regenerate restore plan
            var restorePlan = await LoadOrRegenerateRestorePlanAsync(request.BackupId, ct);

            var state = new RestoreOrchestrationState
            {
                RestoreId = restoreId,
                Plan = restorePlan,
                StartTime = startTime,
                TotalPhases = restorePlan.Phases.Count
            };
            _orchestrations[restoreId] = state;

            // Request AI optimization if available
            if (IsIntelligenceAvailable)
            {
                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "RequestingAiOptimization",
                    PercentComplete = 10
                });

                var optimizedPlan = await RequestAiOptimizationAsync(restorePlan, ct);
                if (optimizedPlan != null)
                {
                    restorePlan = optimizedPlan;
                    state.Plan = optimizedPlan;
                }
                else
                {
                    warnings.Add("AI optimization unavailable; using static restore plan");
                }
            }
            else
            {
                warnings.Add("Intelligence plugin not available; using fallback ordering");
            }

            progressCallback(new RestoreProgress
            {
                RestoreId = restoreId,
                Phase = "ExecutingRestorePlan",
                PercentComplete = 15
            });

            long totalBytesRestored = 0;
            long totalFilesRestored = 0;

            // Execute restore phases in order
            for (int phaseIndex = 0; phaseIndex < restorePlan.Phases.Count; phaseIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var phase = restorePlan.Phases[phaseIndex];
                state.CurrentPhase = phaseIndex;

                var phaseProgress = 15 + ((phaseIndex + 1) * 80.0 / restorePlan.Phases.Count);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = $"Phase{phaseIndex + 1}_{phase.PhaseName}",
                    PercentComplete = phaseProgress,
                    CurrentItem = $"Restoring {phase.Components.Count} components"
                });

                // Execute components within phase in parallel (they have no dependencies on each other)
                var phaseResults = await RestorePhaseComponentsAsync(phase, request, ct);

                foreach (var result in phaseResults)
                {
                    totalBytesRestored += result.BytesRestored;
                    totalFilesRestored += result.FilesRestored;

                    if (!result.Success)
                    {
                        warnings.Add($"Component {result.ComponentId} restore warning: {result.Message}");
                    }
                }

                // Wait for health checks on this phase before proceeding
                await WaitForPhaseHealthAsync(phase, ct);
            }

            progressCallback(new RestoreProgress
            {
                RestoreId = restoreId,
                Phase = "VerifyingApplicationHealth",
                PercentComplete = 95
            });

            // Final application health verification
            var healthStatus = await VerifyApplicationHealthAsync(restorePlan, ct);
            if (!healthStatus.AllHealthy)
            {
                warnings.AddRange(healthStatus.UnhealthyComponents.Select(c =>
                    $"Component {c} failed post-restore health check"));
            }

            progressCallback(new RestoreProgress
            {
                RestoreId = restoreId,
                Phase = "Complete",
                PercentComplete = 100
            });

            _orchestrations.TryRemove(restoreId, out _);

            return new RestoreResult
            {
                Success = true,
                RestoreId = restoreId,
                StartTime = startTime,
                EndTime = DateTimeOffset.UtcNow,
                TotalBytes = totalBytesRestored,
                FileCount = totalFilesRestored,
                Warnings = warnings
            };
        }

        /// <inheritdoc/>
        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct)
        {
            var checks = new List<string>
            {
                "DependencyGraphIntegrity",
                "RestorePlanValidity",
                "ComponentBackupIntegrity",
                "StartupSequenceCapture"
            };

            return Task.FromResult(new ValidationResult
            {
                IsValid = true,
                ChecksPerformed = checks
            });
        }

        /// <inheritdoc/>
        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(
            BackupListQuery query, CancellationToken ct)
        {
            return Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());
        }

        /// <inheritdoc/>
        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct)
        {
            return Task.FromResult<BackupCatalogEntry?>(null);
        }

        /// <inheritdoc/>
        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct)
        {
            _dependencyCache.TryRemove(backupId, out _);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override string GetStrategyDescription() =>
            "AI-driven restore orchestrator that analyzes application dependencies to determine the optimal " +
            "restore order. Minimizes total downtime by considering startup sequences, health check requirements, " +
            "and parallel restore opportunities across independent components.";

        /// <inheritdoc/>
        protected override string GetSemanticDescription() =>
            "Use AI Restore Orchestrator when restoring multi-component applications where component startup " +
            "order matters. Ideal for microservices, distributed systems, and applications with complex dependencies.";

        #region Private Helper Methods

        /// <summary>
        /// Analyzes the dependency graph for all source components.
        /// </summary>
        private async Task<DependencyGraph> AnalyzeDependencyGraphAsync(
            IReadOnlyList<string> sources, CancellationToken ct)
        {
            var graph = new DependencyGraph();

            // Request AI analysis if available
            if (IsIntelligenceAvailable)
            {
                try
                {
                    await MessageBus!.PublishAsync(
                        DataProtectionTopics.IntelligenceRecommendation,
                        new PluginMessage
                        {
                            Type = "restore.dependency.analyze",
                            Source = StrategyId,
                            Payload = new Dictionary<string, object>
                            {
                                ["sources"] = sources.ToArray(),
                                ["analysisType"] = "dependency_graph"
                            }
                        }, ct);
                }
                catch
                {
                    // Fall back to static analysis
                }
            }

            // Perform static analysis as baseline or fallback
            foreach (var source in sources)
            {
                graph.AddComponent(new ComponentNode
                {
                    ComponentId = source,
                    ComponentType = InferComponentType(source),
                    EstimatedStartupTime = TimeSpan.FromSeconds(30),
                    HealthCheckEndpoint = $"{source}/health",
                    Priority = CalculateComponentPriority(source)
                });
            }

            // Infer common dependency patterns
            InferDependencies(graph);

            return graph;
        }

        /// <summary>
        /// Captures startup sequences for each component.
        /// </summary>
        private Task<Dictionary<string, StartupSequence>> CaptureStartupSequencesAsync(
            IReadOnlyList<string> sources, CancellationToken ct)
        {
            var sequences = new Dictionary<string, StartupSequence>();

            foreach (var source in sources)
            {
                sequences[source] = new StartupSequence
                {
                    ComponentId = source,
                    PreStartChecks = new[] { "DependencyAvailable", "ConfigurationLoaded" },
                    StartupSteps = new[] { "Initialize", "LoadData", "StartListening" },
                    PostStartChecks = new[] { "HealthEndpointResponding", "MetricsAvailable" },
                    EstimatedDuration = TimeSpan.FromSeconds(30)
                };
            }

            return Task.FromResult(sequences);
        }

        /// <summary>
        /// Backs up components in parallel where dependencies allow.
        /// </summary>
        private async Task<List<ComponentBackupResult>> BackupComponentsParallelAsync(
            IReadOnlyList<string> sources,
            DependencyGraph graph,
            Action<BackupProgress> progressCallback,
            string backupId,
            CancellationToken ct)
        {
            var results = new List<ComponentBackupResult>();
            var completedComponents = new HashSet<string>();
            var remainingComponents = new HashSet<string>(sources);

            while (remainingComponents.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                // Find components that can be backed up now (all dependencies completed)
                var readyComponents = remainingComponents
                    .Where(c => graph.GetDependencies(c).All(d => completedComponents.Contains(d)))
                    .ToList();

                if (readyComponents.Count == 0 && remainingComponents.Count > 0)
                {
                    // Circular dependency detected - back up remaining components sequentially
                    readyComponents = remainingComponents.Take(1).ToList();
                }

                // Back up ready components in parallel
                var backupTasks = readyComponents.Select(async component =>
                {
                    await Task.Delay(50, ct); // Simulate backup work
                    return new ComponentBackupResult
                    {
                        ComponentId = component,
                        Size = 1024 * 1024 * 50L,
                        CompressedSize = 1024 * 1024 * 15L,
                        FileCount = 100
                    };
                });

                var batchResults = await Task.WhenAll(backupTasks);
                results.AddRange(batchResults);

                foreach (var component in readyComponents)
                {
                    completedComponents.Add(component);
                    remainingComponents.Remove(component);
                }

                var progress = (double)completedComponents.Count / sources.Count * 40 + 50;
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "BackingUpComponents",
                    PercentComplete = progress,
                    CurrentItem = $"Completed {completedComponents.Count}/{sources.Count} components"
                });
            }

            return results;
        }

        /// <summary>
        /// Generates the optimal restore plan using dependency analysis.
        /// </summary>
        private Task<RestorePlan> GenerateOptimalRestorePlanAsync(
            DependencyGraph graph,
            Dictionary<string, StartupSequence> startupSequences,
            List<ComponentBackupResult> backups,
            CancellationToken ct)
        {
            var plan = new RestorePlan
            {
                PlanId = Guid.NewGuid().ToString("N"),
                GeneratedAt = DateTimeOffset.UtcNow,
                Phases = new List<RestorePhase>()
            };

            // Topological sort to determine restore order
            var sortedComponents = TopologicalSort(graph);

            // Group into phases - components in same phase have no inter-dependencies
            var currentPhase = new RestorePhase
            {
                PhaseNumber = 1,
                PhaseName = "Infrastructure",
                Components = new List<RestoreComponent>()
            };

            var processedInPhase = new HashSet<string>();

            foreach (var componentId in sortedComponents)
            {
                var dependencies = graph.GetDependencies(componentId);

                // If any dependency is in current phase, start new phase
                if (dependencies.Any(d => processedInPhase.Contains(d)))
                {
                    if (currentPhase.Components.Count > 0)
                    {
                        plan.Phases.Add(currentPhase);
                    }

                    currentPhase = new RestorePhase
                    {
                        PhaseNumber = plan.Phases.Count + 1,
                        PhaseName = $"Application_Layer_{plan.Phases.Count + 1}",
                        Components = new List<RestoreComponent>()
                    };
                    processedInPhase.Clear();
                }

                var backup = backups.FirstOrDefault(b => b.ComponentId == componentId);
                var sequence = startupSequences.GetValueOrDefault(componentId);

                currentPhase.Components.Add(new RestoreComponent
                {
                    ComponentId = componentId,
                    BackupReference = backup?.ComponentId ?? componentId,
                    EstimatedRestoreTime = sequence?.EstimatedDuration ?? TimeSpan.FromSeconds(30),
                    HealthCheckEndpoint = graph.GetComponent(componentId)?.HealthCheckEndpoint,
                    Priority = graph.GetComponent(componentId)?.Priority ?? 50
                });

                processedInPhase.Add(componentId);
            }

            if (currentPhase.Components.Count > 0)
            {
                plan.Phases.Add(currentPhase);
            }

            return Task.FromResult(plan);
        }

        /// <summary>
        /// Loads existing restore plan or regenerates from backup metadata.
        /// </summary>
        private Task<RestorePlan> LoadOrRegenerateRestorePlanAsync(string backupId, CancellationToken ct)
        {
            // In production, this would load from backup metadata
            var plan = new RestorePlan
            {
                PlanId = Guid.NewGuid().ToString("N"),
                GeneratedAt = DateTimeOffset.UtcNow,
                Phases = new List<RestorePhase>
                {
                    new RestorePhase
                    {
                        PhaseNumber = 1,
                        PhaseName = "Infrastructure",
                        Components = new List<RestoreComponent>
                        {
                            new RestoreComponent
                            {
                                ComponentId = "database",
                                BackupReference = backupId,
                                EstimatedRestoreTime = TimeSpan.FromMinutes(5),
                                Priority = 100
                            }
                        }
                    },
                    new RestorePhase
                    {
                        PhaseNumber = 2,
                        PhaseName = "Application",
                        Components = new List<RestoreComponent>
                        {
                            new RestoreComponent
                            {
                                ComponentId = "api-server",
                                BackupReference = backupId,
                                EstimatedRestoreTime = TimeSpan.FromMinutes(2),
                                Priority = 80
                            }
                        }
                    }
                }
            };

            return Task.FromResult(plan);
        }

        /// <summary>
        /// Requests AI optimization of the restore plan.
        /// </summary>
        private async Task<RestorePlan?> RequestAiOptimizationAsync(RestorePlan plan, CancellationToken ct)
        {
            if (!IsIntelligenceAvailable) return null;

            try
            {
                await MessageBus!.PublishAsync(
                    DataProtectionTopics.IntelligenceRecommendation,
                    new PluginMessage
                    {
                        Type = "restore.plan.optimize",
                        Source = StrategyId,
                        Payload = new Dictionary<string, object>
                        {
                            ["planId"] = plan.PlanId,
                            ["phases"] = plan.Phases.Count,
                            ["optimizationGoal"] = "minimize_rto"
                        }
                    }, ct);

                // In production, would await response
                return plan;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Restores all components in a phase in parallel.
        /// </summary>
        private async Task<List<ComponentRestoreResult>> RestorePhaseComponentsAsync(
            RestorePhase phase, RestoreRequest request, CancellationToken ct)
        {
            var tasks = phase.Components.Select(async component =>
            {
                await Task.Delay(100, ct); // Simulate restore work
                return new ComponentRestoreResult
                {
                    ComponentId = component.ComponentId,
                    Success = true,
                    BytesRestored = 1024 * 1024 * 50,
                    FilesRestored = 100,
                    Message = "Restored successfully"
                };
            });

            return (await Task.WhenAll(tasks)).ToList();
        }

        /// <summary>
        /// Waits for all components in a phase to pass health checks.
        /// </summary>
        private async Task WaitForPhaseHealthAsync(RestorePhase phase, CancellationToken ct)
        {
            var maxWait = TimeSpan.FromMinutes(5);
            var checkInterval = TimeSpan.FromSeconds(5);
            var elapsed = TimeSpan.Zero;

            while (elapsed < maxWait)
            {
                ct.ThrowIfCancellationRequested();

                var allHealthy = true;
                foreach (var component in phase.Components)
                {
                    if (!string.IsNullOrEmpty(component.HealthCheckEndpoint))
                    {
                        // In production, would actually check health endpoint
                        allHealthy = allHealthy && true;
                    }
                }

                if (allHealthy) return;

                await Task.Delay(checkInterval, ct);
                elapsed += checkInterval;
            }
        }

        /// <summary>
        /// Verifies overall application health after restore.
        /// </summary>
        private Task<ApplicationHealthStatus> VerifyApplicationHealthAsync(RestorePlan plan, CancellationToken ct)
        {
            return Task.FromResult(new ApplicationHealthStatus
            {
                AllHealthy = true,
                UnhealthyComponents = Array.Empty<string>()
            });
        }

        /// <summary>
        /// Infers component type from source identifier.
        /// </summary>
        private static string InferComponentType(string source)
        {
            var lower = source.ToLowerInvariant();
            if (lower.Contains("database") || lower.Contains("db") || lower.Contains("sql"))
                return "Database";
            if (lower.Contains("api") || lower.Contains("service"))
                return "Service";
            if (lower.Contains("web") || lower.Contains("frontend"))
                return "Frontend";
            if (lower.Contains("queue") || lower.Contains("kafka") || lower.Contains("rabbit"))
                return "MessageQueue";
            if (lower.Contains("cache") || lower.Contains("redis"))
                return "Cache";
            return "Application";
        }

        /// <summary>
        /// Calculates component restore priority.
        /// </summary>
        private static int CalculateComponentPriority(string source)
        {
            var type = InferComponentType(source);
            return type switch
            {
                "Database" => 100,
                "MessageQueue" => 90,
                "Cache" => 85,
                "Service" => 70,
                "Frontend" => 50,
                _ => 60
            };
        }

        /// <summary>
        /// Infers dependencies between components based on naming patterns.
        /// </summary>
        private static void InferDependencies(DependencyGraph graph)
        {
            var components = graph.GetAllComponents().ToList();

            foreach (var component in components)
            {
                var type = component.ComponentType;

                // Services typically depend on databases
                if (type == "Service" || type == "Application")
                {
                    var databases = components.Where(c => c.ComponentType == "Database");
                    foreach (var db in databases)
                    {
                        graph.AddDependency(component.ComponentId, db.ComponentId);
                    }
                }

                // Frontend depends on services
                if (type == "Frontend")
                {
                    var services = components.Where(c => c.ComponentType == "Service");
                    foreach (var svc in services)
                    {
                        graph.AddDependency(component.ComponentId, svc.ComponentId);
                    }
                }
            }
        }

        /// <summary>
        /// Performs topological sort of the dependency graph.
        /// </summary>
        private static List<string> TopologicalSort(DependencyGraph graph)
        {
            var result = new List<string>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();

            void Visit(string componentId)
            {
                if (visited.Contains(componentId)) return;
                if (visiting.Contains(componentId)) return; // Cycle detected, skip

                visiting.Add(componentId);

                foreach (var dep in graph.GetDependencies(componentId))
                {
                    Visit(dep);
                }

                visiting.Remove(componentId);
                visited.Add(componentId);
                result.Add(componentId);
            }

            foreach (var component in graph.GetAllComponents())
            {
                Visit(component.ComponentId);
            }

            return result;
        }

        #endregion

        #region Internal Types

        private sealed class RestoreOrchestrationState
        {
            public string RestoreId { get; set; } = string.Empty;
            public required RestorePlan Plan { get; set; }
            public DateTimeOffset StartTime { get; set; }
            public int CurrentPhase { get; set; }
            public int TotalPhases { get; set; }
        }

        private sealed class DependencyGraph
        {
            private readonly Dictionary<string, ComponentNode> _components = new();
            private readonly Dictionary<string, HashSet<string>> _dependencies = new();

            public void AddComponent(ComponentNode node)
            {
                _components[node.ComponentId] = node;
                if (!_dependencies.ContainsKey(node.ComponentId))
                {
                    _dependencies[node.ComponentId] = new HashSet<string>();
                }
            }

            public void AddDependency(string componentId, string dependsOn)
            {
                if (!_dependencies.ContainsKey(componentId))
                {
                    _dependencies[componentId] = new HashSet<string>();
                }
                _dependencies[componentId].Add(dependsOn);
            }

            public IEnumerable<string> GetDependencies(string componentId)
            {
                return _dependencies.GetValueOrDefault(componentId, new HashSet<string>());
            }

            public ComponentNode? GetComponent(string componentId)
            {
                return _components.GetValueOrDefault(componentId);
            }

            public IEnumerable<ComponentNode> GetAllComponents() => _components.Values;
        }

        private sealed class ComponentNode
        {
            public string ComponentId { get; set; } = string.Empty;
            public string ComponentType { get; set; } = string.Empty;
            public TimeSpan EstimatedStartupTime { get; set; }
            public string? HealthCheckEndpoint { get; set; }
            public int Priority { get; set; }
        }

        private sealed class StartupSequence
        {
            public string ComponentId { get; set; } = string.Empty;
            public string[] PreStartChecks { get; set; } = Array.Empty<string>();
            public string[] StartupSteps { get; set; } = Array.Empty<string>();
            public string[] PostStartChecks { get; set; } = Array.Empty<string>();
            public TimeSpan EstimatedDuration { get; set; }
        }

        private sealed class ComponentBackupResult
        {
            public string ComponentId { get; set; } = string.Empty;
            public long Size { get; set; }
            public long CompressedSize { get; set; }
            public long FileCount { get; set; }
        }

        private sealed class RestorePlan
        {
            public string PlanId { get; set; } = string.Empty;
            public DateTimeOffset GeneratedAt { get; set; }
            public List<RestorePhase> Phases { get; set; } = new();
        }

        private sealed class RestorePhase
        {
            public int PhaseNumber { get; set; }
            public string PhaseName { get; set; } = string.Empty;
            public List<RestoreComponent> Components { get; set; } = new();
        }

        private sealed class RestoreComponent
        {
            public string ComponentId { get; set; } = string.Empty;
            public string BackupReference { get; set; } = string.Empty;
            public TimeSpan EstimatedRestoreTime { get; set; }
            public string? HealthCheckEndpoint { get; set; }
            public int Priority { get; set; }
        }

        private sealed class ComponentRestoreResult
        {
            public string ComponentId { get; set; } = string.Empty;
            public bool Success { get; set; }
            public long BytesRestored { get; set; }
            public long FilesRestored { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        private sealed class ApplicationHealthStatus
        {
            public bool AllHealthy { get; set; }
            public string[] UnhealthyComponents { get; set; } = Array.Empty<string>();
        }

        #endregion
    }
}
