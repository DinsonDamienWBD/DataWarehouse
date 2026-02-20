using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Content-aware chunking strategy that intelligently chunks based on content type.
/// Uses specialized chunkers for different file formats to maximize deduplication.
/// </summary>
/// <remarks>
/// Features:
/// - Content-type aware chunking
/// - Specialized chunkers for text, structured data, binary
/// - Semantic boundary detection for documents
/// - Format-aware parsing for archives and containers
/// - Adaptive chunk sizes based on content patterns
/// </remarks>
public sealed class ContentAwareChunkingStrategy : DeduplicationStrategyBase
{
    private readonly BoundedDictionary<string, byte[]> _chunkStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<string, ObjectContentMap> _objectMaps = new BoundedDictionary<string, ObjectContentMap>(1000);
    private readonly Dictionary<string, IContentChunker> _chunkers = new();
    private readonly DefaultContentChunker _defaultChunker;

    /// <summary>
    /// Initializes with default chunkers for common content types.
    /// </summary>
    public ContentAwareChunkingStrategy()
    {
        _defaultChunker = new DefaultContentChunker();

        // Register specialized chunkers
        _chunkers["text/plain"] = new TextContentChunker();
        _chunkers["text/html"] = new TextContentChunker();
        _chunkers["text/xml"] = new StructuredDataChunker();
        _chunkers["application/json"] = new StructuredDataChunker();
        _chunkers["application/xml"] = new StructuredDataChunker();
        _chunkers["text/csv"] = new TabularDataChunker();
        _chunkers["application/pdf"] = new BinaryFormatChunker();
        _chunkers["application/zip"] = new ArchiveChunker();
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.contentaware";

    /// <inheritdoc/>
    public override string DisplayName => "Content-Aware Chunking";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 3.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Intelligent content-aware chunking that uses specialized chunkers based on content type. " +
        "Detects semantic boundaries in documents, structure in data files, and optimal points in binary formats. " +
        "Provides superior deduplication for mixed-format storage environments.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "content-aware", "intelligent", "format-specific", "semantic"];

    /// <summary>
    /// Registers a custom chunker for a content type.
    /// </summary>
    /// <param name="contentType">MIME content type.</param>
    /// <param name="chunker">Chunker implementation.</param>
    public void RegisterChunker(string contentType, IContentChunker chunker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(chunker);
        _chunkers[contentType.ToLowerInvariant()] = chunker;
    }

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Read data
        using var memoryStream = new MemoryStream(65536);
        await data.CopyToAsync(memoryStream, ct);
        var dataBytes = memoryStream.ToArray();
        var totalSize = dataBytes.Length;

        if (totalSize == 0)
        {
            sw.Stop();
            return DeduplicationResult.Unique(Array.Empty<byte>(), 0, 0, 0, 0, sw.Elapsed);
        }

        // Select chunker based on content type
        var contentType = context.ContentType?.ToLowerInvariant() ?? DetectContentType(dataBytes);
        var chunker = GetChunker(contentType);

        // Perform content-aware chunking
        var chunks = chunker.Chunk(dataBytes, context);
        var chunkInfos = new List<ContentChunkInfo>();
        int duplicateChunks = 0;
        long storedSize = 0;

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var chunkHash = ComputeHash(chunk.Data);
            var hashString = HashToString(chunkHash);

            chunkInfos.Add(new ContentChunkInfo
            {
                Hash = hashString,
                Offset = chunk.Offset,
                Size = chunk.Size,
                ChunkType = chunk.ChunkType
            });

            if (ChunkIndex.TryGetValue(hashString, out var existing))
            {
                duplicateChunks++;
                Interlocked.Increment(ref existing.ReferenceCount);
            }
            else
            {
                _chunkStore[hashString] = chunk.Data;
                ChunkIndex[hashString] = new ChunkEntry
                {
                    StorageId = hashString,
                    Size = chunk.Size,
                    ReferenceCount = 1
                };
                storedSize += chunk.Size;
            }
        }

