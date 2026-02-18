using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.Benchmarks;

/// <summary>
/// Performance benchmarking suite for DataWarehouse.
/// Run with: dotnet run -c Release
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var config = DefaultConfig.Instance
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn(StatisticColumn.AllStatistics)
            .AddExporter(MarkdownExporter.GitHub)
            .AddJob(Job.Default
                .WithWarmupCount(3)
                .WithIterationCount(10));

        var summary = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}

/// <summary>
/// Benchmarks for storage operations.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class StorageBenchmarks
{
    private byte[] _smallData = null!; // Initialized in [GlobalSetup]
    private byte[] _mediumData = null!; // Initialized in [GlobalSetup]
    private byte[] _largeData = null!; // Initialized in [GlobalSetup]
    private ConcurrentDictionary<string, byte[]> _storage = null!; // Initialized in [GlobalSetup]

    [GlobalSetup]
    public void Setup()
    {
        _smallData = new byte[1024]; // 1 KB
        _mediumData = new byte[1024 * 1024]; // 1 MB
        _largeData = new byte[1024 * 1024 * 100]; // 100 MB

        RandomNumberGenerator.Fill(_smallData);
        RandomNumberGenerator.Fill(_mediumData);
        RandomNumberGenerator.Fill(_largeData);

        _storage = new ConcurrentDictionary<string, byte[]>();
    }

    [Benchmark(Description = "Write 1KB")]
    public void Write1KB()
    {
        var key = Guid.NewGuid().ToString();
        _storage[key] = _smallData.ToArray();
    }

    [Benchmark(Description = "Write 1MB")]
    public void Write1MB()
    {
        var key = Guid.NewGuid().ToString();
        _storage[key] = _mediumData.ToArray();
    }

    [Benchmark(Description = "Read 1KB")]
    public byte[]? Read1KB()
    {
        var key = "test-small";
        _storage[key] = _smallData;
        return _storage.TryGetValue(key, out var data) ? data : null;
    }

    [Benchmark(Description = "Read 1MB")]
    public byte[]? Read1MB()
    {
        var key = "test-medium";
        _storage[key] = _mediumData;
        return _storage.TryGetValue(key, out var data) ? data : null;
    }

    [Benchmark(Description = "Concurrent Writes (100)")]
    public void ConcurrentWrites()
    {
        Parallel.For(0, 100, i =>
        {
            var key = $"concurrent-{i}";
            _storage[key] = _smallData.ToArray();
        });
    }

    [Benchmark(Description = "Concurrent Reads (100)")]
    public void ConcurrentReads()
    {
        // Seed data
        for (int i = 0; i < 100; i++)
        {
            _storage[$"read-{i}"] = _smallData;
        }

        Parallel.For(0, 100, i =>
        {
            _storage.TryGetValue($"read-{i}", out _);
        });
    }
}

/// <summary>
/// Benchmarks for cryptographic operations.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class CryptoBenchmarks
{
    private byte[] _data1MB = null!; // Initialized in [GlobalSetup]
    private byte[] _key = null!; // Initialized in [GlobalSetup]
    private byte[] _iv = null!; // Initialized in [GlobalSetup]

    [GlobalSetup]
    public void Setup()
    {
        _data1MB = new byte[1024 * 1024];
        _key = new byte[32]; // AES-256
        _iv = new byte[16];

        RandomNumberGenerator.Fill(_data1MB);
        RandomNumberGenerator.Fill(_key);
        RandomNumberGenerator.Fill(_iv);
    }

    [Benchmark(Description = "SHA256 Hash 1MB")]
    public byte[] Sha256Hash()
    {
        return SHA256.HashData(_data1MB);
    }

    [Benchmark(Description = "SHA512 Hash 1MB")]
    public byte[] Sha512Hash()
    {
        return SHA512.HashData(_data1MB);
    }

    [Benchmark(Description = "AES Encrypt 1MB")]
    public byte[] AesEncrypt()
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(_data1MB, 0, _data1MB.Length);
    }

    [Benchmark(Description = "AES Decrypt 1MB")]
    public byte[] AesDecrypt()
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(_data1MB, 0, _data1MB.Length);

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }

    [Benchmark(Description = "Random Bytes Generation (1KB)")]
    public byte[] RandomBytes()
    {
        var bytes = new byte[1024];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}

