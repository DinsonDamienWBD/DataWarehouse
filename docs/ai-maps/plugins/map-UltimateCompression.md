# Plugin: UltimateCompression
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateCompression

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/UltimateCompressionPlugin.cs
```csharp
public sealed class UltimateCompressionPlugin : HierarchyCompressionPluginBase
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string SubCategory;;
    public override int DefaultPipelineOrder;;
    public override bool AllowBypass;;
    public override int QualityLevel;;
    public override string CompressionAlgorithm;;
    public void RegisterStrategy(ICompressionStrategy strategy);
    public ICompressionStrategy? GetStrategy(string algorithmName);
    public IReadOnlyCollection<string> GetRegisteredAlgorithms();;
    public ICompressionStrategy SelectBestStrategy(ReadOnlySpan<byte> sample, bool preferSpeed = false);
    public void SetActiveStrategy(string algorithmName);
    public override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default);
    public override async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default);
    public UltimateCompressionPlugin();
    protected override IReadOnlyList<SDK.Contracts.RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<SDK.Contracts.RegisteredCapability>();
        capabilities.Add(new SDK.Contracts.RegisteredCapability { CapabilityId = $"{Id}.compress", DisplayName = $"{Name} - Compress", Description = "Compress data", Category = SDK.Contracts.CapabilityCategory.Compression, PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = new[] { "compression", "data-transformation" } });
        capabilities.Add(new SDK.Contracts.RegisteredCapability { CapabilityId = $"{Id}.decompress", DisplayName = $"{Name} - Decompress", Description = "Decompress data", Category = SDK.Contracts.CapabilityCategory.Compression, PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = new[] { "compression", "data-transformation" } });
        foreach (var kvp in _strategies)
        {
            var strategy = kvp.Value;
            var chars = strategy.Characteristics;
            var tags = new List<string>
            {
                "compression",
                "strategy",
                chars.AlgorithmName.ToLowerInvariant()
            };
            if (chars.SupportsStreaming)
                tags.Add("streaming");
            if (chars.SupportsParallelCompression)
                tags.Add("parallel");
            capabilities.Add(new SDK.Contracts.RegisteredCapability { CapabilityId = $"{Id}.strategy.{chars.AlgorithmName.ToLowerInvariant()}", DisplayName = $"{chars.AlgorithmName} - {strategy.Level}", Description = $"{chars.AlgorithmName} compression at {strategy.Level} level", Category = SDK.Contracts.CapabilityCategory.Compression, SubCategory = chars.AlgorithmName, PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Priority = chars.SupportsStreaming ? 60 : 50, Metadata = new Dictionary<string, object> { ["algorithm"] = chars.AlgorithmName, ["supportsStreaming"] = chars.SupportsStreaming, ["supportsParallelCompression"] = chars.SupportsParallelCompression, ["compressionRatio"] = chars.TypicalCompressionRatio, ["compressionSpeed"] = chars.CompressionSpeed, ["decompressionSpeed"] = chars.DecompressionSpeed }, SemanticDescription = $"Compress using {chars.AlgorithmName} algorithm" });
        }

        return capabilities;
    }
}
    protected override IReadOnlyList<SDK.AI.KnowledgeObject> GetStaticKnowledge();
    public override Task OnMessageAsync(PluginMessage message);
    public string SemanticDescription;;
    public string[] SemanticTags;;
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Scaling/CompressionScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Compression scaling with adaptive buffers and parallel chunks")]
public sealed class CompressionScalingManager : IScalableSubsystem, IDisposable
{
}
    public const int DefaultChunkSize = 4 * 1024 * 1024;
    public const long SmallInputThreshold = 1 * 1024 * 1024;
    public const long LargeInputThreshold = 100 * 1024 * 1024;
    public const int StreamingBufferSize = 4 * 1024 * 1024;
    public CompressionScalingManager(ILogger logger, ScalingLimits? limits = null, int chunkSize = DefaultChunkSize, CompressionQualityLevel defaultQuality = CompressionQualityLevel.Balanced);
    public BoundedCache<string, byte[]> DictionaryCache;;
    public BoundedCache<string, long> CompressionRatioCache;;
    public CompressionQualityLevel DefaultQuality { get => _defaultQuality; set => _defaultQuality = value; }
    public int ChunkSize { get => _chunkSize; set => _chunkSize = value > 0 ? value : DefaultChunkSize; }
    public int CalculateAdaptiveBufferSize(long inputSize);
    public bool ShouldUseParallelCompression(long inputSize);
    public async Task<ChunkCompressionResult> CompressParallelAsync(ReadOnlyMemory<byte> data, Func<ReadOnlyMemory<byte>, CompressionQualityLevel, byte[]> compressChunk, CompressionQualityLevel? quality = null, CancellationToken ct = default);
    public static int MapToZstdLevel(CompressionQualityLevel quality);
    public static int MapToBrotliLevel(CompressionQualityLevel quality);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState
{
    get
    {
        long pending = Interlocked.Read(ref _pendingOperations);
        int maxQueue = _currentLimits.MaxQueueDepth;
        if (pending <= 0)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.5)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.8)
            return BackpressureState.Warning;
        if (pending < maxQueue)
            return BackpressureState.Critical;
        return BackpressureState.Shedding;
    }
}
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/ContextMixing/NnzStrategy.cs
```csharp
public sealed class NnzStrategy : CompressionStrategyBase
{
#endregion
}
    public NnzStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class Perceptron
{
}
    public Perceptron();
    public int Predict(byte contextByte);
    public void Update(byte contextByte, int actualBit);
}
```
```csharp
private sealed class ArithmeticEncoder
{
}
    public ArithmeticEncoder(Stream output);;
    public void EncodeBit(int bit, int prob);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticDecoder
{
}
    public ArithmeticDecoder(Stream input);
    public int DecodeBit(int prob);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/ContextMixing/ZpaqStrategy.cs
