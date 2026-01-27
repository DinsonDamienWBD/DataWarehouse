// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Starts recording commands to a session.
/// </summary>
public sealed class RecordStartCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordStartCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordStartCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.start";

    /// <inheritdoc />
    public override string Description => "Start recording commands to a session";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var name = parameters.GetValueOrDefault("name")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        if (string.IsNullOrEmpty(name))
        {
            return CommandResult.Fail("Session name is required. Usage: record start <name>");
        }

        var description = parameters.GetValueOrDefault("description")?.ToString();

        try
        {
            var session = await _recorder.StartRecordingAsync(name, description);
            return CommandResult.Ok(
                new
                {
                    session.Id,
                    session.Name,
                    session.StartedAt,
                    Status = "Recording"
                },
                $"Recording started. Session: '{name}' (ID: {session.Id})");
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Stops the current recording session.
/// </summary>
public sealed class RecordStopCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordStopCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordStopCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.stop";

    /// <inheritdoc />
    public override string Description => "Stop the current recording session";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var session = await _recorder.StopRecordingAsync();

        if (session == null)
        {
            return CommandResult.Fail("No recording in progress.");
        }

        return CommandResult.Ok(
            new
            {
                session.Id,
                session.Name,
                session.StartedAt,
                session.EndedAt,
                session.CommandCount,
                TotalDurationMs = session.TotalDurationMs
            },
            $"Recording stopped. Session '{session.Name}' saved with {session.CommandCount} command(s).");
    }
}

/// <summary>
/// Lists all recorded sessions.
/// </summary>
public sealed class RecordListCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordListCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordListCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.list";

    /// <inheritdoc />
    public override string Description => "List all recorded sessions";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _recorder.ListSessionsAsync();

        if (sessions.Count == 0)
        {
            return CommandResult.Ok(message: "No recorded sessions found.");
        }

        var summaries = sessions.Select(s => new
        {
            s.Id,
            s.Name,
            Commands = s.CommandCount,
            Started = s.StartedAt,
            Status = s.IsRecording ? "Recording" : "Saved",
            Duration = $"{s.TotalDurationMs:F1}ms"
        }).ToList();

        return CommandResult.Table(summaries, $"Found {sessions.Count} session(s)");
    }
}

/// <summary>
/// Replays a recorded session.
/// </summary>
public sealed class RecordPlayCommand : CommandBase
{
    private readonly CommandRecorder _recorder;
    private readonly CommandExecutor _executor;

    /// <summary>
    /// Initializes a new RecordPlayCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    /// <param name="executor">The command executor.</param>
    public RecordPlayCommand(CommandRecorder recorder, CommandExecutor executor)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public override string Name => "record.play";

    /// <inheritdoc />
    public override string Description => "Replay a recorded session";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var nameOrId = parameters.GetValueOrDefault("name")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        if (string.IsNullOrEmpty(nameOrId))
        {
            return CommandResult.Fail("Session name or ID is required. Usage: record play <name>");
        }

        var dryRun = parameters.GetValueOrDefault("dryrun") as bool? ?? false;
        var stopOnError = parameters.GetValueOrDefault("stoponerror") as bool? ?? true;

        var options = new ReplayOptions
        {
            DryRun = dryRun,
            StopOnError = stopOnError,
            SimulateTiming = false
        };

        var result = await _recorder.ReplayAsync(
            nameOrId,
            async (cmd, parms, ct) =>
            {
                var r = await _executor.ExecuteAsync(cmd, parms, ct);
                return r.Success;
            },
            options,
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            return CommandResult.Fail(result.Error ?? "Replay failed.");
        }

        return CommandResult.Ok(
            new
            {
                result.CommandsExecuted,
                result.CommandsFailed,
                result.CommandsSkipped,
                TotalDurationMs = result.TotalDurationMs
            },
            $"Replay complete. Executed {result.CommandsExecuted} command(s).");
    }
}