        // Store content map
        _objectMaps[context.ObjectId] = new ObjectContentMap
        {
            ObjectId = context.ObjectId,
            ContentType = contentType,
            Chunks = chunkInfos,
            TotalSize = totalSize,
            CreatedAt = DateTime.UtcNow
        };

        var fileHash = ComputeFileHash(chunkInfos.Select(c => c.Hash));
        sw.Stop();

        if (chunks.Count == duplicateChunks && chunks.Count > 0)
        {
            return DeduplicationResult.Duplicate(fileHash, FindDuplicateObject(chunkInfos), totalSize, sw.Elapsed);
        }

        return DeduplicationResult.Unique(fileHash, totalSize, storedSize, chunks.Count, duplicateChunks, sw.Elapsed);
    }

    private IContentChunker GetChunker(string contentType)
    {
        // Try exact match
        if (_chunkers.TryGetValue(contentType, out var chunker))
            return chunker;

        // Try category match (e.g., "text/*")
        var category = contentType.Split('/')[0] + "/*";
        if (_chunkers.TryGetValue(category, out chunker))
            return chunker;

        return _defaultChunker;
    }

    private static string DetectContentType(byte[] data)
    {
        if (data.Length < 4)
            return "application/octet-stream";

        // Check for common magic numbers
        if (data[0] == 0x50 && data[1] == 0x4B) // PK
            return "application/zip";
        if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46) // %PDF
            return "application/pdf";
        if (data[0] == 0x7B) // {
            return "application/json";
        if (data[0] == 0x3C) // <
        {
            var start = Encoding.UTF8.GetString(data, 0, Math.Min(100, data.Length));
            return start.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ? "text/html" : "text/xml";
        }

        // Check for text
        var isText = true;
        for (int i = 0; i < Math.Min(512, data.Length); i++)
        {
            if (data[i] < 0x09 || (data[i] > 0x0D && data[i] < 0x20 && data[i] != 0x1B))
            {
                isText = false;
                break;
            }
        }

        return isText ? "text/plain" : "application/octet-stream";
    }

    private byte[] ComputeFileHash(IEnumerable<string> chunkHashes)
    {
        var combined = string.Join(":", chunkHashes);
        return SHA256.HashData(Encoding.UTF8.GetBytes(combined));
    }

    private string FindDuplicateObject(List<ContentChunkInfo> chunks)
    {
        var targetHashes = chunks.Select(c => c.Hash).ToList();

        foreach (var map in _objectMaps.Values)
        {
            if (map.Chunks.Select(c => c.Hash).SequenceEqual(targetHashes))
            {
                return map.ObjectId;
            }
        }

        return chunks.FirstOrDefault()?.Hash ?? string.Empty;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _chunkStore.Clear();
        _objectMaps.Clear();
        ChunkIndex.Clear();
        return Task.CompletedTask;
    }

    private sealed class ContentChunkInfo
    {
        public required string Hash { get; init; }
        public required int Offset { get; init; }
        public required int Size { get; init; }
        public required string ChunkType { get; init; }
    }

    private sealed class ObjectContentMap
    {
        public required string ObjectId { get; init; }
        public required string ContentType { get; init; }
        public required List<ContentChunkInfo> Chunks { get; init; }
        public required long TotalSize { get; init; }
        public required DateTime CreatedAt { get; init; }
    }
}

/// <summary>
/// Interface for content-specific chunkers.
/// </summary>
public interface IContentChunker
{
    /// <summary>
    /// Chunks data based on content-specific logic.
    /// </summary>
    /// <param name="data">Data to chunk.</param>
    /// <param name="context">Deduplication context.</param>
    /// <returns>List of content chunks.</returns>
    List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}

/// <summary>
/// A chunk with content-aware metadata.
/// </summary>
public sealed class ContentChunk
{
    /// <summary>
    /// Chunk data.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Offset in original data.
    /// </summary>
    public required int Offset { get; init; }

    /// <summary>
    /// Chunk size.
    /// </summary>
    public required int Size { get; init; }

    /// <summary>
    /// Type of content in this chunk.
    /// </summary>
    public required string ChunkType { get; init; }
}

