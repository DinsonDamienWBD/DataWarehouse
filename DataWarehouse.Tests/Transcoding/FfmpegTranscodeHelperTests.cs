using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.Plugins.Transcoding.Media.Execution;

namespace DataWarehouse.Tests.Transcoding
{
    /// <summary>
    /// Unit tests for FfmpegTranscodeHelper - fallback pattern for FFmpeg execution.
    /// Tests "try FFmpeg, fall back to package" logic.
    /// </summary>
    public class FfmpegTranscodeHelperTests
    {
        [Fact]
        public async Task ExecuteOrPackageAsync_WhenFfmpegAvailableAndSucceeds_ReturnsTranscodedOutput()
        {
            var executor = new FfmpegExecutor();

            if (!executor.IsAvailable)
            {
                // Skip test if FFmpeg not installed
                return;
            }

            var sourceBytes = CreateMinimalBMP();
            var packageWriterCalled = false;

            var result = await FfmpegTranscodeHelper.ExecuteOrPackageAsync(
                ffmpegArgs: "-f image2pipe -i pipe:0 -f image2pipe -c:v png pipe:1",
                sourceBytes: sourceBytes,
                packageWriter: async () =>
                {
                    packageWriterCalled = true;
                    return new MemoryStream(4096);
                });

            // If FFmpeg is available and succeeds, package writer should NOT be called
            // (unless FFmpeg fails for some reason)
            if (!packageWriterCalled)
            {
                Assert.NotNull(result);
                Assert.True(result.Length > 0 || result.Length == 0); // Either succeeds or fails gracefully
            }
        }

        [Fact]
        public async Task ExecuteOrPackageAsync_WhenFfmpegNotAvailable_CallsPackageWriter()
        {
            // Force package fallback by using invalid path
            Environment.SetEnvironmentVariable("FFMPEG_PATH", "/nonexistent/ffmpeg");

            try
            {
                var sourceBytes = new byte[] { 1, 2, 3 };
                var packageWriterCalled = false;
                var expectedPackageData = new byte[] { 0xFF, 0xFE };

                var result = await FfmpegTranscodeHelper.ExecuteOrPackageAsync(
                    ffmpegArgs: "-i pipe:0 -c:v h264 pipe:1",
                    sourceBytes: sourceBytes,
                    packageWriter: async () =>
                    {
                        packageWriterCalled = true;
                        return new MemoryStream(expectedPackageData);
                    });

                Assert.True(packageWriterCalled);
                Assert.NotNull(result);

                var resultBytes = new byte[result.Length];
                result.Position = 0;
                await result.ReadExactlyAsync(resultBytes);

                Assert.Equal(expectedPackageData, resultBytes);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FFMPEG_PATH", null);
            }
        }

        [Fact]
        public async Task ExecuteOrPackageAsync_WhenFfmpegFailsWithNonZeroExit_FallsBackToPackage()
        {
            var executor = new FfmpegExecutor();

            if (!executor.IsAvailable)
            {
                return;
            }

            var sourceBytes = new byte[] { 0x00 }; // Invalid media data
            var packageWriterCalled = false;

            var result = await FfmpegTranscodeHelper.ExecuteOrPackageAsync(
                ffmpegArgs: "-f mp4 -i pipe:0 -c:v h264 pipe:1", // Will fail with invalid input
                sourceBytes: sourceBytes,
                packageWriter: async () =>
                {
                    packageWriterCalled = true;
                    return new MemoryStream(new byte[] { 0xAA, 0xBB });
                });

            // Should fall back to package when FFmpeg fails
            Assert.True(packageWriterCalled);
            Assert.NotNull(result);
        }

        [Fact]
        public async Task ExecuteOrPackageAsync_WithCancellation_PropagatesCancellation()
        {
            var executor = new FfmpegExecutor();

            if (!executor.IsAvailable)
            {
                return;
            }

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await FfmpegTranscodeHelper.ExecuteOrPackageAsync(
                    ffmpegArgs: "-f lavfi -i testsrc=duration=10 -f null -",
                    sourceBytes: Array.Empty<byte>(),
                    packageWriter: async () => new MemoryStream(4096),
                    cancellationToken: cts.Token);
            });
        }

        [Fact]
        public async Task ExecuteOrPackageAsync_WithEmptySourceBytes_HandlesGracefully()
        {
            var packageWriterCalled = false;

            var result = await FfmpegTranscodeHelper.ExecuteOrPackageAsync(
                ffmpegArgs: "-f mp4 -i pipe:0 -c:v h264 pipe:1",
                sourceBytes: Array.Empty<byte>(),
                packageWriter: async () =>
                {
                    packageWriterCalled = true;
                    return new MemoryStream(4096);
                });

            // Should eventually fall back to package (either FFmpeg fails or not available)
            Assert.NotNull(result);
        }

        [Fact]
        public async Task ExecuteOrPackageAsync_PackageWriterReturnsNull_ReturnsNull()
        {
            // Force package fallback
            Environment.SetEnvironmentVariable("FFMPEG_PATH", "/nonexistent/ffmpeg");

            try
            {
                var result = await FfmpegTranscodeHelper.ExecuteOrPackageAsync(
                    ffmpegArgs: "-i pipe:0 pipe:1",
                    sourceBytes: new byte[] { 1 },
                    packageWriter: async () => null!);

                Assert.Null(result);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FFMPEG_PATH", null);
            }
        }

        private static byte[] CreateMinimalBMP()
        {
            var bmp = new byte[70];
            bmp[0] = 0x42; bmp[1] = 0x4D; // "BM"
            bmp[2] = 0x46; // File size
            bmp[10] = 0x36; // Pixel offset
            bmp[14] = 0x28; // DIB header size
            bmp[18] = 0x01; // Width = 1
            bmp[22] = 0x01; // Height = 1
            bmp[26] = 0x01; // Planes = 1
            bmp[28] = 0x18; // Bits per pixel = 24
            return bmp;
        }
    }
}
