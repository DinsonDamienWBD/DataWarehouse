using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Hardware.Interop;

#region gRPC Service Contracts

/// <summary>
/// Status codes for gRPC-style service responses.
/// </summary>
public enum ServiceStatusCode
{
    /// <summary>Operation completed successfully.</summary>
    Ok = 0,
    /// <summary>Operation was cancelled.</summary>
    Cancelled = 1,
    /// <summary>Unknown error.</summary>
    Unknown = 2,
    /// <summary>Invalid argument.</summary>
    InvalidArgument = 3,
    /// <summary>Deadline exceeded.</summary>
    DeadlineExceeded = 4,
    /// <summary>Resource not found.</summary>
    NotFound = 5,
    /// <summary>Resource already exists.</summary>
    AlreadyExists = 6,
    /// <summary>Permission denied.</summary>
    PermissionDenied = 7,
    /// <summary>Resource exhausted.</summary>
    ResourceExhausted = 8,
    /// <summary>Precondition failed.</summary>
    FailedPrecondition = 9,
    /// <summary>Operation aborted.</summary>
    Aborted = 10,
    /// <summary>Out of range.</summary>
    OutOfRange = 11,
    /// <summary>Unimplemented.</summary>
    Unimplemented = 12,
    /// <summary>Internal error.</summary>
    Internal = 13,
    /// <summary>Service unavailable.</summary>
    Unavailable = 14,
    /// <summary>Data loss.</summary>
    DataLoss = 15,
    /// <summary>Unauthenticated.</summary>
    Unauthenticated = 16
}

/// <summary>
/// Service response wrapper with status information.
/// </summary>
/// <typeparam name="T">The response payload type.</typeparam>
public sealed class ServiceResponse<T>
{
    /// <summary>Status code.</summary>
    public required ServiceStatusCode Status { get; init; }
    /// <summary>Response payload (null on error).</summary>
    public T? Payload { get; init; }
    /// <summary>Error message (null on success).</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Error details for debugging.</summary>
    public Dictionary<string, string>? ErrorDetails { get; init; }
    /// <summary>Response metadata (trailing headers).</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>Creates a success response.</summary>
    public static ServiceResponse<T> Success(T payload, Dictionary<string, string>? metadata = null)
        => new() { Status = ServiceStatusCode.Ok, Payload = payload, Metadata = metadata ?? new() };

    /// <summary>Creates an error response.</summary>
    public static ServiceResponse<T> Error(ServiceStatusCode status, string message, Dictionary<string, string>? details = null)
        => new() { Status = status, ErrorMessage = message, ErrorDetails = details };
}

/// <summary>
/// Request metadata (initial headers).
/// </summary>
public sealed class ServiceRequestMetadata
{
    /// <summary>Authorization token.</summary>
    public string? Authorization { get; init; }
    /// <summary>Request timeout.</summary>
    public TimeSpan? Deadline { get; init; }
    /// <summary>Custom headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();
    /// <summary>Trace context for distributed tracing.</summary>
    public string? TraceParent { get; init; }
    /// <summary>Client identity for audit.</summary>
    public string? ClientId { get; init; }
}

