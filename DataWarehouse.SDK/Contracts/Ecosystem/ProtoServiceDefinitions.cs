using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Text;
using DataWarehouse.SDK.Contracts.Interface;
using static DataWarehouse.SDK.Contracts.Ecosystem.ProtoSerializationHelpers;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// ═══════════════════════════════════════════════════════════════════════════════
// Proto Column Type Enum (mirrors ColumnDataType from ColumnarBatch)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Protobuf column data types mapping 1:1 to <c>ColumnDataType</c> from
/// <c>DataWarehouse.SDK.Contracts.Query.ColumnarBatch</c>.
/// Values match the proto enum <c>ColumnType</c> in DataWarehouseService.proto.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public enum ProtoColumnType
{
    /// <summary>32-bit signed integer.</summary>
    Int32 = 0,
    /// <summary>64-bit signed integer.</summary>
    Int64 = 1,
    /// <summary>64-bit floating point.</summary>
    Float64 = 2,
    /// <summary>UTF-8 string.</summary>
    String = 3,
    /// <summary>Boolean.</summary>
    Bool = 4,
    /// <summary>Binary data.</summary>
    Binary = 5,
    /// <summary>Decimal number.</summary>
    Decimal = 6,
    /// <summary>Date and time.</summary>
    DateTime = 7,
    /// <summary>Null/absent value.</summary>
    Null = 8
}

// ═══════════════════════════════════════════════════════════════════════════════
// Proto Mirror Types — C# records mirroring .proto message definitions
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// C# mirror of proto <c>StoreRequest</c>. Enables runtime gRPC serving
/// without protoc-generated code via the <see cref="GrpcServiceDescriptor"/> system.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoStoreRequest
{
    /// <summary>Object key (field 1).</summary>
    public required string Key { get; init; }
    /// <summary>Object data payload (field 2).</summary>
    public required byte[] Data { get; init; }
    /// <summary>Content type MIME (field 3).</summary>
    public string ContentType { get; init; } = "application/octet-stream";
    /// <summary>User-defined metadata (field 4).</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Expected version for CAS, -1 = unconditional (field 5).</summary>
    public long ExpectedVersion { get; init; } = -1;
    /// <summary>TTL in seconds, 0 = no expiry (field 6).</summary>
    public long TtlSeconds { get; init; }

    /// <summary>Serializes to length-prefixed binary matching proto wire format field order.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(256);
        WriteString(buffer, Key);
        WriteBytes(buffer, Data);
        WriteString(buffer, ContentType);
        WriteInt32(buffer, Metadata.Count);
        foreach (var kv in Metadata)
        {
            WriteString(buffer, kv.Key);
            WriteString(buffer, kv.Value);
        }
        WriteInt64(buffer, ExpectedVersion);
        WriteInt64(buffer, TtlSeconds);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary format.</summary>
    public static ProtoStoreRequest FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var key = ReadString(data, ref offset);
        var payload = ReadBytes(data, ref offset);
        var contentType = ReadString(data, ref offset);
        var metaCount = ReadCount(data, ref offset);
        var metadata = new Dictionary<string, string>(metaCount);
        for (int i = 0; i < metaCount; i++)
        {
            var k = ReadString(data, ref offset);
            var v = ReadString(data, ref offset);
            metadata[k] = v;
        }
        var expectedVersion = ReadInt64(data, ref offset);
        var ttlSeconds = ReadInt64(data, ref offset);
        return new ProtoStoreRequest
        {
            Key = key,
            Data = payload,
            ContentType = contentType,
            Metadata = metadata,
            ExpectedVersion = expectedVersion,
            TtlSeconds = ttlSeconds
        };
    }
}

