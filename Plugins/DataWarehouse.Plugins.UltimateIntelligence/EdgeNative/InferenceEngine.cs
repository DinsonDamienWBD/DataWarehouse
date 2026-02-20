using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.EdgeNative;

#region IInferenceStrategy Interface

/// <summary>
/// Base interface for all inference strategies.
/// Strategies run ML models on edge devices with WASI-NN backend support.
/// </summary>
public interface IInferenceStrategy
{
    /// <summary>
    /// Gets the model format supported by this strategy (e.g., "onnx", "gguf", "tflite").
    /// </summary>
    string ModelFormat { get; }

    /// <summary>
    /// Runs inference asynchronously on the provided input.
    /// </summary>
    /// <param name="modelPath">Path to the model file.</param>
    /// <param name="input">Input tensor data.</param>
    /// <param name="options">Optional inference options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Inference result containing output tensors.</returns>
    Task<InferenceResult> InferAsync(string modelPath, InferenceInput input, InferenceOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the capabilities of this inference strategy.
    /// </summary>
    /// <returns>Capability descriptor.</returns>
    InferenceCapabilities GetCapabilities();
}

/// <summary>
/// Input data for inference operations.
/// </summary>
public class InferenceInput
{
    /// <summary>
    /// Input tensor data as byte array.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Tensor shape (dimensions).
    /// </summary>
    public required int[] Shape { get; init; }

    /// <summary>
    /// Data type of the tensor.
    /// </summary>
    public TensorDataType DataType { get; init; } = TensorDataType.Float32;

    /// <summary>
    /// Additional metadata for the input.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Result of an inference operation.
/// </summary>
public class InferenceResult
{
    /// <summary>
    /// Output tensor data.
    /// </summary>
    public required byte[] OutputData { get; init; }

    /// <summary>
    /// Output tensor shape.
    /// </summary>
    public required int[] OutputShape { get; init; }

    /// <summary>
    /// Inference latency in milliseconds.
    /// </summary>
    public double LatencyMs { get; init; }

    /// <summary>
    /// Whether the inference was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if inference failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Additional metadata about the inference.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Options for inference operations.
/// </summary>
public class InferenceOptions
{
    /// <summary>
    /// Preferred execution provider (CPU, GPU, NPU).
    /// </summary>
    public ExecutionProvider Provider { get; init; } = ExecutionProvider.Auto;

    /// <summary>
    /// Maximum inference time in milliseconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Whether to use model caching.
    /// </summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// Batch size for inference.
    /// </summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>
    /// Number of threads to use for CPU inference.
    /// </summary>
    public int NumThreads { get; init; } = 4;

    /// <summary>
    /// Additional provider-specific options.
    /// </summary>
    public Dictionary<string, object> ProviderOptions { get; init; } = new();
}

/// <summary>
/// Capabilities of an inference strategy.
/// </summary>
public class InferenceCapabilities
{
    /// <summary>
    /// Supported model format.
    /// </summary>
    public required string ModelFormat { get; init; }

    /// <summary>
    /// Supported execution providers.
    /// </summary>
    public required ExecutionProvider[] SupportedProviders { get; init; }

    /// <summary>
    /// Supported data types.
    /// </summary>
    public required TensorDataType[] SupportedDataTypes { get; init; }

    /// <summary>
    /// Whether streaming inference is supported.
    /// </summary>
    public bool SupportsStreaming { get; init; }

    /// <summary>
    /// Maximum tensor size in bytes.
    /// </summary>
    public long MaxTensorSizeBytes { get; init; } = 1024 * 1024 * 100; // 100MB

    /// <summary>
    /// Additional capability flags.
    /// </summary>
    public Dictionary<string, bool> Features { get; init; } = new();
}

/// <summary>
/// Execution provider types.
/// </summary>
public enum ExecutionProvider
{
    /// <summary>Automatically select best available provider.</summary>
    Auto,

    /// <summary>CPU execution.</summary>
    CPU,

    /// <summary>GPU execution (CUDA/Metal/DirectML).</summary>
    GPU,

    /// <summary>Neural Processing Unit.</summary>
    NPU,

    /// <summary>WebAssembly SIMD.</summary>
    WASM,

