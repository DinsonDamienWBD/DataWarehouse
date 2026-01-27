// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Undoes the last operation or a specific operation.
/// </summary>
public sealed class UndoCommand : CommandBase
{
    private readonly UndoManager _undoManager;

    /// <summary>
    /// Initializes a new UndoCommand.
    /// </summary>
    /// <param name="undoManager">The undo manager service.</param>
    public UndoCommand(UndoManager undoManager)
    {
        _undoManager = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
    }

    /// <inheritdoc />
    public override string Name => "undo";

    /// <inheritdoc />
    public override string Description => "Undo the last operation or a specific operation";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        // Check for list flag
        var showList = parameters.GetValueOrDefault("list") as bool? ?? false;
        if (showList)
        {
            return await ShowUndoHistoryAsync(parameters);
        }

        // Check for specific operation ID
        var operationId = parameters.GetValueOrDefault("id")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        UndoResult result;

        if (!string.IsNullOrEmpty(operationId))
        {
            result = await _undoManager.UndoAsync(operationId, cancellationToken);
        }
        else
        {
            result = await _undoManager.UndoLastAsync(cancellationToken);
        }

        if (!result.Success)
        {
            return CommandResult.Fail(result.Error ?? "Undo failed.");
        }

        return CommandResult.Ok(
            new
            {
                OperationId = result.Operation?.Id,
                Command = result.Operation?.Command,
                Type = result.Operation?.Type.ToString(),
                result.Description
            },
            result.Description ?? "Operation undone.");
    }

    private async Task<CommandResult> ShowUndoHistoryAsync(Dictionary<string, object?> parameters)
    {
        var limit = 10;
        if (parameters.GetValueOrDefault("limit") is int l)
        {
            limit = l;
        }

        var includeExpired = parameters.GetValueOrDefault("all") as bool? ?? false;
        var history = await _undoManager.GetHistoryAsync(limit, includeExpired);

        if (history.Count == 0)
        {
            return CommandResult.Ok(message: "No undo history available.");
        }

        var summaries = history.Select(op => new
        {
            op.Id,
            op.Command,
            Type = op.Type.ToString(),
            op.Timestamp,
            CanUndo = op.CanUndo,
            Status = op.IsRolledBack ? "Undone" : op.IsExpired ? "Expired" : "Available"
        }).ToList();

        return CommandResult.Table(summaries, $"Found {history.Count} operation(s) in undo history");
    }
}

/// <summary>
/// Shows detailed undo history.
/// </summary>
public sealed class UndoListCommand : CommandBase
{
    private readonly UndoManager _undoManager;

    /// <summary>
    /// Initializes a new UndoListCommand.
    /// </summary>
    /// <param name="undoManager">The undo manager service.</param>
    public UndoListCommand(UndoManager undoManager)
    {
        _undoManager = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
    }

    /// <inheritdoc />
    public override string Name => "undo.list";

    /// <inheritdoc />
    public override string Description => "Show undo history";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var limit = 20;
        if (parameters.GetValueOrDefault("limit") is int l)
        {
            limit = l;
        }

        var includeExpired = parameters.GetValueOrDefault("all") as bool? ?? false;
        var history = await _undoManager.GetHistoryAsync(limit, includeExpired);

        if (history.Count == 0)
        {
            return CommandResult.Ok(message: "No undo history available.");
        }

        var summaries = history.Select(op => new
        {
            op.Id,
            op.Command,
            Type = op.Type.ToString(),
            op.Description,
            Timestamp = op.Timestamp,
            ExpiresAt = op.ExpiresAt,
            CanUndo = op.CanUndo,
            Status = op.IsRolledBack ? "Undone" :
                     op.IsExpired ? "Expired" :
                     !op.IsCommitted ? "Pending" : "Available"
        }).ToList();

        return CommandResult.Table(summaries, $"Undo history ({history.Count} operation(s))");
    }
}

/// <summary>
/// Clears undo history.
/// </summary>
public sealed class UndoClearCommand : CommandBase
{
    private readonly UndoManager _undoManager;

    /// <summary>
    /// Initializes a new UndoClearCommand.
    /// </summary>
    /// <param name="undoManager">The undo manager service.</param>
    public UndoClearCommand(UndoManager undoManager)
    {
        _undoManager = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
    }

    /// <inheritdoc />
    public override string Name => "undo.clear";

    /// <inheritdoc />
    public override string Description => "Clear all undo history";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var expiredOnly = parameters.GetValueOrDefault("expired") as bool? ?? false;

        if (expiredOnly)
        {
            var removed = await _undoManager.CleanupExpiredAsync();
            return CommandResult.Ok(message: $"Cleaned up {removed} expired operation(s).");
        }

        await _undoManager.ClearHistoryAsync();
        return CommandResult.Ok(message: "Undo history cleared.");
    }
}

/// <summary>
/// Shows details of a specific undoable operation.
/// </summary>
public sealed class UndoShowCommand : CommandBase
{
    private readonly UndoManager _undoManager;

    /// <summary>
    /// Initializes a new UndoShowCommand.
    /// </summary>
    /// <param name="undoManager">The undo manager service.</param>
    public UndoShowCommand(UndoManager undoManager)
    {
        _undoManager = undoManager ?? throw new ArgumentNullException(nameof(undoManager));
    }

    /// <inheritdoc />
    public override string Name => "undo.show";

    /// <inheritdoc />
    public override string Description => "Show details of an undoable operation";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var operationId = parameters.GetValueOrDefault("id")?.ToString()
            ?? parameters.GetValueOrDefault("arg0")?.ToString();

        if (string.IsNullOrEmpty(operationId))
        {
            return CommandResult.Fail("Operation ID is required. Usage: undo show <operation-id>");
        }

        var operation = await _undoManager.GetOperationAsync(operationId);

        if (operation == null)
        {
            return CommandResult.Fail($"Operation not found: '{operationId}'");
        }

        return CommandResult.Ok(new
        {
            operation.Id,
            operation.Command,
            Type = operation.Type.ToString(),
            operation.Description,
            operation.Timestamp,
            operation.ExpiresAt,
            operation.IsCommitted,
            operation.IsRolledBack,
            operation.CanUndo,
            operation.ResourceId,
            operation.ResourceType,
            operation.OriginalPath,
            operation.NewPath,
            HasBackup = operation.BackupData != null,
            BackupSizeBytes = operation.BackupData?.Length ?? 0,
            Metadata = operation.Metadata.Count > 0 ? operation.Metadata : null
        });
    }
}