/// <summary>
/// C# mirror of proto <c>StoreResponse</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoStoreResponse
{
    /// <summary>Assigned version (field 1).</summary>
    public long Version { get; init; }
    /// <summary>SHA-256 checksum hex (field 2).</summary>
    public required string Checksum { get; init; }
    /// <summary>Store timestamp UTC ticks (field 3).</summary>
    public long StoredAtTicks { get; init; }
    /// <summary>Stored data size in bytes (field 4).</summary>
    public long SizeBytes { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(64);
        WriteInt64(buffer, Version);
        WriteString(buffer, Checksum);
        WriteInt64(buffer, StoredAtTicks);
        WriteInt64(buffer, SizeBytes);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoStoreResponse FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        return new ProtoStoreResponse
        {
            Version = ReadInt64(data, ref offset),
            Checksum = ReadString(data, ref offset),
            StoredAtTicks = ReadInt64(data, ref offset),
            SizeBytes = ReadInt64(data, ref offset)
        };
    }
}

/// <summary>
/// C# mirror of proto <c>RetrieveRequest</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoRetrieveRequest
{
    /// <summary>Object key (field 1).</summary>
    public required string Key { get; init; }
    /// <summary>Version to retrieve, 0 = latest (field 2).</summary>
    public long Version { get; init; }
    /// <summary>Include metadata in response (field 3).</summary>
    public bool IncludeMetadata { get; init; } = true;
    /// <summary>Range start inclusive (field 4).</summary>
    public long RangeStart { get; init; }
    /// <summary>Range end exclusive, 0 = end (field 5).</summary>
    public long RangeEnd { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(64);
        WriteString(buffer, Key);
        WriteInt64(buffer, Version);
        WriteBool(buffer, IncludeMetadata);
        WriteInt64(buffer, RangeStart);
        WriteInt64(buffer, RangeEnd);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoRetrieveRequest FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        return new ProtoRetrieveRequest
        {
            Key = ReadString(data, ref offset),
            Version = ReadInt64(data, ref offset),
            IncludeMetadata = ReadBool(data, ref offset),
            RangeStart = ReadInt64(data, ref offset),
            RangeEnd = ReadInt64(data, ref offset)
        };
    }
}

/// <summary>
/// C# mirror of proto <c>RetrieveChunk</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoRetrieveChunk
{
    /// <summary>Chunk data (field 1).</summary>
    public required byte[] Data { get; init; }
    /// <summary>Chunk index 0-based (field 2).</summary>
    public int ChunkIndex { get; init; }
    /// <summary>Total chunks, 0 if unknown (field 3).</summary>
    public int TotalChunks { get; init; }
    /// <summary>Object metadata, first chunk only (field 4).</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Object version (field 5).</summary>
    public long Version { get; init; }
    /// <summary>Content type (field 6).</summary>
    public string? ContentType { get; init; }
    /// <summary>Total object size bytes (field 7).</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(256);
        WriteBytes(buffer, Data);
        WriteInt32(buffer, ChunkIndex);
        WriteInt32(buffer, TotalChunks);
        WriteInt32(buffer, Metadata.Count);
        foreach (var kv in Metadata)
        {
            WriteString(buffer, kv.Key);
            WriteString(buffer, kv.Value);
        }
        WriteInt64(buffer, Version);
        WriteString(buffer, ContentType ?? string.Empty);
        WriteInt64(buffer, TotalSizeBytes);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoRetrieveChunk FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var chunkData = ReadBytes(data, ref offset);
        var chunkIndex = ReadInt32(data, ref offset);
        var totalChunks = ReadInt32(data, ref offset);
        var metaCount = ReadCount(data, ref offset);
        var metadata = new Dictionary<string, string>(metaCount);
        for (int i = 0; i < metaCount; i++)
            metadata[ReadString(data, ref offset)] = ReadString(data, ref offset);
        return new ProtoRetrieveChunk
        {
            Data = chunkData,
            ChunkIndex = chunkIndex,
            TotalChunks = totalChunks,
            Metadata = metadata,
            Version = ReadInt64(data, ref offset),
            ContentType = ReadString(data, ref offset),
            TotalSizeBytes = ReadInt64(data, ref offset)
        };
    }
}

