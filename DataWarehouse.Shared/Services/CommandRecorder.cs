// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Represents a single recorded command.
/// </summary>
public sealed record RecordedCommand
{
    /// <summary>Gets the command name that was executed.</summary>
    public required string CommandName { get; init; }

    /// <summary>Gets the command parameters.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Gets the timestamp when the command was executed.</summary>
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets whether the command succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the execution duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Gets an optional comment or annotation for this command.</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Represents a recorded session of commands that can be replayed.
/// </summary>
public sealed class RecordedSession
{
    /// <summary>Gets or sets the unique session identifier.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the session name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets when recording started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets when recording ended (null if still recording).</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Gets or sets the list of recorded commands.</summary>
    public List<RecordedCommand> Commands { get; set; } = new();

    /// <summary>Gets or sets optional metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Gets or sets an optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets whether this session is currently being recorded.</summary>
    [JsonIgnore]
    public bool IsRecording => EndedAt == null;

    /// <summary>Gets the total duration of recorded commands.</summary>
    [JsonIgnore]
    public double TotalDurationMs => Commands.Sum(c => c.DurationMs);

    /// <summary>Gets the command count.</summary>
    [JsonIgnore]
    public int CommandCount => Commands.Count;
}

/// <summary>
/// Options for replaying a recorded session.
/// </summary>
public sealed class ReplayOptions
{
    /// <summary>Gets or sets whether to pause between commands (simulating original timing).</summary>
    public bool SimulateTiming { get; set; }

    /// <summary>Gets or sets a delay multiplier for timing simulation (1.0 = original speed).</summary>
    public double TimingMultiplier { get; set; } = 1.0;

    /// <summary>Gets or sets whether to stop on first error.</summary>
    public bool StopOnError { get; set; } = true;

    /// <summary>Gets or sets whether to run in dry-run mode (parse only, no execution).</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets whether to confirm each command before execution.</summary>
    public bool Interactive { get; set; }

    /// <summary>Gets or sets a filter for which commands to replay.</summary>
    public Func<RecordedCommand, bool>? CommandFilter { get; set; }

    /// <summary>Gets or sets parameter overrides to apply during replay.</summary>
    public Dictionary<string, object?> ParameterOverrides { get; set; } = new();
}

/// <summary>
/// Result of a replay operation.
/// </summary>
public sealed record ReplayResult
{
    /// <summary>Gets whether all replayed commands succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the number of commands executed.</summary>
    public int CommandsExecuted { get; init; }

    /// <summary>Gets the number of commands that failed.</summary>
    public int CommandsFailed { get; init; }

    /// <summary>Gets the number of commands skipped.</summary>
    public int CommandsSkipped { get; init; }

    /// <summary>Gets the total execution time in milliseconds.</summary>
    public double TotalDurationMs { get; init; }

    /// <summary>Gets the error message if replay failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets detailed results for each command.</summary>
    public IReadOnlyList<CommandReplayResult> CommandResults { get; init; } = Array.Empty<CommandReplayResult>();
}

/// <summary>
/// Result of replaying a single command.
/// </summary>
public sealed record CommandReplayResult
{
    /// <summary>Gets the command that was replayed.</summary>
    public required RecordedCommand Command { get; init; }

    /// <summary>Gets whether the replay succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the execution time in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Gets whether the command was skipped.</summary>
    public bool Skipped { get; init; }
}

/// <summary>
/// Delegate for command replay confirmation.
/// </summary>
/// <param name="command">The command about to be replayed.</param>
/// <returns>True to continue, false to skip.</returns>
public delegate Task<bool> ReplayConfirmationDelegate(RecordedCommand command);

/// <summary>
/// Delegate for replay progress reporting.
/// </summary>
/// <param name="current">Current command index.</param>
/// <param name="total">Total commands.</param>
/// <param name="command">The command being executed.</param>
public delegate void ReplayProgressDelegate(int current, int total, RecordedCommand command);

/// <summary>
/// Records and replays command sequences for automation and macro creation.
/// Supports exporting to shell scripts for external automation.
/// </summary>
public sealed class CommandRecorder : IDisposable
{
    private readonly string _sessionsPath;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private RecordedSession? _activeSession;
    private bool _disposed;

