using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Security;
using DataWarehouse.Dashboard.Models;
using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for backup and restore operations.
/// Requires admin permissions for all operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class BackupController : ControllerBase
{
    private readonly IBackupService _backupService;
    private readonly ILogger<BackupController> _logger;

    public BackupController(IBackupService backupService, ILogger<BackupController> logger)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Lists all available backups.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResponse<BackupInfo>), StatusCodes.Status200OK)]
    public ActionResult<PaginatedResponse<BackupInfo>> ListBackups([FromQuery] PaginationQuery pagination)
    {
        var backups = _backupService.ListBackups();
        var response = backups.ToPaginated(pagination);
        Response.AddPaginationHeaders(response);
        return Ok(response);
    }

    /// <summary>
    /// Gets details of a specific backup.
    /// </summary>
    [HttpGet("{backupId}")]
    [ProducesResponseType(typeof(BackupInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BackupInfo> GetBackup(string backupId)
    {
        var backup = _backupService.GetBackup(backupId);
        if (backup == null)
            return NotFound(new { error = $"Backup '{backupId}' not found" });

        return Ok(backup);
    }

    /// <summary>
    /// Creates a new backup.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(BackupJobStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BackupJobStatus>> CreateBackup([FromBody] CreateBackupRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        _logger.LogInformation("Starting backup job: Type={Type}, Description={Description}",
            request.BackupType, request.Description);

        var jobId = await _backupService.StartBackupAsync(request);

        var status = _backupService.GetJobStatus(jobId);

        return Accepted($"api/backup/jobs/{jobId}", status);
    }

    /// <summary>
    /// Restores from a backup.
    /// </summary>
    [HttpPost("{backupId}/restore")]
    [ProducesResponseType(typeof(BackupJobStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BackupJobStatus>> RestoreBackup(string backupId, [FromBody] RestoreRequest? request)
    {
        var backup = _backupService.GetBackup(backupId);
        if (backup == null)
            return NotFound(new { error = $"Backup '{backupId}' not found" });

        _logger.LogWarning("Starting restore from backup {BackupId} - THIS WILL OVERWRITE CURRENT DATA",
            backupId);

        var jobId = await _backupService.StartRestoreAsync(backupId, request ?? new RestoreRequest());

        var status = _backupService.GetJobStatus(jobId);

        return Accepted($"api/backup/jobs/{jobId}", status);
    }

    /// <summary>
    /// Gets the status of a backup/restore job.
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(BackupJobStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BackupJobStatus> GetJobStatus(string jobId)
    {
        var status = _backupService.GetJobStatus(jobId);
        if (status == null)
            return NotFound(new { error = $"Job '{jobId}' not found" });

        return Ok(status);
    }

    /// <summary>
    /// Lists all backup/restore jobs.
    /// </summary>
    [HttpGet("jobs")]
    [ProducesResponseType(typeof(IEnumerable<BackupJobStatus>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<BackupJobStatus>> ListJobs()
    {
        var jobs = _backupService.ListJobs();
        return Ok(jobs);
    }

    /// <summary>
    /// Cancels a running backup/restore job.
    /// </summary>
    [HttpPost("jobs/{jobId}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> CancelJob(string jobId)
    {
        var success = await _backupService.CancelJobAsync(jobId);
        if (!success)
            return NotFound(new { error = $"Job '{jobId}' not found or already completed" });

        _logger.LogInformation("Cancelled backup/restore job {JobId}", jobId);
        return Ok(new { message = "Job cancelled" });
    }

    /// <summary>
    /// Deletes a backup.
    /// </summary>
    [HttpDelete("{backupId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteBackup(string backupId)
    {
        var success = await _backupService.DeleteBackupAsync(backupId);
        if (!success)
            return NotFound(new { error = $"Backup '{backupId}' not found" });

        _logger.LogInformation("Deleted backup {BackupId}", backupId);
        return NoContent();
    }

    /// <summary>
    /// Gets backup schedule configuration.
    /// </summary>
    [HttpGet("schedule")]
    [ProducesResponseType(typeof(BackupScheduleConfig), StatusCodes.Status200OK)]
    public ActionResult<BackupScheduleConfig> GetSchedule()
    {
        var schedule = _backupService.GetScheduleConfig();
        return Ok(schedule);
    }

    /// <summary>
    /// Updates backup schedule configuration.
    /// </summary>
    [HttpPut("schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> UpdateSchedule([FromBody] BackupScheduleConfig config)
    {
        await _backupService.UpdateScheduleConfigAsync(config);
        _logger.LogInformation("Updated backup schedule configuration");
        return Ok(new { message = "Schedule updated" });
    }
}

#region Interfaces

/// <summary>
/// Service interface for backup operations.
/// </summary>
public interface IBackupService
{
    IEnumerable<BackupInfo> ListBackups();
    BackupInfo? GetBackup(string backupId);
    Task<string> StartBackupAsync(CreateBackupRequest request);
    Task<string> StartRestoreAsync(string backupId, RestoreRequest request);
    BackupJobStatus? GetJobStatus(string jobId);
    IEnumerable<BackupJobStatus> ListJobs();
    Task<bool> CancelJobAsync(string jobId);
    Task<bool> DeleteBackupAsync(string backupId);
    BackupScheduleConfig GetScheduleConfig();
    Task UpdateScheduleConfigAsync(BackupScheduleConfig config);
}

#endregion

#region Models

/// <summary>
/// Information about a backup.
/// </summary>
public sealed class BackupInfo
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public BackupType Type { get; init; }
    public BackupStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long SizeBytes { get; init; }
    public int ItemCount { get; init; }
    public string? Location { get; init; }
    public bool IsEncrypted { get; init; }
    public string? Checksum { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Request to create a backup.
/// </summary>
public sealed class CreateBackupRequest
{
    [StringLength(100)]
    public string? Name { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    public BackupType BackupType { get; set; } = BackupType.Full;

    public bool Encrypt { get; set; } = true;

    public bool Compress { get; set; } = true;

    public string[] IncludePools { get; set; } = Array.Empty<string>();

    public string[] ExcludePools { get; set; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Request to restore from a backup.
/// </summary>
public sealed class RestoreRequest
{
    public bool OverwriteExisting { get; set; } = false;

    public string[] RestorePools { get; set; } = Array.Empty<string>();

    public bool ValidateChecksum { get; set; } = true;

    public bool DryRun { get; set; } = false;
}

/// <summary>
/// Status of a backup/restore job.
/// </summary>
public sealed record BackupJobStatus
{
    public string JobId { get; init; } = string.Empty;
    public JobType Type { get; init; }
    public JobState State { get; init; }
    public double ProgressPercent { get; init; }
    public string? CurrentOperation { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public int ItemsProcessed { get; init; }
    public int TotalItems { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ResultBackupId { get; init; }
}

/// <summary>
/// Backup schedule configuration.
/// </summary>
public sealed class BackupScheduleConfig
{
    public bool Enabled { get; set; }
    public string CronExpression { get; set; } = "0 2 * * *"; // Daily at 2 AM
    public BackupType DefaultType { get; set; } = BackupType.Incremental;
    public int RetentionDays { get; set; } = 30;
    public int MaxBackupCount { get; set; } = 10;
    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
    public string? NotificationEmail { get; set; }
}

public enum BackupType
{
    Full,
    Incremental,
    Differential,
    Snapshot
}

public enum BackupStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

public enum JobType
{
    Backup,
    Restore
}

public enum JobState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

#endregion

#region Default Implementation

/// <summary>
/// Default in-memory implementation of IBackupService for demonstration.
/// In production, this would integrate with the DataWarehouse.SDK backup infrastructure.
/// </summary>
public sealed class InMemoryBackupService : IBackupService
{
    private readonly ConcurrentDictionary<string, BackupInfo> _backups = new();
    private readonly ConcurrentDictionary<string, BackupJobStatus> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellations = new();
    private BackupScheduleConfig _scheduleConfig = new();
    private readonly ILogger<InMemoryBackupService> _logger;

    public InMemoryBackupService(ILogger<InMemoryBackupService> logger)
    {
        _logger = logger;

        // Add some sample backups
        var sampleBackup = new BackupInfo
        {
            Id = "backup-001",
            Name = "Initial Full Backup",
            Description = "Full backup taken at system installation",
            Type = BackupType.Full,
            Status = BackupStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            CompletedAt = DateTime.UtcNow.AddDays(-7).AddHours(2),
            SizeBytes = 1024L * 1024 * 1024 * 5, // 5 GB
            ItemCount = 150000,
            Location = "/backups/backup-001",
            IsEncrypted = true,
            Checksum = "sha256:abc123def456..."
        };
        _backups[sampleBackup.Id] = sampleBackup;
    }

    public IEnumerable<BackupInfo> ListBackups() => _backups.Values.OrderByDescending(b => b.CreatedAt);

    public BackupInfo? GetBackup(string backupId) => _backups.TryGetValue(backupId, out var backup) ? backup : null;

    public async Task<string> StartBackupAsync(CreateBackupRequest request)
    {
        var jobId = Guid.NewGuid().ToString();
        var backupId = $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var cts = new CancellationTokenSource();

        _jobCancellations[jobId] = cts;

        var status = new BackupJobStatus
        {
            JobId = jobId,
            Type = JobType.Backup,
            State = JobState.Running,
            ProgressPercent = 0,
            CurrentOperation = "Initializing backup...",
            StartedAt = DateTime.UtcNow,
            TotalBytes = 1024L * 1024 * 1024, // Simulated
            TotalItems = 10000
        };
        _jobs[jobId] = status;

        // Simulate backup process in background
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i <= 100; i += 10)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        _jobs[jobId] = status with { State = JobState.Cancelled, CompletedAt = DateTime.UtcNow };
                        return;
                    }

                    await Task.Delay(500, cts.Token);
                    _jobs[jobId] = status with
                    {
                        ProgressPercent = i,
                        BytesProcessed = (long)(status.TotalBytes * (i / 100.0)),
                        ItemsProcessed = (int)(status.TotalItems * (i / 100.0)),
                        CurrentOperation = $"Processing data ({i}%)..."
                    };
                }

                var backup = new BackupInfo
                {
                    Id = backupId,
                    Name = request.Name ?? $"Backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                    Description = request.Description,
                    Type = request.BackupType,
                    Status = BackupStatus.Completed,
                    CreatedAt = status.StartedAt,
                    CompletedAt = DateTime.UtcNow,
                    SizeBytes = status.TotalBytes,
                    ItemCount = status.TotalItems,
                    IsEncrypted = request.Encrypt,
                    Checksum = $"sha256:{Guid.NewGuid():N}",
                    Location = $"/backups/{backupId}"
                };
                _backups[backupId] = backup;

                _jobs[jobId] = status with
                {
                    State = JobState.Completed,
                    ProgressPercent = 100,
                    CompletedAt = DateTime.UtcNow,
                    ResultBackupId = backupId,
                    BytesProcessed = status.TotalBytes,
                    ItemsProcessed = status.TotalItems,
                    CurrentOperation = "Backup completed"
                };

                _logger.LogInformation("Backup {BackupId} completed successfully", backupId);
            }
            catch (OperationCanceledException)
            {
                _jobs[jobId] = status with { State = JobState.Cancelled, CompletedAt = DateTime.UtcNow };
            }
            catch (Exception ex)
            {
                _jobs[jobId] = status with
                {
                    State = JobState.Failed,
                    CompletedAt = DateTime.UtcNow,
                    ErrorMessage = ex.Message
                };
                _logger.LogError(ex, "Backup job {JobId} failed", jobId);
            }
            finally
            {
                _jobCancellations.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    public async Task<string> StartRestoreAsync(string backupId, RestoreRequest request)
    {
        var jobId = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();

        _jobCancellations[jobId] = cts;

        var backup = GetBackup(backupId);
        var status = new BackupJobStatus
        {
            JobId = jobId,
            Type = JobType.Restore,
            State = JobState.Running,
            ProgressPercent = 0,
            CurrentOperation = request.DryRun ? "Validating restore (dry run)..." : "Initializing restore...",
            StartedAt = DateTime.UtcNow,
            TotalBytes = backup?.SizeBytes ?? 0,
            TotalItems = backup?.ItemCount ?? 0
        };
        _jobs[jobId] = status;

        // Simulate restore process in background
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i <= 100; i += 5)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        _jobs[jobId] = status with { State = JobState.Cancelled, CompletedAt = DateTime.UtcNow };
                        return;
                    }

                    await Task.Delay(300, cts.Token);
                    _jobs[jobId] = status with
                    {
                        ProgressPercent = i,
                        BytesProcessed = (long)(status.TotalBytes * (i / 100.0)),
                        ItemsProcessed = (int)(status.TotalItems * (i / 100.0)),
                        CurrentOperation = request.DryRun ? $"Validating ({i}%)..." : $"Restoring data ({i}%)..."
                    };
                }

                _jobs[jobId] = status with
                {
                    State = JobState.Completed,
                    ProgressPercent = 100,
                    CompletedAt = DateTime.UtcNow,
                    BytesProcessed = status.TotalBytes,
                    ItemsProcessed = status.TotalItems,
                    CurrentOperation = request.DryRun ? "Validation completed (dry run)" : "Restore completed"
                };

                _logger.LogInformation("Restore from backup {BackupId} completed successfully (DryRun: {DryRun})",
                    backupId, request.DryRun);
            }
            catch (OperationCanceledException)
            {
                _jobs[jobId] = status with { State = JobState.Cancelled, CompletedAt = DateTime.UtcNow };
            }
            catch (Exception ex)
            {
                _jobs[jobId] = status with
                {
                    State = JobState.Failed,
                    CompletedAt = DateTime.UtcNow,
                    ErrorMessage = ex.Message
                };
                _logger.LogError(ex, "Restore job {JobId} failed", jobId);
            }
            finally
            {
                _jobCancellations.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    public BackupJobStatus? GetJobStatus(string jobId) => _jobs.TryGetValue(jobId, out var status) ? status : null;

    public IEnumerable<BackupJobStatus> ListJobs() => _jobs.Values.OrderByDescending(j => j.StartedAt);

    public Task<bool> CancelJobAsync(string jobId)
    {
        if (_jobCancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> DeleteBackupAsync(string backupId)
    {
        return Task.FromResult(_backups.TryRemove(backupId, out _));
    }

    public BackupScheduleConfig GetScheduleConfig() => _scheduleConfig;

    public Task UpdateScheduleConfigAsync(BackupScheduleConfig config)
    {
        _scheduleConfig = config;
        return Task.CompletedTask;
    }
}

#endregion