```csharp
public sealed class ZpaqStrategy : CompressionStrategyBase
{
#endregion
}
    public ZpaqStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class ArithmeticEncoder
{
}
    public ArithmeticEncoder(Stream output);
    public void EncodeBit(int bit, int prob);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticDecoder
{
}
    public ArithmeticDecoder(Stream input);
    public int DecodeBit(int prob);
}
```
```csharp
private sealed class ContextModel
{
}
    public ContextModel();
    public int GetPrediction(byte context);
    public void Update(byte context, int bit);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, Func<byte[], byte[]> compressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _decompressed?.Position ?? 0; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/ContextMixing/PaqStrategy.cs
```csharp
public sealed class PaqStrategy : CompressionStrategyBase
{
#endregion
}
    public PaqStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class LogisticMixer
{
}
    public LogisticMixer(int numModels, int tableSize);
    public int Predict(byte[] data, int position);
    public void Update(byte[] data, int position, int bit);
}
```
```csharp
private sealed class ArithmeticEncoder
{
}
    public ArithmeticEncoder(Stream output);;
    public void EncodeBit(int bit, int prob);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticDecoder
{
}
    public ArithmeticDecoder(Stream input);
    public int DecodeBit(int prob);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/ContextMixing/PpmStrategy.cs
```csharp
public sealed class PpmStrategy : CompressionStrategyBase
{
#endregion
}
    public PpmStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class PpmTrie
{
}
    public PpmTrie(int maxOrder);;
    public void AddContext(byte[] data, int position);
    public ContextNode? GetContext(byte[] data, int position, int order);
}
```
```csharp
private sealed class ContextNode
{
}
    public int TotalCount { get; private set; }
    public int NumDistinct;;
    public void Increment(byte symbol);
    public int GetCount(byte symbol);
    public int GetCumulativeBelow(byte symbol, HashSet<byte> excluded);
    public (byte symbol, int cumLow, int count) FindSymbol(int target, HashSet<byte> excluded);
    public IEnumerable<byte> GetSymbols();;
}
```
```csharp
private sealed class ArithmeticRangeEncoder
{
}
    public ArithmeticRangeEncoder(Stream output);;
    public void EncodeRange(int cumLow, int cumHigh, int total);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticRangeDecoder
{
}
    public ArithmeticRangeDecoder(Stream input);
    public int GetTarget(int total);
    public void Decode(int cumLow, int cumHigh, int total);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/ContextMixing/PpmdStrategy.cs
```csharp
public sealed class PpmdStrategy : CompressionStrategyBase
{
#endregion
}
    public PpmdStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class PpmdModel
{
}
    public int MaxOrder;;
    public PpmdModel(int maxOrder);
    public void UpdateModel(byte[] data, int position, byte symbol);
    public ContextNode? GetContext(byte[] data, int position, int order);
    public int GetEscapeCount(ContextNode context, int order);
    public void UpdateSee(ContextNode context, int order, bool wasEscape);
}
```
```csharp
private sealed class SeeBin
{
}
    public int EscapeCount { get; set; };
    public int TotalCount { get; set; };
}
```
```csharp
private sealed class ContextNode
{
}
    public int TotalCount { get; private set; }
    public int NumDistinct;;
    public void Increment(byte symbol);
    public int GetCount(byte symbol);
    public int GetCumulativeBelow(byte symbol, HashSet<byte> excluded);
    public (byte symbol, int cumLow, int count) FindSymbol(int target, HashSet<byte> excluded);
    public IEnumerable<byte> GetSymbols();;
}
```
```csharp
private sealed class ArithmeticRangeEncoder
{
}
    public ArithmeticRangeEncoder(Stream output);;
    public void EncodeRange(int cumLow, int cumHigh, int total);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticRangeDecoder
{
}
    public ArithmeticRangeDecoder(Stream input);
    public int GetTarget(int total);
    public void Decode(int cumLow, int cumHigh, int total);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/ContextMixing/CmixStrategy.cs