    /// <summary>WASI-NN runtime.</summary>
    WasiNN
}

/// <summary>
/// Tensor data types.
/// </summary>
public enum TensorDataType
{
    Float32,
    Float16,
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Bool
}

#endregion

#region WasiNnBackendRegistry

/// <summary>
/// Registers and manages WASI-NN ML backends (ONNX, TFLite, PyTorch).
/// Communicates with UltimateCompute plugin via message bus topic "compute.wasi.load-module".
/// </summary>
public class WasiNnBackendRegistry
{
    private readonly IMessageBus _messageBus;
    private readonly BoundedDictionary<string, WasiNnBackend> _backends = new BoundedDictionary<string, WasiNnBackend>(1000);
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the WasiNnBackendRegistry.
    /// </summary>
    /// <param name="messageBus">Message bus for cross-plugin communication.</param>
    public WasiNnBackendRegistry(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        InitializeDefaultBackends();
    }

    /// <summary>
    /// Registers a WASI-NN backend.
    /// </summary>
    /// <param name="backend">Backend to register.</param>
    public void RegisterBackend(WasiNnBackend backend)
    {
        if (backend == null) throw new ArgumentNullException(nameof(backend));

        lock (_lock)
        {
            _backends[backend.Name] = backend;
        }
    }

    /// <summary>
    /// Gets a registered backend by name.
    /// </summary>
    /// <param name="name">Backend name (e.g., "onnx", "tflite", "pytorch").</param>
    /// <returns>Backend instance, or null if not found.</returns>
    public WasiNnBackend? GetBackend(string name)
    {
        _backends.TryGetValue(name, out var backend);
        return backend;
    }

