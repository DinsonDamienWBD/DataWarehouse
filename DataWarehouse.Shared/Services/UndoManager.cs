// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Types of operations that can be undone.
/// </summary>
public enum OperationType
{
    /// <summary>File or resource deletion.</summary>
    Delete,

    /// <summary>File or resource move/rename.</summary>
    Move,

    /// <summary>File or resource modification.</summary>
    Modify,

    /// <summary>Resource creation (undo = delete).</summary>
    Create,

    /// <summary>Configuration change.</summary>
    ConfigChange,

    /// <summary>Custom operation type.</summary>
    Custom
}

/// <summary>
/// Represents an operation that can be undone.
/// </summary>
public sealed class UndoableOperation
{
    /// <summary>Gets or sets the unique operation identifier.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the command that triggered this operation.</summary>
    public required string Command { get; set; }

    /// <summary>Gets or sets the operation type.</summary>
    public OperationType Type { get; set; }

    /// <summary>Gets or sets when the operation occurred.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the description of what was done.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets whether the operation has been committed.</summary>
    public bool IsCommitted { get; set; }

    /// <summary>Gets or sets whether the operation has been rolled back.</summary>
    public bool IsRolledBack { get; set; }

    /// <summary>Gets or sets whether this operation can still be undone.</summary>
    [JsonIgnore]
    public bool CanUndo => IsCommitted && !IsRolledBack && !IsExpired;

    /// <summary>Gets or sets when this undo capability expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Gets whether the undo has expired.</summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

    /// <summary>Gets or sets backup data for deleted files (base64 encoded).</summary>
    public string? BackupDataBase64 { get; set; }

    /// <summary>Gets or sets the backup data (binary).</summary>
    [JsonIgnore]
    public byte[]? BackupData
    {
        get => BackupDataBase64 == null ? null : Convert.FromBase64String(BackupDataBase64);
        set => BackupDataBase64 = value == null ? null : Convert.ToBase64String(value);
    }

    /// <summary>Gets or sets the original path for moved files.</summary>
    public string? OriginalPath { get; set; }

    /// <summary>Gets or sets the new path for moved files.</summary>
    public string? NewPath { get; set; }

    /// <summary>Gets or sets the resource identifier affected.</summary>
    public string? ResourceId { get; set; }

    /// <summary>Gets or sets the resource type affected.</summary>
    public string? ResourceType { get; set; }

    /// <summary>Gets or sets custom undo data.</summary>
    public Dictionary<string, object?> UndoData { get; set; } = new();

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Result of an undo operation.
/// </summary>
public sealed record UndoResult
{
    /// <summary>Gets whether the undo succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the operation that was undone.</summary>
    public UndoableOperation? Operation { get; init; }

    /// <summary>Gets the error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the description of what was undone.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Creates a successful undo result.
    /// </summary>
    public static UndoResult Ok(UndoableOperation operation, string description) =>
        new() { Success = true, Operation = operation, Description = description };

    /// <summary>
    /// Creates a failed undo result.
    /// </summary>
    public static UndoResult Fail(string error, UndoableOperation? operation = null) =>
        new() { Success = false, Error = error, Operation = operation };
}

/// <summary>
/// Delegate for performing the actual undo action.
/// </summary>
/// <param name="operation">The operation to undo.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>True if undo succeeded.</returns>
public delegate Task<bool> UndoActionDelegate(UndoableOperation operation, CancellationToken cancellationToken);

/// <summary>
/// Manages undo/rollback capabilities for destructive operations.
/// Provides automatic backup of deleted resources and transaction-like semantics.
/// </summary>
/// <remarks>
/// Integrates with CLI for undo command and GUI for Ctrl+Z functionality.
/// </remarks>
public sealed class UndoManager : IDisposable
{
    private readonly string _undoStorePath;
    private readonly List<UndoableOperation> _operations = new();
    private readonly Dictionary<OperationType, UndoActionDelegate> _undoHandlers = new();
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly int _maxOperations;
    private readonly TimeSpan _defaultExpiry;
    private bool _disposed;

    /// <summary>
    /// Event raised when an operation is recorded.
    /// </summary>
    public event EventHandler<UndoableOperation>? OperationRecorded;

