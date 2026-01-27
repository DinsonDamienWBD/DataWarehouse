// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.Shared.Commands;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Represents the result of a pipeline stage execution.
/// </summary>
public sealed record PipelineStageResult
{
    /// <summary>Gets whether the stage succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the output data from this stage.</summary>
    public object? Data { get; init; }

    /// <summary>Gets the error message if the stage failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the stage name/command.</summary>
    public required string StageName { get; init; }

    /// <summary>Gets the stage index in the pipeline.</summary>
    public int StageIndex { get; init; }

    /// <summary>Gets the execution time in milliseconds.</summary>
    public double ExecutionTimeMs { get; init; }
}

/// <summary>
/// Represents the final result of a pipeline execution.
/// </summary>
public sealed record PipelineResult
{
    /// <summary>Gets whether all stages completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the results from each stage.</summary>
    public IReadOnlyList<PipelineStageResult> StageResults { get; init; } = Array.Empty<PipelineStageResult>();

    /// <summary>Gets the final output data.</summary>
    public object? FinalOutput { get; init; }

    /// <summary>Gets the error message if any stage failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the total execution time in milliseconds.</summary>
    public double TotalExecutionTimeMs { get; init; }

    /// <summary>Gets the number of stages that were executed.</summary>
    public int StagesExecuted => StageResults.Count;

    /// <summary>
    /// Creates a successful pipeline result.
    /// </summary>
    public static PipelineResult Ok(IReadOnlyList<PipelineStageResult> stages, object? finalOutput, double totalMs) =>
        new()
        {
            Success = true,
            StageResults = stages,
            FinalOutput = finalOutput,
            TotalExecutionTimeMs = totalMs
        };

    /// <summary>
    /// Creates a failed pipeline result.
    /// </summary>
    public static PipelineResult Fail(IReadOnlyList<PipelineStageResult> stages, string error, double totalMs) =>
        new()
        {
            Success = false,
            StageResults = stages,
            Error = error,
            TotalExecutionTimeMs = totalMs
        };
}

/// <summary>
/// Represents a parsed pipeline command with its arguments.
/// </summary>
public sealed record PipelineCommand
{
    /// <summary>Gets the command name.</summary>
    public required string CommandName { get; init; }

    /// <summary>Gets the command arguments/parameters.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Gets the raw command string.</summary>
    public required string RawCommand { get; init; }
}