/// <summary>
/// Default chunker using fixed-size blocks.
/// </summary>
internal sealed class DefaultContentChunker : IContentChunker
{
    private const int DefaultBlockSize = 8192;

    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context)
    {
        var chunks = new List<ContentChunk>();
        var blockSize = context.FixedBlockSize > 0 ? context.FixedBlockSize : DefaultBlockSize;
        int offset = 0;

        while (offset < data.Length)
        {
            var size = Math.Min(blockSize, data.Length - offset);
            var chunkData = new byte[size];
            Array.Copy(data, offset, chunkData, 0, size);

            chunks.Add(new ContentChunk
            {
                Data = chunkData,
                Offset = offset,
                Size = size,
                ChunkType = "block"
            });

            offset += size;
        }

        return chunks;
    }
}

/// <summary>
/// Chunker for text content using line/paragraph boundaries.
/// </summary>
internal sealed class TextContentChunker : IContentChunker
{
    private const int MinChunkSize = 1024;
    private const int MaxChunkSize = 32768;

    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context)
    {
        var chunks = new List<ContentChunk>();
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n');
        var currentChunk = new StringBuilder();
        int currentOffset = 0;
        int chunkStart = 0;

        foreach (var line in lines)
        {
            currentChunk.AppendLine(line);

            if (currentChunk.Length >= MinChunkSize)
            {
                // Check for paragraph boundary (empty line) or max size
                if (string.IsNullOrWhiteSpace(line) || currentChunk.Length >= MaxChunkSize)
                {
                    var chunkBytes = Encoding.UTF8.GetBytes(currentChunk.ToString());
                    chunks.Add(new ContentChunk
                    {
                        Data = chunkBytes,
                        Offset = chunkStart,
                        Size = chunkBytes.Length,
                        ChunkType = "paragraph"
                    });

                    chunkStart = currentOffset + line.Length + 1;
                    currentChunk.Clear();
                }
            }

            currentOffset += line.Length + 1;
        }

        // Handle remaining content
        if (currentChunk.Length > 0)
        {
            var chunkBytes = Encoding.UTF8.GetBytes(currentChunk.ToString());
            chunks.Add(new ContentChunk
            {
                Data = chunkBytes,
                Offset = chunkStart,
                Size = chunkBytes.Length,
                ChunkType = "paragraph"
            });
        }

        return chunks;
    }
}

/// <summary>
/// Chunker for structured data (JSON, XML) using element boundaries.
/// </summary>
internal sealed class StructuredDataChunker : IContentChunker
{
    private const int MinChunkSize = 512;
    private const int MaxChunkSize = 16384;

    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context)
    {
        var chunks = new List<ContentChunk>();
        int depth = 0;
        int chunkStart = 0;

        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];

            // Track nesting depth
            if (b == '{' || b == '[' || b == '<')
                depth++;
            else if (b == '}' || b == ']' || b == '>')
                depth--;

            var currentSize = i - chunkStart + 1;

            // Create chunk at element boundaries when size is appropriate
            if (depth == 1 && currentSize >= MinChunkSize &&
                (b == ',' || b == '>' || currentSize >= MaxChunkSize))
            {
                var chunkSize = i - chunkStart + 1;
                var chunkData = new byte[chunkSize];
                Array.Copy(data, chunkStart, chunkData, 0, chunkSize);

                chunks.Add(new ContentChunk
                {
                    Data = chunkData,
                    Offset = chunkStart,
                    Size = chunkSize,
                    ChunkType = "element"
                });

                chunkStart = i + 1;
            }
        }

        // Handle remaining
        if (chunkStart < data.Length)
        {
            var chunkSize = data.Length - chunkStart;
            var chunkData = new byte[chunkSize];
            Array.Copy(data, chunkStart, chunkData, 0, chunkSize);

            chunks.Add(new ContentChunk
            {
                Data = chunkData,
                Offset = chunkStart,
                Size = chunkSize,
                ChunkType = "element"
            });
        }

        return chunks;
    }
}

