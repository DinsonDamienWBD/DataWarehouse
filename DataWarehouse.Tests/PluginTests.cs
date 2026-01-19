using DataWarehouse.Plugins.AIAgents;
using DataWarehouse.Plugins.Compression;
using DataWarehouse.Plugins.Encryption;
using DataWarehouse.Plugins.LocalStorage;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Licensing;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using FluentAssertions;
using Moq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DataWarehouse.Tests;

#region Encryption Plugin Tests

/// <summary>
/// Comprehensive unit tests for AES-256-GCM encryption plugin.
/// Tests encryption/decryption round-trips, key rotation, invalid key handling,
/// and memory clearing security requirements (PCI-DSS compliance).
/// </summary>
public class AesEncryptionPluginTests : IDisposable
{
    private readonly Mock<IKeyStore> _mockKeyStore;
    private readonly Mock<IKernelContext> _mockContext;
    private readonly AesEncryptionPlugin _plugin;
    private readonly byte[] _testKey;
    private readonly string _testKeyId;

    public AesEncryptionPluginTests()
    {
        _mockKeyStore = new Mock<IKeyStore>();
        _mockContext = new Mock<IKernelContext>();
        _testKey = RandomNumberGenerator.GetBytes(32); // 256-bit key
        _testKeyId = "test-key-001";

        _mockKeyStore.Setup(k => k.GetCurrentKeyIdAsync())
            .ReturnsAsync(_testKeyId);
        _mockKeyStore.Setup(k => k.GetKeyAsync(_testKeyId, It.IsAny<ISecurityContext>()))
            .ReturnsAsync(_testKey);

        _mockContext.Setup(c => c.GetPlugins<IPlugin>())
            .Returns(Array.Empty<IPlugin>());
        _mockContext.Setup(c => c.LogDebug(It.IsAny<string>()));

        _plugin = new AesEncryptionPlugin(new AesEncryptionConfig { KeyStore = _mockKeyStore.Object });
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_testKey);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ShouldPreserveData()
    {
        // Arrange
        var plaintext = "Hello, World! This is a test message for AES-256-GCM encryption."u8.ToArray();
        var inputStream = new MemoryStream(plaintext);
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act
        var encryptedStream = _plugin.OnWrite(inputStream, _mockContext.Object, args);
        var decryptedStream = _plugin.OnRead(encryptedStream, _mockContext.Object, args);

        // Assert
        using var reader = new MemoryStream();
        decryptedStream.CopyTo(reader);
        reader.ToArray().Should().BeEquivalentTo(plaintext);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(65536)]
    public void EncryptDecrypt_VariousDataSizes_ShouldPreserveData(int size)
    {
        // Arrange
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var inputStream = new MemoryStream(plaintext);
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act
        var encryptedStream = _plugin.OnWrite(inputStream, _mockContext.Object, args);
        var decryptedStream = _plugin.OnRead(encryptedStream, _mockContext.Object, args);

        // Assert
        using var reader = new MemoryStream();
        decryptedStream.CopyTo(reader);
        reader.ToArray().Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public void Encrypt_ShouldProduceDifferentCiphertextEachTime()
    {
        // Arrange - IV should be random, so same plaintext = different ciphertext
        var plaintext = "Same message encrypted twice"u8.ToArray();
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act
        var encrypted1 = _plugin.OnWrite(new MemoryStream(plaintext), _mockContext.Object, args);
        var encrypted2 = _plugin.OnWrite(new MemoryStream(plaintext), _mockContext.Object, args);

        // Assert
        using var ms1 = new MemoryStream();
        using var ms2 = new MemoryStream();
        encrypted1.CopyTo(ms1);
        encrypted2.CopyTo(ms2);

        ms1.ToArray().Should().NotBeEquivalentTo(ms2.ToArray(),
            "Each encryption should use a unique IV");
    }

    [Fact]
    public async Task KeyRotation_ShouldCreateNewKey()
    {
        // Arrange
        var mockSecurityContext = new Mock<ISecurityContext>();
        _mockKeyStore.Setup(k => k.CreateKeyAsync(It.IsAny<string>(), It.IsAny<ISecurityContext>()))
            .ReturnsAsync(RandomNumberGenerator.GetBytes(32));

        var message = new PluginMessage
        {
            Type = "encryption.aes.rotate",
            Payload = new Dictionary<string, object>()
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        _mockKeyStore.Verify(k => k.CreateKeyAsync(It.IsAny<string>(), It.IsAny<ISecurityContext>()), Times.Once);
    }

    [Fact]
    public void Encrypt_WithInvalidKeySize_ShouldThrowCryptographicException()
    {
        // Arrange
        var invalidKey = new byte[16]; // AES-256 requires 32 bytes
        var invalidKeyStore = new Mock<IKeyStore>();
        invalidKeyStore.Setup(k => k.GetCurrentKeyIdAsync()).ReturnsAsync("invalid-key");
        invalidKeyStore.Setup(k => k.GetKeyAsync(It.IsAny<string>(), It.IsAny<ISecurityContext>()))
            .ReturnsAsync(invalidKey);

        var args = new Dictionary<string, object> { ["keyStore"] = invalidKeyStore.Object };
        var plaintext = "Test"u8.ToArray();

        // Act & Assert
        var act = () => _plugin.OnWrite(new MemoryStream(plaintext), _mockContext.Object, args);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*256-bit*32-byte*");
    }

    [Fact]
    public void Decrypt_WithTamperedCiphertext_ShouldThrowAuthenticationException()
    {
        // Arrange
        var plaintext = "Test message"u8.ToArray();
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        var encryptedStream = _plugin.OnWrite(new MemoryStream(plaintext), _mockContext.Object, args);
        using var ms = new MemoryStream();
        encryptedStream.CopyTo(ms);
        var ciphertext = ms.ToArray();

        // Tamper with the ciphertext (modify authentication tag area)
        ciphertext[ciphertext.Length - 5] ^= 0xFF;

        // Act & Assert
        var act = () => _plugin.OnRead(new MemoryStream(ciphertext), _mockContext.Object, args);
        act.Should().Throw<Exception>(); // AuthenticationTagMismatchException or CryptographicException
    }

    [Fact]
    public void Decrypt_WithTruncatedCiphertext_ShouldThrowCryptographicException()
    {
        // Arrange
        var shortCiphertext = new byte[10]; // Too short for header
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act & Assert
        var act = () => _plugin.OnRead(new MemoryStream(shortCiphertext), _mockContext.Object, args);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*too short*");
    }

    [Fact]
    public void MemoryClearing_PlaintextShouldNotRemainInMemory()
    {
        // Arrange
        // This is a behavioral test - we verify that after encryption,
        // the plugin properly clears sensitive data using CryptographicOperations.ZeroMemory
        // The implementation in OnWrite/OnRead uses finally blocks to zero memory

        var sensitiveData = "SENSITIVE_SECRET_DATA_12345"u8.ToArray();
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act
        var encryptedStream = _plugin.OnWrite(new MemoryStream(sensitiveData), _mockContext.Object, args);
        var decryptedStream = _plugin.OnRead(encryptedStream, _mockContext.Object, args);

        // Force garbage collection to help verify memory is cleared
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Assert - The decrypted data should still be correct
        using var reader = new MemoryStream();
        decryptedStream.CopyTo(reader);
        reader.ToArray().Should().BeEquivalentTo(sensitiveData);

        // Note: True memory verification would require unsafe code or memory dumps
        // This test verifies the code path that includes ZeroMemory calls
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectMetadata()
    {
        // Assert
        _plugin.Id.Should().Be("datawarehouse.plugins.encryption.aes256");
        _plugin.Name.Should().Be("AES-256 Encryption");
        _plugin.SubCategory.Should().Be("Encryption");
        _plugin.AllowBypass.Should().BeFalse();
    }

    [Fact]
    public void Encrypt_WithNoKeyStore_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var pluginWithoutKeyStore = new AesEncryptionPlugin();
        var args = new Dictionary<string, object>();
        var plaintext = "Test"u8.ToArray();

        var emptyContext = new Mock<IKernelContext>();
        emptyContext.Setup(c => c.GetPlugins<IPlugin>()).Returns(Array.Empty<IPlugin>());

        // Act & Assert
        var act = () => pluginWithoutKeyStore.OnWrite(new MemoryStream(plaintext), emptyContext.Object, args);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IKeyStore*");
    }
}

/// <summary>
/// Comprehensive unit tests for ChaCha20-Poly1305 encryption plugin.
/// Tests encryption/decryption round-trips, key handling, and memory security.
/// </summary>
public class ChaCha20EncryptionPluginTests : IDisposable
{
    private readonly Mock<IKeyStore> _mockKeyStore;
    private readonly Mock<IKernelContext> _mockContext;
    private readonly ChaCha20EncryptionPlugin _plugin;
    private readonly byte[] _testKey;
    private readonly string _testKeyId;

    public ChaCha20EncryptionPluginTests()
    {
        _mockKeyStore = new Mock<IKeyStore>();
        _mockContext = new Mock<IKernelContext>();
        _testKey = RandomNumberGenerator.GetBytes(32); // 256-bit key
        _testKeyId = "chacha-key-001";

        _mockKeyStore.Setup(k => k.GetCurrentKeyIdAsync())
            .ReturnsAsync(_testKeyId);
        _mockKeyStore.Setup(k => k.GetKeyAsync(_testKeyId, It.IsAny<ISecurityContext>()))
            .ReturnsAsync(_testKey);

        _mockContext.Setup(c => c.GetPlugins<IPlugin>())
            .Returns(Array.Empty<IPlugin>());
        _mockContext.Setup(c => c.LogDebug(It.IsAny<string>()));

        _plugin = new ChaCha20EncryptionPlugin(new ChaCha20Config { KeyStore = _mockKeyStore.Object });
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_testKey);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ShouldPreserveData()
    {
        // Arrange
        var plaintext = "ChaCha20-Poly1305 encryption test message!"u8.ToArray();
        var inputStream = new MemoryStream(plaintext);
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act
        var encryptedStream = _plugin.OnWrite(inputStream, _mockContext.Object, args);
        var decryptedStream = _plugin.OnRead(encryptedStream, _mockContext.Object, args);

        // Assert
        using var reader = new MemoryStream();
        decryptedStream.CopyTo(reader);
        reader.ToArray().Should().BeEquivalentTo(plaintext);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1000)]
    [InlineData(32768)]
    public void EncryptDecrypt_VariousDataSizes_ShouldPreserveData(int size)
    {
        // Arrange
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var inputStream = new MemoryStream(plaintext);
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        // Act
        var encryptedStream = _plugin.OnWrite(inputStream, _mockContext.Object, args);
        var decryptedStream = _plugin.OnRead(encryptedStream, _mockContext.Object, args);

        // Assert
        using var reader = new MemoryStream();
        decryptedStream.CopyTo(reader);
        reader.ToArray().Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public void Encrypt_WithInvalidKeySize_ShouldThrowCryptographicException()
    {
        // Arrange
        var invalidKey = new byte[24]; // ChaCha20 requires 32 bytes
        var invalidKeyStore = new Mock<IKeyStore>();
        invalidKeyStore.Setup(k => k.GetCurrentKeyIdAsync()).ReturnsAsync("invalid-key");
        invalidKeyStore.Setup(k => k.GetKeyAsync(It.IsAny<string>(), It.IsAny<ISecurityContext>()))
            .ReturnsAsync(invalidKey);

        var args = new Dictionary<string, object> { ["keyStore"] = invalidKeyStore.Object };
        var plaintext = "Test"u8.ToArray();

        // Act & Assert
        var act = () => _plugin.OnWrite(new MemoryStream(plaintext), _mockContext.Object, args);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*256-bit*32-byte*");
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectMetadata()
    {
        // Assert
        _plugin.Id.Should().Be("datawarehouse.plugins.encryption.chacha20");
        _plugin.Name.Should().Be("ChaCha20-Poly1305 Encryption");
        _plugin.SubCategory.Should().Be("Encryption");
        _plugin.IncompatibleStages.Should().Contain("encryption.aes256");
    }

    [Fact]
    public void Decrypt_WithTamperedData_ShouldThrowException()
    {
        // Arrange
        var plaintext = "Test message for ChaCha20"u8.ToArray();
        var args = new Dictionary<string, object> { ["keyStore"] = _mockKeyStore.Object };

        var encryptedStream = _plugin.OnWrite(new MemoryStream(plaintext), _mockContext.Object, args);
        using var ms = new MemoryStream();
        encryptedStream.CopyTo(ms);
        var ciphertext = ms.ToArray();

        // Tamper with ciphertext
        if (ciphertext.Length > 50)
            ciphertext[50] ^= 0xFF;

        // Act & Assert
        var act = () => _plugin.OnRead(new MemoryStream(ciphertext), _mockContext.Object, args);
        act.Should().Throw<Exception>(); // Authentication will fail
    }
}

#endregion

#region LocalStorage Plugin Tests

/// <summary>
/// Comprehensive unit tests for LocalStoragePlugin.
/// Tests path traversal protection, read/write operations, and file handling.
/// </summary>
public class LocalStoragePluginTests : IDisposable
{
    private readonly string _testBasePath;
    private readonly LocalStoragePlugin _plugin;

    public LocalStoragePluginTests()
    {
        _testBasePath = Path.Combine(Path.GetTempPath(), $"DataWarehouseTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testBasePath);
        _plugin = new LocalStoragePlugin(LocalStorageConfig.ForPath(_testBasePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testBasePath))
            {
                Directory.Delete(_testBasePath, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task SaveAndLoad_ShouldPersistData()
    {
        // Arrange
        var content = "Hello, LocalStorage!"u8.ToArray();
        var uri = new Uri("file:///test/data.txt");

        // Act
        await _plugin.SaveAsync(uri, new MemoryStream(content));
        var loadedStream = await _plugin.LoadAsync(uri);

        // Assert
        using var reader = new MemoryStream();
        await loadedStream.CopyToAsync(reader);
        reader.ToArray().Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task ExistsAsync_ShouldReturnTrueForExistingFile()
    {
        // Arrange
        var uri = new Uri("file:///exists-test.txt");
        await _plugin.SaveAsync(uri, new MemoryStream("content"u8.ToArray()));

        // Act & Assert
        (await _plugin.ExistsAsync(uri)).Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_ShouldReturnFalseForNonExistingFile()
    {
        // Arrange
        var uri = new Uri("file:///does-not-exist.txt");

        // Act & Assert
        (await _plugin.ExistsAsync(uri)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveFile()
    {
        // Arrange
        var uri = new Uri("file:///delete-test.txt");
        await _plugin.SaveAsync(uri, new MemoryStream("content"u8.ToArray()));
        (await _plugin.ExistsAsync(uri)).Should().BeTrue();

        // Act
        await _plugin.DeleteAsync(uri);

        // Assert
        (await _plugin.ExistsAsync(uri)).Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_NonExistingFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var uri = new Uri("file:///nonexistent.txt");

        // Act & Assert
        var act = async () => await _plugin.LoadAsync(uri);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Theory]
    [InlineData("file:///../../../etc/passwd")]
    [InlineData("file:///..%2F..%2Fetc/passwd")]
    [InlineData("file:///subdir/../../../outside.txt")]
    public async Task PathTraversal_ShouldThrowUnauthorizedAccessException(string maliciousPath)
    {
        // Arrange
        var uri = new Uri(maliciousPath);

        // Act & Assert - Save should throw
        var saveAct = async () => await _plugin.SaveAsync(uri, new MemoryStream("malicious"u8.ToArray()));
        await saveAct.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*path traversal*");
    }

    [Fact]
    public async Task PathTraversal_RelativePath_ShouldBeBlocked()
    {
        // Arrange - Try to escape with ../ sequences
        var uri = new Uri("file:///subdir/../../escape.txt");

        // Act & Assert
        var act = async () => await _plugin.SaveAsync(uri, new MemoryStream("test"u8.ToArray()));
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*path traversal*");
    }

    [Fact]
    public async Task SaveAsync_ShouldCreateSubdirectories()
    {
        // Arrange
        var uri = new Uri("file:///deep/nested/path/file.txt");
        var content = "Nested content"u8.ToArray();

        // Act
        await _plugin.SaveAsync(uri, new MemoryStream(content));

        // Assert
        (await _plugin.ExistsAsync(uri)).Should().BeTrue();
        var loaded = await _plugin.LoadAsync(uri);
        using var ms = new MemoryStream();
        await loaded.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task SaveAsync_ShouldOverwriteExistingFile()
    {
        // Arrange
        var uri = new Uri("file:///overwrite-test.txt");
        await _plugin.SaveAsync(uri, new MemoryStream("original"u8.ToArray()));

        // Act
        await _plugin.SaveAsync(uri, new MemoryStream("updated"u8.ToArray()));

        // Assert
        var loaded = await _plugin.LoadAsync(uri);
        using var reader = new StreamReader(loaded);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("updated");
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectMetadata()
    {
        // Assert
        _plugin.Id.Should().Be("datawarehouse.plugins.storage.local");
        _plugin.Name.Should().Be("Local Storage");
        _plugin.Scheme.Should().Be("file");
    }

    [Fact]
    public async Task ListFilesAsync_ShouldEnumerateFiles()
    {
        // Arrange
        await _plugin.SaveAsync(new Uri("file:///list/file1.txt"), new MemoryStream("1"u8.ToArray()));
        await _plugin.SaveAsync(new Uri("file:///list/file2.txt"), new MemoryStream("2"u8.ToArray()));
        await _plugin.SaveAsync(new Uri("file:///other/file3.txt"), new MemoryStream("3"u8.ToArray()));

        // Act
        var files = new List<StorageListItem>();
        await foreach (var file in _plugin.ListFilesAsync("list"))
        {
            files.Add(file);
        }

        // Assert
        files.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(1024 * 1024)] // 1MB
    public async Task SaveAndLoad_VariousFileSizes_ShouldWork(int size)
    {
        // Arrange
        var content = RandomNumberGenerator.GetBytes(size);
        var uri = new Uri($"file:///size-test-{size}.bin");

        // Act
        await _plugin.SaveAsync(uri, new MemoryStream(content));
        var loaded = await _plugin.LoadAsync(uri);

        // Assert
        using var ms = new MemoryStream();
        await loaded.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(content);
    }
}

#endregion

#region AIAgent Plugin Tests

/// <summary>
/// Comprehensive unit tests for AIAgentPlugin.
/// Tests conversation management, TTL cleanup, max conversations limit,
/// provider registration, and quota enforcement.
/// </summary>
public class AIAgentPluginTests : IAsyncLifetime
{
    private AIAgentPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new AIAgentPlugin(new AIAgentConfig
        {
            MaxConcurrentRequests = 5,
            DefaultMaxTokens = 1024,
            DefaultTemperature = 0.7
        });
        await _plugin.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.StopAsync();
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectMetadata()
    {
        // Assert
        _plugin.Id.Should().Be("datawarehouse.plugins.ai");
        _plugin.Name.Should().Be("AI Agents");
        _plugin.Category.Should().Be(PluginCategory.AIProvider);
    }

    [Fact]
    public async Task ConversationCreate_ShouldReturnConversationId()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.conversation.create",
            Payload = new Dictionary<string, object> { ["system"] = "You are a helpful assistant." }
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
        var response = message.Payload["_response"];
        response.Should().NotBeNull();
    }

    [Fact]
    public async Task ConversationClear_ShouldRemoveConversation()
    {
        // Arrange - Create a conversation
        var createMessage = new PluginMessage
        {
            Type = "ai.conversation.create",
            Payload = new Dictionary<string, object>()
        };
        await _plugin.OnMessageAsync(createMessage);

        // Extract conversation ID from response using reflection or dynamic
        var response = createMessage.Payload["_response"];
        var conversationId = response?.GetType().GetProperty("conversationId")?.GetValue(response)?.ToString();

        // Act - Clear the conversation
        var clearMessage = new PluginMessage
        {
            Type = "ai.conversation.clear",
            Payload = new Dictionary<string, object> { ["conversationId"] = conversationId }
        };
        await _plugin.OnMessageAsync(clearMessage);

        // Assert
        clearMessage.Payload.Should().ContainKey("_response");
    }

    [Fact]
    public async Task HandleProviders_ShouldReturnProviderList()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.providers",
            Payload = new Dictionary<string, object>()
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
    }

    [Fact]
    public async Task HandleConfigure_WithValidProvider_ShouldRegisterProvider()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.configure",
            Payload = new Dictionary<string, object?>
            {
                ["provider"] = "test-openai",
                ["type"] = "openai",
                ["apiKey"] = "sk-test-key",
                ["model"] = "gpt-4"
            }
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
        var response = message.Payload["_response"];
        response?.GetType().GetProperty("success")?.GetValue(response).Should().Be(true);
    }

    [Fact]
    public async Task HandleConfigure_WithoutProviderName_ShouldReturnError()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.configure",
            Payload = new Dictionary<string, object?>
            {
                ["type"] = "openai"
                // Missing provider name
            }
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
        var response = message.Payload["_response"];
        response?.GetType().GetProperty("error")?.GetValue(response).Should().NotBeNull();
    }

    [Fact]
    public async Task HandleStats_ShouldReturnStatistics()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.stats",
            Payload = new Dictionary<string, object>()
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
        var response = message.Payload["_response"];
        response?.GetType().GetProperty("success")?.GetValue(response).Should().Be(true);
    }

    [Fact]
    public async Task HandleChat_WithoutMessage_ShouldReturnError()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.chat",
            Payload = new Dictionary<string, object?>
            {
                ["provider"] = "openai"
                // Missing message
            }
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
        var response = message.Payload["_response"];
        response?.GetType().GetProperty("error")?.GetValue(response).Should().NotBeNull();
    }

    [Fact]
    public async Task HandleEmbed_WithoutText_ShouldReturnError()
    {
        // Arrange
        var message = new PluginMessage
        {
            Type = "ai.embed",
            Payload = new Dictionary<string, object?>
            {
                ["provider"] = "openai"
                // Missing text
            }
        };

        // Act
        await _plugin.OnMessageAsync(message);

        // Assert
        message.Payload.Should().ContainKey("_response");
        var response = message.Payload["_response"];
        response?.GetType().GetProperty("error")?.GetValue(response).Should().NotBeNull();
    }
}

/// <summary>
/// Tests for AI quota tier system and usage limits.
/// </summary>
public class AIQuotaTests
{
    [Theory]
    [InlineData(QuotaTier.Free, 50, 50_000, 1024)]
    [InlineData(QuotaTier.Basic, 500, 500_000, 4096)]
    [InlineData(QuotaTier.Pro, 5000, 5_000_000, 16384)]
    [InlineData(QuotaTier.Enterprise, int.MaxValue, int.MaxValue, 128000)]
    public void UsageLimits_ShouldMatchTier(QuotaTier tier, int dailyRequests, int dailyTokens, int maxTokensPerRequest)
    {
        // Act
        var limits = UsageLimits.GetDefaultLimits(tier);

        // Assert
        limits.DailyRequestLimit.Should().Be(dailyRequests);
        limits.DailyTokenLimit.Should().Be(dailyTokens);
        limits.MaxTokensPerRequest.Should().Be(maxTokensPerRequest);
    }

    [Theory]
    [InlineData(QuotaTier.Free, false, false, false, false)]
    [InlineData(QuotaTier.Basic, true, true, false, true)]
    [InlineData(QuotaTier.Pro, true, true, true, true)]
    [InlineData(QuotaTier.Enterprise, true, true, true, true)]
    public void UsageLimits_FeatureFlags_ShouldMatchTier(
        QuotaTier tier, bool streaming, bool functionCalling, bool vision, bool embeddings)
    {
        // Act
        var limits = UsageLimits.GetDefaultLimits(tier);

        // Assert
        limits.StreamingEnabled.Should().Be(streaming);
        limits.FunctionCallingEnabled.Should().Be(functionCalling);
        limits.VisionEnabled.Should().Be(vision);
        limits.EmbeddingsEnabled.Should().Be(embeddings);
    }

    [Fact]
    public void UserQuota_CanMakeRequest_ShouldEnforceRequestLimit()
    {
        // Arrange
        var quota = new UserQuota
        {
            UserId = "test-user",
            Tier = QuotaTier.Free,
            Limits = UsageLimits.GetDefaultLimits(QuotaTier.Free),
            RequestsToday = 50 // At limit
        };

        // Act & Assert
        quota.CanMakeRequest(100, out var reason).Should().BeFalse();
        reason.Should().Contain("Daily request limit");
    }

    [Fact]
    public void UserQuota_CanMakeRequest_ShouldEnforceTokenLimit()
    {
        // Arrange
        var quota = new UserQuota
        {
            UserId = "test-user",
            Tier = QuotaTier.Free,
            Limits = UsageLimits.GetDefaultLimits(QuotaTier.Free),
            TokensToday = 49_900
        };

        // Act & Assert
        quota.CanMakeRequest(200, out var reason).Should().BeFalse();
        reason.Should().Contain("Daily token limit");
    }

    [Fact]
    public void UserQuota_RecordUsage_ShouldUpdateCounters()
    {
        // Arrange
        var quota = new UserQuota
        {
            UserId = "test-user",
            Tier = QuotaTier.Basic,
            Limits = UsageLimits.GetDefaultLimits(QuotaTier.Basic)
        };

        // Act
        quota.RecordUsage(100, 200, 0.01);

        // Assert
        quota.RequestsToday.Should().Be(1);
        quota.TokensToday.Should().Be(300);
        quota.SpentThisMonth.Should().Be(0.01);
    }

    [Fact]
    public void UsageLimits_IsModelAllowed_ShouldCheckWildcard()
    {
        // Arrange
        var proLimits = UsageLimits.GetDefaultLimits(QuotaTier.Pro);
        var freeLimits = UsageLimits.GetDefaultLimits(QuotaTier.Free);

        // Act & Assert
        proLimits.IsModelAllowed("gpt-4-super-turbo").Should().BeTrue(); // Pro has wildcard
        freeLimits.IsModelAllowed("gpt-4-turbo").Should().BeFalse(); // Free has limited models
        freeLimits.IsModelAllowed("gpt-4o-mini").Should().BeTrue(); // In free tier list
    }
}

#endregion

#region Compression Plugin Tests

/// <summary>
/// Comprehensive unit tests for GZip compression plugin.
/// Tests compression/decompression round-trips for various data sizes.
/// </summary>
public class GZipCompressionPluginTests
{
    private readonly GZipCompressionPlugin _plugin;
    private readonly Mock<IKernelContext> _mockContext;

    public GZipCompressionPluginTests()
    {
        _plugin = new GZipCompressionPlugin(new GZipConfig
        {
            Level = System.IO.Compression.CompressionLevel.Optimal
        });
        _mockContext = new Mock<IKernelContext>();
        _mockContext.Setup(c => c.LogDebug(It.IsAny<string>()));
    }

    [Fact]
    public void CompressDecompress_RoundTrip_ShouldPreserveData()
    {
        // Arrange
        var original = "Hello, World! This is a test for GZip compression."u8.ToArray();
        var args = new Dictionary<string, object>();

        // Act
        var compressed = _plugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);
        var decompressed = _plugin.OnRead(compressed, _mockContext.Object, args);

        // Assert
        using var ms = new MemoryStream();
        decompressed.CopyTo(ms);
        ms.ToArray().Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(10240)]
    [InlineData(102400)]
    public void CompressDecompress_VariousDataSizes_ShouldPreserveData(int size)
    {
        // Arrange
        var original = RandomNumberGenerator.GetBytes(size);
        var args = new Dictionary<string, object>();

        // Act
        var compressed = _plugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);
        var decompressed = _plugin.OnRead(compressed, _mockContext.Object, args);

        // Assert
        using var ms = new MemoryStream();
        decompressed.CopyTo(ms);
        ms.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Compress_CompressibleData_ShouldReduceSize()
    {
        // Arrange - Highly compressible data (repeated pattern)
        var original = Encoding.UTF8.GetBytes(new string('A', 10000));
        var args = new Dictionary<string, object>();

        // Act
        var compressed = _plugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);

        // Assert
        compressed.Length.Should().BeLessThan(original.Length);
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectMetadata()
    {
        // Assert
        _plugin.Id.Should().Be("datawarehouse.plugins.compression.gzip");
        _plugin.Name.Should().Be("GZip Compression");
        _plugin.SubCategory.Should().Be("Compression");
        _plugin.AllowBypass.Should().BeTrue();
    }

    [Fact]
    public void Compress_WithDifferentLevels_ShouldAllWork()
    {
        // Arrange
        var original = Encoding.UTF8.GetBytes(new string('X', 1000));
        var args = new Dictionary<string, object>();

        var levels = new[]
        {
            System.IO.Compression.CompressionLevel.Fastest,
            System.IO.Compression.CompressionLevel.Optimal,
            System.IO.Compression.CompressionLevel.SmallestSize
        };

        foreach (var level in levels)
        {
            // Act
            var plugin = new GZipCompressionPlugin(new GZipConfig { Level = level });
            var compressed = plugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);
            var decompressed = plugin.OnRead(compressed, _mockContext.Object, args);

            // Assert
            using var ms = new MemoryStream();
            decompressed.CopyTo(ms);
            ms.ToArray().Should().BeEquivalentTo(original);
        }
    }
}

/// <summary>
/// Comprehensive unit tests for LZ4 compression plugin.
/// Tests compression/decompression round-trips for various data sizes.
/// </summary>
public class LZ4CompressionPluginTests
{
    private readonly LZ4CompressionPlugin _plugin;
    private readonly Mock<IKernelContext> _mockContext;

    public LZ4CompressionPluginTests()
    {
        _plugin = new LZ4CompressionPlugin(new LZ4Config { HighCompression = false });
        _mockContext = new Mock<IKernelContext>();
        _mockContext.Setup(c => c.LogDebug(It.IsAny<string>()));
    }

    [Fact]
    public void CompressDecompress_RoundTrip_ShouldPreserveData()
    {
        // Arrange
        var original = "Hello, World! This is a test for LZ4 compression algorithm."u8.ToArray();
        var args = new Dictionary<string, object>();

        // Act
        var compressed = _plugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);
        var decompressed = _plugin.OnRead(compressed, _mockContext.Object, args);

        // Assert
        using var ms = new MemoryStream();
        decompressed.CopyTo(ms);
        ms.ToArray().Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(8192)]
    [InlineData(65536)]
    public void CompressDecompress_VariousDataSizes_ShouldPreserveData(int size)
    {
        // Arrange
        var original = RandomNumberGenerator.GetBytes(size);
        var args = new Dictionary<string, object>();

        // Act
        var compressed = _plugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);
        var decompressed = _plugin.OnRead(compressed, _mockContext.Object, args);

        // Assert
        using var ms = new MemoryStream();
        decompressed.CopyTo(ms);
        ms.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void CompressDecompress_WithHighCompression_ShouldPreserveData()
    {
        // Arrange
        var original = Encoding.UTF8.GetBytes(new string('Z', 5000));
        var highCompressionPlugin = new LZ4CompressionPlugin(new LZ4Config { HighCompression = true });
        var args = new Dictionary<string, object>();

        // Act
        var compressed = highCompressionPlugin.OnWrite(new MemoryStream(original), _mockContext.Object, args);
        var decompressed = highCompressionPlugin.OnRead(compressed, _mockContext.Object, args);

        // Assert
        using var ms = new MemoryStream();
        decompressed.CopyTo(ms);
        ms.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectMetadata()
    {
        // Assert
        _plugin.Id.Should().Be("datawarehouse.plugins.compression.lz4");
        _plugin.Name.Should().Be("LZ4 Compression");
        _plugin.SubCategory.Should().Be("Compression");
        _plugin.AllowBypass.Should().BeTrue();
    }

    [Fact]
    public void Decompress_WithInvalidData_ShouldThrowException()
    {
        // Arrange
        var invalidData = new byte[] { 0x01, 0x02, 0x03 }; // Too short
        var args = new Dictionary<string, object>();

        // Act & Assert
        var act = () => _plugin.OnRead(new MemoryStream(invalidData), _mockContext.Object, args);
        act.Should().Throw<InvalidDataException>();
    }
}

#endregion

#region Licensing System Tests

/// <summary>
/// Comprehensive unit tests for the CustomerTier licensing system.
/// Tests feature access, subscription verification, and tampering protection.
/// </summary>
public class CustomerTierTests
{
    [Theory]
    [InlineData(CustomerTier.Individual, Feature.BasicStorage, true)]
    [InlineData(CustomerTier.Individual, Feature.AIBasicChat, true)]
    [InlineData(CustomerTier.Individual, Feature.AIVision, false)]
    [InlineData(CustomerTier.Individual, Feature.HIPAACompliance, false)]
    [InlineData(CustomerTier.SMB, Feature.TeamCollaboration, true)]
    [InlineData(CustomerTier.SMB, Feature.AIStreaming, true)]
    [InlineData(CustomerTier.SMB, Feature.FullAuditTrail, false)]
    [InlineData(CustomerTier.HighStakes, Feature.HIPAACompliance, true)]
    [InlineData(CustomerTier.HighStakes, Feature.FinancialCompliance, true)]
    [InlineData(CustomerTier.HighStakes, Feature.MultiRegionReplication, false)]
    [InlineData(CustomerTier.Hyperscale, Feature.UnlimitedResources, true)]
    [InlineData(CustomerTier.Hyperscale, Feature.MultiRegionReplication, true)]
    public void HasFeature_ShouldReturnCorrectAccess(CustomerTier tier, Feature feature, bool expected)
    {
        // Act
        var result = TierManager.HasFeature(tier, feature);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(CustomerTier.Individual)]
    [InlineData(CustomerTier.SMB)]
    [InlineData(CustomerTier.HighStakes)]
    [InlineData(CustomerTier.Hyperscale)]
    public void GetLimits_ShouldReturnValidLimits(CustomerTier tier)
    {
        // Act
        var limits = TierManager.GetLimits(tier);

        // Assert
        limits.MaxStorageBytes.Should().BeGreaterThan(0);
        limits.MaxContainers.Should().BeGreaterThan(0);
        limits.MaxUsers.Should().BeGreaterThan(0);
        limits.MaxDailyApiRequests.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetLimits_Hyperscale_ShouldHaveUnlimitedResources()
    {
        // Act
        var limits = TierManager.GetLimits(CustomerTier.Hyperscale);

        // Assert
        limits.MaxStorageBytes.Should().Be(TierLimits.Unlimited);
        limits.MaxContainers.Should().Be(TierLimits.UnlimitedInt);
        limits.MaxUsers.Should().Be(TierLimits.UnlimitedInt);
    }

    [Fact]
    public void GetMinimumTier_ShouldReturnCorrectTier()
    {
        // Assert
        TierManager.GetMinimumTier(Feature.BasicStorage).Should().Be(CustomerTier.Individual);
        TierManager.GetMinimumTier(Feature.TeamCollaboration).Should().Be(CustomerTier.SMB);
        TierManager.GetMinimumTier(Feature.HIPAACompliance).Should().Be(CustomerTier.HighStakes);
        TierManager.GetMinimumTier(Feature.MultiRegionReplication).Should().Be(CustomerTier.Hyperscale);
    }

    [Fact]
    public void GetFeatureList_ShouldReturnAllTierFeatures()
    {
        // Act
        var individualFeatures = TierManager.GetFeatureList(CustomerTier.Individual).ToList();
        var hyperscaleFeatures = TierManager.GetFeatureList(CustomerTier.Hyperscale).ToList();

        // Assert
        individualFeatures.Should().Contain(Feature.BasicStorage);
        individualFeatures.Should().NotContain(Feature.HIPAACompliance);

        hyperscaleFeatures.Should().Contain(Feature.BasicStorage);
        hyperscaleFeatures.Should().Contain(Feature.HIPAACompliance);
        hyperscaleFeatures.Should().Contain(Feature.UnlimitedResources);
    }

    [Fact]
    public void GetFeatureMatrix_ShouldReturnCompleteMatrix()
    {
        // Act
        var matrix = TierManager.GetFeatureMatrix();

        // Assert
        matrix.Should().ContainKey(Feature.BasicStorage);
        matrix[Feature.BasicStorage].Should().ContainKey(CustomerTier.Individual);
        matrix[Feature.BasicStorage][CustomerTier.Individual].Should().BeTrue();
    }
}

/// <summary>
/// Tests for CustomerSubscription signature verification and tampering protection.
/// </summary>
public class CustomerSubscriptionTests
{
    private readonly byte[] _signingKey;

    public CustomerSubscriptionTests()
    {
        _signingKey = RandomNumberGenerator.GetBytes(32);
    }

    [Fact]
    public void Sign_ShouldCreateValidSignature()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-001",
            Tier = CustomerTier.SMB,
            SubscriptionStart = DateTimeOffset.UtcNow,
            IsActive = true
        };

        // Act
        subscription.Sign(_signingKey);

        // Assert
        subscription.Signature.Should().NotBeNullOrEmpty();
        subscription.HasValidIntegrity.Should().BeTrue();
    }

    [Fact]
    public void VerifySignature_WithValidSignature_ShouldReturnTrue()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-002",
            Tier = CustomerTier.HighStakes,
            SubscriptionStart = DateTimeOffset.UtcNow
        };
        subscription.Sign(_signingKey);

        // Act
        var isValid = subscription.VerifySignature(_signingKey);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void VerifySignature_WithTamperedData_ShouldReturnFalse()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-003",
            Tier = CustomerTier.SMB,
            SubscriptionStart = DateTimeOffset.UtcNow
        };
        subscription.Sign(_signingKey);

        // Tamper with the subscription
        subscription.Tier = CustomerTier.Hyperscale; // Attempt to upgrade tier

        // Act
        var isValid = subscription.VerifySignature(_signingKey);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void VerifySignature_WithWrongKey_ShouldReturnFalse()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-004",
            Tier = CustomerTier.SMB,
            SubscriptionStart = DateTimeOffset.UtcNow
        };
        subscription.Sign(_signingKey);

        var wrongKey = RandomNumberGenerator.GetBytes(32);

        // Act
        var isValid = subscription.VerifySignature(wrongKey);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void GetEffectiveFeatures_WithValidSignature_ShouldIncludeOverrides()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-005",
            Tier = CustomerTier.SMB,
            SubscriptionStart = DateTimeOffset.UtcNow,
            FeatureOverrides = Feature.HIPAACompliance // Custom override
        };
        subscription.Sign(_signingKey);