/// <summary>
/// C# mirror of proto <c>QueryRequest</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoQueryRequest
{
    /// <summary>SQL-like query string (field 1).</summary>
    public required string Query { get; init; }
    /// <summary>Query parameters (field 2).</summary>
    public Dictionary<string, string> Parameters { get; init; } = new();
    /// <summary>Max result rows, 0 = unlimited (field 3).</summary>
    public int MaxResults { get; init; }
    /// <summary>Timeout in milliseconds, 0 = default (field 4).</summary>
    public int TimeoutMs { get; init; }
    /// <summary>Batch size for streaming (field 5).</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(128);
        WriteString(buffer, Query);
        WriteInt32(buffer, Parameters.Count);
        foreach (var kv in Parameters)
        {
            WriteString(buffer, kv.Key);
            WriteString(buffer, kv.Value);
        }
        WriteInt32(buffer, MaxResults);
        WriteInt32(buffer, TimeoutMs);
        WriteInt32(buffer, BatchSize);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoQueryRequest FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var query = ReadString(data, ref offset);
        var paramCount = ReadInt32(data, ref offset);
        var parameters = new Dictionary<string, string>(paramCount);
        for (int i = 0; i < paramCount; i++)
            parameters[ReadString(data, ref offset)] = ReadString(data, ref offset);
        return new ProtoQueryRequest
        {
            Query = query,
            Parameters = parameters,
            MaxResults = ReadInt32(data, ref offset),
            TimeoutMs = ReadInt32(data, ref offset),
            BatchSize = ReadInt32(data, ref offset)
        };
    }
}

/// <summary>
/// C# mirror of proto <c>QueryResultBatch</c> with column descriptors.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoQueryResultBatch
{
    /// <summary>Column descriptors, first batch only (field 1).</summary>
    public IReadOnlyList<ProtoColumnDescriptor> Columns { get; init; } = Array.Empty<ProtoColumnDescriptor>();
    /// <summary>Row data as serialized columnar arrays (field 2).</summary>
    public IReadOnlyList<ProtoRowData> Rows { get; init; } = Array.Empty<ProtoRowData>();
    /// <summary>Batch sequence number (field 3).</summary>
    public int BatchIndex { get; init; }
    /// <summary>Whether this is the last batch (field 4).</summary>
    public bool IsLast { get; init; }
    /// <summary>Total row count, 0 if unknown (field 5).</summary>
    public long TotalRows { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(512);
        WriteInt32(buffer, Columns.Count);
        foreach (var col in Columns)
        {
            WriteString(buffer, col.Name);
            WriteInt32(buffer, (int)col.DataType);
            WriteBool(buffer, col.Nullable);
        }
        WriteInt32(buffer, Rows.Count);
        foreach (var row in Rows)
        {
            WriteInt32(buffer, row.Values.Count);
            foreach (var v in row.Values)
                WriteBytes(buffer, v);
            WriteBytes(buffer, row.NullBitmap);
        }
        WriteInt32(buffer, BatchIndex);
        WriteBool(buffer, IsLast);
        WriteInt64(buffer, TotalRows);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoQueryResultBatch FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var colCount = ReadCount(data, ref offset);
        var columns = new ProtoColumnDescriptor[colCount];
        for (int i = 0; i < colCount; i++)
            columns[i] = new ProtoColumnDescriptor
            {
                Name = ReadString(data, ref offset),
                DataType = (ProtoColumnType)ReadInt32(data, ref offset),
                Nullable = ReadBool(data, ref offset)
            };
        var rowCount = ReadCount(data, ref offset);
        var rows = new ProtoRowData[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            var valCount = ReadCount(data, ref offset);
            var values = new byte[valCount][];
            for (int j = 0; j < valCount; j++)
                values[j] = ReadBytes(data, ref offset);
            rows[i] = new ProtoRowData { Values = values, NullBitmap = ReadBytes(data, ref offset) };
        }
        return new ProtoQueryResultBatch
        {
            Columns = columns,
            Rows = rows,
            BatchIndex = ReadInt32(data, ref offset),
            IsLast = ReadBool(data, ref offset),
            TotalRows = ReadInt64(data, ref offset)
        };
    }
}

/// <summary>Column descriptor within a query result batch.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoColumnDescriptor
{
    /// <summary>Column name.</summary>
    public required string Name { get; init; }
    /// <summary>Column data type.</summary>
    public ProtoColumnType DataType { get; init; }
    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; init; }
}