    /// <summary>
    /// Event raised when a command is recorded.
    /// </summary>
    public event EventHandler<RecordedCommand>? CommandRecorded;

    /// <summary>
    /// Event raised when recording starts.
    /// </summary>
    public event EventHandler<RecordedSession>? RecordingStarted;

    /// <summary>
    /// Event raised when recording stops.
    /// </summary>
    public event EventHandler<RecordedSession>? RecordingStopped;

    /// <summary>
    /// Initializes a new CommandRecorder.
    /// </summary>
    /// <param name="sessionsPath">Optional custom path for storing sessions.</param>
    public CommandRecorder(string? sessionsPath = null)
    {
        _sessionsPath = sessionsPath ?? GetDefaultSessionsPath();
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        EnsureSessionsDirectory();
    }

    /// <summary>
    /// Gets whether a recording session is currently active.
    /// </summary>
    public bool IsRecording
    {
        get
        {
            lock (_lock)
            {
                return _activeSession != null;
            }
        }
    }

    /// <summary>
    /// Gets the current active session, if any.
    /// </summary>
    public RecordedSession? ActiveSession
    {
        get
        {
            lock (_lock)
            {
                return _activeSession;
            }
        }
    }

    /// <summary>
    /// Starts a new recording session.
    /// </summary>
    /// <param name="sessionName">The name for this recording session.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The new session.</returns>
    /// <exception cref="InvalidOperationException">If a recording is already in progress.</exception>
    public Task<RecordedSession> StartRecordingAsync(string sessionName, string? description = null)
    {
        lock (_lock)
        {
            if (_activeSession != null)
            {
                throw new InvalidOperationException(
                    $"Recording already in progress: '{_activeSession.Name}'. Stop it first.");
            }

            _activeSession = new RecordedSession
            {
                Id = GenerateSessionId(),
                Name = sessionName,
                Description = description,
                StartedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["Environment"] = Environment.OSVersion.ToString(),
                    ["MachineName"] = Environment.MachineName,
                    ["UserName"] = Environment.UserName
                }
            };

            RecordingStarted?.Invoke(this, _activeSession);
            return Task.FromResult(_activeSession);
        }
    }