```csharp
public sealed class CmixStrategy : CompressionStrategyBase
{
#endregion
}
    public CmixStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class GradientMixer
{
}
    public GradientMixer();
    public int Predict(byte[] data, int position);
    public void Update(byte[] data, int position, int bit);
}
```
```csharp
private sealed class ArithmeticEncoder
{
}
    public ArithmeticEncoder(Stream output);;
    public void EncodeBit(int bit, int prob);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticDecoder
{
}
    public ArithmeticDecoder(Stream input);
    public int DecodeBit(int prob);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transform/BrotliStrategy.cs
```csharp
public sealed class BrotliStrategy : CompressionStrategyBase
{
}
    public int Quality { get; set; };
    public BrotliStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transform/Bzip2Strategy.cs
```csharp
public sealed class Bzip2Strategy : CompressionStrategyBase
{
}
    public Bzip2Strategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transform/MtfStrategy.cs
```csharp
public sealed class MtfStrategy : CompressionStrategyBase
{
}
    public MtfStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class MtfCompressionStream : Stream
{
}
    public MtfCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class MtfDecompressionStream : Stream
{
}
    public MtfDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Flush();;
    protected override void Dispose(bool disposing);
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transform/BwtStrategy.cs
```csharp
public sealed class BwtStrategy : CompressionStrategyBase
{
}
    public BwtStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BwtCompressionStream : Stream
{
}
    public BwtCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BwtDecompressionStream : Stream
{
}
    public BwtDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Flush();;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/GZipStrategy.cs
```csharp
public sealed class GZipStrategy : CompressionStrategyBase
{
}
    public GZipStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/SnappyStrategy.cs
```csharp
public sealed class SnappyStrategy : CompressionStrategyBase
{
}
    public SnappyStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class SnappyBufferedCompressionStream : Stream
{
}
    public SnappyBufferedCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class SnappyBufferedDecompressionStream : Stream
{
}
    public SnappyBufferedDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => EnsureDecompressed().Position; set => EnsureDecompressed().Position = value; }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/Lz78Strategy.cs
```csharp
public sealed class Lz78Strategy : CompressionStrategyBase
{
}
    public Lz78Strategy() : base(CompressionLevel.Default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    public override CompressionCharacteristics Characteristics { get; };
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
}
```
```csharp
private sealed class BitWriter
{
}
    public BitWriter(Stream stream);
    public void WriteBits(int value, int numBits);
    public void FlushBits();
}
```
```csharp
private sealed class BitReader
{
}
    public BitReader(Stream stream);
    public int ReadBits(int numBits);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/LzmaStrategy.cs