/// <summary>
/// Benchmarks for serialization operations.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class SerializationBenchmarks
{
    private TestObject _simpleObject = null!; // Initialized in [GlobalSetup]
    private TestObject[] _objectArray = null!; // Initialized in [GlobalSetup]
    private string _jsonString = null!; // Initialized in [GlobalSetup]

    [GlobalSetup]
    public void Setup()
    {
        _simpleObject = new TestObject
        {
            Id = Guid.NewGuid(),
            Name = "Test Object",
            Value = 42.5,
            Timestamp = DateTime.UtcNow,
            Tags = new[] { "tag1", "tag2", "tag3" },
            Metadata = new Dictionary<string, object>
            {
                ["key1"] = "value1",
                ["key2"] = 123,
                ["key3"] = true
            }
        };

        _objectArray = Enumerable.Range(0, 1000).Select(i => new TestObject
        {
            Id = Guid.NewGuid(),
            Name = $"Object {i}",
            Value = i * 1.5,
            Timestamp = DateTime.UtcNow.AddMinutes(-i),
            Tags = new[] { $"tag-{i}" },
            Metadata = new Dictionary<string, object> { [$"key-{i}"] = i }
        }).ToArray();

        _jsonString = System.Text.Json.JsonSerializer.Serialize(_simpleObject);
    }

    [Benchmark(Description = "JSON Serialize Simple")]
    public string SerializeSimple()
    {
        return System.Text.Json.JsonSerializer.Serialize(_simpleObject);
    }

    [Benchmark(Description = "JSON Deserialize Simple")]
    public TestObject? DeserializeSimple()
    {
        return System.Text.Json.JsonSerializer.Deserialize<TestObject>(_jsonString);
    }

    [Benchmark(Description = "JSON Serialize Array (1000)")]
    public string SerializeArray()
    {
        return System.Text.Json.JsonSerializer.Serialize(_objectArray);
    }

    [Benchmark(Description = "MessagePack Serialize Simple")]
    public byte[] MessagePackSerialize()
    {
        // Using manual serialization as MessagePack NuGet may not be available
        using var ms = new MemoryStream(4096);
        using var writer = new BinaryWriter(ms);
        writer.Write(_simpleObject.Id.ToByteArray());
        writer.Write(_simpleObject.Name ?? "");
        writer.Write(_simpleObject.Value);
        writer.Write(_simpleObject.Timestamp.ToBinary());
        return ms.ToArray();
    }

    public class TestObject
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public string[]? Tags { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }
}

