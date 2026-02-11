using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DataWarehouse.Plugins.UltimateAccessControl.Strategies.Watermarking;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Security
{
    /// <summary>
    /// Tests for forensic watermarking with traitor tracing (T89).
    /// </summary>
    public class WatermarkingStrategyTests
    {
        private WatermarkingStrategy CreateStrategy()
        {
            var strategy = new WatermarkingStrategy();
            var config = new Dictionary<string, object>
            {
                ["SigningKeyBase64"] = Convert.ToBase64String(new byte[32]) // Use predictable key for testing
            };
            strategy.InitializeAsync(config).Wait();
            return strategy;
        }

        #region Watermark Generation Tests

        [Fact]
        public void GenerateWatermark_CreatesUniqueWatermark_WithRequiredFields()
        {
            // Arrange
            var strategy = CreateStrategy();

            // Act
            var watermark = strategy.GenerateWatermark("alice@company.com", "document-123");

            // Assert
            watermark.Should().NotBeNull();
            watermark.WatermarkId.Should().NotBeNullOrEmpty();
            watermark.WatermarkData.Should().NotBeNull();
            watermark.WatermarkData.Length.Should().Be(32); // 256-bit watermark
            watermark.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void GenerateWatermark_CreatesDistinctWatermarks_ForSameUser()
        {
            // Arrange
            var strategy = CreateStrategy();

            // Act
            var watermark1 = strategy.GenerateWatermark("alice@company.com", "document-123");
            var watermark2 = strategy.GenerateWatermark("alice@company.com", "document-123");

            // Assert
            watermark1.WatermarkId.Should().NotBe(watermark2.WatermarkId);
            watermark1.WatermarkData.Should().NotBeEquivalentTo(watermark2.WatermarkData);
        }

        [Fact]
        public void GenerateWatermark_StoresWatermarkRecord_ForLaterTracing()
        {
            // Arrange
            var strategy = CreateStrategy();

            // Act
            var watermark = strategy.GenerateWatermark("bob@company.com", "sensitive-file");
            var traitorInfo = strategy.TraceWatermark(watermark);

            // Assert
            traitorInfo.Should().NotBeNull();
            traitorInfo!.UserId.Should().Be("bob@company.com");
            traitorInfo.ResourceId.Should().Be("sensitive-file");
        }

        #endregion

        #region Binary Embedding Tests

        [Fact]
        public void EmbedInBinary_EmbedsWatermark_InPdfData()
        {
            // Arrange
            var strategy = CreateStrategy();
            var pdfData = GenerateSyntheticPdf(1024);
            var watermark = strategy.GenerateWatermark("charlie@company.com", "confidential.pdf");

            // Act
            var watermarkedData = strategy.EmbedInBinary(pdfData, watermark);

            // Assert
            watermarkedData.Should().NotBeNull();
            watermarkedData.Length.Should().Be(pdfData.Length);
        }

        [Fact]
        public void EmbedInBinary_EmbedsWatermark_InImageData()
        {
            // Arrange
            var strategy = CreateStrategy();
            var imageData = GenerateSyntheticImage(2048);
            var watermark = strategy.GenerateWatermark("diana@company.com", "blueprint.png");

            // Act
            var watermarkedData = strategy.EmbedInBinary(imageData, watermark);

            // Assert
            watermarkedData.Should().NotBeNull();
            watermarkedData.Length.Should().Be(imageData.Length);
        }

        [Fact]
        public void EmbedInBinary_PreservesDataSize()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalData = new byte[4096];
            new Random(42).NextBytes(originalData);
            var watermark = strategy.GenerateWatermark("user@company.com", "resource");

            // Act
            var watermarkedData = strategy.EmbedInBinary(originalData, watermark);

            // Assert
            watermarkedData.Length.Should().Be(originalData.Length);
        }

        [Fact]
        public void EmbedInBinary_ThrowsException_WhenDataTooSmall()
        {
            // Arrange
            var strategy = CreateStrategy();
            var tooSmallData = new byte[100]; // Less than 256 bytes required
            var watermark = strategy.GenerateWatermark("user@company.com", "resource");

            // Act & Assert
            var act = () => strategy.EmbedInBinary(tooSmallData, watermark);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("Data too small for watermarking*");
        }

        [Fact]
        public void ExtractFromBinary_RecoversExactWatermark()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalData = new byte[4096];
            new Random(42).NextBytes(originalData);
            var originalWatermark = strategy.GenerateWatermark("eve@company.com", "secret-doc");

            // Act
            var watermarkedData = strategy.EmbedInBinary(originalData, originalWatermark);
            var extractedWatermark = strategy.ExtractFromBinary(watermarkedData);

            // Assert
            extractedWatermark.Should().NotBeNull();
            extractedWatermark!.WatermarkId.Should().Be(originalWatermark.WatermarkId);
            extractedWatermark.WatermarkData.Should().BeEquivalentTo(originalWatermark.WatermarkData);
        }

        #endregion

        #region Text Embedding Tests

        [Fact]
        public void EmbedInText_EmbedsWatermark_UsingZeroWidthCharacters()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalText = "This is a confidential document containing sensitive information about the project timeline.";
            var watermark = strategy.GenerateWatermark("frank@company.com", "text-document");

            // Act
            var watermarkedText = strategy.EmbedInText(originalText, watermark);

            // Assert
            watermarkedText.Should().NotBeNull();
            watermarkedText.Length.Should().BeGreaterThan(originalText.Length); // Contains zero-width chars
        }

        [Fact]
        public void EmbedInText_PreservesVisibleText()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalText = "The quick brown fox jumps over the lazy dog.";
            var watermark = strategy.GenerateWatermark("grace@company.com", "article");

            // Act
            var watermarkedText = strategy.EmbedInText(originalText, watermark);
            var visibleText = RemoveZeroWidthCharacters(watermarkedText);

            // Assert
            visibleText.Should().Be(originalText);
        }

        [Fact]
        public void ExtractFromText_RecoversExactWatermark()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalText = "Confidential memo regarding Q4 strategy and revenue projections for next fiscal year.";
            var originalWatermark = strategy.GenerateWatermark("helen@company.com", "memo");

            // Act
            var watermarkedText = strategy.EmbedInText(originalText, originalWatermark);
            var extractedWatermark = strategy.ExtractFromText(watermarkedText);

            // Assert
            extractedWatermark.Should().NotBeNull();
            extractedWatermark!.WatermarkId.Should().Be(originalWatermark.WatermarkId);
            extractedWatermark.WatermarkData.Should().BeEquivalentTo(originalWatermark.WatermarkData);
        }

        [Fact]
        public void ExtractFromText_SurvivesCopyPaste()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalText = "Copy and paste this text to another document.";
            var originalWatermark = strategy.GenerateWatermark("ivan@company.com", "copied-text");

            // Act
            var watermarkedText = strategy.EmbedInText(originalText, originalWatermark);
            // Simulate copy-paste (zero-width characters are preserved in most text operations)
            var copiedText = new string(watermarkedText.ToCharArray());
            var extractedWatermark = strategy.ExtractFromText(copiedText);

            // Assert
            extractedWatermark.Should().NotBeNull();
            extractedWatermark!.WatermarkId.Should().Be(originalWatermark.WatermarkId);
        }

        #endregion

        #region Watermark Robustness Tests

        [Fact]
        public void ExtractFromBinary_ReturnsNull_WhenWatermarkCorrupted()
        {
            // Arrange
            var strategy = CreateStrategy();
            var originalData = new byte[4096];
            new Random(42).NextBytes(originalData);
            var watermark = strategy.GenerateWatermark("user@company.com", "resource");

            // Act
            var watermarkedData = strategy.EmbedInBinary(originalData, watermark);
            // Corrupt the watermarked data heavily
            for (int i = 0; i < watermarkedData.Length; i += 10)
            {
                watermarkedData[i] ^= 0xFF; // Flip all bits every 10 bytes
            }

            var extractedWatermark = strategy.ExtractFromBinary(watermarkedData);

            // Assert
            extractedWatermark.Should().BeNull();
        }

        [Fact]
        public void ExtractFromText_ReturnsNull_WhenNoWatermarkPresent()
        {
            // Arrange
            var strategy = CreateStrategy();
            var plainText = "This text has no watermark embedded in it.";

            // Act
            var extractedWatermark = strategy.ExtractFromText(plainText);

            // Assert
            extractedWatermark.Should().BeNull();
        }

        [Fact]
        public void ExtractFromBinary_ReturnsNull_WhenDataTooSmall()
        {
            // Arrange
            var strategy = CreateStrategy();
            var tooSmallData = new byte[100];

            // Act
            var extractedWatermark = strategy.ExtractFromBinary(tooSmallData);

            // Assert
            extractedWatermark.Should().BeNull();
        }

        #endregion

        #region Traitor Tracing Tests

        [Fact]
        public void TraceWatermark_IdentifiesCorrectUser()
        {
            // Arrange
            var strategy = CreateStrategy();
            var expectedUserId = "judy@company.com";
            var expectedResourceId = "financial-report.xlsx";
            var watermark = strategy.GenerateWatermark(expectedUserId, expectedResourceId);

            // Act
            var traitorInfo = strategy.TraceWatermark(watermark);

            // Assert
            traitorInfo.Should().NotBeNull();
            traitorInfo!.UserId.Should().Be(expectedUserId);
            traitorInfo.ResourceId.Should().Be(expectedResourceId);
        }

        [Fact]
        public void TraceWatermark_ReturnsAccessTimestamp()
        {
            // Arrange
            var strategy = CreateStrategy();
            var watermark = strategy.GenerateWatermark("karl@company.com", "report");

            // Act
            var traitorInfo = strategy.TraceWatermark(watermark);

            // Assert
            traitorInfo.Should().NotBeNull();
            traitorInfo!.AccessTimestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void TraceWatermark_ReturnsNull_ForUnregisteredWatermark()
        {
            // Arrange
            var strategy = CreateStrategy();
            var fakeWatermark = new WatermarkInfo
            {
                WatermarkId = Guid.NewGuid().ToString(),
                WatermarkData = new byte[32],
                CreatedAt = DateTime.UtcNow
            };

            // Act
            var traitorInfo = strategy.TraceWatermark(fakeWatermark);

            // Assert
            traitorInfo.Should().BeNull();
        }

        #endregion

        #region Collision Resistance Tests

        [Fact]
        public void GenerateWatermark_ProducesDifferentWatermarks_ForDifferentUsers()
        {
            // Arrange
            var strategy = CreateStrategy();

            // Act
            var watermark1 = strategy.GenerateWatermark("user1@company.com", "resource");
            var watermark2 = strategy.GenerateWatermark("user2@company.com", "resource");

            // Assert
            watermark1.WatermarkData.Should().NotBeEquivalentTo(watermark2.WatermarkData);
        }

        [Fact]
        public void WatermarkRegistry_HandlesLargeNumberOfWatermarks()
        {
            // Arrange
            var strategy = CreateStrategy();
            var watermarks = new List<WatermarkInfo>();

            // Act - Generate 1000 watermarks
            for (int i = 0; i < 1000; i++)
            {
                var watermark = strategy.GenerateWatermark($"user{i}@company.com", $"resource{i}");
                watermarks.Add(watermark);
            }

            // Assert - All can be traced back
            foreach (var watermark in watermarks)
            {
                var traitorInfo = strategy.TraceWatermark(watermark);
                traitorInfo.Should().NotBeNull();
            }
        }

        #endregion

        #region False Positive Tests

        [Fact]
        public void ExtractFromBinary_DoesNotProduceFalsePositive_OnRandomData()
        {
            // Arrange
            var strategy = CreateStrategy();
            var randomData = new byte[4096];

            // Act & Assert - Test 100 random samples
            int falsePositives = 0;
            for (int i = 0; i < 100; i++)
            {
                new Random(i).NextBytes(randomData);
                var extracted = strategy.ExtractFromBinary(randomData);
                if (extracted != null)
                {
                    falsePositives++;
                }
            }

            // False positive rate should be 0%
            falsePositives.Should().Be(0);
        }

        [Fact]
        public void ExtractFromText_DoesNotProduceFalsePositive_OnPlainText()
        {
            // Arrange
            var strategy = CreateStrategy();
            var plainTexts = new[]
            {
                "This is plain text without any watermark.",
                "Another document with no embedded information.",
                "Just regular content here, nothing hidden.",
                "No zero-width characters in this text.",
                "Standard ASCII text only."
            };

            // Act & Assert
            foreach (var text in plainTexts)
            {
                var extracted = strategy.ExtractFromText(text);
                extracted.Should().BeNull();
            }
        }

        #endregion

        #region End-to-End Leak Scenario

        [Fact]
        public async Task EndToEnd_LeakScenario_TraceWatermarkBackToUser()
        {
            // Arrange - Setup strategy
            var strategy = CreateStrategy();

            // Step 1: Generate watermark for Alice accessing confidential PDF
            var userId = "alice@company.com";
            var resourceId = "Q4-financial-projections.pdf";
            var metadata = new Dictionary<string, object>
            {
                ["ClientIp"] = "192.168.1.100",
                ["AccessTime"] = DateTime.UtcNow.ToString("o"),
                ["Department"] = "Finance"
            };

            var watermark = strategy.GenerateWatermark(userId, resourceId, metadata);

            // Step 2: Embed watermark in confidential PDF document
            var confidentialPdf = GenerateSyntheticPdf(8192);
            var watermarkedPdf = strategy.EmbedInBinary(confidentialPdf, watermark);

            // Step 3: Distribute watermarked document to Alice
            await Task.Delay(10); // Simulate distribution

            // Step 4: Simulate leak - document appears on external site
            var leakedDocument = watermarkedPdf;

            // Step 5: Extract watermark from leaked document
            var extractedWatermark = strategy.ExtractFromBinary(leakedDocument);

            // Step 6: Trace watermark back to source user
            var traitorInfo = strategy.TraceWatermark(extractedWatermark!);

            // Assert - Successfully identified Alice as the source
            traitorInfo.Should().NotBeNull();
            traitorInfo!.UserId.Should().Be(userId);
            traitorInfo.ResourceId.Should().Be(resourceId);
            traitorInfo.Metadata["ClientIp"].Should().Be("192.168.1.100");
            traitorInfo.Metadata["Department"].Should().Be("Finance");
            traitorInfo.AccessTimestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        #endregion

        #region Helper Methods

        private byte[] GenerateSyntheticPdf(int size)
        {
            var data = new byte[size];
            // PDF header magic bytes
            var header = Encoding.ASCII.GetBytes("%PDF-1.7\n");
            Array.Copy(header, data, Math.Min(header.Length, size));

            // Fill rest with pseudo-random content
            var rng = new Random(42);
            for (int i = header.Length; i < size; i++)
            {
                data[i] = (byte)rng.Next(32, 127); // Printable ASCII
            }

            return data;
        }

        private byte[] GenerateSyntheticImage(int size)
        {
            var data = new byte[size];
            // PNG header magic bytes
            data[0] = 0x89;
            data[1] = 0x50; // 'P'
            data[2] = 0x4E; // 'N'
            data[3] = 0x47; // 'G'
            data[4] = 0x0D;
            data[5] = 0x0A;
            data[6] = 0x1A;
            data[7] = 0x0A;

            // Fill rest with pseudo-random pixel data
            var rng = new Random(43);
            rng.NextBytes(data.AsSpan(8));

            return data;
        }

        private string RemoveZeroWidthCharacters(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (c != '\u200B' && c != '\u200C')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        #endregion
    }
}