/// <summary>
/// Core storage gRPC service contract for Read/Write/Delete/List/Query operations.
/// Proto3-compatible service definition with streaming support.
/// </summary>
public interface IStorageServiceContract
{
    /// <summary>
    /// Reads an object by URI. Unary RPC.
    /// Proto: rpc Read(ReadRequest) returns (ReadResponse);
    /// </summary>
    Task<ServiceResponse<ReadObjectResponse>> ReadAsync(
        ReadObjectRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Writes an object. Unary RPC.
    /// Proto: rpc Write(WriteRequest) returns (WriteResponse);
    /// </summary>
    Task<ServiceResponse<WriteObjectResponse>> WriteAsync(
        WriteObjectRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes an object by URI. Unary RPC.
    /// Proto: rpc Delete(DeleteRequest) returns (DeleteResponse);
    /// </summary>
    Task<ServiceResponse<DeleteObjectResponse>> DeleteAsync(
        DeleteObjectRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Lists objects matching a prefix. Server-streaming RPC.
    /// Proto: rpc List(ListRequest) returns (stream ListEntry);
    /// </summary>
    IAsyncEnumerable<ListObjectEntry> ListAsync(
        ListObjectsRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Queries objects with filter expressions. Server-streaming RPC.
    /// Proto: rpc Query(QueryRequest) returns (stream QueryResult);
    /// </summary>
    IAsyncEnumerable<QueryResultEntry> QueryAsync(
        QueryObjectsRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Bulk write via client-streaming RPC.
    /// Proto: rpc BulkWrite(stream WriteRequest) returns (BulkWriteResponse);
    /// </summary>
    Task<ServiceResponse<BulkWriteResponse>> BulkWriteAsync(
        IAsyncEnumerable<WriteObjectRequest> requests, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Bidirectional streaming for real-time sync.
    /// Proto: rpc StreamSync(stream SyncMessage) returns (stream SyncMessage);
    /// </summary>
    IAsyncEnumerable<SyncMessage> StreamSyncAsync(
        IAsyncEnumerable<SyncMessage> requests, ServiceRequestMetadata? metadata = null, CancellationToken ct = default);
}

#region Request/Response Messages (Proto3-compatible)

/// <summary>Read request message.</summary>
public sealed class ReadObjectRequest
{
    /// <summary>Object URI (e.g., "dw://bucket/key").</summary>
    public required string Uri { get; init; }
    /// <summary>Optional version to read. 0 = latest.</summary>
    public long Version { get; init; }
    /// <summary>Whether to include object metadata.</summary>
    public bool IncludeMetadata { get; init; } = true;
    /// <summary>Optional byte range start (inclusive).</summary>
    public long? RangeStart { get; init; }
    /// <summary>Optional byte range end (exclusive).</summary>
    public long? RangeEnd { get; init; }
}

/// <summary>Read response message.</summary>
public sealed class ReadObjectResponse
{
    /// <summary>Object data.</summary>
    public required byte[] Data { get; init; }
    /// <summary>Object version.</summary>
    public long Version { get; init; }
    /// <summary>Object metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Content type (MIME).</summary>
    public string? ContentType { get; init; }
    /// <summary>Object size in bytes.</summary>
    public long SizeBytes { get; init; }
    /// <summary>SHA-256 checksum.</summary>
    public string? Checksum { get; init; }
}

/// <summary>Write request message.</summary>
public sealed class WriteObjectRequest
{
    /// <summary>Object URI.</summary>
    public required string Uri { get; init; }
    /// <summary>Object data.</summary>
    public required byte[] Data { get; init; }
    /// <summary>Content type.</summary>
    public string ContentType { get; init; } = "application/octet-stream";
    /// <summary>Object metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Expected version for CAS (Compare-And-Swap). -1 = unconditional.</summary>
    public long ExpectedVersion { get; init; } = -1;
    /// <summary>TTL in seconds (0 = no expiry).</summary>
    public long TtlSeconds { get; init; }
}

/// <summary>Write response message.</summary>
public sealed class WriteObjectResponse
{
    /// <summary>Assigned object version.</summary>
    public long Version { get; init; }
    /// <summary>SHA-256 checksum of written data.</summary>
    public required string Checksum { get; init; }
    /// <summary>Timestamp of write.</summary>
    public DateTimeOffset WrittenAt { get; init; }
}

/// <summary>Delete request message.</summary>
public sealed class DeleteObjectRequest
{
    /// <summary>Object URI.</summary>
    public required string Uri { get; init; }
    /// <summary>Expected version for CAS delete. -1 = unconditional.</summary>
    public long ExpectedVersion { get; init; } = -1;
    /// <summary>Whether to hard-delete (vs soft-delete/tombstone).</summary>
    public bool HardDelete { get; init; }
}

/// <summary>Delete response message.</summary>
public sealed class DeleteObjectResponse
{
    /// <summary>Whether the object was actually deleted (false = not found).</summary>
    public bool Deleted { get; init; }
    /// <summary>Tombstone version if soft-deleted.</summary>
    public long? TombstoneVersion { get; init; }
}

/// <summary>List request message.</summary>
public sealed class ListObjectsRequest
{
    /// <summary>URI prefix to list.</summary>
    public required string Prefix { get; init; }
    /// <summary>Delimiter for hierarchical listing (e.g., "/").</summary>
    public string? Delimiter { get; init; }
    /// <summary>Maximum entries to return (0 = unlimited).</summary>
    public int MaxEntries { get; init; }
    /// <summary>Continuation token for pagination.</summary>
    public string? ContinuationToken { get; init; }
}

/// <summary>List entry in response stream.</summary>
public sealed class ListObjectEntry
{
    /// <summary>Object URI.</summary>
    public required string Uri { get; init; }
    /// <summary>Object size in bytes.</summary>
    public long SizeBytes { get; init; }
    /// <summary>Last modified timestamp.</summary>
    public DateTimeOffset LastModified { get; init; }
    /// <summary>Whether this is a "common prefix" (directory-like entry).</summary>
    public bool IsPrefix { get; init; }
    /// <summary>Continuation token if more results available.</summary>
    public string? ContinuationToken { get; init; }
}

/// <summary>Query request message.</summary>
public sealed class QueryObjectsRequest
{
    /// <summary>Filter expression (SQL-like WHERE clause on metadata).</summary>
    public required string Filter { get; init; }
    /// <summary>Optional projection (metadata fields to return).</summary>
    public string[]? Projection { get; init; }
    /// <summary>Sort expression.</summary>
    public string? OrderBy { get; init; }
    /// <summary>Maximum results.</summary>
    public int MaxResults { get; init; }
    /// <summary>Offset for pagination.</summary>
    public int Offset { get; init; }
}

/// <summary>Query result entry.</summary>
public sealed class QueryResultEntry
{
    /// <summary>Object URI.</summary>
    public required string Uri { get; init; }
    /// <summary>Matched metadata fields.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Relevance score (0.0-1.0).</summary>
    public double Score { get; init; }
}

/// <summary>Bulk write response.</summary>
public sealed class BulkWriteResponse
{
    /// <summary>Total objects written.</summary>
    public int TotalWritten { get; init; }
    /// <summary>Total objects failed.</summary>
    public int TotalFailed { get; init; }
    /// <summary>Per-object error details.</summary>
    public Dictionary<string, string> Errors { get; init; } = new();
}

/// <summary>Sync message for bidirectional streaming.</summary>
public sealed class SyncMessage
{
    /// <summary>Message type.</summary>
    public required SyncMessageType Type { get; init; }
    /// <summary>Object URI affected.</summary>
    public string? Uri { get; init; }
    /// <summary>Object data (for Put operations).</summary>
    public byte[]? Data { get; init; }
    /// <summary>Vector clock for conflict resolution.</summary>
    public Dictionary<string, long> VectorClock { get; init; } = new();
    /// <summary>Sequence number.</summary>
    public long SequenceNumber { get; init; }
}

/// <summary>Sync message types.</summary>
public enum SyncMessageType
{
    /// <summary>Object created or updated.</summary>
    Put,
    /// <summary>Object deleted.</summary>
    Delete,
    /// <summary>Acknowledgment.</summary>
    Ack,
    /// <summary>Heartbeat/keepalive.</summary>
    Heartbeat,
    /// <summary>Request full sync.</summary>
    FullSyncRequest,
    /// <summary>Full sync complete.</summary>
    FullSyncComplete
}

#endregion

#region gRPC Server Implementation

/// <summary>
/// In-process gRPC service implementation that routes to the storage engine.
/// Provides the server-side implementation of IStorageServiceContract.
/// </summary>
public sealed class StorageGrpcService : IStorageServiceContract
{
    private readonly Func<string, byte[]?, Task<byte[]?>> _readHandler;
    private readonly Func<string, byte[], Dictionary<string, string>, Task<long>> _writeHandler;
    private readonly Func<string, Task<bool>> _deleteHandler;
    private readonly Func<string, Task<IEnumerable<(string Uri, long Size, DateTimeOffset Modified)>>> _listHandler;
    private readonly BoundedDictionary<string, long> _versionTracker = new BoundedDictionary<string, long>(1000);
    private long _nextVersion = 1;

    /// <summary>
    /// Creates a new gRPC storage service with pluggable storage handlers.
    /// </summary>
    public StorageGrpcService(
        Func<string, byte[]?, Task<byte[]?>> readHandler,
        Func<string, byte[], Dictionary<string, string>, Task<long>> writeHandler,
        Func<string, Task<bool>> deleteHandler,
        Func<string, Task<IEnumerable<(string Uri, long Size, DateTimeOffset Modified)>>> listHandler)
    {
        _readHandler = readHandler ?? throw new ArgumentNullException(nameof(readHandler));
        _writeHandler = writeHandler ?? throw new ArgumentNullException(nameof(writeHandler));
        _deleteHandler = deleteHandler ?? throw new ArgumentNullException(nameof(deleteHandler));
        _listHandler = listHandler ?? throw new ArgumentNullException(nameof(listHandler));
    }

    /// <inheritdoc/>
    public async Task<ServiceResponse<ReadObjectResponse>> ReadAsync(
        ReadObjectRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(request.Uri))
                return ServiceResponse<ReadObjectResponse>.Error(ServiceStatusCode.InvalidArgument, "URI is required");

            var data = await _readHandler(request.Uri, null);
            if (data == null)
                return ServiceResponse<ReadObjectResponse>.Error(ServiceStatusCode.NotFound, $"Object not found: {request.Uri}");

            byte[] resultData = data;
            if (request.RangeStart.HasValue || request.RangeEnd.HasValue)
            {
                var start = request.RangeStart ?? 0;
                var end = request.RangeEnd ?? data.Length;
                var length = (int)(end - start);
                resultData = new byte[length];
                Array.Copy(data, start, resultData, 0, length);
            }

            var version = _versionTracker.GetOrAdd(request.Uri, _ => Interlocked.Increment(ref _nextVersion));
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(resultData));

            return ServiceResponse<ReadObjectResponse>.Success(new ReadObjectResponse
            {
                Data = resultData,
                Version = version,
                ContentType = "application/octet-stream",
                SizeBytes = resultData.Length,
                Checksum = checksum
            });
        }
        catch (OperationCanceledException)
        {
            return ServiceResponse<ReadObjectResponse>.Error(ServiceStatusCode.Cancelled, "Operation cancelled");
        }
        catch (Exception ex)
        {
            return ServiceResponse<ReadObjectResponse>.Error(ServiceStatusCode.Internal, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<ServiceResponse<WriteObjectResponse>> WriteAsync(
        WriteObjectRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(request.Uri))
                return ServiceResponse<WriteObjectResponse>.Error(ServiceStatusCode.InvalidArgument, "URI is required");
            if (request.Data == null || request.Data.Length == 0)
                return ServiceResponse<WriteObjectResponse>.Error(ServiceStatusCode.InvalidArgument, "Data is required");

            // CAS check
            if (request.ExpectedVersion >= 0)
            {
                var currentVersion = _versionTracker.GetValueOrDefault(request.Uri, 0);
                if (currentVersion != request.ExpectedVersion)
                    return ServiceResponse<WriteObjectResponse>.Error(
                        ServiceStatusCode.FailedPrecondition,
                        $"Version mismatch: expected {request.ExpectedVersion}, current {currentVersion}");
            }

            var version = await _writeHandler(request.Uri, request.Data, request.Metadata);
            _versionTracker[request.Uri] = version;
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(request.Data));

            return ServiceResponse<WriteObjectResponse>.Success(new WriteObjectResponse
            {
                Version = version,
                Checksum = checksum,
                WrittenAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            return ServiceResponse<WriteObjectResponse>.Error(ServiceStatusCode.Cancelled, "Operation cancelled");
        }
        catch (Exception ex)
        {
            return ServiceResponse<WriteObjectResponse>.Error(ServiceStatusCode.Internal, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<ServiceResponse<DeleteObjectResponse>> DeleteAsync(
        DeleteObjectRequest request, ServiceRequestMetadata? metadata = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(request.Uri))
                return ServiceResponse<DeleteObjectResponse>.Error(ServiceStatusCode.InvalidArgument, "URI is required");

            var deleted = await _deleteHandler(request.Uri);
            _versionTracker.TryRemove(request.Uri, out _);

            return ServiceResponse<DeleteObjectResponse>.Success(new DeleteObjectResponse
            {
                Deleted = deleted,
                TombstoneVersion = deleted && !request.HardDelete
                    ? Interlocked.Increment(ref _nextVersion) : null
            });
        }
        catch (Exception ex)
        {
            return ServiceResponse<DeleteObjectResponse>.Error(ServiceStatusCode.Internal, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ListObjectEntry> ListAsync(
        ListObjectsRequest request, ServiceRequestMetadata? metadata = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var entries = await _listHandler(request.Prefix);
        var count = 0;
        foreach (var (uri, size, modified) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (request.MaxEntries > 0 && count >= request.MaxEntries) yield break;

            yield return new ListObjectEntry
            {
                Uri = uri,
                SizeBytes = size,
                LastModified = modified
            };
            count++;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Filter expression is evaluated as a URI prefix match when the filter is of the form
    /// <c>uri LIKE 'prefix%'</c>. Complex SQL-like filters are not yet supported (finding P2-380).
    /// </remarks>
    public async IAsyncEnumerable<QueryResultEntry> QueryAsync(
        QueryObjectsRequest request, ServiceRequestMetadata? metadata = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Extract URI prefix from simple "uri LIKE 'prefix%'" filter patterns (finding P2-380)
        string prefix = "";
        if (!string.IsNullOrEmpty(request.Filter))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                request.Filter,
                @"uri\s+LIKE\s+'([^%']+)%'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
                prefix = m.Groups[1].Value;
        }

        var entries = await _listHandler(prefix);
        var count = 0;
        foreach (var (uri, _, _) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (request.MaxResults > 0 && count >= request.MaxResults) yield break;
            if (count < request.Offset) { count++; continue; }

            yield return new QueryResultEntry { Uri = uri, Score = 1.0 };
            count++;
        }
    }

    /// <inheritdoc/>
    public async Task<ServiceResponse<BulkWriteResponse>> BulkWriteAsync(
        IAsyncEnumerable<WriteObjectRequest> requests, ServiceRequestMetadata? metadata = null, CancellationToken ct = default)
    {
        var written = 0;
        var failed = 0;
        var errors = new Dictionary<string, string>();

        await foreach (var request in requests.WithCancellation(ct))
        {
            var result = await WriteAsync(request, metadata, ct);
            if (result.Status == ServiceStatusCode.Ok) written++;
            else
            {
                failed++;
                errors[request.Uri] = result.ErrorMessage ?? "Unknown error";
            }
        }

        return ServiceResponse<BulkWriteResponse>.Success(new BulkWriteResponse
        {
            TotalWritten = written,
            TotalFailed = failed,
            Errors = errors
        });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SyncMessage> StreamSyncAsync(
        IAsyncEnumerable<SyncMessage> requests, ServiceRequestMetadata? metadata = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        long sequence = 0;
        await foreach (var msg in requests.WithCancellation(ct))
        {
            // Echo ACK for each received message
            yield return new SyncMessage
            {
                Type = SyncMessageType.Ack,
                Uri = msg.Uri,
                SequenceNumber = Interlocked.Increment(ref sequence),
                VectorClock = msg.VectorClock
            };
        }
    }
}

#endregion

#region Schema Evolution Support

/// <summary>
/// Proto3-compatible field descriptor for schema evolution.
/// Uses key-based fields (not index-based) for forward/backward compatibility.
/// </summary>
public sealed class FieldDescriptor
{
    /// <summary>Field number (proto3 tag).</summary>
    public required int FieldNumber { get; init; }
    /// <summary>Field name.</summary>
    public required string Name { get; init; }
    /// <summary>Wire type.</summary>
    public required WireType Type { get; init; }
    /// <summary>Whether this field is optional (proto3 optional keyword).</summary>
    public bool IsOptional { get; init; }
    /// <summary>Whether this field is repeated.</summary>
    public bool IsRepeated { get; init; }
    /// <summary>Default value for missing fields (schema evolution).</summary>
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Proto3 wire types.
/// </summary>
public enum WireType
{
    /// <summary>Varint (int32, int64, uint32, uint64, sint32, sint64, bool, enum).</summary>
    Varint = 0,
    /// <summary>64-bit (fixed64, sfixed64, double).</summary>
    Fixed64 = 1,
    /// <summary>Length-delimited (string, bytes, embedded messages, repeated fields).</summary>
    LengthDelimited = 2,
    /// <summary>32-bit (fixed32, sfixed32, float).</summary>
    Fixed32 = 5
}

/// <summary>
/// Service descriptor for gRPC service registration and reflection.
/// </summary>
public sealed class ServiceDescriptor
{
    /// <summary>Service name (e.g., "datawarehouse.storage.v1.StorageService").</summary>
    public required string FullName { get; init; }
    /// <summary>Package name (e.g., "datawarehouse.storage.v1").</summary>
    public required string Package { get; init; }
    /// <summary>Method descriptors.</summary>
    public IReadOnlyList<MethodDescriptor> Methods { get; init; } = Array.Empty<MethodDescriptor>();
}

/// <summary>
/// Method descriptor for gRPC method reflection.
/// </summary>
public sealed class MethodDescriptor
{
    /// <summary>Method name (e.g., "Read").</summary>
    public required string Name { get; init; }
    /// <summary>Full method name (e.g., "/datawarehouse.storage.v1.StorageService/Read").</summary>
    public required string FullName { get; init; }
    /// <summary>Whether client sends a stream.</summary>
    public bool IsClientStreaming { get; init; }
    /// <summary>Whether server returns a stream.</summary>
    public bool IsServerStreaming { get; init; }
    /// <summary>Input message type name.</summary>
    public required string InputType { get; init; }
    /// <summary>Output message type name.</summary>
    public required string OutputType { get; init; }
}

/// <summary>
/// Service descriptor for the core storage service.
/// Provides proto3 reflection metadata.
/// </summary>
public static class StorageServiceDescriptor
{
    /// <summary>Full service name.</summary>
    public const string ServiceName = "datawarehouse.storage.v1.StorageService";

    /// <summary>Package name.</summary>
    public const string PackageName = "datawarehouse.storage.v1";

    /// <summary>Gets the service descriptor.</summary>
    public static ServiceDescriptor GetDescriptor() => new()
    {
        FullName = ServiceName,
        Package = PackageName,
        Methods = new[]
        {
            new MethodDescriptor { Name = "Read", FullName = $"/{ServiceName}/Read", InputType = "ReadRequest", OutputType = "ReadResponse" },
            new MethodDescriptor { Name = "Write", FullName = $"/{ServiceName}/Write", InputType = "WriteRequest", OutputType = "WriteResponse" },
            new MethodDescriptor { Name = "Delete", FullName = $"/{ServiceName}/Delete", InputType = "DeleteRequest", OutputType = "DeleteResponse" },
            new MethodDescriptor { Name = "List", FullName = $"/{ServiceName}/List", InputType = "ListRequest", OutputType = "ListEntry", IsServerStreaming = true },
            new MethodDescriptor { Name = "Query", FullName = $"/{ServiceName}/Query", InputType = "QueryRequest", OutputType = "QueryResult", IsServerStreaming = true },
            new MethodDescriptor { Name = "BulkWrite", FullName = $"/{ServiceName}/BulkWrite", InputType = "WriteRequest", OutputType = "BulkWriteResponse", IsClientStreaming = true },
            new MethodDescriptor { Name = "StreamSync", FullName = $"/{ServiceName}/StreamSync", InputType = "SyncMessage", OutputType = "SyncMessage", IsClientStreaming = true, IsServerStreaming = true }
        }
    };
}

#endregion

#endregion
