using DataWarehouse.SDK.Contracts;
using System;
using System.IO;

namespace DataWarehouse.SDK.Storage.Fabric;

/// <summary>
/// Thrown when no registered backend can handle the specified storage address.
/// </summary>
/// <remarks>
/// This exception indicates a routing failure -- the fabric has no backend registered
/// that matches the address scheme, backend ID, or address pattern. Check that the
/// target backend is registered in <see cref="IBackendRegistry"/> and that the address
/// is well-formed.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public class BackendNotFoundException : InvalidOperationException
{
    /// <summary>
    /// Gets the backend ID that was not found, if available.
    /// </summary>
    public string? BackendId { get; }

    /// <summary>
    /// Gets the storage address that could not be routed, if available.
    /// </summary>
    public StorageAddress? Address { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendNotFoundException"/> with a message.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    public BackendNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendNotFoundException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="inner">The exception that caused this error.</param>
    public BackendNotFoundException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendNotFoundException"/> with address and backend context.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="backendId">The backend ID that was not found.</param>
    /// <param name="address">The storage address that could not be routed.</param>
    public BackendNotFoundException(string message, string? backendId, StorageAddress? address)
        : base(message)
    {
        BackendId = backendId;
        Address = address;
    }
}

/// <summary>
/// Thrown when a registered backend is unhealthy or unreachable and cannot serve requests.
/// </summary>
/// <remarks>
/// Unlike <see cref="BackendNotFoundException"/>, this exception means the backend exists
/// in the registry but is currently unavailable. The fabric may retry with a fallback backend
/// or surface this to the caller for retry handling.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public class BackendUnavailableException : IOException
{
    /// <summary>
    /// Gets the ID of the unavailable backend.
    /// </summary>
    public string? BackendId { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendUnavailableException"/> with a message.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    public BackendUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendUnavailableException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="inner">The exception that caused this error.</param>
    public BackendUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendUnavailableException"/> with backend context.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="backendId">The ID of the unavailable backend.</param>
    public BackendUnavailableException(string message, string backendId)
        : base(message)
    {
        BackendId = backendId;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="BackendUnavailableException"/> with backend context and inner exception.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="backendId">The ID of the unavailable backend.</param>
    /// <param name="inner">The exception that caused this error.</param>
    public BackendUnavailableException(string message, string backendId, Exception inner)
        : base(message, inner)
    {
        BackendId = backendId;
    }
}

/// <summary>
/// Thrown when no registered backend matches the specified placement hints.
/// </summary>
/// <remarks>
/// This exception indicates that the fabric found backends but none satisfy the caller's
/// placement requirements (tier, tags, region, encryption, versioning, capacity).
/// Consider relaxing placement hints or registering additional backends.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public class PlacementFailedException : InvalidOperationException
{
    /// <summary>
    /// Gets the placement hints that could not be satisfied, if available.
    /// </summary>
    public StoragePlacementHints? Hints { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="PlacementFailedException"/> with a message.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    public PlacementFailedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PlacementFailedException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="inner">The exception that caused this error.</param>
    public PlacementFailedException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PlacementFailedException"/> with placement hints context.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="hints">The placement hints that could not be satisfied.</param>
    public PlacementFailedException(string message, StoragePlacementHints? hints)
        : base(message)
    {
        Hints = hints;
    }
}

/// <summary>
/// Thrown when a cross-backend copy or move operation fails.
/// </summary>
/// <remarks>
/// Migration failures can occur due to network issues, backend unavailability,
/// or incompatible data formats between source and destination backends.
/// The source data remains intact on failure -- partial writes to the destination
/// should be cleaned up by the fabric.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public class MigrationFailedException : IOException
{
    /// <summary>
    /// Gets the source storage address of the failed migration.
    /// </summary>
    public StorageAddress? SourceAddress { get; }

    /// <summary>
    /// Gets the destination storage address of the failed migration.
    /// </summary>
    public StorageAddress? DestinationAddress { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="MigrationFailedException"/> with a message.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    public MigrationFailedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MigrationFailedException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="inner">The exception that caused this error.</param>
    public MigrationFailedException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MigrationFailedException"/> with migration context.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="source">The source storage address.</param>
    /// <param name="destination">The destination storage address.</param>
    public MigrationFailedException(string message, StorageAddress? source, StorageAddress? destination)
        : base(message)
    {
        SourceAddress = source;
        DestinationAddress = destination;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MigrationFailedException"/> with migration context and inner exception.
    /// </summary>
    /// <param name="message">A description of the error.</param>
    /// <param name="source">The source storage address.</param>
    /// <param name="destination">The destination storage address.</param>
    /// <param name="inner">The exception that caused this error.</param>
    public MigrationFailedException(string message, StorageAddress? source, StorageAddress? destination, Exception inner)
        : base(message, inner)
    {
        SourceAddress = source;
        DestinationAddress = destination;
    }
}
