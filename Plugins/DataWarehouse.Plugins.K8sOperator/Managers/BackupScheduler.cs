using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.K8sOperator.Managers;

/// <summary>
/// Manages backup scheduling for DataWarehouse Kubernetes resources.
/// Provides CronJob resource creation, backup retention policy management,
/// cross-namespace backup support, Velero integration, and backup verification jobs.
/// </summary>
public sealed class BackupScheduler
{
    private readonly IKubernetesClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the BackupScheduler.
    /// </summary>
    /// <param name="client">Kubernetes client for API interactions.</param>
    public BackupScheduler(IKubernetesClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Creates a CronJob for scheduled backups.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="backupName">Name of the backup job.</param>
    /// <param name="config">Backup schedule configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the CronJob creation.</returns>
    public async Task<BackupScheduleResult> CreateBackupCronJobAsync(
        string namespaceName,
        string backupName,
        BackupScheduleConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupName);
        ArgumentNullException.ThrowIfNull(config);

        var cronJob = new CronJob
        {
            ApiVersion = "batch/v1",
            Kind = "CronJob",
            Metadata = new ObjectMeta
            {
                Name = $"{backupName}-backup",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/component"] = "backup",
                    ["datawarehouse.io/backup-name"] = backupName
                },
                Annotations = new Dictionary<string, string>
                {
                    ["datawarehouse.io/retention-days"] = config.RetentionDays.ToString(),
                    ["datawarehouse.io/backup-type"] = config.BackupType.ToString()
                }
            },
            Spec = new CronJobSpec
            {
                Schedule = config.Schedule,
                TimeZone = config.TimeZone,
                ConcurrencyPolicy = config.ConcurrencyPolicy.ToString(),
                StartingDeadlineSeconds = config.StartingDeadlineSeconds,
                SuccessfulJobsHistoryLimit = config.SuccessfulJobsHistoryLimit,
                FailedJobsHistoryLimit = config.FailedJobsHistoryLimit,
                Suspend = config.Suspended,
                JobTemplate = new JobTemplateSpec
                {
                    Spec = new JobSpec
                    {
                        BackoffLimit = config.BackoffLimit,
                        ActiveDeadlineSeconds = config.ActiveDeadlineSeconds,
                        TtlSecondsAfterFinished = config.TtlSecondsAfterFinished,
                        Template = new PodTemplateSpec
                        {
                            Metadata = new ObjectMeta
                            {
                                Labels = new Dictionary<string, string>
                                {
                                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                                    ["app.kubernetes.io/component"] = "backup-job"
                                }
                            },
                            Spec = new PodSpec
                            {
                                RestartPolicy = "OnFailure",
                                ServiceAccountName = config.ServiceAccountName ?? "datawarehouse-backup",
                                Containers = new List<Container>
                                {
                                    new Container
                                    {
                                        Name = "backup",
                                        Image = config.BackupImage ?? "datawarehouse/backup:latest",
                                        ImagePullPolicy = "IfNotPresent",
                                        Command = BuildBackupCommand(config),
                                        Env = BuildEnvironmentVariables(config),
                                        VolumeMounts = BuildVolumeMounts(config),
                                        Resources = new ResourceRequirements
                                        {
                                            Requests = new Dictionary<string, string>
                                            {
                                                ["cpu"] = config.CpuRequest ?? "100m",
                                                ["memory"] = config.MemoryRequest ?? "256Mi"
                                            },
                                            Limits = new Dictionary<string, string>
                                            {
                                                ["cpu"] = config.CpuLimit ?? "1000m",
                                                ["memory"] = config.MemoryLimit ?? "1Gi"
                                            }
                                        }
                                    }
                                },
                                Volumes = BuildVolumes(config)
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(cronJob, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "batch/v1",
            "cronjobs",
            namespaceName,
            $"{backupName}-backup",
            json,
            ct);

        return new BackupScheduleResult
        {
            Success = result.Success,
            CronJobName = $"{backupName}-backup",
            Schedule = config.Schedule,
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a Velero backup schedule for cluster-level backups.
    /// </summary>
    /// <param name="backupName">Name of the backup.</param>
    /// <param name="config">Velero backup configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the Velero schedule creation.</returns>
    public async Task<BackupScheduleResult> CreateVeleroBackupScheduleAsync(
        string backupName,
        VeleroBackupConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupName);
        ArgumentNullException.ThrowIfNull(config);

        var schedule = new VeleroSchedule
        {
            ApiVersion = "velero.io/v1",
            Kind = "Schedule",
            Metadata = new ObjectMeta
            {
                Name = backupName,
                Namespace = config.VeleroNamespace ?? "velero",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            Spec = new VeleroScheduleSpec
            {
                Schedule = config.Schedule,
                UseOwnerReferencesInBackup = config.UseOwnerReferencesInBackup,
                Template = new VeleroBackupSpec
                {
                    IncludedNamespaces = config.IncludedNamespaces,
                    ExcludedNamespaces = config.ExcludedNamespaces,
                    IncludedResources = config.IncludedResources,
                    ExcludedResources = config.ExcludedResources,
                    IncludeClusterResources = config.IncludeClusterResources,
                    LabelSelector = config.LabelSelector != null ? new LabelSelector
                    {
                        MatchLabels = config.LabelSelector
                    } : null,
                    StorageLocation = config.StorageLocation,
                    VolumeSnapshotLocations = config.VolumeSnapshotLocations,
                    Ttl = config.RetentionDuration ?? "720h", // 30 days default
                    SnapshotVolumes = config.SnapshotVolumes,
                    DefaultVolumesToFsBackup = config.DefaultVolumesToFsBackup,
                    Hooks = BuildVeleroHooks(config)
                }
            }
        };

        var json = JsonSerializer.Serialize(schedule, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "velero.io/v1",
            "schedules",
            config.VeleroNamespace ?? "velero",
            backupName,
            json,
            ct);

        return new BackupScheduleResult
        {
            Success = result.Success,
            CronJobName = backupName,
            Schedule = config.Schedule,
            Message = result.Message,
            IsVeleroBackup = true
        };
    }

    /// <summary>
    /// Creates a backup verification job.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="backupName">Name of the backup to verify.</param>
    /// <param name="config">Verification configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the verification job creation.</returns>
    public async Task<BackupScheduleResult> CreateBackupVerificationJobAsync(
        string namespaceName,
        string backupName,
        BackupVerificationConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupName);
        ArgumentNullException.ThrowIfNull(config);

        var job = new Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new ObjectMeta
            {
                Name = $"{backupName}-verify-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/component"] = "backup-verification",
                    ["datawarehouse.io/backup-name"] = backupName
                }
            },
            Spec = new JobSpec
            {
                BackoffLimit = 3,
                TtlSecondsAfterFinished = 3600,
                Template = new PodTemplateSpec
                {
                    Spec = new PodSpec
                    {
                        RestartPolicy = "Never",
                        ServiceAccountName = config.ServiceAccountName ?? "datawarehouse-backup",
                        Containers = new List<Container>
                        {
                            new Container
                            {
                                Name = "verify",
                                Image = config.VerificationImage ?? "datawarehouse/backup:latest",
                                ImagePullPolicy = "IfNotPresent",
                                Command = new List<string>
                                {
                                    "/bin/sh", "-c",
                                    BuildVerificationScript(config)
                                },
                                Env = new List<EnvVar>
                                {
                                    new EnvVar { Name = "BACKUP_NAME", Value = backupName },
                                    new EnvVar { Name = "BACKUP_LOCATION", Value = config.BackupLocation },
                                    new EnvVar { Name = "VERIFICATION_TYPE", Value = config.VerificationType.ToString() }
                                },
                                Resources = new ResourceRequirements
                                {
                                    Requests = new Dictionary<string, string>
                                    {
                                        ["cpu"] = "100m",
                                        ["memory"] = "256Mi"
                                    },
                                    Limits = new Dictionary<string, string>
                                    {
                                        ["cpu"] = "500m",
                                        ["memory"] = "512Mi"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(job, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "batch/v1",
            "jobs",
            namespaceName,
            job.Metadata.Name,
            json,
            ct);

        return new BackupScheduleResult
        {
            Success = result.Success,
            CronJobName = job.Metadata.Name,
            Message = result.Message,
            IsVerificationJob = true
        };
    }

    /// <summary>
    /// Configures backup retention policy.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="backupName">Name of the backup configuration.</param>
    /// <param name="config">Retention policy configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the policy configuration.</returns>
    public async Task<BackupScheduleResult> ConfigureRetentionPolicyAsync(
        string namespaceName,
        string backupName,
        RetentionPolicyConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupName);
        ArgumentNullException.ThrowIfNull(config);

        // Create a cleanup CronJob that enforces retention policy
        var cleanupJob = new CronJob
        {
            ApiVersion = "batch/v1",
            Kind = "CronJob",
            Metadata = new ObjectMeta
            {
                Name = $"{backupName}-cleanup",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/component"] = "backup-cleanup"
                }
            },
            Spec = new CronJobSpec
            {
                Schedule = config.CleanupSchedule ?? "0 2 * * *", // 2 AM daily
                ConcurrencyPolicy = "Forbid",
                SuccessfulJobsHistoryLimit = 1,
                FailedJobsHistoryLimit = 3,
                JobTemplate = new JobTemplateSpec
                {
                    Spec = new JobSpec
                    {
                        BackoffLimit = 3,
                        TtlSecondsAfterFinished = 3600,
                        Template = new PodTemplateSpec
                        {
                            Spec = new PodSpec
                            {
                                RestartPolicy = "OnFailure",
                                ServiceAccountName = config.ServiceAccountName ?? "datawarehouse-backup",
                                Containers = new List<Container>
                                {
                                    new Container
                                    {
                                        Name = "cleanup",
                                        Image = config.CleanupImage ?? "datawarehouse/backup:latest",
                                        ImagePullPolicy = "IfNotPresent",
                                        Command = new List<string>
                                        {
                                            "/bin/sh", "-c",
                                            BuildRetentionScript(config)
                                        },
                                        Env = new List<EnvVar>
                                        {
                                            new EnvVar { Name = "BACKUP_NAME", Value = backupName },
                                            new EnvVar { Name = "RETENTION_DAYS", Value = config.RetentionDays.ToString() },
                                            new EnvVar { Name = "MIN_BACKUPS", Value = config.MinBackupsToKeep.ToString() },
                                            new EnvVar { Name = "MAX_BACKUPS", Value = config.MaxBackupsToKeep.ToString() }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(cleanupJob, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "batch/v1",
            "cronjobs",
            namespaceName,
            $"{backupName}-cleanup",
            json,
            ct);

        return new BackupScheduleResult
        {
            Success = result.Success,
            CronJobName = $"{backupName}-cleanup",
            Schedule = config.CleanupSchedule ?? "0 2 * * *",
            Message = result.Message
        };
    }

    /// <summary>
    /// Triggers an immediate backup.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="cronJobName">Name of the CronJob to trigger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the trigger operation.</returns>
    public async Task<BackupScheduleResult> TriggerImmediateBackupAsync(
        string namespaceName,
        string cronJobName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronJobName);

        // Get the CronJob
        var cronJobResult = await _client.GetResourceAsync(
            "batch/v1",
            "cronjobs",
            namespaceName,
            cronJobName,
            ct);

        if (!cronJobResult.Found)
        {
            return new BackupScheduleResult
            {
                Success = false,
                Message = $"CronJob {cronJobName} not found"
            };
        }

        // Create a Job from the CronJob template
        var cronJob = JsonSerializer.Deserialize<JsonElement>(cronJobResult.Json!);
        var jobTemplate = cronJob.GetProperty("spec").GetProperty("jobTemplate");

        var job = new Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new ObjectMeta
            {
                Name = $"{cronJobName}-manual-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["datawarehouse.io/triggered-by"] = "manual"
                },
                Annotations = new Dictionary<string, string>
                {
                    ["cronjob.kubernetes.io/instantiate"] = "manual"
                }
            },
            Spec = JsonSerializer.Deserialize<JobSpec>(jobTemplate.GetProperty("spec").GetRawText(), _jsonOptions)!
        };

        var json = JsonSerializer.Serialize(job, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "batch/v1",
            "jobs",
            namespaceName,
            job.Metadata.Name,
            json,
            ct);

        return new BackupScheduleResult
        {
            Success = result.Success,
            CronJobName = job.Metadata.Name,
            Message = result.Success ? "Backup triggered successfully" : result.Message
        };
    }

    /// <summary>
    /// Lists backup jobs and their status.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="backupName">Optional backup name filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of backup job statuses.</returns>
    public async Task<BackupListResult> ListBackupJobsAsync(
        string namespaceName,
        string? backupName = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);

        var labelSelector = "app.kubernetes.io/component=backup-job";
        if (!string.IsNullOrEmpty(backupName))
        {
            labelSelector += $",datawarehouse.io/backup-name={backupName}";
        }

        var result = await _client.ListResourcesAsync(
            "batch/v1",
            "jobs",
            namespaceName,
            labelSelector,
            ct);

        if (!result.Success)
        {
            return new BackupListResult
            {
                Success = false,
                Message = result.Error ?? "Failed to list backup jobs"
            };
        }

        var jobs = new List<BackupJobStatus>();
        foreach (var item in result.Items)
        {
            var job = JsonSerializer.Deserialize<JsonElement>(item);
            jobs.Add(ParseJobStatus(job));
        }

        return new BackupListResult
        {
            Success = true,
            Jobs = jobs
        };
    }

    /// <summary>
    /// Deletes a backup schedule.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="backupName">Name of the backup schedule.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the deletion.</returns>
    public async Task<BackupScheduleResult> DeleteBackupScheduleAsync(
        string namespaceName,
        string backupName,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        // Delete backup CronJob
        var backupResult = await _client.DeleteResourceAsync(
            "batch/v1",
            "cronjobs",
            namespaceName,
            $"{backupName}-backup",
            ct);
        if (!backupResult.Success && !backupResult.NotFound)
            errors.Add($"Backup CronJob: {backupResult.Message}");

        // Delete cleanup CronJob
        var cleanupResult = await _client.DeleteResourceAsync(
            "batch/v1",
            "cronjobs",
            namespaceName,
            $"{backupName}-cleanup",
            ct);
        if (!cleanupResult.Success && !cleanupResult.NotFound)
            errors.Add($"Cleanup CronJob: {cleanupResult.Message}");

        return new BackupScheduleResult
        {
            Success = errors.Count == 0,
            CronJobName = backupName,
            Message = errors.Count > 0 ? string.Join("; ", errors) : "Backup schedule deleted"
        };
    }

    private List<string> BuildBackupCommand(BackupScheduleConfig config)
    {
        var command = new List<string>
        {
            "/usr/local/bin/datawarehouse-backup",
            "--type", config.BackupType.ToString().ToLower(),
            "--destination", config.Destination
        };

        if (config.Compression)
            command.AddRange(new[] { "--compression", config.CompressionLevel.ToString() });

        if (config.Encryption)
            command.AddRange(new[] { "--encryption", config.EncryptionKeySecret ?? "backup-encryption-key" });

        if (config.IncludeNamespaces?.Count > 0)
            command.AddRange(new[] { "--namespaces", string.Join(",", config.IncludeNamespaces) });

        if (!string.IsNullOrEmpty(config.LabelSelector))
            command.AddRange(new[] { "--selector", config.LabelSelector });

        return command;
    }

    private List<EnvVar> BuildEnvironmentVariables(BackupScheduleConfig config)
    {
        var env = new List<EnvVar>
        {
            new EnvVar { Name = "BACKUP_NAME", Value = config.BackupType.ToString() },
            new EnvVar { Name = "BACKUP_DESTINATION", Value = config.Destination }
        };

        if (!string.IsNullOrEmpty(config.S3BucketSecret))
        {
            env.Add(new EnvVar
            {
                Name = "AWS_ACCESS_KEY_ID",
                ValueFrom = new EnvVarSource
                {
                    SecretKeyRef = new SecretKeySelector
                    {
                        Name = config.S3BucketSecret,
                        Key = "access-key"
                    }
                }
            });
            env.Add(new EnvVar
            {
                Name = "AWS_SECRET_ACCESS_KEY",
                ValueFrom = new EnvVarSource
                {
                    SecretKeyRef = new SecretKeySelector
                    {
                        Name = config.S3BucketSecret,
                        Key = "secret-key"
                    }
                }
            });
        }

        return env;
    }

    private List<VolumeMount> BuildVolumeMounts(BackupScheduleConfig config)
    {
        var mounts = new List<VolumeMount>();

        if (config.MountDataVolume)
        {
            mounts.Add(new VolumeMount
            {
                Name = "data",
                MountPath = "/data",
                ReadOnly = true
            });
        }

        if (!string.IsNullOrEmpty(config.EncryptionKeySecret))
        {
            mounts.Add(new VolumeMount
            {
                Name = "encryption-key",
                MountPath = "/etc/backup/encryption",
                ReadOnly = true
            });
        }

        return mounts;
    }

    private List<Volume> BuildVolumes(BackupScheduleConfig config)
    {
        var volumes = new List<Volume>();

        if (config.MountDataVolume && !string.IsNullOrEmpty(config.DataPvcName))
        {
            volumes.Add(new Volume
            {
                Name = "data",
                PersistentVolumeClaim = new PersistentVolumeClaimVolumeSource
                {
                    ClaimName = config.DataPvcName
                }
            });
        }

        if (!string.IsNullOrEmpty(config.EncryptionKeySecret))
        {
            volumes.Add(new Volume
            {
                Name = "encryption-key",
                Secret = new SecretVolumeSource
                {
                    SecretName = config.EncryptionKeySecret
                }
            });
        }

        return volumes;
    }

    private VeleroBackupHooks? BuildVeleroHooks(VeleroBackupConfig config)
    {
        if (config.PreBackupHooks.Count == 0 && config.PostBackupHooks.Count == 0)
            return null;

        return new VeleroBackupHooks
        {
            Resources = config.PreBackupHooks.Concat(config.PostBackupHooks)
                .Select(h => new VeleroResourceHook
                {
                    Name = h.Name,
                    IncludedNamespaces = h.IncludedNamespaces,
                    LabelSelector = h.LabelSelector != null ? new LabelSelector
                    {
                        MatchLabels = h.LabelSelector
                    } : null,
                    Pre = h.Phase == HookPhase.Pre ? new List<VeleroExecHook>
                    {
                        new VeleroExecHook
                        {
                            Container = h.Container,
                            Command = h.Command,
                            OnError = h.OnError.ToString(),
                            Timeout = h.TimeoutSeconds > 0 ? $"{h.TimeoutSeconds}s" : null
                        }
                    } : null,
                    Post = h.Phase == HookPhase.Post ? new List<VeleroExecHook>
                    {
                        new VeleroExecHook
                        {
                            Container = h.Container,
                            Command = h.Command,
                            OnError = h.OnError.ToString(),
                            Timeout = h.TimeoutSeconds > 0 ? $"{h.TimeoutSeconds}s" : null
                        }
                    } : null
                }).ToList()
        };
    }

    private string BuildVerificationScript(BackupVerificationConfig config)
    {
        return config.VerificationType switch
        {
            VerificationType.Checksum => @"
                echo 'Verifying backup checksums...'
                datawarehouse-backup verify --type checksum --location ""$BACKUP_LOCATION"" --name ""$BACKUP_NAME""
                exit $?
            ",
            VerificationType.Restore => @"
                echo 'Performing test restore...'
                datawarehouse-backup verify --type restore --location ""$BACKUP_LOCATION"" --name ""$BACKUP_NAME"" --target /tmp/restore-test
                RESULT=$?
                rm -rf /tmp/restore-test
                exit $RESULT
            ",
            VerificationType.Integrity => @"
                echo 'Checking backup integrity...'
                datawarehouse-backup verify --type integrity --location ""$BACKUP_LOCATION"" --name ""$BACKUP_NAME""
                exit $?
            ",
            _ => @"
                echo 'Running full backup verification...'
                datawarehouse-backup verify --type full --location ""$BACKUP_LOCATION"" --name ""$BACKUP_NAME""
                exit $?
            "
        };
    }

    private string BuildRetentionScript(RetentionPolicyConfig config)
    {
        return $@"
            echo 'Enforcing backup retention policy...'
            datawarehouse-backup cleanup \
                --name ""$BACKUP_NAME"" \
                --retention-days ""$RETENTION_DAYS"" \
                --min-keep ""$MIN_BACKUPS"" \
                --max-keep ""$MAX_BACKUPS"" \
                {(config.DryRun ? "--dry-run" : "")}
            exit $?
        ";
    }

    private BackupJobStatus ParseJobStatus(JsonElement job)
    {
        var metadata = job.GetProperty("metadata");
        var status = job.TryGetProperty("status", out var s) ? s : default;

        var name = metadata.GetProperty("name").GetString() ?? "";
        var ns = metadata.GetProperty("namespace").GetString() ?? "";
        var createdAt = metadata.TryGetProperty("creationTimestamp", out var ct)
            ? DateTime.Parse(ct.GetString()!)
            : DateTime.MinValue;

        var succeeded = status.TryGetProperty("succeeded", out var succ) ? succ.GetInt32() : 0;
        var failed = status.TryGetProperty("failed", out var fail) ? fail.GetInt32() : 0;
        var active = status.TryGetProperty("active", out var act) ? act.GetInt32() : 0;

        var completedAt = status.TryGetProperty("completionTime", out var ctime)
            ? DateTime.Parse(ctime.GetString()!)
            : (DateTime?)null;

        var phase = active > 0 ? BackupJobPhase.Running
            : succeeded > 0 ? BackupJobPhase.Succeeded
            : failed > 0 ? BackupJobPhase.Failed
            : BackupJobPhase.Pending;

        return new BackupJobStatus
        {
            Name = name,
            Namespace = ns,
            Phase = phase,
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Succeeded = succeeded,
            Failed = failed,
            Active = active
        };
    }
}

#region Configuration Classes

/// <summary>Configuration for backup scheduling.</summary>
public sealed class BackupScheduleConfig
{
    /// <summary>Cron schedule expression.</summary>
    public string Schedule { get; set; } = "0 0 * * *"; // Daily at midnight

    /// <summary>Timezone for the schedule (e.g., "America/New_York").</summary>
    public string? TimeZone { get; set; }

    /// <summary>Type of backup.</summary>
    public BackupType BackupType { get; set; } = BackupType.Full;

    /// <summary>Backup destination (S3 URL, local path, etc.).</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Number of days to retain backups.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Concurrency policy (Allow, Forbid, Replace).</summary>
    public ConcurrencyPolicy ConcurrencyPolicy { get; set; } = ConcurrencyPolicy.Forbid;

    /// <summary>Deadline in seconds for starting the job.</summary>
    public long? StartingDeadlineSeconds { get; set; }

    /// <summary>Number of successful jobs to keep in history.</summary>
    public int SuccessfulJobsHistoryLimit { get; set; } = 3;

    /// <summary>Number of failed jobs to keep in history.</summary>
    public int FailedJobsHistoryLimit { get; set; } = 1;

    /// <summary>Whether the CronJob is suspended.</summary>
    public bool Suspended { get; set; }

    /// <summary>Number of retries before marking job as failed.</summary>
    public int BackoffLimit { get; set; } = 3;

    /// <summary>Maximum time for the job to run.</summary>
    public long? ActiveDeadlineSeconds { get; set; }

    /// <summary>Seconds to retain completed jobs.</summary>
    public int? TtlSecondsAfterFinished { get; set; }

    /// <summary>ServiceAccount name for the backup pod.</summary>
    public string? ServiceAccountName { get; set; }

    /// <summary>Backup container image.</summary>
    public string? BackupImage { get; set; }

    /// <summary>Whether to enable compression.</summary>
    public bool Compression { get; set; } = true;

    /// <summary>Compression level (1-9).</summary>
    public int CompressionLevel { get; set; } = 6;

    /// <summary>Whether to enable encryption.</summary>
    public bool Encryption { get; set; } = true;

    /// <summary>Secret name containing encryption key.</summary>
    public string? EncryptionKeySecret { get; set; }

    /// <summary>Secret name containing S3 credentials.</summary>
    public string? S3BucketSecret { get; set; }

    /// <summary>Namespaces to include in backup.</summary>
    public List<string>? IncludeNamespaces { get; set; }

    /// <summary>Label selector for resources to backup.</summary>
    public string? LabelSelector { get; set; }

    /// <summary>Whether to mount data volume.</summary>
    public bool MountDataVolume { get; set; }

    /// <summary>Name of the data PVC to mount.</summary>
    public string? DataPvcName { get; set; }

    /// <summary>CPU resource request.</summary>
    public string? CpuRequest { get; set; }

    /// <summary>Memory resource request.</summary>
    public string? MemoryRequest { get; set; }

    /// <summary>CPU resource limit.</summary>
    public string? CpuLimit { get; set; }

    /// <summary>Memory resource limit.</summary>
    public string? MemoryLimit { get; set; }
}

/// <summary>Backup type enumeration.</summary>
public enum BackupType
{
    /// <summary>Full backup of all data.</summary>
    Full,
    /// <summary>Incremental backup since last full backup.</summary>
    Incremental,
    /// <summary>Differential backup since last backup.</summary>
    Differential,
    /// <summary>Snapshot-based backup.</summary>
    Snapshot
}

/// <summary>Concurrency policy for CronJobs.</summary>
public enum ConcurrencyPolicy
{
    /// <summary>Allow concurrent jobs.</summary>
    Allow,
    /// <summary>Forbid concurrent jobs.</summary>
    Forbid,
    /// <summary>Replace existing job with new one.</summary>
    Replace
}

/// <summary>Configuration for Velero backups.</summary>
public sealed class VeleroBackupConfig
{
    /// <summary>Cron schedule expression.</summary>
    public string Schedule { get; set; } = "0 0 * * *";

    /// <summary>Velero namespace.</summary>
    public string? VeleroNamespace { get; set; }

    /// <summary>Namespaces to include in backup.</summary>
    public List<string>? IncludedNamespaces { get; set; }

    /// <summary>Namespaces to exclude from backup.</summary>
    public List<string>? ExcludedNamespaces { get; set; }

    /// <summary>Resources to include in backup.</summary>
    public List<string>? IncludedResources { get; set; }

    /// <summary>Resources to exclude from backup.</summary>
    public List<string>? ExcludedResources { get; set; }

    /// <summary>Whether to include cluster-scoped resources.</summary>
    public bool? IncludeClusterResources { get; set; }

    /// <summary>Label selector for resources.</summary>
    public Dictionary<string, string>? LabelSelector { get; set; }

    /// <summary>Backup storage location name.</summary>
    public string? StorageLocation { get; set; }

    /// <summary>Volume snapshot location names.</summary>
    public List<string>? VolumeSnapshotLocations { get; set; }

    /// <summary>Backup retention duration (e.g., "720h").</summary>
    public string? RetentionDuration { get; set; }

    /// <summary>Whether to snapshot volumes.</summary>
    public bool? SnapshotVolumes { get; set; }

    /// <summary>Whether to use filesystem backup for volumes by default.</summary>
    public bool? DefaultVolumesToFsBackup { get; set; }

    /// <summary>Whether to use owner references in backup.</summary>
    public bool? UseOwnerReferencesInBackup { get; set; }

    /// <summary>Pre-backup hooks.</summary>
    public List<BackupHookConfig> PreBackupHooks { get; set; } = new();

    /// <summary>Post-backup hooks.</summary>
    public List<BackupHookConfig> PostBackupHooks { get; set; } = new();
}

/// <summary>Configuration for backup hooks.</summary>
public sealed class BackupHookConfig
{
    /// <summary>Hook name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hook phase (Pre or Post).</summary>
    public HookPhase Phase { get; set; } = HookPhase.Pre;

    /// <summary>Namespaces to apply hook to.</summary>
    public List<string>? IncludedNamespaces { get; set; }

    /// <summary>Label selector for pods.</summary>
    public Dictionary<string, string>? LabelSelector { get; set; }

    /// <summary>Container to execute hook in.</summary>
    public string? Container { get; set; }

    /// <summary>Command to execute.</summary>
    public List<string> Command { get; set; } = new();

    /// <summary>Action on error.</summary>
    public HookErrorAction OnError { get; set; } = HookErrorAction.Fail;

    /// <summary>Timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Hook phase.</summary>
public enum HookPhase { Pre, Post }

/// <summary>Action to take on hook error.</summary>
public enum HookErrorAction { Continue, Fail }

/// <summary>Configuration for backup verification.</summary>
public sealed class BackupVerificationConfig
{
    /// <summary>Backup location to verify.</summary>
    public string BackupLocation { get; set; } = string.Empty;

    /// <summary>Type of verification to perform.</summary>
    public VerificationType VerificationType { get; set; } = VerificationType.Checksum;

    /// <summary>Verification container image.</summary>
    public string? VerificationImage { get; set; }

    /// <summary>ServiceAccount name.</summary>
    public string? ServiceAccountName { get; set; }
}

/// <summary>Verification type enumeration.</summary>
public enum VerificationType
{
    /// <summary>Verify checksums only.</summary>
    Checksum,
    /// <summary>Perform test restore.</summary>
    Restore,
    /// <summary>Check backup integrity.</summary>
    Integrity,
    /// <summary>Full verification.</summary>
    Full
}

/// <summary>Configuration for retention policy.</summary>
public sealed class RetentionPolicyConfig
{
    /// <summary>Number of days to retain backups.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Minimum number of backups to keep regardless of age.</summary>
    public int MinBackupsToKeep { get; set; } = 3;

    /// <summary>Maximum number of backups to keep.</summary>
    public int MaxBackupsToKeep { get; set; } = 100;

    /// <summary>Cron schedule for cleanup job.</summary>
    public string? CleanupSchedule { get; set; }

    /// <summary>ServiceAccount name for cleanup job.</summary>
    public string? ServiceAccountName { get; set; }

    /// <summary>Cleanup container image.</summary>
    public string? CleanupImage { get; set; }

    /// <summary>Whether to perform dry run only.</summary>
    public bool DryRun { get; set; }
}

/// <summary>Result of a backup schedule operation.</summary>
public sealed class BackupScheduleResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the CronJob.</summary>
    public string CronJobName { get; set; } = string.Empty;

    /// <summary>Cron schedule expression.</summary>
    public string? Schedule { get; set; }

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Whether this is a Velero backup.</summary>
    public bool IsVeleroBackup { get; set; }

    /// <summary>Whether this is a verification job.</summary>
    public bool IsVerificationJob { get; set; }
}

/// <summary>Result of listing backup jobs.</summary>
public sealed class BackupListResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>List of backup job statuses.</summary>
    public List<BackupJobStatus> Jobs { get; set; } = new();

    /// <summary>Error message if any.</summary>
    public string? Message { get; set; }
}

/// <summary>Status of a backup job.</summary>
public sealed class BackupJobStatus
{
    /// <summary>Job name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Job namespace.</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Current phase.</summary>
    public BackupJobPhase Phase { get; set; }

    /// <summary>When the job was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the job completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Number of succeeded pods.</summary>
    public int Succeeded { get; set; }

    /// <summary>Number of failed pods.</summary>
    public int Failed { get; set; }

    /// <summary>Number of active pods.</summary>
    public int Active { get; set; }
}

/// <summary>Backup job phase.</summary>
public enum BackupJobPhase
{
    /// <summary>Job is pending.</summary>
    Pending,
    /// <summary>Job is running.</summary>
    Running,
    /// <summary>Job succeeded.</summary>
    Succeeded,
    /// <summary>Job failed.</summary>
    Failed
}

#endregion

#region Kubernetes Resource Types

internal sealed class CronJob
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public CronJobSpec Spec { get; set; } = new();
}

internal sealed class CronJobSpec
{
    public string Schedule { get; set; } = string.Empty;
    public string? TimeZone { get; set; }
    public string? ConcurrencyPolicy { get; set; }
    public long? StartingDeadlineSeconds { get; set; }
    public int? SuccessfulJobsHistoryLimit { get; set; }
    public int? FailedJobsHistoryLimit { get; set; }
    public bool? Suspend { get; set; }
    public JobTemplateSpec JobTemplate { get; set; } = new();
}

internal sealed class JobTemplateSpec
{
    public ObjectMeta? Metadata { get; set; }
    public JobSpec Spec { get; set; } = new();
}

internal sealed class Job
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public JobSpec Spec { get; set; } = new();
}

internal sealed class JobSpec
{
    public int? BackoffLimit { get; set; }
    public long? ActiveDeadlineSeconds { get; set; }
    public int? TtlSecondsAfterFinished { get; set; }
    public PodTemplateSpec Template { get; set; } = new();
}

internal sealed class PodTemplateSpec
{
    public ObjectMeta? Metadata { get; set; }
    public PodSpec Spec { get; set; } = new();
}

internal sealed class PodSpec
{
    public string? RestartPolicy { get; set; }
    public string? ServiceAccountName { get; set; }
    public List<Container>? Containers { get; set; }
    public List<Volume>? Volumes { get; set; }
}

internal sealed class Container
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string? ImagePullPolicy { get; set; }
    public List<string>? Command { get; set; }
    public List<string>? Args { get; set; }
    public List<EnvVar>? Env { get; set; }
    public List<VolumeMount>? VolumeMounts { get; set; }
    public ResourceRequirements? Resources { get; set; }
}

internal sealed class EnvVar
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public EnvVarSource? ValueFrom { get; set; }
}

internal sealed class EnvVarSource
{
    public SecretKeySelector? SecretKeyRef { get; set; }
    public ConfigMapKeySelector? ConfigMapKeyRef { get; set; }
}

internal sealed class SecretKeySelector
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

internal sealed class ConfigMapKeySelector
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

internal sealed class VolumeMount
{
    public string Name { get; set; } = string.Empty;
    public string MountPath { get; set; } = string.Empty;
    public bool? ReadOnly { get; set; }
    public string? SubPath { get; set; }
}

internal sealed class Volume
{
    public string Name { get; set; } = string.Empty;
    public PersistentVolumeClaimVolumeSource? PersistentVolumeClaim { get; set; }
    public SecretVolumeSource? Secret { get; set; }
    public ConfigMapVolumeSource? ConfigMap { get; set; }
}

internal sealed class PersistentVolumeClaimVolumeSource
{
    public string ClaimName { get; set; } = string.Empty;
}

internal sealed class SecretVolumeSource
{
    public string SecretName { get; set; } = string.Empty;
}

internal sealed class ConfigMapVolumeSource
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class ResourceRequirements
{
    public Dictionary<string, string>? Requests { get; set; }
    public Dictionary<string, string>? Limits { get; set; }
}

internal sealed class VeleroSchedule
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public VeleroScheduleSpec Spec { get; set; } = new();
}

internal sealed class VeleroScheduleSpec
{
    public string Schedule { get; set; } = string.Empty;
    public bool? UseOwnerReferencesInBackup { get; set; }
    public VeleroBackupSpec Template { get; set; } = new();
}

internal sealed class VeleroBackupSpec
{
    public List<string>? IncludedNamespaces { get; set; }
    public List<string>? ExcludedNamespaces { get; set; }
    public List<string>? IncludedResources { get; set; }
    public List<string>? ExcludedResources { get; set; }
    public bool? IncludeClusterResources { get; set; }
    public LabelSelector? LabelSelector { get; set; }
    public string? StorageLocation { get; set; }
    public List<string>? VolumeSnapshotLocations { get; set; }
    public string? Ttl { get; set; }
    public bool? SnapshotVolumes { get; set; }
    public bool? DefaultVolumesToFsBackup { get; set; }
    public VeleroBackupHooks? Hooks { get; set; }
}

internal sealed class VeleroBackupHooks
{
    public List<VeleroResourceHook>? Resources { get; set; }
}

internal sealed class VeleroResourceHook
{
    public string? Name { get; set; }
    public List<string>? IncludedNamespaces { get; set; }
    public LabelSelector? LabelSelector { get; set; }
    public List<VeleroExecHook>? Pre { get; set; }
    public List<VeleroExecHook>? Post { get; set; }
}

internal sealed class VeleroExecHook
{
    public string? Container { get; set; }
    public List<string>? Command { get; set; }
    public string? OnError { get; set; }
    public string? Timeout { get; set; }
}

#endregion
