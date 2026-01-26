// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Immutable;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Base interface for all executable commands in the DataWarehouse system.
/// Commands encapsulate business logic and can be executed from CLI, GUI, or API.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// Gets the unique name of the command (e.g., "storage.list", "backup.create").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a human-readable description of what the command does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the category this command belongs to (e.g., "storage", "backup", "config").
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Gets the list of required features for this command to be available.
    /// </summary>
    IReadOnlyList<string> RequiredFeatures { get; }

    /// <summary>
    /// Executes the command with the provided context and parameters.
    /// </summary>
    /// <param name="context">The execution context containing services.</param>
    /// <param name="parameters">The parameters passed to the command.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <returns>The result of the command execution.</returns>
    Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Output format for command results.
/// </summary>
public enum OutputFormat
{
    /// <summary>Default format based on data type.</summary>
    Default,
    /// <summary>Table format for collections.</summary>
    Table,
    /// <summary>JSON format.</summary>
    Json,
    /// <summary>YAML format.</summary>
    Yaml,
    /// <summary>Plain text format.</summary>
    Text,
    /// <summary>CSV format.</summary>
    Csv
}

/// <summary>
/// Context for command execution containing parameters, services, and connection info.
/// </summary>
public sealed record CommandContext
{
    /// <summary>
    /// Gets or sets the parameters passed to the command.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Gets or sets the current instance manager for accessing connected instances.
    /// </summary>
    public required InstanceManager InstanceManager { get; init; }

    /// <summary>
    /// Gets or sets the capability manager for checking available features.
    /// </summary>
    public required CapabilityManager CapabilityManager { get; init; }

    /// <summary>
    /// Gets or sets the message bridge for sending messages to instances.
    /// </summary>
    public MessageBridge? MessageBridge { get; init; }

    /// <summary>
    /// Gets or sets the desired output format for command results.
    /// </summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Default;

    /// <summary>
    /// Gets or sets whether verbose output is enabled.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// Gets or sets additional context data for extensibility.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ExtendedData { get; init; } = ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Gets a typed parameter value, or default if not found.
    /// </summary>
    public T GetParameter<T>(string name, T defaultValue = default!)
    {
        if (!Parameters.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is T typed)
        {
            return typed;
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Gets a required parameter value.
    /// </summary>
    public T GetRequiredParameter<T>(string name)
    {
        if (!Parameters.TryGetValue(name, out var value) || value is null)
        {
            throw new ArgumentException($"Required parameter '{name}' is missing.", name);
        }

        if (value is T typed)
        {
            return typed;
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Parameter '{name}' cannot be converted to {typeof(T).Name}.", name, ex);
        }
    }

    /// <summary>
    /// Checks if a parameter exists and has a non-null value.
    /// </summary>
    public bool HasParameter(string name) =>
        Parameters.TryGetValue(name, out var value) && value is not null;

    /// <summary>
    /// Creates a new context with additional parameters.
    /// </summary>
    public CommandContext WithParameters(IReadOnlyDictionary<string, object?> additionalParams)
    {
        var merged = new Dictionary<string, object?>(Parameters);
        foreach (var kv in additionalParams)
        {
            merged[kv.Key] = kv.Value;
        }
        return this with { Parameters = merged };
    }

    /// <summary>
    /// Ensures the context is connected to an instance.
    /// Does NOT throw in development mode - assumes mock connection.
    /// </summary>
    public void EnsureConnected()
    {
        // Development mode: always allow commands to proceed
        // In production, InstanceManager.IsConnected should be checked
    }

    /// <summary>
    /// Checks if a feature is available.
    /// </summary>
    public bool HasFeature(string feature) => CapabilityManager.HasFeature(feature);
}

/// <summary>
/// Result of command validation.
/// </summary>
public sealed class CommandValidationResult
{
    /// <summary>
    /// Gets whether the validation passed.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Gets the list of validation errors.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the list of validation warnings.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static CommandValidationResult Success() => new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result with errors.
    /// </summary>
    public static CommandValidationResult Failed(params string[] errors) =>
        new() { IsValid = false, Errors = errors };

    /// <summary>
    /// Creates a successful validation result with warnings.
    /// </summary>
    public static CommandValidationResult SuccessWithWarnings(params string[] warnings) =>
        new() { IsValid = true, Warnings = warnings };
}
