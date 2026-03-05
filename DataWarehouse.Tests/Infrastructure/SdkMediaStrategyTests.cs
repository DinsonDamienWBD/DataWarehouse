using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Media;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK media strategy infrastructure types:
    /// IMediaStrategy, MediaFormat, MediaCapabilities, Resolution, Bitrate,
    /// TranscodeOptions, MediaMetadata.
    /// </summary>
    public class SdkMediaStrategyTests
    {
        #region IMediaStrategy Interface Tests

        [Fact]
        public void IMediaStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IMediaStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(MediaCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IMediaStrategy_DefinesTranscodeAsyncMethod()
        {
            var method = typeof(IMediaStrategy).GetMethod("TranscodeAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<Stream>), method!.ReturnType);
        }

        [Fact]
        public void IMediaStrategy_DefinesExtractMetadataAsyncMethod()
        {
            var method = typeof(IMediaStrategy).GetMethod("ExtractMetadataAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<MediaMetadata>), method!.ReturnType);
        }

        [Fact]
        public void IMediaStrategy_DefinesGenerateThumbnailAsyncMethod()
        {
            var method = typeof(IMediaStrategy).GetMethod("GenerateThumbnailAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<Stream>), method!.ReturnType);
        }

        [Fact]
        public void IMediaStrategy_DefinesStreamAsyncMethod()
        {
            var method = typeof(IMediaStrategy).GetMethod("StreamAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<Uri>), method!.ReturnType);
        }

        #endregion

        #region MediaFormat Enum Tests

        [Fact]
        public void MediaFormat_ContainsVideoFormats()
        {
            var values = Enum.GetValues<MediaFormat>();
            Assert.Contains(MediaFormat.Mp4, values);
            Assert.Contains(MediaFormat.WebM, values);
            Assert.Contains(MediaFormat.Mkv, values);
            Assert.Contains(MediaFormat.Avi, values);
            Assert.Contains(MediaFormat.Mov, values);
        }

        [Fact]
        public void MediaFormat_ContainsStreamingFormats()
        {
            var values = Enum.GetValues<MediaFormat>();
            Assert.Contains(MediaFormat.Hls, values);
            Assert.Contains(MediaFormat.Dash, values);
        }

        [Fact]
        public void MediaFormat_ContainsAudioFormats()
        {
            var values = Enum.GetValues<MediaFormat>();
            Assert.Contains(MediaFormat.Mp3, values);
            Assert.Contains(MediaFormat.Aac, values);
            Assert.Contains(MediaFormat.Flac, values);
            Assert.Contains(MediaFormat.Wav, values);
            Assert.Contains(MediaFormat.Opus, values);
        }

        [Fact]
        public void MediaFormat_ContainsImageFormats()
        {
            var values = Enum.GetValues<MediaFormat>();
            Assert.Contains(MediaFormat.Jpeg, values);
            Assert.Contains(MediaFormat.Png, values);
            Assert.Contains(MediaFormat.WebP, values);
        }

        #endregion

        #region MediaCapabilities Tests

        [Fact]
        public void MediaCapabilities_DefaultConstructor_HasMinimalCapabilities()
        {
            var caps = new MediaCapabilities();

            Assert.Empty(caps.SupportedInputFormats);
            Assert.Empty(caps.SupportedOutputFormats);
            Assert.False(caps.SupportsStreaming);
            Assert.False(caps.SupportsAdaptiveBitrate);
            Assert.Null(caps.MaxResolution);
            Assert.Null(caps.MaxBitrate);
            Assert.Empty(caps.SupportedCodecs);
            Assert.False(caps.SupportsThumbnailGeneration);
            Assert.False(caps.SupportsMetadataExtraction);
            Assert.False(caps.SupportsHardwareAcceleration);
        }

        [Fact]
        public void MediaCapabilities_SupportsTranscode_ChecksBothFormats()
        {
            var inputFormats = new HashSet<MediaFormat> { MediaFormat.Mp4, MediaFormat.WebM };
            var outputFormats = new HashSet<MediaFormat> { MediaFormat.Hls, MediaFormat.Dash };

            var caps = new MediaCapabilities(
                SupportedInputFormats: inputFormats,
                SupportedOutputFormats: outputFormats,
                SupportsStreaming: true,
                SupportsAdaptiveBitrate: true,
                MaxResolution: Resolution.Uhd,
                MaxBitrate: 50_000_000,
                SupportedCodecs: new HashSet<string> { "h264", "h265" },
                SupportsThumbnailGeneration: true,
                SupportsMetadataExtraction: true,
                SupportsHardwareAcceleration: false);

            Assert.True(caps.SupportsTranscode(MediaFormat.Mp4, MediaFormat.Hls));
            Assert.False(caps.SupportsTranscode(MediaFormat.Avi, MediaFormat.Hls));
            Assert.False(caps.SupportsTranscode(MediaFormat.Mp4, MediaFormat.Mp3));
        }

        [Fact]
        public void MediaCapabilities_SupportsResolution_ChecksMaxResolution()
        {
            var caps = new MediaCapabilities(
                SupportedInputFormats: new HashSet<MediaFormat>(),
                SupportedOutputFormats: new HashSet<MediaFormat>(),
                SupportsStreaming: false,
                SupportsAdaptiveBitrate: false,
                MaxResolution: Resolution.FullHd,
                MaxBitrate: null,
                SupportedCodecs: new HashSet<string>(),
                SupportsThumbnailGeneration: false,
                SupportsMetadataExtraction: false,
                SupportsHardwareAcceleration: false);

            Assert.True(caps.SupportsResolution(Resolution.Hd));
            Assert.True(caps.SupportsResolution(Resolution.FullHd));
            Assert.False(caps.SupportsResolution(Resolution.Uhd));
        }

        [Fact]
        public void MediaCapabilities_SupportsBitrate_ChecksMaxBitrate()
        {
            var caps = new MediaCapabilities(
                SupportedInputFormats: new HashSet<MediaFormat>(),
                SupportedOutputFormats: new HashSet<MediaFormat>(),
                SupportsStreaming: false,
                SupportsAdaptiveBitrate: false,
                MaxResolution: null,
                MaxBitrate: 10_000_000,
                SupportedCodecs: new HashSet<string>(),
                SupportsThumbnailGeneration: false,
                SupportsMetadataExtraction: false,
                SupportsHardwareAcceleration: false);

            Assert.True(caps.SupportsBitrate(5_000_000));
            Assert.True(caps.SupportsBitrate(10_000_000));
            Assert.False(caps.SupportsBitrate(20_000_000));
        }

        [Fact]
        public void MediaCapabilities_SupportsCodec_IsCaseInsensitive()
        {
            var caps = new MediaCapabilities(
                SupportedInputFormats: new HashSet<MediaFormat>(),
                SupportedOutputFormats: new HashSet<MediaFormat>(),
                SupportsStreaming: false,
                SupportsAdaptiveBitrate: false,
                MaxResolution: null,
                MaxBitrate: null,
                SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "vp9" },
                SupportsThumbnailGeneration: false,
                SupportsMetadataExtraction: false,
                SupportsHardwareAcceleration: false);

            Assert.True(caps.SupportsCodec("h264"));
            Assert.True(caps.SupportsCodec("H264"));
            Assert.False(caps.SupportsCodec("av1"));
        }

        #endregion

        #region Resolution Tests

        [Fact]
        public void Resolution_StandardPresets_HaveCorrectDimensions()
        {
            Assert.Equal(640, Resolution.Sd.Width);
            Assert.Equal(480, Resolution.Sd.Height);
            Assert.Equal(1920, Resolution.FullHd.Width);
            Assert.Equal(1080, Resolution.FullHd.Height);
            Assert.Equal(3840, Resolution.Uhd.Width);
            Assert.Equal(2160, Resolution.Uhd.Height);
        }

        [Fact]
        public void Resolution_PixelCount_CalculatesCorrectly()
        {
            var res = new Resolution(1920, 1080);
            Assert.Equal(1920L * 1080, res.PixelCount);
        }

        [Fact]
        public void Resolution_AspectRatio_CalculatesCorrectly()
        {
            var res = new Resolution(1920, 1080);
            Assert.Equal(16.0 / 9.0, res.AspectRatio, 3);
        }

        [Fact]
        public void Resolution_ToString_FormatsAsWxH()
        {
            var res = new Resolution(3840, 2160);
            Assert.Equal("3840x2160", res.ToString());
        }

        #endregion

        #region Bitrate Tests

        [Fact]
        public void Bitrate_Kbps_ConvertsCorrectly()
        {
            var bitrate = new Bitrate(128_000);
            Assert.Equal(128.0, bitrate.Kbps);
        }

        [Fact]
        public void Bitrate_Mbps_ConvertsCorrectly()
        {
            var bitrate = new Bitrate(5_000_000);
            Assert.Equal(5.0, bitrate.Mbps);
        }

        [Fact]
        public void Bitrate_Presets_HaveCorrectValues()
        {
            Assert.Equal(128_000, Bitrate.AudioLow.BitsPerSecond);
            Assert.Equal(2_000_000, Bitrate.VideoSd.BitsPerSecond);
            Assert.Equal(25_000_000, Bitrate.Video4K.BitsPerSecond);
        }

        #endregion

        #region TranscodeOptions Tests

        [Fact]
        public void TranscodeOptions_Construction_SetsProperties()
        {
            var options = new TranscodeOptions(
                TargetFormat: MediaFormat.Hls,
                VideoCodec: "h265",
                AudioCodec: "aac",
                TargetResolution: Resolution.FullHd,
                TargetBitrate: Bitrate.VideoFullHd);

            Assert.Equal(MediaFormat.Hls, options.TargetFormat);
            Assert.Equal("h265", options.VideoCodec);
            Assert.Equal("aac", options.AudioCodec);
            Assert.Equal(Resolution.FullHd, options.TargetResolution);
            Assert.False(options.TwoPass);
        }

        #endregion

        #region MediaMetadata Tests

        [Fact]
        public void MediaMetadata_Construction_SetsProperties()
        {
            var metadata = new MediaMetadata(
                Duration: TimeSpan.FromMinutes(5),
                Format: MediaFormat.Mp4,
                VideoCodec: "h264",
                AudioCodec: "aac",
                Resolution: Resolution.FullHd,
                Bitrate: Bitrate.VideoFullHd,
                FrameRate: 30.0,
                AudioChannels: 2,
                SampleRate: 44100,
                FileSize: 50_000_000);

            Assert.Equal(TimeSpan.FromMinutes(5), metadata.Duration);
            Assert.Equal(MediaFormat.Mp4, metadata.Format);
            Assert.True(metadata.IsVideo);
            Assert.False(metadata.IsAudioOnly);
        }

        [Fact]
        public void MediaMetadata_AudioOnly_IsDetectedCorrectly()
        {
            var metadata = new MediaMetadata(
                Duration: TimeSpan.FromMinutes(3),
                Format: MediaFormat.Mp3,
                VideoCodec: null,
                AudioCodec: "mp3",
                Resolution: null,
                Bitrate: Bitrate.AudioStandard,
                FrameRate: null,
                AudioChannels: 2,
                SampleRate: 44100,
                FileSize: 5_000_000);

            Assert.False(metadata.IsVideo);
            Assert.True(metadata.IsAudioOnly);
        }

        #endregion
    }
}