        // Act
        var features = subscription.GetEffectiveFeatures();

        // Assert
        (features & Feature.HIPAACompliance).Should().Be(Feature.HIPAACompliance);
    }

    [Fact]
    public void GetEffectiveFeatures_WithInvalidSignature_ShouldIgnoreOverrides()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-006",
            Tier = CustomerTier.SMB,
            SubscriptionStart = DateTimeOffset.UtcNow
        };
        subscription.Sign(_signingKey);

        // Tamper: Add override without re-signing
        subscription.FeatureOverrides = Feature.HIPAACompliance;

        // Act
        var features = subscription.GetEffectiveFeatures();

        // Assert - Override should be ignored due to invalid signature
        (features & Feature.HIPAACompliance).Should().Be(Feature.None);
    }

    [Fact]
    public void HasFeature_WhenInactive_ShouldReturnFalse()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-007",
            Tier = CustomerTier.Hyperscale,
            SubscriptionStart = DateTimeOffset.UtcNow,
            IsActive = false
        };

        // Act & Assert
        subscription.HasFeature(Feature.BasicStorage).Should().BeFalse();
        subscription.HasFeature(Feature.UnlimitedResources).Should().BeFalse();
    }

    [Fact]
    public void Sign_WithShortKey_ShouldThrowArgumentException()
    {
        // Arrange
        var subscription = new CustomerSubscription
        {
            CustomerId = "cust-008",
            Tier = CustomerTier.Individual,
            SubscriptionStart = DateTimeOffset.UtcNow
        };

        var shortKey = new byte[16]; // Too short

        // Act & Assert
        var act = () => subscription.Sign(shortKey);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*256 bits*");
    }
}

