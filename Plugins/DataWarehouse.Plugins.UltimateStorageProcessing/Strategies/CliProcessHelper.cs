using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies;

/// <summary>
/// Shared helper for CLI-based storage processing strategies that invoke external tools
/// via <see cref="System.Diagnostics.Process"/>. Provides process execution, output parsing,
/// and common result construction utilities.
/// </summary>
internal static class CliProcessHelper
{
    /// <summary>
    /// Executes a CLI command and captures stdout, stderr, exit code, and elapsed time.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="timeoutMs">Maximum execution time in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The captured process output.</returns>
    public static async Task<CliOutput> RunAsync(
        string fileName, string arguments, string? workingDirectory = null,
        int timeoutMs = 300_000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder(4096);
        var stderr = new StringBuilder(2048);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    $"Failed to start process '{process.StartInfo.FileName}'. " +
                    "Check that the executable exists and the system allows process creation.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await process.WaitForExitAsync(cts.Token);
            sw.Stop();

            return new CliOutput
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                Elapsed = sw.Elapsed,
                Success = process.ExitCode == 0
            };
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* cleanup */ }
            sw.Stop();
            return new CliOutput
            {
                ExitCode = -1,
                StandardOutput = stdout.ToString(),
                StandardError = "Process timed out or was cancelled",
                Elapsed = sw.Elapsed,
                Success = false
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CliOutput
            {
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"Failed to start process '{fileName}': {ex.Message}",
                Elapsed = sw.Elapsed,
                Success = false
            };
        }
    }

    /// <summary>
    /// Creates a processing result from a CLI output.
    /// </summary>
    public static ProcessingResult ToProcessingResult(
        CliOutput output, string sourcePath, string toolName, Dictionary<string, object?>? extraData = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["tool"] = toolName,
            ["exitCode"] = output.ExitCode,
            ["success"] = output.Success,
            ["stdout"] = output.StandardOutput.Length > 4096 ? output.StandardOutput[..4096] : output.StandardOutput,
            ["stderr"] = output.StandardError.Length > 2048 ? output.StandardError[..2048] : output.StandardError,
            ["elapsedMs"] = output.Elapsed.TotalMilliseconds
        };

        if (extraData != null)
            foreach (var kvp in extraData)
                data[kvp.Key] = kvp.Value;

        return new ProcessingResult
        {
            Data = data,
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = 1, RowsReturned = 1,
                BytesProcessed = output.StandardOutput.Length + output.StandardError.Length,
                ProcessingTimeMs = output.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <summary>
    /// Creates an error result when a tool is not available.
    /// </summary>
    public static ProcessingResult ToolNotFound(string toolName, string source, Stopwatch sw)
    {
        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["error"] = $"Tool '{toolName}' not found or not accessible",
                ["source"] = source,
                ["tool"] = toolName
            },
            Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        };
    }

    /// <summary>
    /// Enumerates files in a directory matching criteria, producing ProcessingResults.
    /// </summary>
    public static async IAsyncEnumerable<ProcessingResult> EnumerateProjectFiles(
        ProcessingQuery query, string[] extensions, Stopwatch sw, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Normalize and validate source to prevent path-traversal attacks.
        string normalizedSource;
        try
        {
            normalizedSource = Path.GetFullPath(query.Source);
        }
        catch
        {
            await Task.CompletedTask;
            yield break;
        }
        if (!Directory.Exists(normalizedSource)) { await Task.CompletedTask; yield break; }

        var limit = query.Limit ?? 1000; // Cap at 1000 files to prevent OOM on unbounded walks (finding 4245)
        var offset = query.Offset ?? 0;
        var idx = 0;
        foreach (var file in Directory.EnumerateFiles(normalizedSource, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (extensions.Length > 0 && !extensions.Contains(ext)) continue;

            if (idx < offset) { idx++; continue; }
            if (idx - offset >= limit) break;

            var info = new FileInfo(file);
            yield return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["filePath"] = file, ["fileName"] = info.Name, ["size"] = info.Length,
                    ["extension"] = ext, ["lastModified"] = info.LastWriteTimeUtc.ToString("O")
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = info.Length, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
            idx++;
        }
    }

    /// <summary>
    /// Aggregates file counts/sizes from the source path for build strategies.
    /// </summary>
    public static Task<AggregationResult> AggregateProjectFiles(ProcessingQuery query, AggregationType aggregationType, string[] extensions, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sizes = new List<long>();

        if (Directory.Exists(query.Source))
        {
            foreach (var f in Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                sizes.Add(new FileInfo(f).Length);
            }
        }
        else if (File.Exists(query.Source))
        {
            sizes.Add(new FileInfo(query.Source).Length);
        }

        sw.Stop();
        object value = aggregationType switch
        {
            AggregationType.Count => (long)sizes.Count,
            AggregationType.Sum => sizes.Count > 0 ? sizes.Sum() : 0L,
            AggregationType.Average => sizes.Count > 0 ? sizes.Average() : 0.0,
            AggregationType.Min => sizes.Count > 0 ? (object)sizes.Min() : 0L,
            AggregationType.Max => sizes.Count > 0 ? (object)sizes.Max() : 0L,
            _ => 0.0
        };

        return Task.FromResult(new AggregationResult
        {
            AggregationType = aggregationType,
            Value = value,
            Metadata = new ProcessingMetadata { RowsProcessed = sizes.Count, RowsReturned = 1, BytesProcessed = sizes.Sum(), ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        });
    }

    /// <summary>
    /// Gets an option value from a ProcessingQuery.
    /// </summary>
    public static T? GetOption<T>(ProcessingQuery query, string key)
    {
        if (query.Options?.TryGetValue(key, out var v) == true && v is T t) return t;
        return default;
    }

    /// <summary>
    /// Validates that a string value does not contain shell metacharacters that could
    /// enable command injection when the value is interpolated into CLI argument strings.
    /// Throws <see cref="ArgumentException"/> if invalid characters are found.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="paramName">Parameter name for the exception message.</param>
    public static void ValidateNoShellMetachars(string value, string paramName)
    {
        // Shell metacharacters that could inject additional commands or arguments
        const string Forbidden = ";&|`$(){}[]<>!\\\"'\n\r\t";
        foreach (var c in value)
        {
            if (Forbidden.Contains(c))
            {
                throw new ArgumentException(
                    $"'{paramName}' contains forbidden character '{c}' (0x{(int)c:X2}) that could enable command injection.",
                    paramName);
            }
        }
    }

    /// <summary>
    /// Validates that a string value matches an expected allowlist of safe values.
    /// Throws <see cref="ArgumentException"/> if the value is not in the allowlist.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="paramName">Parameter name for the exception message.</param>
    /// <param name="allowedValues">Case-sensitive set of permitted values.</param>
    public static void ValidateAllowlist(string value, string paramName, IReadOnlySet<string> allowedValues)
    {
        if (!allowedValues.Contains(value))
        {
            throw new ArgumentException(
                $"'{paramName}' value '{value}' is not in the allowlist. Permitted: {string.Join(", ", allowedValues)}",
                paramName);
        }
    }

    /// <summary>
    /// Validates that a string value matches a safe identifier pattern (alphanumeric, hyphens, underscores, dots only).
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="paramName">Parameter name for the exception message.</param>
    public static void ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{paramName}' must not be null or whitespace.", paramName);

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.' && c != ':')
            {
                throw new ArgumentException(
                    $"'{paramName}' contains invalid character '{c}' (0x{(int)c:X2}). Only alphanumeric, hyphen, underscore, dot, and colon are allowed.",
                    paramName);
            }
        }
    }
}

/// <summary>
/// Represents the output of a CLI process execution.
/// </summary>
internal sealed record CliOutput
{
    /// <summary>Gets the process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Gets the captured standard output.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Gets the captured standard error.</summary>
    public required string StandardError { get; init; }

    /// <summary>Gets the execution time.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Gets whether the process completed successfully (exit code 0).</summary>
    public required bool Success { get; init; }
}
