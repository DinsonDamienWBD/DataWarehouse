using System;
using System.IO;
using System.Net.Http;
using DataWarehouse.SDK.Storage.Fabric;

namespace DataWarehouse.Plugins.UniversalFabric.Resilience;

/// <summary>
/// Maps backend-specific exceptions to standard fabric exception types.
/// Provides consistent error classification across heterogeneous storage backends
/// so that callers see a uniform exception hierarchy regardless of which backend failed.
/// </summary>
/// <remarks>
/// <para>
/// Different backends fail in different ways: S3 returns HTTP 503, local disk throws
/// <see cref="IOException"/>, network storage times out. The ErrorNormalizer translates
/// all of these into the fabric exception hierarchy defined in
/// <see cref="DataWarehouse.SDK.Storage.Fabric"/>.
/// </para>
/// <para>
/// In addition to normalization, ErrorNormalizer classifies exceptions as retryable
/// (transient failures that may succeed on retry) or fallback-worthy (backend-level
/// failures that warrant trying an alternative backend).
/// </para>
/// </remarks>
public class ErrorNormalizer
{
    /// <summary>
    /// Normalizes any backend exception into a standard fabric exception.
    /// Already-normalized exceptions (fabric exception types) are returned as-is.
    /// </summary>
    /// <param name="ex">The original exception thrown by the backend.</param>
    /// <param name="backendId">The identifier of the backend that threw the exception.</param>
    /// <param name="operation">The operation being performed (e.g., "Store", "Retrieve", "Delete").</param>
    /// <param name="key">The storage key involved in the operation, if applicable.</param>
    /// <returns>A normalized exception from the fabric exception hierarchy.</returns>
    public Exception Normalize(Exception ex, string backendId, string operation, string? key = null)
    {
        return ex switch
        {
            // Already normalized -- pass through
            StorageFabricException => ex,
            BackendNotFoundException => ex,
            BackendUnavailableException => ex,
            PlacementFailedException => ex,
            MigrationFailedException => ex,

            // Network/connectivity -- backend is unreachable
            HttpRequestException httpEx => new BackendUnavailableException(
                $"Backend '{backendId}' unreachable during {operation}: {httpEx.Message}",
                backendId, httpEx),

            // Timeout -- backend did not respond in time
            TimeoutException => new BackendUnavailableException(
                $"Backend '{backendId}' timed out during {operation}",
                backendId, ex),

            // Operation cancelled -- propagate without wrapping
            OperationCanceledException => ex,

            // Not found -- must come before generic IOException since these inherit from IOException
            FileNotFoundException => new KeyNotFoundException(
                $"Object not found in backend '{backendId}': {key ?? "(unknown)"}"),

            DirectoryNotFoundException => new KeyNotFoundException(
                $"Path not found in backend '{backendId}': {key ?? "(unknown)"}"),

            // I/O errors -- disk full is a fabric error, others are unavailable
            IOException ioEx when ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase) =>
                new StorageFabricException(
                    $"Backend '{backendId}' disk full during {operation}", ex),

            IOException => new BackendUnavailableException(
                $"Backend '{backendId}' I/O error during {operation}: {ex.Message}",
                backendId, ex),

            // Auth errors -- access denied is a fabric-level error
            UnauthorizedAccessException => new StorageFabricException(
                $"Access denied to backend '{backendId}': {ex.Message}", ex),

            // Default -- wrap in general fabric exception
            _ => new StorageFabricException(
                $"Backend '{backendId}' error during {operation}: {ex.Message}", ex)
        };
    }

    /// <summary>
    /// Determines whether an exception represents a transient failure that may succeed on retry.
    /// Retryable exceptions include backend unavailability, timeouts, and HTTP request failures.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>True if the operation should be retried; false otherwise.</returns>
    public bool IsRetryable(Exception ex) => ex is BackendUnavailableException
                                              or TimeoutException
                                              or HttpRequestException;

    /// <summary>
    /// Determines whether an exception warrants falling back to an alternative backend.
    /// Fallback is appropriate when the backend itself is unavailable, not when the
    /// specific operation failed due to bad input or missing data.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>True if the fabric should try an alternative backend; false otherwise.</returns>
    public bool ShouldFallback(Exception ex) => ex is BackendUnavailableException;
}