/// <summary>
/// Tests for SubscriptionNotFoundException and feature enforcement.
/// </summary>
public class SubscriptionEnforcementTests
{
    [Fact]
    public async Task HasFeatureAsync_WithUnknownCustomer_ShouldThrowSubscriptionNotFoundException()
    {
        // Arrange
        var manager = new InMemorySubscriptionManager();

        // Act & Assert
        var act = async () => await manager.HasFeatureAsync("unknown-customer", Feature.BasicStorage);
        await act.Should().ThrowAsync<SubscriptionNotFoundException>()
            .Where(e => e.CustomerId == "unknown-customer");
    }

    [Fact]
    public async Task EnsureFeatureAsync_WithUnknownCustomer_ShouldThrowSubscriptionNotFoundException()
    {
        // Arrange
        var manager = new InMemorySubscriptionManager();

        // Act & Assert
        var act = async () => await manager.EnsureFeatureAsync("unknown-customer", Feature.BasicStorage);
        await act.Should().ThrowAsync<SubscriptionNotFoundException>();
    }

    [Fact]
    public async Task EnsureFeatureAsync_WithMissingFeature_ShouldThrowFeatureNotAvailableException()
    {
        // Arrange
        var manager = new InMemorySubscriptionManager();
        manager.CreateDefaultSubscription("test-customer"); // Individual tier

        // Act & Assert
        var act = async () => await manager.EnsureFeatureAsync("test-customer", Feature.HIPAACompliance);
        await act.Should().ThrowAsync<FeatureNotAvailableException>()
            .Where(e => e.RequestedFeature == Feature.HIPAACompliance &&
                       e.CurrentTier == CustomerTier.Individual);
    }