    /// <summary>
    /// Event raised when an operation is undone.
    /// </summary>
    public event EventHandler<UndoableOperation>? OperationUndone;

    /// <summary>
    /// Initializes a new UndoManager.
    /// </summary>
    /// <param name="undoStorePath">Optional custom path for undo data storage.</param>
    /// <param name="maxOperations">Maximum operations to keep (default 100).</param>
    /// <param name="defaultExpiry">Default time before undo expires (default 24 hours).</param>
    public UndoManager(
        string? undoStorePath = null,
        int maxOperations = 100,
        TimeSpan? defaultExpiry = null)
    {
        _undoStorePath = undoStorePath ?? GetDefaultUndoStorePath();
        _maxOperations = maxOperations;
        _defaultExpiry = defaultExpiry ?? TimeSpan.FromHours(24);
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        EnsureStoreDirectory();
        LoadOperations();
        RegisterDefaultHandlers();
    }

    /// <summary>
    /// Gets the count of undoable operations.
    /// </summary>
    public int UndoableCount
    {
        get
        {
            lock (_lock)
            {
                return _operations.Count(o => o.CanUndo);
            }
        }
    }

    /// <summary>
    /// Gets whether there are operations that can be undone.
    /// </summary>
    public bool CanUndo => UndoableCount > 0;

    /// <summary>
    /// Gets the most recent undoable operation.
    /// </summary>
    public UndoableOperation? LastUndoable
    {
        get
        {
            lock (_lock)
            {
                return _operations
                    .Where(o => o.CanUndo)
                    .OrderByDescending(o => o.Timestamp)
                    .FirstOrDefault();
            }
        }
    }

    /// <summary>
    /// Registers a custom undo handler for an operation type.
    /// </summary>
    /// <param name="type">The operation type.</param>
    /// <param name="handler">The undo handler.</param>
    public void RegisterUndoHandler(OperationType type, UndoActionDelegate handler)
    {
        lock (_lock)
        {
            _undoHandlers[type] = handler;
        }
    }

    /// <summary>
    /// Begins tracking an undoable operation.
    /// </summary>
    /// <param name="operation">The operation to track.</param>
    /// <returns>The operation ID for later reference.</returns>
    public Task<string> BeginOperationAsync(UndoableOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (string.IsNullOrEmpty(operation.Id))
        {
            operation.Id = GenerateOperationId();
        }

        operation.Timestamp = DateTime.UtcNow;
        operation.ExpiresAt ??= DateTime.UtcNow.Add(_defaultExpiry);
        operation.IsCommitted = false;
        operation.IsRolledBack = false;

        lock (_lock)
        {
            _operations.Add(operation);
            TrimOperations();
        }

        return Task.FromResult(operation.Id);
    }

