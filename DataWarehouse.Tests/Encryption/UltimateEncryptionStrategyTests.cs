using System.Security.Cryptography;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.Aead;
using DataWarehouse.SDK.Contracts.Encryption;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Encryption;

/// <summary>
/// Tests for UltimateEncryption strategy implementations.
/// Validates encrypt/decrypt roundtrips, key handling, and strategy metadata.
/// </summary>
[Trait("Category", "Unit")]
public class UltimateEncryptionStrategyTests
{
    private static byte[] GenerateKey(int bytes) => RandomNumberGenerator.GetBytes(bytes);
    private static byte[] GenerateTestData(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    #region AES-GCM Strategy

    [Fact]
    public void AesGcmStrategy_ShouldHaveCorrectMetadata()
    {
        var strategy = new AesGcmStrategy();
        strategy.StrategyId.Should().Be("aes-256-gcm");
        strategy.StrategyName.Should().Be("AES-256-GCM");
        strategy.CipherInfo.KeySizeBits.Should().Be(256);
        strategy.CipherInfo.Capabilities.IsAuthenticated.Should().BeTrue();
        strategy.CipherInfo.Capabilities.SupportsAead.Should().BeTrue();
    }

    [Fact]
    public async Task AesGcmStrategy_EncryptDecrypt_Roundtrip()
    {
        var strategy = new AesGcmStrategy();
        var key = GenerateKey(32); // 256-bit
        var plaintext = GenerateTestData(256);
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, cancellationToken: ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public async Task AesGcmStrategy_ShouldProduceDifferentCiphertexts()
    {
        var strategy = new AesGcmStrategy();
        var key = GenerateKey(32);
        var plaintext = GenerateTestData(128);
        var ct = TestContext.Current.CancellationToken;

        var ct1 = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var ct2 = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);

        ct1.Should().NotBeEquivalentTo(ct2, "each encryption should use a unique nonce");
    }

    [Fact]
    public async Task AesGcmStrategy_WithAssociatedData_ShouldRoundtrip()
    {
        var strategy = new AesGcmStrategy();
        var key = GenerateKey(32);
        var plaintext = GenerateTestData(64);
        var aad = System.Text.Encoding.UTF8.GetBytes("associated-context");
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, aad, ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, aad, ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public async Task AesGcmStrategy_WrongKey_ShouldThrow()
    {
        var strategy = new AesGcmStrategy();
        var key1 = GenerateKey(32);
        var key2 = GenerateKey(32);
        var plaintext = GenerateTestData(64);
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key1, cancellationToken: ct);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await strategy.DecryptAsync(ciphertext, key2, cancellationToken: ct));
    }

    #endregion

    #region AES-128-GCM Strategy

    [Fact]
    public void Aes128GcmStrategy_ShouldHaveCorrectKeySize()
    {
        var strategy = new Aes128GcmStrategy();
        strategy.CipherInfo.KeySizeBits.Should().Be(128);
    }

    [Fact]
    public async Task Aes128GcmStrategy_EncryptDecrypt_Roundtrip()
    {
        var strategy = new Aes128GcmStrategy();
        var key = GenerateKey(16); // 128-bit
        var plaintext = GenerateTestData(200);
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, cancellationToken: ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    #endregion

    #region AES-CBC Strategy

    [Fact]
    public void Aes256CbcStrategy_ShouldHaveCorrectMetadata()
    {
        var strategy = new Aes256CbcStrategy();
        strategy.CipherInfo.KeySizeBits.Should().Be(256);
        strategy.CipherInfo.AlgorithmName.Should().Contain("CBC");
    }

    [Fact]
    public async Task Aes256CbcStrategy_EncryptDecrypt_Roundtrip()
    {
        var strategy = new Aes256CbcStrategy();
        var key = GenerateKey(32);
        var plaintext = GenerateTestData(128);
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, cancellationToken: ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    #endregion

    #region AES-CTR Strategy

    [Fact]
    public void AesCtrStrategy_ShouldBeStreamable()
    {
        var strategy = new AesCtrStrategy();
        strategy.CipherInfo.Capabilities.IsStreamable.Should().BeTrue();
    }

    [Fact]
    public async Task AesCtrStrategy_EncryptDecrypt_Roundtrip()
    {
        var strategy = new AesCtrStrategy();
        var key = GenerateKey(32);
        var plaintext = GenerateTestData(300);
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, cancellationToken: ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    #endregion

    #region EncryptionStrategyBase Contract Tests

    [Fact]
    public void EncryptionStrategyBase_ShouldBeAbstract()
    {
        typeof(EncryptionStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void EncryptionStrategyBase_ShouldImplementIEncryptionStrategy()
    {
        typeof(EncryptionStrategyBase).GetInterfaces()
            .Should().Contain(typeof(IEncryptionStrategy));
    }

    [Fact]
    public void EncryptionStrategyBase_ShouldDefineRequiredProperties()
    {
        var type = typeof(EncryptionStrategyBase);
        type.GetProperty("StrategyId").Should().NotBeNull();
        type.GetProperty("StrategyName").Should().NotBeNull();
        type.GetProperty("CipherInfo").Should().NotBeNull();
    }

    [Fact]
    public async Task AesGcmStrategy_EmptyPlaintext_ShouldRoundtrip()
    {
        var strategy = new AesGcmStrategy();
        var key = GenerateKey(32);
        var plaintext = Array.Empty<byte>();
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, cancellationToken: ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public async Task AesGcmStrategy_LargeData_ShouldRoundtrip()
    {
        var strategy = new AesGcmStrategy();
        var key = GenerateKey(32);
        var plaintext = GenerateTestData(1024 * 100); // 100 KB
        var ct = TestContext.Current.CancellationToken;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(ciphertext, key, cancellationToken: ct);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    #endregion
}