/// <summary>A single row of data in a query result batch.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoRowData
{
    /// <summary>Serialized column values.</summary>
    public IReadOnlyList<byte[]> Values { get; init; } = Array.Empty<byte[]>();
    /// <summary>Null bitmap (one bit per column, LSB first).</summary>
    public byte[] NullBitmap { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// C# mirror of proto <c>SearchRequest</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoSearchRequest
{
    /// <summary>Full-text search query (field 1).</summary>
    public required string Query { get; init; }
    /// <summary>Max results (field 2).</summary>
    public int MaxResults { get; init; } = 100;
    /// <summary>Offset for pagination (field 3).</summary>
    public int Offset { get; init; }
    /// <summary>Optional filter expression (field 4).</summary>
    public string? Filter { get; init; }
    /// <summary>Sort order (field 5).</summary>
    public string SortBy { get; init; } = "relevance";

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(64);
        WriteString(buffer, Query);
        WriteInt32(buffer, MaxResults);
        WriteInt32(buffer, Offset);
        WriteString(buffer, Filter ?? string.Empty);
        WriteString(buffer, SortBy);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoSearchRequest FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        return new ProtoSearchRequest
        {
            Query = ReadString(data, ref offset),
            MaxResults = ReadInt32(data, ref offset),
            Offset = ReadInt32(data, ref offset),
            Filter = ReadString(data, ref offset),
            SortBy = ReadString(data, ref offset)
        };
    }
}

/// <summary>
/// C# mirror of proto <c>SearchResult</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoSearchResult
{
    /// <summary>Object key (field 1).</summary>
    public required string Key { get; init; }
    /// <summary>Relevance score 0.0-1.0 (field 2).</summary>
    public double Score { get; init; }
    /// <summary>Object metadata (field 3).</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    /// <summary>Content snippet with highlighting (field 4).</summary>
    public string? Snippet { get; init; }
    /// <summary>Object size bytes (field 5).</summary>
    public long SizeBytes { get; init; }
    /// <summary>Last modified UTC ticks (field 6).</summary>
    public long LastModifiedTicks { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(128);
        WriteString(buffer, Key);
        WriteInt64(buffer, BitConverter.DoubleToInt64Bits(Score));
        WriteInt32(buffer, Metadata.Count);
        foreach (var kv in Metadata)
        {
            WriteString(buffer, kv.Key);
            WriteString(buffer, kv.Value);
        }
        WriteString(buffer, Snippet ?? string.Empty);
        WriteInt64(buffer, SizeBytes);
        WriteInt64(buffer, LastModifiedTicks);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoSearchResult FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var key = ReadString(data, ref offset);
        var score = BitConverter.Int64BitsToDouble(ReadInt64(data, ref offset));
        var metaCount = ReadCount(data, ref offset);
        var metadata = new Dictionary<string, string>(metaCount);
        for (int i = 0; i < metaCount; i++)
            metadata[ReadString(data, ref offset)] = ReadString(data, ref offset);
        return new ProtoSearchResult
        {
            Key = key,
            Score = score,
            Metadata = metadata,
            Snippet = ReadString(data, ref offset),
            SizeBytes = ReadInt64(data, ref offset),
            LastModifiedTicks = ReadInt64(data, ref offset)
        };
    }
}

/// <summary>
/// C# mirror of proto <c>HealthResponse</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoHealthResponse
{
    /// <summary>Overall health status (field 1). 0=Healthy, 1=Degraded, 2=Unhealthy.</summary>
    public int Status { get; init; }
    /// <summary>Instance uptime seconds (field 2).</summary>
    public long UptimeSeconds { get; init; }
    /// <summary>DataWarehouse version string (field 3).</summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>Per-component health (field 4).</summary>
    public Dictionary<string, ProtoComponentHealth> Components { get; init; } = new();
    /// <summary>Check timestamp UTC ticks (field 5).</summary>
    public long CheckedAtTicks { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(128);
        WriteInt32(buffer, Status);
        WriteInt64(buffer, UptimeSeconds);
        WriteString(buffer, Version);
        WriteInt32(buffer, Components.Count);
        foreach (var kv in Components)
        {
            WriteString(buffer, kv.Key);
            WriteString(buffer, kv.Value.Name);
            WriteInt32(buffer, kv.Value.Status);
            WriteString(buffer, kv.Value.Message);
        }
        WriteInt64(buffer, CheckedAtTicks);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoHealthResponse FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var status = ReadInt32(data, ref offset);
        var uptime = ReadInt64(data, ref offset);
        var version = ReadString(data, ref offset);
        var compCount = ReadCount(data, ref offset);
        var components = new Dictionary<string, ProtoComponentHealth>(compCount);
        for (int i = 0; i < compCount; i++)
        {
            var compKey = ReadString(data, ref offset);
            components[compKey] = new ProtoComponentHealth
            {
                Name = ReadString(data, ref offset),
                Status = ReadInt32(data, ref offset),
                Message = ReadString(data, ref offset)
            };
        }
        return new ProtoHealthResponse
        {
            Status = status,
            UptimeSeconds = uptime,
            Version = version,
            Components = components,
            CheckedAtTicks = ReadInt64(data, ref offset)
        };
    }
}

/// <summary>Per-component health information.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoComponentHealth
{
    /// <summary>Component name.</summary>
    public required string Name { get; init; }
    /// <summary>Health status (0=Healthy, 1=Degraded, 2=Unhealthy).</summary>
    public int Status { get; init; }
    /// <summary>Status message.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// C# mirror of proto <c>CapabilitiesResponse</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoCapabilitiesResponse
{
    /// <summary>Available capabilities (field 1).</summary>
    public IReadOnlyList<ProtoCapability> Capabilities { get; init; } = Array.Empty<ProtoCapability>();
    /// <summary>Total plugin count (field 2).</summary>
    public int PluginCount { get; init; }
    /// <summary>Total strategy count (field 3).</summary>
    public int StrategyCount { get; init; }

    /// <summary>Serializes to length-prefixed binary.</summary>
    public byte[] ToBytes()
    {
        var buffer = new List<byte>(256);
        WriteInt32(buffer, Capabilities.Count);
        foreach (var cap in Capabilities)
        {
            WriteString(buffer, cap.Id);
            WriteString(buffer, cap.DisplayName);
            WriteString(buffer, cap.Description);
            WriteString(buffer, cap.Category);
            WriteBool(buffer, cap.IsAvailable);
            WriteInt32(buffer, cap.Tags.Count);
            foreach (var tag in cap.Tags)
                WriteString(buffer, tag);
        }
        WriteInt32(buffer, PluginCount);
        WriteInt32(buffer, StrategyCount);
        return buffer.ToArray();
    }

    /// <summary>Deserializes from length-prefixed binary.</summary>
    public static ProtoCapabilitiesResponse FromBytes(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var capCount = ReadCount(data, ref offset);
        var capabilities = new ProtoCapability[capCount];
        for (int i = 0; i < capCount; i++)
        {
            var id = ReadString(data, ref offset);
            var displayName = ReadString(data, ref offset);
            var description = ReadString(data, ref offset);
            var category = ReadString(data, ref offset);
            var isAvailable = ReadBool(data, ref offset);
            var tagCount = ReadInt32(data, ref offset);
            var tags = new string[tagCount];
            for (int j = 0; j < tagCount; j++)
                tags[j] = ReadString(data, ref offset);
            capabilities[i] = new ProtoCapability
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                Category = category,
                IsAvailable = isAvailable,
                Tags = tags
            };
        }
        return new ProtoCapabilitiesResponse
        {
            Capabilities = capabilities,
            PluginCount = ReadInt32(data, ref offset),
            StrategyCount = ReadInt32(data, ref offset)
        };
    }
}

/// <summary>A single capability descriptor.</summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public sealed record ProtoCapability
{
    /// <summary>Capability ID.</summary>
    public required string Id { get; init; }
    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }
    /// <summary>Description.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Category.</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>Whether available.</summary>
    public bool IsAvailable { get; init; }
    /// <summary>Discovery tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// DataWarehouseGrpcServices — Service Descriptor Registry
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Provides <see cref="GrpcServiceDescriptor"/> instances for all 6 canonical
/// DataWarehouse proto services. These descriptors integrate with the existing
/// <see cref="GrpcServiceGenerator"/> system for runtime gRPC serving.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Ecosystem proto definitions (ECOS-12)")]
public static class DataWarehouseGrpcServices
{
    private const string Package = "datawarehouse.v1";

    /// <summary>
    /// Returns service descriptors for all 6 canonical DataWarehouse gRPC services.
    /// </summary>
    public static IReadOnlyList<GrpcServiceDescriptor> GetServiceDescriptors()
    {
        return new[]
        {
            CreateStorageService(),
            CreateQueryService(),
            CreateTagService(),
            CreateSearchService(),
            CreateStreamService(),
            CreateAdminService()
        };
    }

    private static GrpcServiceDescriptor CreateStorageService() => new()
    {
        FullServiceName = $"{Package}.StorageService",
        ServiceName = "StorageService",
        PluginId = "core.storage",
        Methods = new GrpcMethodDescriptor[]
        {
            new() { Name = "Store", InputType = "StoreRequest", OutputType = "StoreResponse", CapabilityName = "storage.store" },
            new() { Name = "Retrieve", InputType = "RetrieveRequest", OutputType = "RetrieveChunk", ServerStreaming = true, CapabilityName = "storage.retrieve" },
            new() { Name = "Delete", InputType = "DeleteRequest", OutputType = "DeleteResponse", CapabilityName = "storage.delete" },
            new() { Name = "Exists", InputType = "ExistsRequest", OutputType = "ExistsResponse", CapabilityName = "storage.exists" },
            new() { Name = "GetMetadata", InputType = "GetMetadataRequest", OutputType = "MetadataResponse", CapabilityName = "storage.get_metadata" },
            new() { Name = "List", InputType = "ListRequest", OutputType = "ListEntry", ServerStreaming = true, CapabilityName = "storage.list" }
        }
    };

    private static GrpcServiceDescriptor CreateQueryService() => new()
    {
        FullServiceName = $"{Package}.QueryService",
        ServiceName = "QueryService",
        PluginId = "core.query",
        Methods = new GrpcMethodDescriptor[]
        {
            new() { Name = "Execute", InputType = "QueryRequest", OutputType = "QueryResultBatch", ServerStreaming = true, CapabilityName = "query.execute" },
            new() { Name = "Prepare", InputType = "PrepareRequest", OutputType = "PrepareResponse", CapabilityName = "query.prepare" },
            new() { Name = "ExecutePrepared", InputType = "ExecutePreparedRequest", OutputType = "QueryResultBatch", ServerStreaming = true, CapabilityName = "query.execute_prepared" }
        }
    };

    private static GrpcServiceDescriptor CreateTagService() => new()
    {
        FullServiceName = $"{Package}.TagService",
        ServiceName = "TagService",
        PluginId = "core.tag",
        Methods = new GrpcMethodDescriptor[]
        {
            new() { Name = "SetTags", InputType = "SetTagsRequest", OutputType = "TagResponse", CapabilityName = "tag.set" },
            new() { Name = "GetTags", InputType = "GetTagsRequest", OutputType = "TagResponse", CapabilityName = "tag.get" },
            new() { Name = "RemoveTags", InputType = "RemoveTagsRequest", OutputType = "TagResponse", CapabilityName = "tag.remove" }
        }
    };

    private static GrpcServiceDescriptor CreateSearchService() => new()
    {
        FullServiceName = $"{Package}.SearchService",
        ServiceName = "SearchService",
        PluginId = "core.search",
        Methods = new GrpcMethodDescriptor[]
        {
            new() { Name = "Search", InputType = "SearchRequest", OutputType = "SearchResult", ServerStreaming = true, CapabilityName = "search.fulltext" },
            new() { Name = "SearchByTags", InputType = "TagSearchRequest", OutputType = "SearchResult", ServerStreaming = true, CapabilityName = "search.by_tags" }
        }
    };

    private static GrpcServiceDescriptor CreateStreamService() => new()
    {
        FullServiceName = $"{Package}.StreamService",
        ServiceName = "StreamService",
        PluginId = "core.stream",
        Methods = new GrpcMethodDescriptor[]
        {
            new() { Name = "StreamStore", InputType = "StoreChunk", OutputType = "StoreResponse", ClientStreaming = true, CapabilityName = "stream.store" },
            new() { Name = "StreamRetrieve", InputType = "RetrieveRequest", OutputType = "RetrieveChunk", ServerStreaming = true, CapabilityName = "stream.retrieve" },
            new() { Name = "Subscribe", InputType = "SubscribeRequest", OutputType = "Event", ServerStreaming = true, CapabilityName = "stream.subscribe" }
        }
    };

    private static GrpcServiceDescriptor CreateAdminService() => new()
    {
        FullServiceName = $"{Package}.AdminService",
        ServiceName = "AdminService",
        PluginId = "core.admin",
        Methods = new GrpcMethodDescriptor[]
        {
            new() { Name = "GetHealth", InputType = "HealthRequest", OutputType = "HealthResponse", CapabilityName = "admin.health" },
            new() { Name = "GetCapabilities", InputType = "CapabilitiesRequest", OutputType = "CapabilitiesResponse", CapabilityName = "admin.capabilities" },
            new() { Name = "ManagePlugin", InputType = "PluginRequest", OutputType = "PluginResponse", CapabilityName = "admin.manage_plugin" },
            new() { Name = "GetMetrics", InputType = "MetricsRequest", OutputType = "MetricsResponse", CapabilityName = "admin.metrics" }
        }
    };
}

// ═══════════════════════════════════════════════════════════════════════════════
// Binary Serialization Helpers (length-prefixed, matching proto wire order)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared binary serialization helpers for proto mirror types.
/// Uses length-prefixed fields matching proto wire format field numbers.
/// </summary>
internal static class ProtoSerializationHelpers
{
    internal static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    internal static void WriteBytes(List<byte> buffer, byte[] value)
    {
        WriteInt32(buffer, value.Length);
        buffer.AddRange(value);
    }

    internal static void WriteInt32(List<byte> buffer, int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
        buffer.Add(tmp[0]);
        buffer.Add(tmp[1]);
        buffer.Add(tmp[2]);
        buffer.Add(tmp[3]);
    }

    internal static void WriteInt64(List<byte> buffer, long value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
        for (int i = 0; i < 8; i++)
            buffer.Add(tmp[i]);
    }

    internal static void WriteBool(List<byte> buffer, bool value)
    {
        buffer.Add(value ? (byte)1 : (byte)0);
    }

    /// <summary>Maximum element count allowed in deserialized collections to prevent unbounded allocation.</summary>
    private const int MaxCollectionCount = 10_000;

    internal static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = ReadInt32(data, ref offset);
        if (length < 0 || offset + length > data.Length)
            throw new InvalidOperationException($"Invalid string length {length} at offset {offset - 4}.");
        var result = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return result;
    }

    internal static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = ReadInt32(data, ref offset);
        if (length < 0 || offset + length > data.Length)
            throw new InvalidOperationException($"Invalid byte array length {length} at offset {offset - 4}.");
        var result = data.Slice(offset, length).ToArray();
        offset += length;
        return result;
    }

    /// <summary>Reads a count value that will be used to allocate a collection, with bounds checking.</summary>
    internal static int ReadCount(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = ReadInt32(data, ref offset);
        if (value < 0 || value > MaxCollectionCount)
            throw new InvalidOperationException($"Collection count {value} exceeds maximum allowed ({MaxCollectionCount}).");
        return value;
    }

    internal static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 4 > data.Length)
            throw new InvalidOperationException($"Insufficient data to read Int32 at offset {offset}.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return value;
    }

    internal static long ReadInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
        offset += 8;
        return value;
    }

    internal static bool ReadBool(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = data[offset] != 0;
        offset += 1;
        return value;
    }
}