/// <summary>
/// Benchmarks for concurrent data structures.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class ConcurrencyBenchmarks
{
    private ConcurrentDictionary<int, int> _concurrentDict = null!; // Initialized in [GlobalSetup]
    private ConcurrentQueue<int> _concurrentQueue = null!; // Initialized in [GlobalSetup]
    private ConcurrentBag<int> _concurrentBag = null!; // Initialized in [GlobalSetup]

    [GlobalSetup]
    public void Setup()
    {
        _concurrentDict = new ConcurrentDictionary<int, int>();
        _concurrentQueue = new ConcurrentQueue<int>();
        _concurrentBag = new ConcurrentBag<int>();

        for (int i = 0; i < 10000; i++)
        {
            _concurrentDict[i] = i;
            _concurrentQueue.Enqueue(i);
            _concurrentBag.Add(i);
        }
    }

    [Benchmark(Description = "ConcurrentDict GetOrAdd (10K)")]
    public void ConcurrentDictGetOrAdd()
    {
        Parallel.For(0, 10000, i =>
        {
            _concurrentDict.GetOrAdd(i + 10000, k => k * 2);
        });
    }

    [Benchmark(Description = "ConcurrentQueue Enqueue/Dequeue (10K)")]
    public void ConcurrentQueueOps()
    {
        var queue = new ConcurrentQueue<int>();
        Parallel.For(0, 10000, i =>
        {
            queue.Enqueue(i);
            queue.TryDequeue(out _);
        });
    }

    [Benchmark(Description = "Channel Write/Read (10K)")]
    public async Task ChannelOps()
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<int>(1000);

        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < 10000; i++)
            {
                await channel.Writer.WriteAsync(i);
            }
            channel.Writer.Complete();
        });

        var reader = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                _ = item;
            }
        });

        await Task.WhenAll(writer, reader);
    }

    [Benchmark(Description = "SemaphoreSlim Contention (100 tasks)")]
    public async Task SemaphoreContention()
    {
        var semaphore = new SemaphoreSlim(10);
        var tasks = Enumerable.Range(0, 100).Select(async i =>
        {
            await semaphore.WaitAsync();
            try
            {
                await Task.Delay(1); // Simulate work
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// Benchmarks for memory allocation patterns.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class MemoryBenchmarks
{
    private ArrayPool<byte> _arrayPool = null!; // Initialized in [GlobalSetup]

    [GlobalSetup]
    public void Setup()
    {
        _arrayPool = ArrayPool<byte>.Shared;
    }

    [Benchmark(Baseline = true, Description = "New byte[] allocation (1MB)")]
    public byte[] NewAllocation()
    {
        return new byte[1024 * 1024];
    }

    [Benchmark(Description = "ArrayPool rent/return (1MB)")]
    public void ArrayPoolRentReturn()
    {
        var array = _arrayPool.Rent(1024 * 1024);
        _arrayPool.Return(array);
    }

    [Benchmark(Description = "Span<byte> stack alloc (1KB)")]
    public int SpanStackAlloc()
    {
        Span<byte> buffer = stackalloc byte[1024];
        buffer.Fill(0xFF);
        return buffer.Length;
    }

    [Benchmark(Description = "Memory<byte> operations")]
    public int MemoryOps()
    {
        Memory<byte> memory = new byte[1024];
        var slice = memory.Slice(0, 512);
        slice.Span.Fill(0xAA);
        return slice.Length;
    }

    [Benchmark(Description = "String concatenation (100 strings)")]
    public string StringConcat()
    {
        var result = "";
        for (int i = 0; i < 100; i++)
        {
            result += i.ToString();
        }
        return result;
    }

    [Benchmark(Description = "StringBuilder (100 strings)")]
    public string StringBuilder()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append(i);
        }
        return sb.ToString();
    }

    [Benchmark(Description = "String.Join (100 strings)")]
    public string StringJoin()
    {
        return string.Join("", Enumerable.Range(0, 100).Select(i => i.ToString()));
    }
}

/// <summary>
/// Benchmarks for compression operations.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class CompressionBenchmarks
{
    private byte[] _compressibleData = null!; // Initialized in [GlobalSetup]
    private byte[] _randomData = null!; // Initialized in [GlobalSetup]

    [GlobalSetup]
    public void Setup()
    {
        // Highly compressible data (repeated patterns)
        _compressibleData = new byte[1024 * 1024];
        for (int i = 0; i < _compressibleData.Length; i++)
        {
            _compressibleData[i] = (byte)(i % 256);
        }

        // Random data (less compressible)
        _randomData = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(_randomData);
    }

    [Benchmark(Description = "GZip Compress 1MB (compressible)")]
    public byte[] GzipCompressCompressible()
    {
        using var output = new MemoryStream(1024 * 1024);
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            gzip.Write(_compressibleData);
        }
        return output.ToArray();
    }

    [Benchmark(Description = "GZip Compress 1MB (random)")]
    public byte[] GzipCompressRandom()
    {
        using var output = new MemoryStream(1024 * 1024);
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            gzip.Write(_randomData);
        }
        return output.ToArray();
    }

    [Benchmark(Description = "Brotli Compress 1MB (compressible)")]
    public byte[] BrotliCompressCompressible()
    {
        using var output = new MemoryStream(1024 * 1024);
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            brotli.Write(_compressibleData);
        }
        return output.ToArray();
    }

    [Benchmark(Description = "Deflate Compress 1MB (compressible)")]
    public byte[] DeflateCompressCompressible()
    {
        using var output = new MemoryStream(1024 * 1024);
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            deflate.Write(_compressibleData);
        }
        return output.ToArray();
    }
}
