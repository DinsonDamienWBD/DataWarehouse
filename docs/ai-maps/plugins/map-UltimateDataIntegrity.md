# Plugin: UltimateDataIntegrity
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataIntegrity

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegrity/UltimateDataIntegrityPlugin.cs
```csharp
public sealed class UltimateDataIntegrityPlugin : IntegrityProviderPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override IReadOnlyList<HashAlgorithmType> SupportedAlgorithms { get; };
    public UltimateDataIntegrityPlugin();
    public UltimateDataIntegrityPlugin(ILogger<UltimateDataIntegrityPlugin> logger);
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override byte[] ComputeHashCore(ReadOnlySpan<byte> data, HashAlgorithmType algorithm);
    public override async Task OnMessageAsync(PluginMessage message);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataIntegrity/Hashing/HashProviders.cs
```csharp
public interface IHashProvider
{
}
    string AlgorithmName { get; }
    int HashSizeBytes { get; }
    byte[] ComputeHash(ReadOnlySpan<byte> data);;
    byte[] ComputeHash(Stream data);;
    Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);;
}
```
```csharp
internal static class HashProviderGuards
{
}
    internal const long MaxStreamSizeBytes = 10L * 1024 * 1024 * 1024;
    internal static void ValidateStream(Stream data, string algorithmName);
    internal static void ValidateStreamSize(Stream data, string algorithmName);
}
```
```csharp
public class Sha3_256Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Sha3_384Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Sha3_512Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Keccak256Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Keccak384Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Keccak512Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class HmacSha256Provider : IHashProvider
{
}
    public HmacSha256Provider(byte[] key);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class HmacSha384Provider : IHashProvider
{
}
    public HmacSha384Provider(byte[] key);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class HmacSha512Provider : IHashProvider
{
}
    public HmacSha512Provider(byte[] key);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class HmacSha3_256Provider : IHashProvider
{
}
    public HmacSha3_256Provider(byte[] key);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class HmacSha3_384Provider : IHashProvider
{
}
    public HmacSha3_384Provider(byte[] key);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class HmacSha3_512Provider : IHashProvider
{
}
    public HmacSha3_512Provider(byte[] key);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class SaltedHashProvider : IHashProvider
{
}
    public SaltedHashProvider(IHashProvider inner, byte[] salt);
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
private sealed class SaltedStream : Stream
{
}
    public SaltedStream(byte[] salt, Stream inner);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _saltPosition < _salt.Length ? _saltPosition : _salt.Length + _inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    public override void Flush();
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
}
```
```csharp
public static class HashProviderFactory
{
}
    public static IReadOnlyList<string> SupportedAlgorithms { get; };
    public static IHashProvider Create(string algorithm);
    public static IHashProvider CreateHmac(string algorithm, byte[] key);
    public static IHashProvider CreateSalted(string algorithm, byte[] salt);
    public static bool IsSupported(string algorithm);
}
```
```csharp
public class Sha256Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Sha384Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
```csharp
public class Sha512Provider : IHashProvider
{
}
    public string AlgorithmName;;
    public int HashSizeBytes;;
    public byte[] ComputeHash(ReadOnlySpan<byte> data);
    public byte[] ComputeHash(Stream data);
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```
