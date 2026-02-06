using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Result of a terminal write operation.
/// </summary>
public record TerminalResult
{
    /// <summary>Terminal ID that was written to.</summary>
    public string TerminalId { get; init; } = string.Empty;

    /// <summary>Terminal type (e.g., "primary", "replica").</summary>
    public string TerminalType { get; init; } = string.Empty;

    /// <summary>Whether the write succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Exception if failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Time taken for this terminal's write.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Bytes written to this terminal.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Storage path where data was written.</summary>
    public string? StoragePath { get; init; }

    /// <summary>Version ID if versioning is supported.</summary>
    public string? VersionId { get; init; }

    /// <summary>ETag/checksum of stored data.</summary>
    public string? ETag { get; init; }

    /// <summary>Additional metadata from the terminal.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Aggregate result of writing to all terminals in a pipeline.
/// </summary>
public record PipelineStorageResult
{
    /// <summary>Results from each terminal.</summary>
    public List<TerminalResult> TerminalResults { get; init; } = new();

    /// <summary>Whether all critical terminals succeeded.</summary>
    public bool Success => TerminalResults.TrueForAll(r => r.Success || !IsCritical(r.TerminalType));

    /// <summary>Whether any terminal failed.</summary>
    public bool HasFailures => TerminalResults.Exists(r => !r.Success);

    /// <summary>Total bytes written across all terminals.</summary>
    public long TotalBytesWritten => TerminalResults.Sum(r => r.BytesWritten);

    /// <summary>Total time for all terminal writes.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>The manifest after pipeline execution.</summary>
    public object? Manifest { get; init; }

    /// <summary>Transaction ID if transaction support was enabled.</summary>
    public string? TransactionId { get; init; }

    /// <summary>Primary terminal's storage path (for retrieval).</summary>
    public string? PrimaryPath => TerminalResults.FirstOrDefault(r => r.TerminalType == "primary")?.StoragePath;

    private static bool IsCritical(string terminalType) =>
        terminalType == "primary" || terminalType == "metadata";
}