/// <summary>
/// Chunker for tabular data (CSV) using row boundaries.
/// </summary>
internal sealed class TabularDataChunker : IContentChunker
{
    private const int RowsPerChunk = 100;

    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context)
    {
        var chunks = new List<ContentChunk>();
        int chunkStart = 0;
        int rowCount = 0;

        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == '\n')
            {
                rowCount++;

                if (rowCount >= RowsPerChunk)
                {
                    var chunkSize = i - chunkStart + 1;
                    var chunkData = new byte[chunkSize];
                    Array.Copy(data, chunkStart, chunkData, 0, chunkSize);

                    chunks.Add(new ContentChunk
                    {
                        Data = chunkData,
                        Offset = chunkStart,
                        Size = chunkSize,
                        ChunkType = "rows"
                    });

                    chunkStart = i + 1;
                    rowCount = 0;
                }
            }
        }

        // Handle remaining
        if (chunkStart < data.Length)
        {
            var chunkSize = data.Length - chunkStart;
            var chunkData = new byte[chunkSize];
            Array.Copy(data, chunkStart, chunkData, 0, chunkSize);

            chunks.Add(new ContentChunk
            {
                Data = chunkData,
                Offset = chunkStart,
                Size = chunkSize,
                ChunkType = "rows"
            });
        }

        return chunks;
    }
}

/// <summary>
/// Chunker for binary formats using fixed blocks with header detection.
/// </summary>
internal sealed class BinaryFormatChunker : IContentChunker
{
    private const int BlockSize = 16384;

    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context)
    {
        var chunks = new List<ContentChunk>();
        int offset = 0;

        // First chunk is header (first block)
        var headerSize = Math.Min(BlockSize, data.Length);
        var header = new byte[headerSize];
        Array.Copy(data, 0, header, 0, headerSize);

        chunks.Add(new ContentChunk
        {
            Data = header,
            Offset = 0,
            Size = headerSize,
            ChunkType = "header"
        });

        offset = headerSize;

        // Rest as data blocks
        while (offset < data.Length)
        {
            var size = Math.Min(BlockSize, data.Length - offset);
            var chunkData = new byte[size];
            Array.Copy(data, offset, chunkData, 0, size);

            chunks.Add(new ContentChunk
            {
                Data = chunkData,
                Offset = offset,
                Size = size,
                ChunkType = "data"
            });

            offset += size;
        }

        return chunks;
    }
}

/// <summary>
/// Chunker for archive formats that treats each entry as a chunk.
/// </summary>
internal sealed class ArchiveChunker : IContentChunker
{
    private const int MaxEntrySize = 65536;

    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context)
    {
        var chunks = new List<ContentChunk>();

        // For ZIP files, look for local file headers (PK\x03\x04)
        int offset = 0;
        int entryStart = 0;

        while (offset < data.Length - 4)
        {
            // Check for ZIP local file header signature
            if (data[offset] == 0x50 && data[offset + 1] == 0x4B &&
                data[offset + 2] == 0x03 && data[offset + 3] == 0x04)
            {
                if (offset > entryStart)
                {
                    // Save previous chunk
                    var prevSize = offset - entryStart;
                    var prevData = new byte[prevSize];
                    Array.Copy(data, entryStart, prevData, 0, prevSize);

                    chunks.Add(new ContentChunk
                    {
                        Data = prevData,
                        Offset = entryStart,
                        Size = prevSize,
                        ChunkType = "entry"
                    });
                }
                entryStart = offset;
            }

            offset++;
        }

        // Handle last entry
        if (entryStart < data.Length)
        {
            var lastSize = data.Length - entryStart;
            var lastData = new byte[lastSize];
            Array.Copy(data, entryStart, lastData, 0, lastSize);

            chunks.Add(new ContentChunk
            {
                Data = lastData,
                Offset = entryStart,
                Size = lastSize,
                ChunkType = "entry"
            });
        }

        // If no entries found, fall back to block chunking
        if (chunks.Count == 0)
        {
            return new DefaultContentChunker().Chunk(data, context);
        }

        return chunks;
    }
}