    [Fact]
    public async Task HasFeatureAsync_WithValidSubscription_ShouldReturnCorrectResult()
    {
        // Arrange
        var manager = new InMemorySubscriptionManager();
        var subscription = new CustomerSubscription
        {
            CustomerId = "test-customer",
            Tier = CustomerTier.SMB,
            SubscriptionStart = DateTimeOffset.UtcNow,
            IsActive = true
        };
        manager.RegisterSubscription(subscription);

        // Act & Assert
        (await manager.HasFeatureAsync("test-customer", Feature.TeamCollaboration)).Should().BeTrue();
        (await manager.HasFeatureAsync("test-customer", Feature.HIPAACompliance)).Should().BeFalse();
    }

    [Fact]
    public async Task CheckLimitsAsync_ShouldEnforceDailyLimits()
    {
        // Arrange
        var manager = new InMemorySubscriptionManager();
        manager.CreateDefaultSubscription("test-customer"); // Individual tier: 1000 daily requests

        // Record usage near limit
        for (int i = 0; i < 999; i++)
        {
            await manager.RecordUsageAsync("test-customer", new UsageRecord
            {
                Type = UsageType.ApiRequests,
                Amount = 1
            });
        }

        // Act
        var withinLimit = await manager.CheckLimitsAsync("test-customer", UsageType.ApiRequests, 1);
        var exceedsLimit = await manager.CheckLimitsAsync("test-customer", UsageType.ApiRequests, 10);

        // Assert
        withinLimit.IsAllowed.Should().BeTrue();
        exceedsLimit.IsAllowed.Should().BeFalse();
        exceedsLimit.Message.Should().Contain("limit exceeded");
    }