/// <summary>
/// Exports a recorded session to a shell script.
/// </summary>
public sealed class RecordExportCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordExportCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordExportCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.export";

    /// <inheritdoc />
    public override string Description => "Export a session as a shell script";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var nameOrId = parameters.GetValueOrDefault("name")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        if (string.IsNullOrEmpty(nameOrId))
        {
            return CommandResult.Fail("Session name or ID is required. Usage: record export <name> --format bash|ps1");
        }

        var formatStr = parameters.GetValueOrDefault("format")?.ToString()?.ToLowerInvariant() ?? "bash";
        var format = formatStr switch
        {
            "powershell" or "ps1" => ScriptFormat.PowerShell,
            "batch" or "bat" or "cmd" => ScriptFormat.Batch,
            _ => ScriptFormat.Bash
        };

        var output = parameters.GetValueOrDefault("output")?.ToString();

        var script = await _recorder.ExportAsync(nameOrId, format);

        if (script == null)
        {
            return CommandResult.Fail($"Session not found: '{nameOrId}'");
        }

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, script, cancellationToken);
            return CommandResult.Ok(message: $"Session exported to: {output}");
        }

        return CommandResult.Ok(script, $"Session exported as {format} script");
    }
}

/// <summary>
/// Deletes a recorded session.
/// </summary>
public sealed class RecordDeleteCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordDeleteCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordDeleteCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.delete";

    /// <inheritdoc />
    public override string Description => "Delete a recorded session";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var nameOrId = parameters.GetValueOrDefault("name")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        if (string.IsNullOrEmpty(nameOrId))
        {
            return CommandResult.Fail("Session name or ID is required. Usage: record delete <name>");
        }

        var deleted = await _recorder.DeleteSessionAsync(nameOrId);

        if (!deleted)
        {
            return CommandResult.Fail($"Session not found: '{nameOrId}'");
        }

        return CommandResult.Ok(message: $"Session '{nameOrId}' deleted.");
    }
}

/// <summary>
/// Shows details of a recorded session.
/// </summary>
public sealed class RecordShowCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordShowCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordShowCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.show";

    /// <inheritdoc />
    public override string Description => "Show details of a recorded session";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var nameOrId = parameters.GetValueOrDefault("name")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        if (string.IsNullOrEmpty(nameOrId))
        {
            return CommandResult.Fail("Session name or ID is required. Usage: record show <name>");
        }

        var session = await _recorder.GetSessionAsync(nameOrId);

        if (session == null)
        {
            return CommandResult.Fail($"Session not found: '{nameOrId}'");
        }

        var detail = new
        {
            session.Id,
            session.Name,
            session.Description,
            session.StartedAt,
            session.EndedAt,
            Status = session.IsRecording ? "Recording" : "Saved",
            CommandCount = session.Commands.Count,
            TotalDurationMs = session.TotalDurationMs,
            Commands = session.Commands.Select(c => new
            {
                c.CommandName,
                c.Success,
                DurationMs = c.DurationMs,
                c.ExecutedAt,
                c.Comment
            }).ToList()
        };

        return CommandResult.Ok(detail);
    }
}

/// <summary>
/// Gets the current recording status.
/// </summary>
public sealed class RecordStatusCommand : CommandBase
{
    private readonly CommandRecorder _recorder;

    /// <summary>
    /// Initializes a new RecordStatusCommand.
    /// </summary>
    /// <param name="recorder">The command recorder service.</param>
    public RecordStatusCommand(CommandRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <inheritdoc />
    public override string Name => "record.status";

    /// <inheritdoc />
    public override string Description => "Get the current recording status";

    /// <inheritdoc />
    public override string Category => "record";

    /// <inheritdoc />
    public override Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var session = _recorder.ActiveSession;

        if (session == null)
        {
            return Task.FromResult(CommandResult.Ok(
                new { IsRecording = false },
                "No active recording session."));
        }

        return Task.FromResult(CommandResult.Ok(
            new
            {
                IsRecording = true,
                session.Id,
                session.Name,
                session.StartedAt,
                CommandCount = session.Commands.Count,
                Duration = (DateTime.UtcNow - session.StartedAt).TotalSeconds
            },
            $"Recording in progress: '{session.Name}' ({session.Commands.Count} commands)"));
    }
}