    /// <summary>
    /// Records a command to the active session.
    /// </summary>
    /// <param name="commandName">The command name.</param>
    /// <param name="parameters">The command parameters.</param>
    /// <param name="success">Whether the command succeeded.</param>
    /// <param name="durationMs">The execution duration.</param>
    /// <param name="comment">Optional comment.</param>
    /// <returns>True if recorded, false if no active session.</returns>
    public Task<bool> RecordCommandAsync(
        string commandName,
        Dictionary<string, object?>? parameters = null,
        bool success = true,
        double durationMs = 0,
        string? comment = null)
    {
        lock (_lock)
        {
            if (_activeSession == null)
                return Task.FromResult(false);

            var command = new RecordedCommand
            {
                CommandName = commandName,
                Parameters = parameters ?? new Dictionary<string, object?>(),
                ExecutedAt = DateTime.UtcNow,
                Success = success,
                DurationMs = durationMs,
                Comment = comment
            };

            _activeSession.Commands.Add(command);
            CommandRecorded?.Invoke(this, command);

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Stops the current recording and saves the session.
    /// </summary>
    /// <returns>The completed session, or null if no active recording.</returns>
    public async Task<RecordedSession?> StopRecordingAsync()
    {
        RecordedSession? session;

        lock (_lock)
        {
            session = _activeSession;
            if (session == null)
                return null;

            session.EndedAt = DateTime.UtcNow;
            _activeSession = null;
        }

        // Save the session
        await SaveSessionAsync(session);
        RecordingStopped?.Invoke(this, session);

        return session;
    }

    /// <summary>
    /// Gets a saved session by name or ID.
    /// </summary>
    /// <param name="nameOrId">The session name or ID.</param>
    /// <returns>The session, or null if not found.</returns>
    public async Task<RecordedSession?> GetSessionAsync(string nameOrId)
    {
        var sessions = await ListSessionsAsync();
        return sessions.FirstOrDefault(s =>
            s.Id.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists all saved sessions.
    /// </summary>
    /// <returns>List of recorded sessions.</returns>
    public Task<IReadOnlyList<RecordedSession>> ListSessionsAsync()
    {
        EnsureSessionsDirectory();

        var sessions = new List<RecordedSession>();
        var files = Directory.GetFiles(_sessionsPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<RecordedSession>(json, _jsonOptions);
                if (session != null)
                {
                    sessions.Add(session);
                }
            }
            catch
            {
                // Skip invalid files
            }
        }

        // Include active session if any
        lock (_lock)
        {
            if (_activeSession != null)
            {
                sessions.Add(_activeSession);
            }
        }

        return Task.FromResult<IReadOnlyList<RecordedSession>>(
            sessions.OrderByDescending(s => s.StartedAt).ToList());
    }

    /// <summary>
    /// Deletes a saved session.
    /// </summary>
    /// <param name="nameOrId">The session name or ID.</param>
    /// <returns>True if deleted, false if not found.</returns>
    public Task<bool> DeleteSessionAsync(string nameOrId)
    {
        var filePath = GetSessionFilePath(nameOrId);
        if (!File.Exists(filePath))
        {
            // Try to find by name
            var files = Directory.GetFiles(_sessionsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var session = JsonSerializer.Deserialize<RecordedSession>(json, _jsonOptions);
                    if (session?.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        File.Delete(file);
                        return Task.FromResult(true);
                    }
                }
                catch
                {
                    // Continue
                }
            }

            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Replays a recorded session using the provided executor.
    /// </summary>
    /// <param name="sessionNameOrId">The session name or ID to replay.</param>
    /// <param name="executor">Function to execute each command.</param>
    /// <param name="options">Replay options.</param>
    /// <param name="confirmAction">Optional confirmation callback for interactive mode.</param>
    /// <param name="progressCallback">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replay result.</returns>
    public async Task<ReplayResult> ReplayAsync(
        string sessionNameOrId,
        Func<string, Dictionary<string, object?>, CancellationToken, Task<bool>> executor,
        ReplayOptions? options = null,
        ReplayConfirmationDelegate? confirmAction = null,
        ReplayProgressDelegate? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ReplayOptions();

        var session = await GetSessionAsync(sessionNameOrId);
        if (session == null)
        {
            return new ReplayResult
            {
                Success = false,
                Error = $"Session not found: '{sessionNameOrId}'"
            };
        }

        var results = new List<CommandReplayResult>();
        var startTime = DateTime.UtcNow;
        int executed = 0;
        int failed = 0;
        int skipped = 0;
        DateTime? lastCommandTime = null;

        for (int i = 0; i < session.Commands.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = session.Commands[i];

            // Apply filter
            if (options.CommandFilter != null && !options.CommandFilter(command))
            {
                results.Add(new CommandReplayResult
                {
                    Command = command,
                    Success = true,
                    Skipped = true
                });
                skipped++;
                continue;
            }

            // Report progress
            progressCallback?.Invoke(i + 1, session.Commands.Count, command);

            // Simulate timing if enabled
            if (options.SimulateTiming && lastCommandTime.HasValue)
            {
                var delay = command.ExecutedAt - lastCommandTime.Value;
                if (delay > TimeSpan.Zero)
                {
                    var adjustedDelay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * options.TimingMultiplier);
                    if (adjustedDelay.TotalMilliseconds > 0 && adjustedDelay.TotalSeconds < 30)
                    {
                        await Task.Delay(adjustedDelay, cancellationToken);
                    }
                }
            }
            lastCommandTime = command.ExecutedAt;

            // Interactive confirmation
            if (options.Interactive && confirmAction != null)
            {
                var confirmed = await confirmAction(command);
                if (!confirmed)
                {
                    results.Add(new CommandReplayResult
                    {
                        Command = command,
                        Success = true,
                        Skipped = true
                    });
                    skipped++;
                    continue;
                }
            }

            // Dry run
            if (options.DryRun)
            {
                results.Add(new CommandReplayResult
                {
                    Command = command,
                    Success = true,
                    Skipped = true
                });
                skipped++;
                continue;
            }

            // Apply parameter overrides
            var parameters = new Dictionary<string, object?>(command.Parameters);
            foreach (var (key, value) in options.ParameterOverrides)
            {
                parameters[key] = value;
            }

            // Execute
            var cmdStartTime = DateTime.UtcNow;
            bool success;
            string? error = null;

            try
            {
                success = await executor(command.CommandName, parameters, cancellationToken);
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
            }

            var duration = (DateTime.UtcNow - cmdStartTime).TotalMilliseconds;

            results.Add(new CommandReplayResult
            {
                Command = command,
                Success = success,
                Error = error,
                DurationMs = duration
            });

            if (success)
            {
                executed++;
            }
            else
            {
                failed++;
                if (options.StopOnError)
                {
                    return new ReplayResult
                    {
                        Success = false,
                        CommandsExecuted = executed,
                        CommandsFailed = failed,
                        CommandsSkipped = skipped,
                        TotalDurationMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
                        Error = error ?? $"Command failed: {command.CommandName}",
                        CommandResults = results
                    };
                }
            }
        }

        return new ReplayResult
        {
            Success = failed == 0,
            CommandsExecuted = executed,
            CommandsFailed = failed,
            CommandsSkipped = skipped,
            TotalDurationMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
            CommandResults = results
        };
    }

    /// <summary>
    /// Exports a session to a shell script.
    /// </summary>
    /// <param name="sessionNameOrId">The session name or ID.</param>
    /// <param name="format">The script format (bash, powershell).</param>
    /// <returns>The script content, or null if session not found.</returns>
    public async Task<string?> ExportAsync(string sessionNameOrId, ScriptFormat format)
    {
        var session = await GetSessionAsync(sessionNameOrId);
        if (session == null)
            return null;

        return format switch
        {
            ScriptFormat.Bash => ExportToBash(session),
            ScriptFormat.PowerShell => ExportToPowerShell(session),
            ScriptFormat.Batch => ExportToBatch(session),
            _ => ExportToBash(session)
        };
    }

    private static string ExportToBash(RecordedSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine($"# DataWarehouse Session Export: {session.Name}");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"# Original session: {session.StartedAt:O} - {session.EndedAt:O}");
        sb.AppendLine($"# Commands: {session.Commands.Count}");
        if (!string.IsNullOrEmpty(session.Description))
        {
            sb.AppendLine($"# Description: {session.Description}");
        }
        sb.AppendLine();
        sb.AppendLine("set -e  # Exit on error");
        sb.AppendLine();

        foreach (var cmd in session.Commands)
        {
            if (!string.IsNullOrEmpty(cmd.Comment))
            {
                sb.AppendLine($"# {cmd.Comment}");
            }

            sb.Append($"dw {cmd.CommandName}");
            foreach (var (key, value) in cmd.Parameters)
            {
                if (key.StartsWith("__") || key is "verbose" or "format")
                    continue;

                if (value is bool b)
                {
                    if (b) sb.Append($" --{key}");
                }
                else if (value != null)
                {
                    var strValue = value.ToString()!;
                    if (strValue.Contains(' '))
                    {
                        sb.Append($" --{key} \"{EscapeBash(strValue)}\"");
                    }
                    else
                    {
                        sb.Append($" --{key} {strValue}");
                    }
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("echo 'Session replay complete.'");

        return sb.ToString();
    }

    private static string ExportToPowerShell(RecordedSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DataWarehouse Session Export: " + session.Name);
        sb.AppendLine("# Generated: " + DateTime.UtcNow.ToString("O"));
        sb.AppendLine("# Original session: " + session.StartedAt.ToString("O") + " - " + session.EndedAt?.ToString("O"));
        sb.AppendLine("# Commands: " + session.Commands.Count);
        if (!string.IsNullOrEmpty(session.Description))
        {
            sb.AppendLine("# Description: " + session.Description);
        }
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();

        foreach (var cmd in session.Commands)
        {
            if (!string.IsNullOrEmpty(cmd.Comment))
            {
                sb.AppendLine($"# {cmd.Comment}");
            }

            sb.Append($"dw {cmd.CommandName}");
            foreach (var (key, value) in cmd.Parameters)
            {
                if (key.StartsWith("__") || key is "verbose" or "format")
                    continue;

                if (value is bool b)
                {
                    if (b) sb.Append($" --{key}");
                }
                else if (value != null)
                {
                    var strValue = value.ToString()!;
                    if (strValue.Contains(' '))
                    {
                        sb.Append($" --{key} \"{EscapePowerShell(strValue)}\"");
                    }
                    else
                    {
                        sb.Append($" --{key} {strValue}");
                    }
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Write-Host 'Session replay complete.'");

        return sb.ToString();
    }

    private static string ExportToBatch(RecordedSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"REM DataWarehouse Session Export: {session.Name}");
        sb.AppendLine($"REM Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"REM Commands: {session.Commands.Count}");
        sb.AppendLine();

        foreach (var cmd in session.Commands)
        {
            if (!string.IsNullOrEmpty(cmd.Comment))
            {
                sb.AppendLine($"REM {cmd.Comment}");
            }

            sb.Append($"dw {cmd.CommandName}");
            foreach (var (key, value) in cmd.Parameters)
            {
                if (key.StartsWith("__") || key is "verbose" or "format")
                    continue;

                if (value is bool b)
                {
                    if (b) sb.Append($" --{key}");
                }
                else if (value != null)
                {
                    var strValue = value.ToString()!;
                    if (strValue.Contains(' '))
                    {
                        sb.Append($" --{key} \"{strValue}\"");
                    }
                    else
                    {
                        sb.Append($" --{key} {strValue}");
                    }
                }
            }
            sb.AppendLine();
            sb.AppendLine("if errorlevel 1 goto :error");
        }

        sb.AppendLine();
        sb.AppendLine("echo Session replay complete.");
        sb.AppendLine("goto :eof");
        sb.AppendLine(":error");
        sb.AppendLine("echo Error occurred during replay.");
        sb.AppendLine("exit /b 1");

        return sb.ToString();
    }

    private static string EscapeBash(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$");

    private static string EscapePowerShell(string value) =>
        value.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");

    private async Task SaveSessionAsync(RecordedSession session)
    {
        EnsureSessionsDirectory();
        var filePath = GetSessionFilePath(session.Id);
        var json = JsonSerializer.Serialize(session, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    private void EnsureSessionsDirectory()
    {
        if (!Directory.Exists(_sessionsPath))
        {
            Directory.CreateDirectory(_sessionsPath);
        }
    }

    private string GetSessionFilePath(string id) =>
        Path.Combine(_sessionsPath, $"{id}.json");

    private static string GenerateSessionId() =>
        $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string GetDefaultSessionsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "recordings");

    /// <summary>
    /// Disposes resources used by the CommandRecorder.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Save active session if any
        lock (_lock)
        {
            if (_activeSession != null)
            {
                _activeSession.EndedAt = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(_activeSession, _jsonOptions);
                var filePath = GetSessionFilePath(_activeSession.Id);
                File.WriteAllText(filePath, json);
                _activeSession = null;
            }
        }
    }
}

/// <summary>
/// Supported script export formats.
/// </summary>
public enum ScriptFormat
{
    /// <summary>Bash shell script.</summary>
    Bash,

    /// <summary>PowerShell script.</summary>
    PowerShell,

    /// <summary>Windows batch file.</summary>
    Batch
}