    /// <summary>
    /// Loads a WASM module for ML inference via UltimateCompute.
    /// Sends message to "compute.wasi.load-module" topic.
    /// </summary>
    /// <param name="backendName">Backend name (onnx, tflite, etc.).</param>
    /// <param name="modulePath">Path to WASM module.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if module loaded successfully.</returns>
    public async Task<bool> LoadModuleAsync(string backendName, string modulePath, CancellationToken ct = default)
    {
        var backend = GetBackend(backendName);
        if (backend == null)
        {
            throw new InvalidOperationException($"Backend '{backendName}' is not registered.");
        }

        var message = PluginMessage.Create("compute.wasi.load-module", new Dictionary<string, object>
        {
            ["backend"] = backendName,
            ["modulePath"] = modulePath,
            ["moduleType"] = "wasi-nn",
            ["timestamp"] = DateTime.UtcNow
        });

        try
        {
            var response = await _messageBus.SendAsync("compute.wasi.load-module", message, TimeSpan.FromSeconds(30), ct);

            if (response.Success)
            {
                backend.IsLoaded = true;
                backend.LoadedModulePath = modulePath;
                return true;
            }
            else
            {
                throw new InvalidOperationException($"Failed to load module: {response.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to communicate with UltimateCompute: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets all registered backends.
    /// </summary>
    /// <returns>Collection of registered backends.</returns>
    public IEnumerable<WasiNnBackend> GetAllBackends()
    {
        return _backends.Values.ToArray();
    }

    private void InitializeDefaultBackends()
    {
        RegisterBackend(new WasiNnBackend
        {
            Name = "onnx",
            DisplayName = "ONNX Runtime",
            Description = "Open Neural Network Exchange format for interoperable ML models.",
            SupportedFormats = new[] { "onnx" },
            IsAvailable = true
        });

        RegisterBackend(new WasiNnBackend
        {
            Name = "tflite",
            DisplayName = "TensorFlow Lite",
            Description = "Lightweight TensorFlow runtime for mobile and edge devices.",
            SupportedFormats = new[] { "tflite" },
            IsAvailable = true
        });

        RegisterBackend(new WasiNnBackend
        {
            Name = "pytorch",
            DisplayName = "PyTorch Mobile",
            Description = "PyTorch runtime for edge deployment.",
            SupportedFormats = new[] { "pt", "pth" },
            IsAvailable = true
        });

        RegisterBackend(new WasiNnBackend
        {
            Name = "openvino",
            DisplayName = "OpenVINO",
            Description = "Intel OpenVINO toolkit for optimized inference.",
            SupportedFormats = new[] { "xml", "bin" },
            IsAvailable = false // Requires OpenVINO installation
        });
    }
}

/// <summary>
/// Represents a WASI-NN backend.
/// </summary>
public class WasiNnBackend
{
    /// <summary>Backend identifier (e.g., "onnx", "tflite").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Description of the backend.</summary>
    public required string Description { get; init; }

    /// <summary>Supported model formats.</summary>
    public required string[] SupportedFormats { get; init; }

    /// <summary>Whether this backend is available on the current system.</summary>
    public bool IsAvailable { get; set; }

    /// <summary>Whether a module is currently loaded.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>Path to the loaded WASM module.</summary>
    public string? LoadedModulePath { get; set; }

    /// <summary>Additional backend metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

#endregion

#region WasiNnGpuBridge

/// <summary>
/// Maps WASI-NN inference requests to GPU execution.
/// Uses "compute.gpu.execute" message topic to delegate to UltimateCompute.
/// Falls back to CPU if GPU is unavailable.
/// </summary>
public class WasiNnGpuBridge
{
    private readonly IMessageBus _messageBus;
    private bool _gpuAvailable = true;
    private int _gpuFailureCount = 0;
    private const int MaxGpuFailures = 3;

    /// <summary>
    /// Initializes a new instance of the WasiNnGpuBridge.
    /// </summary>
    /// <param name="messageBus">Message bus for GPU communication.</param>
    public WasiNnGpuBridge(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Gets whether GPU acceleration is currently available.
    /// </summary>
    public bool IsGpuAvailable => _gpuAvailable && _gpuFailureCount < MaxGpuFailures;

    /// <summary>
    /// Executes inference on GPU via message bus.
    /// Falls back to CPU on failure.
    /// </summary>
    /// <param name="modelPath">Path to model file.</param>
    /// <param name="input">Input data.</param>
    /// <param name="options">Inference options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Inference result.</returns>
    public async Task<InferenceResult> ExecuteAsync(string modelPath, InferenceInput input, InferenceOptions options, CancellationToken ct = default)
    {
        // Try GPU first if available
        if (IsGpuAvailable && options.Provider != ExecutionProvider.CPU)
        {
            try
            {
                return await ExecuteOnGpuAsync(modelPath, input, options, ct);
            }
            catch (Exception)
            {
                _gpuFailureCount++;

                if (_gpuFailureCount >= MaxGpuFailures)
                {
                    _gpuAvailable = false;
                }

                // Fall through to CPU fallback
            }
        }

        // Fallback to CPU
        return await ExecuteOnCpuAsync(modelPath, input, options, ct);
    }

    private async Task<InferenceResult> ExecuteOnGpuAsync(string modelPath, InferenceInput input, InferenceOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var message = PluginMessage.Create("compute.gpu.execute", new Dictionary<string, object>
        {
            ["operation"] = "ml-inference",
            ["modelPath"] = modelPath,
            ["inputData"] = Convert.ToBase64String(input.Data),
            ["inputShape"] = input.Shape,
            ["dataType"] = input.DataType.ToString(),
            ["timeoutMs"] = options.TimeoutMs,
            ["numThreads"] = options.NumThreads,
            ["batchSize"] = options.BatchSize
        });

        var response = await _messageBus.SendAsync("compute.gpu.execute", message, TimeSpan.FromMilliseconds(options.TimeoutMs), ct);
        sw.Stop();

        if (!response.Success)
        {
            throw new InvalidOperationException($"GPU execution failed: {response.ErrorMessage}");
        }

        var outputData = Convert.FromBase64String((string)response.Payload!);
        var outputShape = System.Text.Json.JsonSerializer.Deserialize<int[]>(((System.Text.Json.JsonElement)response.Metadata["outputShape"]).GetRawText())!;

        // Reset failure count on success
        _gpuFailureCount = 0;

        return new InferenceResult
        {
            OutputData = outputData,
            OutputShape = outputShape,
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            Success = true,
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = "GPU",
                ["backend"] = response.Metadata.GetValueOrDefault("backend", "unknown")
            }
        };
    }

    private async Task<InferenceResult> ExecuteOnCpuAsync(string modelPath, InferenceInput input, InferenceOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var message = PluginMessage.Create("compute.wasm.execute", new Dictionary<string, object>
        {
            ["operation"] = "ml-inference",
            ["modelPath"] = modelPath,
            ["inputData"] = Convert.ToBase64String(input.Data),
            ["inputShape"] = input.Shape,
            ["dataType"] = input.DataType.ToString(),
            ["timeoutMs"] = options.TimeoutMs,
            ["numThreads"] = options.NumThreads,
            ["provider"] = "CPU"
        });

        var response = await _messageBus.SendAsync("compute.wasm.execute", message, TimeSpan.FromMilliseconds(options.TimeoutMs), ct);
        sw.Stop();

        if (!response.Success)
        {
            throw new InvalidOperationException($"CPU execution failed: {response.ErrorMessage}");
        }

        var outputData = Convert.FromBase64String((string)response.Payload!);
        var outputShape = System.Text.Json.JsonSerializer.Deserialize<int[]>(((System.Text.Json.JsonElement)response.Metadata["outputShape"]).GetRawText())!;

        return new InferenceResult
        {
            OutputData = outputData,
            OutputShape = outputShape,
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            Success = true,
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = "CPU",
                ["fallback"] = !IsGpuAvailable
            }
        };
    }

    /// <summary>
    /// Resets GPU availability status (for testing or recovery).
    /// </summary>
    public void ResetGpuStatus()
    {
        _gpuAvailable = true;
        _gpuFailureCount = 0;
    }
}

#endregion

#region WasiNnModelCache

/// <summary>
/// LRU cache for loaded ML models with memory pressure awareness.
/// Prevents redundant model loading and optimizes inference performance.
/// </summary>
public class WasiNnModelCache
{
    private readonly int _maxCacheSize;
    private readonly long _maxMemoryBytes;
    private readonly BoundedDictionary<string, CachedModel> _cache = new BoundedDictionary<string, CachedModel>(1000);
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lock = new();
    private long _currentMemoryUsage = 0;

    /// <summary>
    /// Initializes a new instance of the WasiNnModelCache.
    /// </summary>
    /// <param name="maxCacheSize">Maximum number of models to cache.</param>
    /// <param name="maxMemoryMb">Maximum memory usage in MB.</param>
    public WasiNnModelCache(int maxCacheSize = 10, int maxMemoryMb = 512)
    {
        _maxCacheSize = maxCacheSize;
        _maxMemoryBytes = maxMemoryMb * 1024L * 1024L;
    }

    /// <summary>
    /// Gets the current number of cached models.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Gets the current memory usage in bytes.
    /// </summary>
    public long CurrentMemoryUsage => Interlocked.Read(ref _currentMemoryUsage);

    /// <summary>
    /// Gets or adds a model to the cache.
    /// </summary>
    /// <param name="modelPath">Path to the model file.</param>
    /// <param name="loader">Function to load the model if not cached.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cached model data.</returns>
    public async Task<byte[]> GetOrAddAsync(string modelPath, Func<Task<byte[]>> loader, CancellationToken ct = default)
    {
        // Check if already cached
        if (_cache.TryGetValue(modelPath, out var cached))
        {
            lock (_lock)
            {
                // Move to front of LRU list
                _lruList.Remove(cached.LruNode);
                _lruList.AddFirst(cached.LruNode);
            }

            cached.LastAccessTime = DateTime.UtcNow;
            cached.HitCount++;
            return cached.Data;
        }

        // Load model
        var data = await loader();
        var sizeBytes = data.Length;

        lock (_lock)
        {
            // Evict if necessary
            while ((_cache.Count >= _maxCacheSize || _currentMemoryUsage + sizeBytes > _maxMemoryBytes) && _lruList.Count > 0)
            {
                EvictLeastRecentlyUsed();
            }

            // Add to cache
            var node = _lruList.AddFirst(modelPath);
            var model = new CachedModel
            {
                Path = modelPath,
                Data = data,
                SizeBytes = sizeBytes,
                LoadedAt = DateTime.UtcNow,
                LastAccessTime = DateTime.UtcNow,
                LruNode = node,
                HitCount = 0
            };

            _cache[modelPath] = model;
            Interlocked.Add(ref _currentMemoryUsage, sizeBytes);
        }

        return data;
    }

    /// <summary>
    /// Removes a model from the cache.
    /// </summary>
    /// <param name="modelPath">Path to the model file.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool Remove(string modelPath)
    {
        if (_cache.TryRemove(modelPath, out var cached))
        {
            lock (_lock)
            {
                _lruList.Remove(cached.LruNode);
                Interlocked.Add(ref _currentMemoryUsage, -cached.SizeBytes);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears all cached models.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
            Interlocked.Exchange(ref _currentMemoryUsage, 0);
        }
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    /// <returns>Cache statistics.</returns>
    public CacheStatistics GetStatistics()
    {
        var models = _cache.Values.ToArray();
        return new CacheStatistics
        {
            CachedModels = models.Length,
            TotalMemoryBytes = Interlocked.Read(ref _currentMemoryUsage),
            MaxMemoryBytes = _maxMemoryBytes,
            MaxCacheSize = _maxCacheSize,
            TotalHits = models.Sum(m => m.HitCount),
            AverageModelSizeBytes = models.Length > 0 ? models.Average(m => m.SizeBytes) : 0,
            OldestModelAge = models.Length > 0 ? models.Max(m => (DateTime.UtcNow - m.LoadedAt).TotalSeconds) : 0
        };
    }

    private void EvictLeastRecentlyUsed()
    {
        var lruPath = _lruList.Last?.Value;
        if (lruPath != null && _cache.TryRemove(lruPath, out var evicted))
        {
            _lruList.RemoveLast();
            Interlocked.Add(ref _currentMemoryUsage, -evicted.SizeBytes);
        }
    }

    private class CachedModel
    {
        public required string Path { get; init; }
        public required byte[] Data { get; init; }
        public required long SizeBytes { get; init; }
        public required DateTime LoadedAt { get; init; }
        public required DateTime LastAccessTime { get; set; }
        public required LinkedListNode<string> LruNode { get; init; }
        public long HitCount { get; set; }
    }
}

/// <summary>
/// Statistics for model cache.
/// </summary>
public class CacheStatistics
{
    public int CachedModels { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long MaxMemoryBytes { get; init; }
    public int MaxCacheSize { get; init; }
    public long TotalHits { get; init; }
    public double AverageModelSizeBytes { get; init; }
    public double OldestModelAge { get; init; }
}

#endregion

#region OnnxInferenceStrategy

/// <summary>
/// Inference strategy for ONNX models.
/// Delegates execution to UltimateCompute via "compute.wasm.execute" topic.
/// </summary>
public class OnnxInferenceStrategy : IInferenceStrategy
{
    private readonly WasiNnGpuBridge _gpuBridge;
    private readonly WasiNnModelCache _modelCache;
    private readonly WasiNnBackendRegistry _backendRegistry;

    /// <inheritdoc/>
    public string ModelFormat => "onnx";

    /// <summary>
    /// Initializes a new instance of the OnnxInferenceStrategy.
    /// </summary>
    public OnnxInferenceStrategy(WasiNnGpuBridge gpuBridge, WasiNnModelCache modelCache, WasiNnBackendRegistry backendRegistry)
    {
        _gpuBridge = gpuBridge ?? throw new ArgumentNullException(nameof(gpuBridge));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _backendRegistry = backendRegistry ?? throw new ArgumentNullException(nameof(backendRegistry));
    }

    /// <inheritdoc/>
    public async Task<InferenceResult> InferAsync(string modelPath, InferenceInput input, InferenceOptions? options = null, CancellationToken ct = default)
    {
        options ??= new InferenceOptions();

        try
        {
            // Ensure ONNX backend is loaded
            var backend = _backendRegistry.GetBackend("onnx");
            if (backend != null && !backend.IsLoaded)
            {
                await _backendRegistry.LoadModuleAsync("onnx", modelPath, ct);
            }

            // Load model from cache or disk
            if (options.UseCache)
            {
                await _modelCache.GetOrAddAsync(modelPath, () => LoadModelAsync(modelPath), ct);
            }

            // Execute inference via GPU bridge
            return await _gpuBridge.ExecuteAsync(modelPath, input, options, ct);
        }
        catch (Exception ex)
        {
            return new InferenceResult
            {
                OutputData = Array.Empty<byte>(),
                OutputShape = Array.Empty<int>(),
                Success = false,
                ErrorMessage = $"ONNX inference failed: {ex.Message}",
                Metadata = new Dictionary<string, object>
                {
                    ["exception"] = ex.GetType().Name,
                    ["modelFormat"] = "onnx"
                }
            };
        }
    }

    /// <inheritdoc/>
    public InferenceCapabilities GetCapabilities()
    {
        return new InferenceCapabilities
        {
            ModelFormat = "onnx",
            SupportedProviders = new[] { ExecutionProvider.CPU, ExecutionProvider.GPU, ExecutionProvider.WasiNN },
            SupportedDataTypes = new[]
            {
                TensorDataType.Float32,
                TensorDataType.Float16,
                TensorDataType.Int8,
                TensorDataType.Int32,
                TensorDataType.Int64,
                TensorDataType.UInt8
            },
            SupportsStreaming = false,
            MaxTensorSizeBytes = 1024 * 1024 * 500, // 500MB
            Features = new Dictionary<string, bool>
            {
                ["quantization"] = true,
                ["dynamic_shapes"] = true,
                ["graph_optimization"] = true
            }
        };
    }

    private static async Task<byte[]> LoadModelAsync(string path)
    {
        return await File.ReadAllBytesAsync(path);
    }
}

#endregion

#region GgufInferenceStrategy

/// <summary>
/// Inference strategy for GGUF quantized LLMs (Llama, Mistral, etc.).
/// Optimized for large language model inference on resource-constrained devices.
/// </summary>
public class GgufInferenceStrategy : IInferenceStrategy
{
    private readonly WasiNnGpuBridge _gpuBridge;
    private readonly WasiNnModelCache _modelCache;
    private readonly WasiNnBackendRegistry _backendRegistry;

    /// <inheritdoc/>
    public string ModelFormat => "gguf";

    /// <summary>
    /// Initializes a new instance of the GgufInferenceStrategy.
    /// </summary>
    public GgufInferenceStrategy(WasiNnGpuBridge gpuBridge, WasiNnModelCache modelCache, WasiNnBackendRegistry backendRegistry)
    {
        _gpuBridge = gpuBridge ?? throw new ArgumentNullException(nameof(gpuBridge));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _backendRegistry = backendRegistry ?? throw new ArgumentNullException(nameof(backendRegistry));
    }

    /// <inheritdoc/>
    public async Task<InferenceResult> InferAsync(string modelPath, InferenceInput input, InferenceOptions? options = null, CancellationToken ct = default)
    {
        options ??= new InferenceOptions();

        try
        {
            // GGUF models typically use custom backends (llama.cpp)
            // Load model from cache
            if (options.UseCache)
            {
                await _modelCache.GetOrAddAsync(modelPath, () => LoadModelAsync(modelPath), ct);
            }

            // Execute inference via GPU bridge (delegates to compute plugin)
            var result = await _gpuBridge.ExecuteAsync(modelPath, input, options, ct);

            result.Metadata["modelFormat"] = "gguf";
            result.Metadata["quantized"] = true;

            return result;
        }
        catch (Exception ex)
        {
            return new InferenceResult
            {
                OutputData = Array.Empty<byte>(),
                OutputShape = Array.Empty<int>(),
                Success = false,
                ErrorMessage = $"GGUF inference failed: {ex.Message}",
                Metadata = new Dictionary<string, object>
                {
                    ["exception"] = ex.GetType().Name,
                    ["modelFormat"] = "gguf"
                }
            };
        }
    }

    /// <inheritdoc/>
    public InferenceCapabilities GetCapabilities()
    {
        return new InferenceCapabilities
        {
            ModelFormat = "gguf",
            SupportedProviders = new[] { ExecutionProvider.CPU, ExecutionProvider.GPU },
            SupportedDataTypes = new[]
            {
                TensorDataType.Float32,
                TensorDataType.Float16,
                TensorDataType.Int8 // GGUF supports quantization
            },
            SupportsStreaming = true, // GGUF models support streaming generation
            MaxTensorSizeBytes = 1024L * 1024 * 1024 * 10, // 10GB for large LLMs
            Features = new Dictionary<string, bool>
            {
                ["quantization"] = true,
                ["4bit_quantization"] = true,
                ["8bit_quantization"] = true,
                ["streaming_generation"] = true,
                ["context_window"] = true,
                ["llama_architecture"] = true,
                ["mistral_architecture"] = true
            }
        };
    }

    private static async Task<byte[]> LoadModelAsync(string path)
    {
        // For large GGUF models, we might only load metadata initially
        // Full model is loaded by the backend on demand
        return await File.ReadAllBytesAsync(path);
    }
}

#endregion

#region StreamInferencePipeline

/// <summary>
/// Zero-copy streaming inference pipeline for low-latency ML serving.
/// Optimized for real-time applications with minimal memory overhead.
/// </summary>
public class StreamInferencePipeline
{
    private readonly IInferenceStrategy _strategy;
    private readonly WasiNnModelCache _modelCache;
    private readonly SemaphoreSlim _semaphore;
    private long _totalInferences = 0;
    private long _totalLatencyMs = 0;

    /// <summary>
    /// Initializes a new instance of the StreamInferencePipeline.
    /// </summary>
    /// <param name="strategy">Inference strategy to use.</param>
    /// <param name="modelCache">Model cache for optimization.</param>
    /// <param name="maxConcurrency">Maximum concurrent inference operations.</param>
    public StreamInferencePipeline(IInferenceStrategy strategy, WasiNnModelCache modelCache, int maxConcurrency = 4)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>
    /// Gets the average inference latency in milliseconds.
    /// </summary>
    public double AverageLatencyMs
    {
        get
        {
            var total = Interlocked.Read(ref _totalInferences);
            return total > 0 ? Interlocked.Read(ref _totalLatencyMs) / (double)total : 0;
        }
    }

    /// <summary>
    /// Gets the total number of inferences processed.
    /// </summary>
    public long TotalInferences => Interlocked.Read(ref _totalInferences);

    /// <summary>
    /// Processes a stream of inference requests with zero-copy optimization.
    /// </summary>
    /// <param name="modelPath">Path to the model.</param>
    /// <param name="inputStream">Stream of input data.</param>
    /// <param name="options">Inference options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream of inference results.</returns>
    public async IAsyncEnumerable<InferenceResult> ProcessStreamAsync(
        string modelPath,
        IAsyncEnumerable<InferenceInput> inputStream,
        InferenceOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new InferenceOptions();

        // Pre-warm cache
        if (options.UseCache)
        {
            await _modelCache.GetOrAddAsync(modelPath, () => File.ReadAllBytesAsync(modelPath), ct);
        }

        await foreach (var input in inputStream.WithCancellation(ct))
        {
            await _semaphore.WaitAsync(ct);

            try
            {
                var sw = Stopwatch.StartNew();
                var result = await _strategy.InferAsync(modelPath, input, options, ct);
                sw.Stop();

                Interlocked.Increment(ref _totalInferences);
                Interlocked.Add(ref _totalLatencyMs, (long)sw.Elapsed.TotalMilliseconds);

                yield return result;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Processes a batch of inputs in parallel.
    /// </summary>
    /// <param name="modelPath">Path to the model.</param>
    /// <param name="inputs">Batch of inputs.</param>
    /// <param name="options">Inference options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of inference results.</returns>
    public async Task<InferenceResult[]> ProcessBatchAsync(
        string modelPath,
        InferenceInput[] inputs,
        InferenceOptions? options = null,
        CancellationToken ct = default)
    {
        var tasks = inputs.Select(input => ProcessSingleAsync(modelPath, input, options, ct));
        return await Task.WhenAll(tasks);
    }

    private async Task<InferenceResult> ProcessSingleAsync(
        string modelPath,
        InferenceInput input,
        InferenceOptions? options,
        CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _strategy.InferAsync(modelPath, input, options, ct);
            sw.Stop();

            Interlocked.Increment(ref _totalInferences);
            Interlocked.Add(ref _totalLatencyMs, (long)sw.Elapsed.TotalMilliseconds);

            return result;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Resets pipeline statistics.
    /// </summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _totalInferences, 0);
        Interlocked.Exchange(ref _totalLatencyMs, 0);
    }
}

#endregion