/// <summary>
/// Processes Unix-style command pipelines, allowing commands to be chained with the | operator.
/// Supports stdin input detection and output streaming for inter-command communication.
/// </summary>
/// <remarks>
/// Usage: dw list | dw filter --type image | dw backup
/// Each command receives the output of the previous command as input.
/// </remarks>
public sealed class PipelineProcessor : IDisposable
{
    private readonly CommandExecutor _executor;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the PipelineProcessor.
    /// </summary>
    /// <param name="executor">The command executor for running individual commands.</param>
    /// <param name="stdin">Optional custom stdin reader. Defaults to Console.In.</param>
    /// <param name="stdout">Optional custom stdout writer. Defaults to Console.Out.</param>
    public PipelineProcessor(CommandExecutor executor, TextReader? stdin = null, TextWriter? stdout = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _stdin = stdin ?? Console.In;
        _stdout = stdout ?? Console.Out;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Detects whether stdin has piped input available.
    /// </summary>
    /// <returns>True if stdin is redirected (piped input available).</returns>
    public bool HasStdinInput()
    {
        try
        {
            return Console.IsInputRedirected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if stdin has data available without blocking.
    /// </summary>
    /// <returns>True if data is immediately available.</returns>
    public bool HasStdinDataAvailable()
    {
        if (!HasStdinInput())
            return false;

        try
        {
            return _stdin.Peek() >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads all available input from stdin asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete stdin content.</returns>
    public async Task<string?> ReadAllStdinAsync(CancellationToken cancellationToken = default)
    {
        if (!HasStdinInput())
            return null;

        try
        {
            return await _stdin.ReadToEndAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads stdin line by line as an async enumerable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of lines from stdin.</returns>
    public async IAsyncEnumerable<string> ReadStdinAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!HasStdinInput())
            yield break;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _stdin.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch
            {
                yield break;
            }

            if (line == null)
                yield break;

            yield return line;
        }
    }

    /// <summary>
    /// Writes data to stdout for piping to the next command.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteStdoutAsync(object? data, CancellationToken cancellationToken = default)
    {
        if (data == null)
            return;

        string output;

        if (data is string strData)
        {
            output = strData;
        }
        else if (data is IEnumerable<string> lines)
        {
            output = string.Join(Environment.NewLine, lines);
        }
        else
        {
            // Serialize complex objects as JSON for pipeline transport
            output = JsonSerializer.Serialize(data, _jsonOptions);
        }

        await _stdout.WriteLineAsync(output.AsMemory(), cancellationToken);
        await _stdout.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Parses a pipeline string into individual commands.
    /// </summary>
    /// <param name="pipelineString">The pipeline string with | separators.</param>
    /// <returns>List of parsed pipeline commands.</returns>
    public IReadOnlyList<PipelineCommand> ParsePipeline(string pipelineString)
    {
        if (string.IsNullOrWhiteSpace(pipelineString))
            return Array.Empty<PipelineCommand>();

        var commands = new List<PipelineCommand>();
        var segments = SplitPipeline(pipelineString);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var parsed = ParseSingleCommand(trimmed);
            commands.Add(parsed);
        }

        return commands;
    }

    /// <summary>
    /// Executes a complete pipeline from a string.
    /// </summary>
    /// <param name="pipelineString">The pipeline string with | separators.</param>
    /// <param name="initialInput">Optional initial input data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the pipeline execution.</returns>
    public async Task<PipelineResult> ExecutePipelineAsync(
        string pipelineString,
        object? initialInput = null,
        CancellationToken cancellationToken = default)
    {
        var commands = ParsePipeline(pipelineString);
        return await ExecutePipelineAsync(commands, initialInput, cancellationToken);
    }

    /// <summary>
    /// Executes a pipeline of parsed commands.
    /// </summary>
    /// <param name="commands">The commands to execute in sequence.</param>
    /// <param name="initialInput">Optional initial input data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the pipeline execution.</returns>
    public async Task<PipelineResult> ExecutePipelineAsync(
        IReadOnlyList<PipelineCommand> commands,
        object? initialInput = null,
        CancellationToken cancellationToken = default)
    {
        if (commands.Count == 0)
        {
            return PipelineResult.Fail(
                Array.Empty<PipelineStageResult>(),
                "No commands in pipeline",
                0);
        }

        var stageResults = new List<PipelineStageResult>();
        var currentInput = initialInput;
        var totalStartTime = DateTime.UtcNow;

        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            var stageStartTime = DateTime.UtcNow;

            try
            {
                // Inject pipeline input if available
                var parameters = new Dictionary<string, object?>(command.Parameters);
                if (currentInput != null)
                {
                    parameters["__pipeline_input"] = currentInput;
                    parameters["__pipeline_stage"] = i;
                }

                var result = await _executor.ExecuteAsync(
                    command.CommandName,
                    parameters,
                    cancellationToken);

                var executionTime = (DateTime.UtcNow - stageStartTime).TotalMilliseconds;

                var stageResult = new PipelineStageResult
                {
                    Success = result.Success,
                    Data = result.Data,
                    Error = result.Error,
                    StageName = command.CommandName,
                    StageIndex = i,
                    ExecutionTimeMs = executionTime
                };

                stageResults.Add(stageResult);

                if (!result.Success)
                {
                    // Pipeline fails on first error
                    return PipelineResult.Fail(
                        stageResults,
                        $"Pipeline failed at stage {i + 1} ({command.CommandName}): {result.Error}",
                        (DateTime.UtcNow - totalStartTime).TotalMilliseconds);
                }

                // Pass output to next stage
                currentInput = result.Data;
            }
            catch (OperationCanceledException)
            {
                stageResults.Add(new PipelineStageResult
                {
                    Success = false,
                    Error = "Cancelled",
                    StageName = command.CommandName,
                    StageIndex = i,
                    ExecutionTimeMs = (DateTime.UtcNow - stageStartTime).TotalMilliseconds
                });

                return PipelineResult.Fail(
                    stageResults,
                    "Pipeline cancelled",
                    (DateTime.UtcNow - totalStartTime).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stageResults.Add(new PipelineStageResult
                {
                    Success = false,
                    Error = ex.Message,
                    StageName = command.CommandName,
                    StageIndex = i,
                    ExecutionTimeMs = (DateTime.UtcNow - stageStartTime).TotalMilliseconds
                });

                return PipelineResult.Fail(
                    stageResults,
                    $"Pipeline failed at stage {i + 1} ({command.CommandName}): {ex.Message}",
                    (DateTime.UtcNow - totalStartTime).TotalMilliseconds);
            }
        }

        return PipelineResult.Ok(
            stageResults,
            currentInput,
            (DateTime.UtcNow - totalStartTime).TotalMilliseconds);
    }

    /// <summary>
    /// Checks if a command string contains pipeline operators.
    /// </summary>
    /// <param name="commandString">The command string to check.</param>
    /// <returns>True if the string contains unquoted pipe operators.</returns>
    public static bool IsPipeline(string commandString)
    {
        if (string.IsNullOrEmpty(commandString))
            return false;

        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < commandString.Length; i++)
        {
            char c = commandString[i];

            // Track quote state
            if (c == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;
            else if (c == '|' && !inSingleQuote && !inDoubleQuote)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Deserializes pipeline input from JSON.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="pipelineInput">The pipeline input object.</param>
    /// <returns>The deserialized object, or default if conversion fails.</returns>
    public T? DeserializePipelineInput<T>(object? pipelineInput)
    {
        if (pipelineInput == null)
            return default;

        if (pipelineInput is T typed)
            return typed;

        if (pipelineInput is string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, _jsonOptions);
            }
            catch
            {
                return default;
            }
        }

        if (pipelineInput is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>(_jsonOptions);
            }
            catch
            {
                return default;
            }
        }

        // Try to convert via JSON round-trip
        try
        {
            var serialized = JsonSerializer.Serialize(pipelineInput, _jsonOptions);
            return JsonSerializer.Deserialize<T>(serialized, _jsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static IEnumerable<string> SplitPipeline(string pipelineString)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < pipelineString.Length; i++)
        {
            char c = pipelineString[i];

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                current.Append(c);
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(c);
            }
            else if (c == '|' && !inSingleQuote && !inDoubleQuote)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    private static PipelineCommand ParseSingleCommand(string commandString)
    {
        var parts = SplitArguments(commandString);
        if (parts.Count == 0)
        {
            return new PipelineCommand
            {
                CommandName = "",
                RawCommand = commandString
            };
        }

        var commandName = parts[0];
        var parameters = new Dictionary<string, object?>();

        // Handle "dw" prefix
        if (commandName.Equals("dw", StringComparison.OrdinalIgnoreCase) && parts.Count > 1)
        {
            commandName = parts[1];
            parts = parts.Skip(2).ToList();
        }
        else
        {
            parts = parts.Skip(1).ToList();
        }

        // Parse remaining arguments
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];

            if (part.StartsWith("--"))
            {
                var key = part[2..];
                if (i + 1 < parts.Count && !parts[i + 1].StartsWith("-"))
                {
                    parameters[key] = parts[++i];
                }
                else
                {
                    parameters[key] = true;
                }
            }
            else if (part.StartsWith("-") && part.Length == 2)
            {
                var key = part[1..];
                if (i + 1 < parts.Count && !parts[i + 1].StartsWith("-"))
                {
                    parameters[key] = parts[++i];
                }
                else
                {
                    parameters[key] = true;
                }
            }
            else
            {
                // Positional argument
                parameters[$"arg{parameters.Count}"] = part;
            }
        }

        return new PipelineCommand
        {
            CommandName = commandName,
            Parameters = parameters,
            RawCommand = commandString
        };
    }

    private static List<string> SplitArguments(string commandString)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < commandString.Length; i++)
        {
            char c = commandString[i];

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>
    /// Disposes resources used by the PipelineProcessor.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Don't dispose stdin/stdout as they may be Console streams
    }
}