```csharp
public sealed class LzmaStrategy : CompressionStrategyBase
{
}
    public LzmaStrategy() : base(SDK.Contracts.Compression.CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/Lzma2Strategy.cs
```csharp
public sealed class Lzma2Strategy : CompressionStrategyBase
{
}
    public Lzma2Strategy() : base(SDK.Contracts.Compression.CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/Lz4Strategy.cs
```csharp
public sealed class Lz4Strategy : CompressionStrategyBase
{
}
    public Lz4Strategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/LzoStrategy.cs
```csharp
public sealed class LzoStrategy : CompressionStrategyBase
{
}
    public LzoStrategy() : base(CompressionLevel.Default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    public override CompressionCharacteristics Characteristics { get; };
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
}
```
```csharp
internal sealed class BufferedAlgorithmCompressionStream : Stream
{
}
    public BufferedAlgorithmCompressionStream(Stream output, bool leaveOpen, Func<byte[], byte[]> compressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
```csharp
internal sealed class BufferedAlgorithmDecompressionStream : Stream
{
}
    public BufferedAlgorithmDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => EnsureDecompressed().Position; set => EnsureDecompressed().Position = value; }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/LzhStrategy.cs
```csharp
public sealed class LzhStrategy : CompressionStrategyBase
{
}
    public LzhStrategy() : base(CompressionLevel.Default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    public override CompressionCharacteristics Characteristics { get; };
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
}
```
```csharp
private sealed class LzhBitWriter
{
}
    public LzhBitWriter(Stream stream);;
    public void WriteBits(int value, int numBits);
    public void WriteByte(byte value);;
    public void WriteBytes(byte[] data);;
    public void WriteInt32(int value);
    public void Flush();
}
```
```csharp
private sealed class LzhBitReader
{
}
    public LzhBitReader(Stream stream);;
    public int ReadBits(int numBits);
    public byte ReadByte();
    public int ReadInt32();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/Lz77Strategy.cs
```csharp
public sealed class Lz77Strategy : CompressionStrategyBase
{
}
    public Lz77Strategy() : base(CompressionLevel.Default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    public override CompressionCharacteristics Characteristics { get; };
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/ZstdStrategy.cs
```csharp
public sealed class ZstdStrategy : CompressionStrategyBase
{
}
    public ZstdStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/DeflateStrategy.cs
```csharp
public sealed class DeflateStrategy : CompressionStrategyBase
{
}
    public DeflateStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/LzxStrategy.cs
```csharp
public sealed class LzxStrategy : CompressionStrategyBase
{
}
    public LzxStrategy() : base(SdkCompressionLevel.Default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    public override CompressionCharacteristics Characteristics { get; };
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class LzxCompressionStream : Stream
{
}
    public LzxCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class LzxDecompressionStream : Stream
{
}
    public LzxDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Flush();;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/LzfseStrategy.cs
```csharp
public sealed class LzfseStrategy : CompressionStrategyBase
{
}
    public LzfseStrategy() : base(CompressionLevel.Default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    public override CompressionCharacteristics Characteristics { get; };
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/DnaCompressionStrategy.cs
```csharp
public sealed class DnaCompressionStrategy : CompressionStrategyBase
{
#endregion
}
    public DnaCompressionStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private class HuffmanNode
{
}
    public byte Symbol { get; set; }
    public int Frequency { get; set; }
    public HuffmanNode? Left { get; set; }
    public HuffmanNode? Right { get; set; }
}
```
```csharp
private struct HuffmanCode
{
}
    public uint Code { get; set; }
    public int Length { get; set; }
}
```
```csharp
private class BitPackWriter
{
}
    public BitPackWriter(Stream stream);;
    public void WriteBit(int bit);
    public void WriteBits(uint value, int count);
    public void Flush();
}
```
```csharp
private class BitPackReader
{
}
    public BitPackReader(byte[] data);;
    public int ReadBit();
    public uint ReadBits(int count);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, DnaCompressionStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, DnaCompressionStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/FlacStrategy.cs
```csharp
public sealed class FlacStrategy : CompressionStrategyBase
{
#endregion
}
    public FlacStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BitWriter
{
}
    public BitWriter(Stream stream);
    public void WriteBit(int bit);
    public void Flush();
}
```
```csharp
private sealed class BitReader
{
}
    public BitReader(byte[] data);
    public int ReadBit();
}
```
```csharp
private sealed class FlacCompressionStream : Stream
{
}
    public FlacCompressionStream(Stream output, bool leaveOpen, FlacStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class FlacDecompressionStream : Stream
{
}
    public FlacDecompressionStream(Stream input, bool leaveOpen, FlacStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/AvifLosslessStrategy.cs
```csharp
public sealed class AvifLosslessStrategy : CompressionStrategyBase
{
#endregion
}
    public AvifLosslessStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, AvifLosslessStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, AvifLosslessStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/TimeSeriesStrategy.cs
```csharp
public sealed class TimeSeriesStrategy : CompressionStrategyBase
{
#endregion
}
    public TimeSeriesStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private class GorillaWriter
{
}
    public GorillaWriter(Stream stream);;
    public void WriteBit(int bit);
    public void WriteBits(ulong value, int count);
    public void WriteUInt64(ulong value);
    public void Flush();
}
```
```csharp
private class GorillaReader
{
}
    public GorillaReader(byte[] data);;
    public int ReadBit();
    public ulong ReadBits(int count);
    public ulong ReadUInt64();
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, TimeSeriesStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, TimeSeriesStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/ApngStrategy.cs
```csharp
public sealed class ApngStrategy : CompressionStrategyBase
{
#endregion
}
    public ApngStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, Func<byte[], byte[]> compressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/WebpLosslessStrategy.cs