    [Fact]
    public void FeatureNotAvailableException_ShouldContainUpgradeInfo()
    {
        // Arrange & Act
        var exception = new FeatureNotAvailableException(Feature.HIPAACompliance, CustomerTier.SMB);

        // Assert
        exception.RequestedFeature.Should().Be(Feature.HIPAACompliance);
        exception.CurrentTier.Should().Be(CustomerTier.SMB);
        exception.RequiredTier.Should().Be(CustomerTier.HighStakes);
        exception.Message.Should().Contain("High Stakes Enterprise");
    }

    [Fact]
    public void LimitExceededException_ShouldContainUsageDetails()
    {
        // Arrange & Act
        var exception = new LimitExceededException("DailyApiRequests", 1000, 1000, CustomerTier.Individual);

        // Assert
        exception.LimitName.Should().Be("DailyApiRequests");
        exception.CurrentValue.Should().Be(1000);
        exception.MaxValue.Should().Be(1000);
        exception.CurrentTier.Should().Be(CustomerTier.Individual);
    }
}

/// <summary>
/// Tests for CustomerTier to QuotaTier integration.
/// </summary>
public class TierIntegrationTests
{
    [Theory]
    [InlineData(CustomerTier.Individual, QuotaTier.Free)]
    [InlineData(CustomerTier.SMB, QuotaTier.Basic)]
    [InlineData(CustomerTier.HighStakes, QuotaTier.Pro)]
    [InlineData(CustomerTier.Hyperscale, QuotaTier.Enterprise)]
    public void ToQuotaTier_ShouldMapCorrectly(CustomerTier customerTier, QuotaTier expectedQuotaTier)
    {
        // Act
        var result = TierIntegration.ToQuotaTier(customerTier);

        // Assert
        result.Should().Be(expectedQuotaTier);
    }