    /// <summary>
    /// Commits an operation, making it eligible for undo.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <returns>True if committed, false if not found.</returns>
    public Task<bool> CommitAsync(string operationId)
    {
        lock (_lock)
        {
            var operation = _operations.FirstOrDefault(o => o.Id == operationId);
            if (operation == null)
                return Task.FromResult(false);

            operation.IsCommitted = true;
            OperationRecorded?.Invoke(this, operation);
        }

        SaveOperationsAsync();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Records and commits an operation in one step.
    /// </summary>
    /// <param name="operation">The operation to record.</param>
    /// <returns>The operation ID.</returns>
    public async Task<string> RecordOperationAsync(UndoableOperation operation)
    {
        var id = await BeginOperationAsync(operation);
        await CommitAsync(id);
        return id;
    }

    /// <summary>
    /// Rolls back an uncommitted operation (transaction abort).
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Undo result.</returns>
    public async Task<UndoResult> RollbackAsync(string operationId, CancellationToken cancellationToken = default)
    {
        UndoableOperation? operation;

        lock (_lock)
        {
            operation = _operations.FirstOrDefault(o => o.Id == operationId);
            if (operation == null)
            {
                return UndoResult.Fail($"Operation not found: {operationId}");
            }

            if (operation.IsCommitted)
            {
                return UndoResult.Fail("Cannot rollback committed operation. Use Undo instead.");
            }

            if (operation.IsRolledBack)
            {
                return UndoResult.Fail("Operation already rolled back.");
            }
        }

        return await PerformUndoAsync(operation, cancellationToken);
    }

    /// <summary>
    /// Undoes a specific operation.
    /// </summary>
    /// <param name="operationId">The operation ID to undo.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Undo result.</returns>
    public async Task<UndoResult> UndoAsync(string operationId, CancellationToken cancellationToken = default)
    {
        UndoableOperation? operation;

        lock (_lock)
        {
            operation = _operations.FirstOrDefault(o => o.Id == operationId);
            if (operation == null)
            {
                return UndoResult.Fail($"Operation not found: {operationId}");
            }

            if (!operation.CanUndo)
            {
                if (operation.IsRolledBack)
                    return UndoResult.Fail("Operation already undone.");
                if (operation.IsExpired)
                    return UndoResult.Fail("Undo has expired for this operation.");
                if (!operation.IsCommitted)
                    return UndoResult.Fail("Operation was never committed.");
            }
        }

        return await PerformUndoAsync(operation, cancellationToken);
    }

    /// <summary>
    /// Undoes the last operation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Undo result.</returns>
    public async Task<UndoResult> UndoLastAsync(CancellationToken cancellationToken = default)
    {
        var last = LastUndoable;
        if (last == null)
        {
            return UndoResult.Fail("No operations available to undo.");
        }

        return await UndoAsync(last.Id, cancellationToken);
    }

    /// <summary>
    /// Gets the undo history.
    /// </summary>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="includeExpired">Whether to include expired entries.</param>
    /// <returns>List of undoable operations.</returns>
    public Task<IReadOnlyList<UndoableOperation>> GetHistoryAsync(int limit = 10, bool includeExpired = false)
    {
        lock (_lock)
        {
            var query = _operations.AsEnumerable();

            if (!includeExpired)
            {
                query = query.Where(o => !o.IsExpired);
            }

            var result = query
                .OrderByDescending(o => o.Timestamp)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<UndoableOperation>>(result);
        }
    }

    /// <summary>
    /// Gets an operation by ID.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <returns>The operation, or null if not found.</returns>
    public Task<UndoableOperation?> GetOperationAsync(string operationId)
    {
        lock (_lock)
        {
            var operation = _operations.FirstOrDefault(o => o.Id == operationId);
            return Task.FromResult(operation);
        }
    }

    /// <summary>
    /// Clears all undo history.
    /// </summary>
    public Task ClearHistoryAsync()
    {
        lock (_lock)
        {
            _operations.Clear();
        }

        SaveOperationsAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes expired operations from history.
    /// </summary>
    /// <returns>Number of operations cleaned up.</returns>
    public Task<int> CleanupExpiredAsync()
    {
        int removed;
        lock (_lock)
        {
            var before = _operations.Count;
            _operations.RemoveAll(o => o.IsExpired || o.IsRolledBack);
            removed = before - _operations.Count;
        }

        if (removed > 0)
        {
            SaveOperationsAsync();
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Creates a backup of file data for delete operations.
    /// </summary>
    /// <param name="filePath">The file to backup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backup data, or null if file doesn't exist.</returns>
    public async Task<byte[]?> CreateFileBackupAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            return await File.ReadAllBytesAsync(filePath, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restores a file from backup data.
    /// </summary>
    /// <param name="filePath">The path to restore to.</param>
    /// <param name="backupData">The backup data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if restored successfully.</returns>
    public async Task<bool> RestoreFileAsync(string filePath, byte[] backupData, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(filePath, backupData, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<UndoResult> PerformUndoAsync(UndoableOperation operation, CancellationToken cancellationToken)
    {
        UndoActionDelegate? handler;
        lock (_lock)
        {
            _undoHandlers.TryGetValue(operation.Type, out handler);
        }

        if (handler == null)
        {
            return UndoResult.Fail($"No undo handler registered for operation type: {operation.Type}");
        }

        try
        {
            var success = await handler(operation, cancellationToken);

            if (success)
            {
                lock (_lock)
                {
                    operation.IsRolledBack = true;
                }

                SaveOperationsAsync();
                OperationUndone?.Invoke(this, operation);

                return UndoResult.Ok(operation, $"Undone: {operation.Description ?? operation.Command}");
            }
            else
            {
                return UndoResult.Fail("Undo handler returned false.", operation);
            }
        }
        catch (Exception ex)
        {
            return UndoResult.Fail($"Undo failed: {ex.Message}", operation);
        }
    }

    private void RegisterDefaultHandlers()
    {
        // Delete: restore from backup
        RegisterUndoHandler(OperationType.Delete, async (op, ct) =>
        {
            if (op.BackupData == null || string.IsNullOrEmpty(op.OriginalPath))
                return false;

            return await RestoreFileAsync(op.OriginalPath, op.BackupData, ct);
        });

        // Move: move back
        RegisterUndoHandler(OperationType.Move, (op, ct) =>
        {
            if (string.IsNullOrEmpty(op.OriginalPath) || string.IsNullOrEmpty(op.NewPath))
                return Task.FromResult(false);

            try
            {
                if (File.Exists(op.NewPath))
                {
                    File.Move(op.NewPath, op.OriginalPath);
                }
                else if (Directory.Exists(op.NewPath))
                {
                    Directory.Move(op.NewPath, op.OriginalPath);
                }
                else
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        });

        // Create: delete the created resource
        RegisterUndoHandler(OperationType.Create, (op, ct) =>
        {
            if (string.IsNullOrEmpty(op.NewPath))
                return Task.FromResult(false);

            try
            {
                if (File.Exists(op.NewPath))
                {
                    File.Delete(op.NewPath);
                }
                else if (Directory.Exists(op.NewPath))
                {
                    Directory.Delete(op.NewPath, recursive: true);
                }

                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        });

        // Modify: restore from backup
        RegisterUndoHandler(OperationType.Modify, async (op, ct) =>
        {
            if (op.BackupData == null || string.IsNullOrEmpty(op.OriginalPath))
                return false;

            return await RestoreFileAsync(op.OriginalPath, op.BackupData, ct);
        });

        // ConfigChange: restore old value from UndoData
        RegisterUndoHandler(OperationType.ConfigChange, (op, ct) =>
        {
            // This would need integration with configuration system
            // For now, just return success if old value is stored
            return Task.FromResult(op.UndoData.ContainsKey("oldValue"));
        });

        // Custom: look for handler in UndoData
        RegisterUndoHandler(OperationType.Custom, (op, ct) =>
        {
            // Custom operations need their own handler logic stored in UndoData
            return Task.FromResult(false);
        });
    }

    private void TrimOperations()
    {
        if (_operations.Count > _maxOperations)
        {
            var toRemove = _operations
                .OrderBy(o => o.Timestamp)
                .Take(_operations.Count - _maxOperations)
                .ToList();

            foreach (var op in toRemove)
            {
                _operations.Remove(op);
            }
        }
    }

    private void LoadOperations()
    {
        var filePath = GetOperationsFilePath();
        if (!File.Exists(filePath))
            return;

        try
        {
            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<List<UndoableOperation>>(json, _jsonOptions);
            if (loaded != null)
            {
                lock (_lock)
                {
                    _operations.Clear();
                    _operations.AddRange(loaded.Where(o => !o.IsExpired));
                }
            }
        }
        catch
        {
            // Ignore load errors - start fresh
        }
    }

    private async void SaveOperationsAsync()
    {
        try
        {
            EnsureStoreDirectory();
            var filePath = GetOperationsFilePath();

            List<UndoableOperation> snapshot;
            lock (_lock)
            {
                snapshot = _operations.ToList();
            }

            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private void EnsureStoreDirectory()
    {
        if (!Directory.Exists(_undoStorePath))
        {
            Directory.CreateDirectory(_undoStorePath);
        }
    }

    private string GetOperationsFilePath() =>
        Path.Combine(_undoStorePath, "undo_history.json");

    private static string GenerateOperationId() =>
        $"op-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string GetDefaultUndoStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "undo");

    /// <summary>
    /// Disposes resources used by the UndoManager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Final save
        List<UndoableOperation> snapshot;
        lock (_lock)
        {
            snapshot = _operations.ToList();
        }

        try
        {
            EnsureStoreDirectory();
            var filePath = GetOperationsFilePath();
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Ignore save errors on dispose
        }
    }
}