```csharp
public sealed class WebpLosslessStrategy : CompressionStrategyBase
{
#endregion
}
    public WebpLosslessStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCodecStream : Stream
{
}
    public BufferedCodecStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> codec, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override int Read(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Domain/JxlLosslessStrategy.cs
```csharp
public sealed class JxlLosslessStrategy : CompressionStrategyBase
{
#endregion
}
    public JxlLosslessStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class JxlCompressionStream : Stream
{
}
    public JxlCompressionStream(Stream output, bool leaveOpen, JxlLosslessStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class JxlDecompressionStream : Stream
{
}
    public JxlDecompressionStream(Stream input, bool leaveOpen, JxlLosslessStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Emerging/LizardStrategy.cs
```csharp
public sealed class LizardStrategy : CompressionStrategyBase
{
#endregion
}
    public LizardStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, LizardStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, LizardStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Emerging/GipfeligStrategy.cs
```csharp
public sealed class GipfeligStrategy : CompressionStrategyBase
{
#endregion
}
    public GipfeligStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, GipfeligStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, GipfeligStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Emerging/DensityStrategy.cs
```csharp
public sealed class DensityStrategy : CompressionStrategyBase
{
#endregion
}
    public DensityStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, DensityStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, DensityStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Emerging/ZlingStrategy.cs
```csharp
public sealed class ZlingStrategy : CompressionStrategyBase
{
#endregion
}
    public ZlingStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, ZlingStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, ZlingStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Emerging/OodleStrategy.cs
```csharp
public sealed class OodleStrategy : CompressionStrategyBase
{
#endregion
}
    public OodleStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, OodleStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, OodleStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/EntropyCoding/AnsStrategy.cs
```csharp
public sealed class AnsStrategy : CompressionStrategyBase
{
#endregion
}
    public AnsStrategy() : this(CompressionLevel.Default, tableLogSize: 11, precision: 11);
    public AnsStrategy(CompressionLevel level, int tableLogSize = 11, int precision = 11) : base(level);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private struct EncodingEntry
{
}
    public int Start;
    public int Frequency;
}
```
```csharp
private struct DecodingEntry
{
}
    public byte Symbol;
    public int Frequency;
    public int Start;
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/EntropyCoding/RleStrategy.cs
```csharp
public sealed class RleStrategy : CompressionStrategyBase
{
#endregion
}
    public RleStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class RleCompressionStream : Stream
{
}
    public RleCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class RleDecompressionStream : Stream
{
}
    public RleDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/EntropyCoding/RansStrategy.cs
```csharp
public sealed class RansStrategy : CompressionStrategyBase
{
#endregion
}
    public RansStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/EntropyCoding/ArithmeticStrategy.cs
```csharp
public sealed class ArithmeticStrategy : CompressionStrategyBase
{
#endregion
}
    public ArithmeticStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class AdaptiveModel
{
}
    public int TotalFrequency { get; private set; }
    public AdaptiveModel();
    public (int cumLow, int cumHigh, int total) GetRange(byte symbol);
    public byte FindSymbol(int target);
    public void Update(byte symbol);
}
```
```csharp
private sealed class ArithmeticEncoder
{
}
    public ArithmeticEncoder(Stream output);;
    public void Encode(int cumLow, int cumHigh, int total);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticDecoder
{
}
    public ArithmeticDecoder(Stream input);
    public int GetTarget(int total);
    public void Decode(int cumLow, int cumHigh, int total);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/EntropyCoding/HuffmanStrategy.cs
```csharp
public sealed class HuffmanStrategy : CompressionStrategyBase
{
#endregion
}
    public HuffmanStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class HuffmanNode
{
}
    public byte Symbol { get; set; }
    public bool IsLeaf { get; set; }
    public HuffmanNode? Left { get; set; }
    public HuffmanNode? Right { get; set; }
    public long Frequency { get; set; }
}
```
```csharp
private sealed class HuffmanDecoder
{
}
    public HuffmanDecoder((uint code, int length)[] codes, byte[] codeLengths);
    public byte DecodeSymbol(BitReader reader);
}
```
```csharp
private sealed class BitWriter
{
}
    public BitWriter(Stream output);;
    public void WriteBits(uint value, int numBits);
    public byte Flush();
}
```
```csharp
private sealed class BitReader
{
}
    public BitReader(byte[] data, int paddingBits);
    public int ReadBit();
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Generative/GenerativeCompressionStrategy.cs
```csharp
public sealed class ContentAnalysisResult
{
}
    public GenerativeContentType ContentType { get; init; }
    public double SuitabilityScore { get; init; }
    public double Entropy { get; init; }
    public string[] DetectedPatterns { get; init; };
    public GenerativeCompressionMode RecommendedMode { get; init; }
    public bool RecommendHybridMode { get; init; }
    public double EstimatedCompressionRatio { get; init; }
}
```
```csharp
public sealed class ContentAnalyzer
{
}
    public ContentAnalysisResult Analyze(ReadOnlySpan<byte> data);
}
```
```csharp
public sealed class DetectedScene
{
}
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public SceneType Type { get; init; }
    public double StabilityScore { get; init; }
    public bool SuitableForGenerativeCompression { get; init; }
    public double RecommendedQualityThreshold { get; init; }
    public int EstimatedBitsPerFrame { get; init; }
    public byte[] SceneSignature { get; init; };
}
```
```csharp
public sealed class VideoSceneDetector
{
}
    public VideoSceneDetector(int blockSize = 16, double motionThreshold = 0.15, int minSceneLength = 5);
    public DetectedScene[] DetectScenes(byte[][] frames, int frameWidth, int frameHeight);
    public DetectedScene[] DetectScenesFromData(ReadOnlySpan<byte> videoData);
}
```
```csharp
public sealed class TrainingConfiguration
{
}
    public int Epochs { get; init; };
    public float LearningRate { get; init; };
    public float LearningRateDecay { get; init; };
    public int ContextSize { get; init; };
    public int HiddenLayerSize { get; init; };
    public int TableSize { get; init; };
    public bool UseGpuAcceleration { get; init; };
    public int BatchSize { get; init; };
    public double QualityThreshold { get; init; };
}
```
```csharp
public sealed class TrainingStatistics
{
}
    public long TrainingTimeMs { get; init; }
    public long SamplesProcessed { get; init; }
    public double FinalLoss { get; init; }
    public int ModelSizeBytes { get; init; }
    public double TrainingCompressionRatio { get; init; }
    public bool UsedGpuAcceleration { get; init; }
    public int EpochsCompleted { get; init; }
}
```
```csharp
public sealed class PromptGenerator
{
}
    public ContentPrompt GeneratePrompt(ReadOnlySpan<byte> data, GenerativeContentType contentType);
}
```
```csharp
public sealed class ContentPrompt
{
}
    public GenerativeContentType ContentType { get; init; }
    public int DataLength { get; init; }
    public long Timestamp { get; init; }
    public byte[] ByteDistribution { get; init; };
    public byte[] SequencePatterns { get; init; };
    public byte[] StructuralHints { get; init; };
    public string SemanticDescriptor { get; init; };
    public byte[] Serialize();
    public static ContentPrompt Deserialize(byte[] data);
}
```
```csharp
public sealed class QualityValidationResult
{
}
    public bool Passed { get; init; }
    public double QualityScore { get; init; }
    public double ByteAccuracy { get; init; }
    public double PatternPreservation { get; init; }
    public double StructuralSimilarity { get; init; }
    public int MismatchCount { get; init; }
    public int[] MismatchPositions { get; init; };
}
```
```csharp
public sealed class QualityValidator
{
}
    public QualityValidator(double qualityThreshold = 0.95, double byteAccuracyWeight = 0.5, double patternWeight = 0.25, double structuralWeight = 0.25);
    public QualityValidationResult Validate(ReadOnlySpan<byte> original, ReadOnlySpan<byte> reconstructed);
}
```
```csharp
public sealed class HybridCompressionResult
{
}
    public byte[] CompressedData { get; init; };
    public int LosslessRegionCount { get; init; }
    public int GenerativeRegionCount { get; init; }
    public double CompressionRatio { get; init; }
    public int OriginalSize { get; init; }
    public int CompressedSize { get; init; }
}
```
```csharp
public sealed class HybridCompressor
{
}
    public HybridCompressor(double importanceThreshold = 0.7, int minRegionSize = 64);
    public HybridCompressionResult Compress(byte[] data, double[]? importanceMap = null);
    public byte[] Decompress(byte[] compressedData);
}
```
```csharp
public sealed class CompressionRatioReport
{
}
    public long OriginalSize { get; set; }
    public long CompressedSize { get; set; }
    public long ModelSize { get; set; }
    public double CompressionRatio;;
    public double SpaceSavingsPercent;;
    public double CompressionSpeedMBps { get; set; }
    public double DecompressionSpeedMBps { get; set; }
    public long CompressionTimeMs { get; set; }
    public long DecompressionTimeMs { get; set; }
    public GenerativeContentType ContentType { get; set; }
    public GenerativeCompressionMode Mode { get; set; }
    public double QualityScore { get; set; }
    public bool UsedGpuAcceleration { get; set; }
    public int OperationCount { get; set; };
    public DateTime Timestamp { get; set; };
}
```
```csharp
public sealed class CompressionRatioReporter
{
}
    public void Record(CompressionRatioReport report);
    public CompressionRatioReport GetAggregateStats();
    public double GetCumulativeRatio();
    public CompressionRatioReport[] GetRecentReports(int count = 10);
    public void Reset();
}
```
```csharp
public sealed class GpuAccelerator
{
}
    public bool IsAvailable;;
    public string DeviceName;;
    public long MemoryBytes;;
    public GpuAccelerator();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
public float[] MatrixMultiply(float[] input, float[] weights, int inputSize, int outputSize);
    public void TrainingStep(float[] gradients, float[] weights, float learningRate);
    public GpuStatistics GetStatistics();
}
```
```csharp
public sealed class GpuStatistics
{
}
    public bool IsAvailable { get; init; }
    public string DeviceName { get; init; };
    public long TotalMemoryBytes { get; init; }
    public long UsedMemoryBytes { get; init; }
    public float Temperature { get; init; }
    public float Utilization { get; init; }
}
```
```csharp
public sealed class GenerativeCompressionStrategy : CompressionStrategyBase
{
#endregion
}
    public GenerativeCompressionStrategy() : base(CompressionLevel.Best);
    public override CompressionCharacteristics Characteristics { get; };
    public ContentAnalyzer ContentAnalyzer;;
    public VideoSceneDetector VideoSceneDetector;;
    public PromptGenerator PromptGenerator;;
    public QualityValidator QualityValidator;;
    public HybridCompressor HybridCompressor;;
    public CompressionRatioReporter RatioReporter;;
    public GpuAccelerator GpuAccelerator;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
    public override bool ShouldCompress(ReadOnlySpan<byte> input);
}
```
```csharp
private sealed class GenerativeModel
{
}
    public GenerativeModel(int contextSize, int tableSize, int hiddenSize, GpuAccelerator? gpuAccelerator = null);
    public TrainingStatistics Train(byte[] data, TrainingConfiguration config);
    public BytePrediction Predict(byte[] context);
    public void UpdatePrediction(int hash, int actualBit);
    public byte[] Serialize();
    public static GenerativeModel Deserialize(byte[] data, int contextSize, int tableSize, int hiddenSize, GpuAccelerator? gpuAccelerator);
}
```
```csharp
private sealed class BytePrediction
{
}
    public BytePrediction(int prediction, int contextHash, GenerativeModel model);
    public int GetBitProbability(int bitIndex);
    public void UpdateBitContext(int actualBit);
}
```
```csharp
private sealed class ArithmeticEncoder
{
}
    public ArithmeticEncoder(Stream output);
    public void EncodeBit(int bit, int prob);
    public void Flush();
}
```
```csharp
private sealed class ArithmeticDecoder
{
}
    public ArithmeticDecoder(Stream input);
    public int DecodeBit(int prob);
}
```
```csharp
private sealed class BufferedCompressionStream : Stream
{
}
    public BufferedCompressionStream(Stream output, bool leaveOpen, Func<byte[], byte[]> compressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class BufferedDecompressionStream : Stream
{
}
    public BufferedDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _decompressed?.Position ?? 0; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/GZipTransitStrategy.cs
```csharp
public sealed class GZipTransitStrategy : CompressionStrategyBase
{
}
    public GZipTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/Lz4TransitStrategy.cs