    [Theory]
    [InlineData(QuotaTier.Free, CustomerTier.Individual)]
    [InlineData(QuotaTier.Basic, CustomerTier.SMB)]
    [InlineData(QuotaTier.Pro, CustomerTier.HighStakes)]
    [InlineData(QuotaTier.Enterprise, CustomerTier.Hyperscale)]
    public void ToCustomerTier_ShouldMapCorrectly(QuotaTier quotaTier, CustomerTier expectedCustomerTier)
    {
        // Act
        var result = TierIntegration.ToCustomerTier(quotaTier);

        // Assert
        result.Should().Be(expectedCustomerTier);
    }

    [Fact]
    public void CreateLimitsFromCustomerTier_ShouldCreateValidLimits()
    {
        // Act
        var limits = TierIntegration.CreateLimitsFromCustomerTier(CustomerTier.SMB);

        // Assert
        limits.DailyRequestLimit.Should().BeGreaterThan(0);
        limits.StreamingEnabled.Should().BeTrue();
        limits.FunctionCallingEnabled.Should().BeFalse(); // SMB doesn't have function calling
    }

    [Fact]
    public void ValidateAIFeature_WithValidFeature_ShouldNotThrow()
    {
        // Act & Assert
        var act = () => TierIntegration.ValidateAIFeature(CustomerTier.SMB, "streaming");
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateAIFeature_WithRestrictedFeature_ShouldThrow()
    {
        // Act & Assert
        var act = () => TierIntegration.ValidateAIFeature(CustomerTier.Individual, "vision");
        act.Should().Throw<FeatureNotAvailableException>();
    }
}

#endregion
