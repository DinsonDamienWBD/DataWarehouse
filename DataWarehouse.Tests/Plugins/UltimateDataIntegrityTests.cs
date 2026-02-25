using DataWarehouse.Plugins.UltimateDataIntegrity.Hashing;
using FluentAssertions;
using System.Text;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataIntegrityTests
{
    [Fact]
    public void Sha3_256Provider_ShouldHaveCorrectProperties()
    {
        var provider = new Sha3_256Provider();
        provider.AlgorithmName.Should().Be("SHA3-256");
        provider.HashSizeBytes.Should().Be(32);
    }

    [Fact]
    public void Sha3_256Provider_ShouldComputeConsistentHash()
    {
        var provider = new Sha3_256Provider();
        var data = Encoding.UTF8.GetBytes("Hello, World!");

        var hash1 = provider.ComputeHash(data);
        var hash2 = provider.ComputeHash(data);

        hash1.Should().HaveCount(32);
        hash1.Should().BeEquivalentTo(hash2, "same input should yield same hash");
    }

    [Fact]
    public void Sha3_256Provider_ShouldProduceDifferentHashForDifferentInput()
    {
        var provider = new Sha3_256Provider();
        var hash1 = provider.ComputeHash(Encoding.UTF8.GetBytes("input-a"));
        var hash2 = provider.ComputeHash(Encoding.UTF8.GetBytes("input-b"));

        hash1.Should().NotBeEquivalentTo(hash2);
    }

    [Fact]
    public void Sha3_384Provider_ShouldHaveCorrectProperties()
    {
        var provider = new Sha3_384Provider();
        provider.AlgorithmName.Should().Be("SHA3-384");
        provider.HashSizeBytes.Should().Be(48);

        var hash = provider.ComputeHash(Encoding.UTF8.GetBytes("test"));
        hash.Should().HaveCount(48);
    }

    [Fact]
    public void Sha3_512Provider_ShouldHaveCorrectProperties()
    {
        var provider = new Sha3_512Provider();
        provider.AlgorithmName.Should().Be("SHA3-512");
        provider.HashSizeBytes.Should().Be(64);

        var hash = provider.ComputeHash(Encoding.UTF8.GetBytes("test"));
        hash.Should().HaveCount(64);
    }

    [Fact]
    public void Keccak256Provider_ShouldHaveCorrectProperties()
    {
        var provider = new Keccak256Provider();
        provider.AlgorithmName.Should().Be("Keccak-256");
        provider.HashSizeBytes.Should().Be(32);

        var hash = provider.ComputeHash(Encoding.UTF8.GetBytes("ethereum"));
        hash.Should().HaveCount(32);
    }

    [Fact]
    public async Task Sha3_256Provider_ShouldComputeHashFromStreamAsync()
    {
        var provider = new Sha3_256Provider();
        var data = Encoding.UTF8.GetBytes("streaming data test");

        using var stream = new MemoryStream(data);
        var hashFromStream = await provider.ComputeHashAsync(stream);

        var hashFromSpan = provider.ComputeHash(data);

        hashFromStream.Should().BeEquivalentTo(hashFromSpan, "stream and span hash should match");
    }

    [Fact]
    public void Sha3_256Provider_ShouldComputeHashFromSyncStream()
    {
        var provider = new Sha3_256Provider();
        var data = Encoding.UTF8.GetBytes("sync stream test");

        using var stream = new MemoryStream(data);
        var hashFromStream = provider.ComputeHash(stream);

        var hashFromSpan = provider.ComputeHash(data);
        hashFromStream.Should().BeEquivalentTo(hashFromSpan);
    }

    [Fact]
    public void IHashProvider_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IHashProvider);
        iface.GetProperty("AlgorithmName").Should().NotBeNull();
        iface.GetProperty("HashSizeBytes").Should().NotBeNull();
        iface.GetMethod("ComputeHash", [typeof(ReadOnlySpan<byte>)]).Should().NotBeNull();
    }
}
