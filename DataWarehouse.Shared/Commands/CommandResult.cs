// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Represents the type of data in a command result.
/// </summary>
public enum ResultDataType
{
    /// <summary>No data.</summary>
    None,
    /// <summary>Single object.</summary>
    Object,
    /// <summary>Collection for table display.</summary>
    Table,
    /// <summary>Nested data for tree display.</summary>
    Tree,
    /// <summary>Key-value pairs.</summary>
    KeyValue,
    /// <summary>Chart data for visualization.</summary>
    Chart,
    /// <summary>Progress indicator data.</summary>
    Progress,
    /// <summary>Empty result (success with no data).</summary>
    Empty
}

/// <summary>
/// Represents the result of a command execution.
/// </summary>
public sealed record CommandResult
{
    /// <summary>
    /// Gets whether the command succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the result data (if any).
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Gets the result message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the error message (if failed).
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the exit code (0 for success, non-zero for errors).
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Gets the exception that caused the failure (if any).
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Gets the type of data in the result.
    /// </summary>
    public ResultDataType DataType { get; init; }

    /// <summary>
    /// Creates a successful result with optional data and message.
    /// </summary>
    public static CommandResult Ok(object? data = null, string? message = null)
    {
        return new CommandResult
        {
            Success = true,
            Data = data,
            Message = message,
            ExitCode = 0,
            DataType = data == null ? ResultDataType.None : ResultDataType.Object
        };
    }

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static CommandResult Fail(string error, int exitCode = 1)
    {
        return new CommandResult
        {
            Success = false,
            Error = error,
            ExitCode = exitCode,
            DataType = ResultDataType.None
        };
    }

    /// <summary>
    /// Creates a failed result with an error message and exception.
    /// </summary>
    public static CommandResult Fail(string error, Exception exception)
    {
        return new CommandResult
        {
            Success = false,
            Error = error,
            ExitCode = 1,
            Exception = exception,
            DataType = ResultDataType.None
        };
    }

    /// <summary>
    /// Creates a successful result with table data.
    /// </summary>
    public static CommandResult Table<T>(IEnumerable<T> data, string? message = null)
    {
        return new CommandResult
        {
            Success = true,
            Data = data,
            Message = message,
            ExitCode = 0,
            DataType = ResultDataType.Table
        };
    }

    /// <summary>
    /// Creates a result indicating a feature is not available.
    /// </summary>
    public static CommandResult FeatureNotAvailable(string feature)
    {
        return new CommandResult
        {
            Success = false,
            Error = $"Feature '{feature}' is not available. Please ensure the required plugins are loaded.",
            ExitCode = 2,
            DataType = ResultDataType.None
        };
    }

    /// <summary>
    /// Creates a result indicating the user is not connected.
    /// </summary>
    public static CommandResult NotConnected()
    {
        return new CommandResult
        {
            Success = false,
            Error = "Not connected to any instance. Use 'dw connect' to connect to an instance.",
            ExitCode = 3,
            DataType = ResultDataType.None
        };
    }
}