```csharp
public sealed class Lz4TransitStrategy : CompressionStrategyBase
{
}
    public Lz4TransitStrategy(CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/SnappyTransitStrategy.cs
```csharp
public sealed class SnappyTransitStrategy : CompressionStrategyBase
{
}
    public SnappyTransitStrategy() : base(CompressionLevel.Default);
    public SnappyTransitStrategy(CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
    public override bool ShouldCompress(ReadOnlySpan<byte> input);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/BrotliTransitStrategy.cs
```csharp
public sealed class BrotliTransitStrategy : CompressionStrategyBase
{
}
    public BrotliTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
    public override bool ShouldCompress(ReadOnlySpan<byte> input);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/ZstdTransitStrategy.cs
```csharp
public sealed class ZstdTransitStrategy : CompressionStrategyBase
{
}
    public ZstdTransitStrategy(CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/DeflateTransitStrategy.cs
```csharp
public sealed class DeflateTransitStrategy : CompressionStrategyBase
{
}
    public DeflateTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/AdaptiveTransitStrategy.cs
```csharp
public sealed class AdaptiveTransitStrategy : CompressionStrategyBase
{
}
    public AdaptiveTransitStrategy(CompressionLevel level, bool lowLatencyMode = false, bool meteredConnection = false) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override bool ShouldCompress(ReadOnlySpan<byte> input);
    public override long EstimateCompressedSize(long inputSize);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transit/NullTransitStrategy.cs
```csharp
public sealed class NullTransitStrategy : CompressionStrategyBase
{
}
    public NullTransitStrategy(CompressionLevel level) : base(level);
    public override CompressionCharacteristics Characteristics;;
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
    public override bool ShouldCompress(ReadOnlySpan<byte> input);
}
```
```csharp
private sealed class PassThroughStream : Stream
{
}
    public PassThroughStream(Stream baseStream, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _baseStream.Position; set => _baseStream.Position = value; }
    public override void Flush();;
    public override Task FlushAsync(CancellationToken cancellationToken);;
    public override int Read(byte[] buffer, int offset, int count);;
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);;
    public override void Write(byte[] buffer, int offset, int count);;
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Archive/ZipStrategy.cs
```csharp
public sealed class ZipStrategy : CompressionStrategyBase
{
#endregion
}
    public ZipStrategy() : base(SDK.Contracts.Compression.CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class ZipCompressionStream : Stream
{
}
    public ZipCompressionStream(Stream output, bool leaveOpen, SDK.Contracts.Compression.CompressionLevel level);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class ZipDecompressionStream : Stream
{
}
    public ZipDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Archive/RarStrategy.cs
```csharp
public sealed class RarStrategy : CompressionStrategyBase
{
#endregion
}
    public RarStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class RarCompressionStream : Stream
{
}
    public RarCompressionStream(Stream output, bool leaveOpen, RarStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class RarDecompressionStream : Stream
{
}
    public RarDecompressionStream(Stream input, bool leaveOpen, RarStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Archive/SevenZipStrategy.cs
```csharp
public sealed class SevenZipStrategy : CompressionStrategyBase
{
#endregion
}
    public SevenZipStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class SevenZipCompressionStream : Stream
{
}
    public SevenZipCompressionStream(Stream output, bool leaveOpen, SevenZipStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class SevenZipDecompressionStream : Stream
{
}
    public SevenZipDecompressionStream(Stream input, bool leaveOpen, SevenZipStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Archive/XzStrategy.cs
```csharp
public sealed class XzStrategy : CompressionStrategyBase
{
}
    public XzStrategy() : base(SdkCompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class XzCompressionStream : Stream
{
}
    public XzCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class XzDecompressionStream : Stream
{
}
    public XzDecompressionStream(Stream source, bool leaveOpen, XzStrategy owner);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Archive/TarStrategy.cs
```csharp
public sealed class TarStrategy : CompressionStrategyBase
{
#endregion
}
    public TarStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async ValueTask DisposeAsyncCore();
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class TarCompressionStream : Stream
{
}
    public TarCompressionStream(Stream output, bool leaveOpen, TarStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    protected override void Dispose(bool disposing);
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```
```csharp
private sealed class TarDecompressionStream : Stream
{
}
    public TarDecompressionStream(Stream input, bool leaveOpen, TarStrategy strategy);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    protected override void Dispose(bool disposing);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Delta/DeltaStrategy.cs
```csharp
public sealed class DeltaStrategy : CompressionStrategyBase
{
#endregion
}
    public DeltaStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class DeltaCompressionStream : Stream
{
}
    public DeltaCompressionStream(Stream output, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();;
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class DeltaDecompressionStream : Stream
{
}
    public DeltaDecompressionStream(Stream input, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);;
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Delta/VcdiffStrategy.cs
```csharp
public sealed class VcdiffStrategy : CompressionStrategyBase
{
#endregion
}
    public VcdiffStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Delta/XdeltaStrategy.cs
```csharp
public sealed class XdeltaStrategy : CompressionStrategyBase
{
#endregion
}
    public XdeltaStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Delta/BsdiffStrategy.cs
```csharp
public sealed class BsdiffStrategy : CompressionStrategyBase
{
#endregion
}
    public BsdiffStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Delta/ZdeltaStrategy.cs
```csharp
public sealed class ZdeltaStrategy : CompressionStrategyBase
{
#endregion
}
    public ZdeltaStrategy() : base(CompressionLevel.Default);
    public override CompressionCharacteristics Characteristics { get; };
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override ValueTask DisposeAsyncCore();
    protected override byte[] CompressCore(byte[] input);
    protected override byte[] DecompressCore(byte[] input);
    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);
    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);
    public override long EstimateCompressedSize(long inputSize);
}
```
```csharp
private sealed class BufferedTransformStream : Stream
{
}
    public BufferedTransformStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> transform, bool isCompression);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);;
    public override void Write(byte[] buffer, int offset, int count);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
}
```
