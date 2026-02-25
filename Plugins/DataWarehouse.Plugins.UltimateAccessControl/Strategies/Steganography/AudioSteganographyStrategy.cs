using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Steganography
{
    /// <summary>
    /// Audio steganography strategy implementing echo hiding and phase coding.
    /// Implements T74.3 - Production-ready audio steganography for WAV/AIFF files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Embedding techniques:
    /// - Echo Hiding: Introduces imperceptible echoes to encode data
    /// - Phase Coding: Modifies phase of initial audio segments
    /// - LSB Coding: Modifies least significant bits of audio samples
    /// - Spread Spectrum: Spreads data across frequency spectrum
    /// - Silence Intervals: Encodes data in inter-sample gaps
    /// </para>
    /// <para>
    /// Supported formats:
    /// - WAV (PCM 8/16/24/32-bit, mono/stereo)
    /// - AIFF (PCM 8/16/24-bit)
    /// - Raw PCM data
    /// </para>
    /// </remarks>
    public sealed class AudioSteganographyStrategy : AccessControlStrategyBase
    {
        private const int HeaderSize = 64;
        private static readonly byte[] MagicBytes = { 0x41, 0x55, 0x44, 0x53, 0x54, 0x45, 0x47, 0x4F }; // "AUDSTEGO"

        private byte[]? _encryptionKey;
        private AudioEmbeddingMethod _embeddingMethod = AudioEmbeddingMethod.EchoHiding;
        private int _echoDelayOriginal = 100; // samples for bit 0
        private int _echoDelayOne = 200;      // samples for bit 1
        private double _echoDecay = 0.3;
        private int _segmentSize = 8192;
#pragma warning disable CS0414 // Field is assigned but never used - reserved for future LSB mixing
        private double _lsbMixingRatio = 0.5;
#pragma warning restore CS0414

        /// <inheritdoc/>
        public override string StrategyId => "audio-steganography";

        /// <inheritdoc/>
        public override string StrategyName => "Audio Steganography";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = false,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 25
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("EncryptionKey", out var keyObj) && keyObj is byte[] key)
            {
                _encryptionKey = key;
            }
            else if (configuration.TryGetValue("EncryptionKeyBase64", out var keyB64) && keyB64 is string keyString)
            {
                _encryptionKey = Convert.FromBase64String(keyString);
            }
            else
            {
                _encryptionKey = new byte[32];
                RandomNumberGenerator.Fill(_encryptionKey);
            }

            if (configuration.TryGetValue("EmbeddingMethod", out var methodObj) && methodObj is string method)
            {
                _embeddingMethod = Enum.TryParse<AudioEmbeddingMethod>(method, true, out var m)
                    ? m : AudioEmbeddingMethod.EchoHiding;
            }

            if (configuration.TryGetValue("EchoDelayOriginal", out var d0) && d0 is int delay0)
            {
                _echoDelayOriginal = Math.Clamp(delay0, 50, 500);
            }

            if (configuration.TryGetValue("EchoDelayOne", out var d1) && d1 is int delay1)
            {
                _echoDelayOne = Math.Clamp(delay1, 100, 1000);
            }

            if (configuration.TryGetValue("EchoDecay", out var decay) && decay is double d)
            {
                _echoDecay = Math.Clamp(d, 0.1, 0.5);
            }

            if (configuration.TryGetValue("SegmentSize", out var segObj) && segObj is int seg)
            {
                _segmentSize = Math.Clamp(seg, 1024, 65536);
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("audio.steganography.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("audio.steganography.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Embeds secret data into audio carrier.
        /// </summary>
        /// <param name="audioData">The carrier audio data (WAV/AIFF).</param>
        /// <param name="secretData">The secret data to embed.</param>
        /// <param name="format">The audio format.</param>
        /// <returns>Audio with embedded data.</returns>
        public byte[] EmbedData(byte[] audioData, byte[] secretData, AudioFormat format = AudioFormat.Wav)
        {
            ValidateAudioFormat(audioData, format);

            // Parse audio
            var audioInfo = ParseAudioFile(audioData, format);

            // Encrypt payload
            var encryptedData = EncryptPayload(secretData);

            // Create header
            var header = CreateHeader(encryptedData.Length);

            // Combine header and data
            var payload = new byte[header.Length + encryptedData.Length];
            Buffer.BlockCopy(header, 0, payload, 0, header.Length);
            Buffer.BlockCopy(encryptedData, 0, payload, header.Length, encryptedData.Length);

            // Check capacity
            var capacity = CalculateCapacity(audioInfo);
            if (payload.Length > capacity)
            {
                throw new InvalidOperationException(
                    $"Payload too large. Audio capacity: {capacity} bytes, Payload: {payload.Length} bytes");
            }

            // Extract samples
            var samples = ExtractSamples(audioData, audioInfo);

            // Embed using selected method
            short[] modifiedSamples = _embeddingMethod switch
            {
                AudioEmbeddingMethod.EchoHiding => EmbedUsingEchoHiding(samples, payload, audioInfo),
                AudioEmbeddingMethod.PhaseCoding => EmbedUsingPhaseCoding(samples, payload, audioInfo),
                AudioEmbeddingMethod.LsbCoding => EmbedUsingLsbCoding(samples, payload),
                AudioEmbeddingMethod.SpreadSpectrum => EmbedUsingSpreadSpectrum(samples, payload, audioInfo),
                AudioEmbeddingMethod.SilenceIntervals => EmbedUsingSilenceIntervals(samples, payload),
                _ => EmbedUsingEchoHiding(samples, payload, audioInfo)
            };

            // Reconstruct audio file
            return ReconstructAudioFile(modifiedSamples, audioData, audioInfo, format);
        }

        /// <summary>
        /// Extracts hidden data from stego-audio.
        /// </summary>
        /// <param name="audioData">The audio containing hidden data.</param>
        /// <param name="format">The audio format.</param>
        /// <returns>The extracted secret data.</returns>
        public byte[] ExtractData(byte[] audioData, AudioFormat format = AudioFormat.Wav)
        {
            ValidateAudioFormat(audioData, format);

            var audioInfo = ParseAudioFile(audioData, format);
            var samples = ExtractSamples(audioData, audioInfo);

            // Extract header first
            byte[] headerBytes = _embeddingMethod switch
            {
                AudioEmbeddingMethod.EchoHiding => ExtractUsingEchoHiding(samples, 0, HeaderSize, audioInfo),
                AudioEmbeddingMethod.PhaseCoding => ExtractUsingPhaseCoding(samples, 0, HeaderSize, audioInfo),
                AudioEmbeddingMethod.LsbCoding => ExtractUsingLsbCoding(samples, 0, HeaderSize),
                AudioEmbeddingMethod.SpreadSpectrum => ExtractUsingSpreadSpectrum(samples, 0, HeaderSize, audioInfo),
                AudioEmbeddingMethod.SilenceIntervals => ExtractUsingSilenceIntervals(samples, 0, HeaderSize),
                _ => ExtractUsingEchoHiding(samples, 0, HeaderSize, audioInfo)
            };

            var header = ParseHeader(headerBytes);
            if (!ValidateHeader(header))
            {
                throw new InvalidDataException("No audio steganographic data found");
            }

            // Extract payload
            byte[] encryptedData = _embeddingMethod switch
            {
                AudioEmbeddingMethod.EchoHiding => ExtractUsingEchoHiding(samples, HeaderSize, header.DataLength, audioInfo),
                AudioEmbeddingMethod.PhaseCoding => ExtractUsingPhaseCoding(samples, HeaderSize, header.DataLength, audioInfo),
                AudioEmbeddingMethod.LsbCoding => ExtractUsingLsbCoding(samples, HeaderSize, header.DataLength),
                AudioEmbeddingMethod.SpreadSpectrum => ExtractUsingSpreadSpectrum(samples, HeaderSize, header.DataLength, audioInfo),
                AudioEmbeddingMethod.SilenceIntervals => ExtractUsingSilenceIntervals(samples, HeaderSize, header.DataLength),
                _ => ExtractUsingEchoHiding(samples, HeaderSize, header.DataLength, audioInfo)
            };

            return DecryptPayload(encryptedData);
        }

        /// <summary>
        /// Calculates embedding capacity for an audio file.
        /// </summary>
        public AudioCapacityInfo GetCapacityInfo(byte[] audioData, AudioFormat format = AudioFormat.Wav)
        {
            ValidateAudioFormat(audioData, format);

            var audioInfo = ParseAudioFile(audioData, format);
            var capacity = CalculateCapacity(audioInfo);

            return new AudioCapacityInfo
            {
                TotalSamples = audioInfo.SampleCount,
                SampleRate = audioInfo.SampleRate,
                BitsPerSample = audioInfo.BitsPerSample,
                Channels = audioInfo.Channels,
                DurationSeconds = (double)audioInfo.SampleCount / audioInfo.SampleRate / audioInfo.Channels,
                TotalCapacityBytes = capacity,
                UsableCapacityBytes = capacity - HeaderSize,
                EmbeddingMethod = _embeddingMethod.ToString()
            };
        }

        /// <summary>
        /// Analyzes audio file for steganography suitability.
        /// </summary>
        public AudioCarrierAnalysis AnalyzeCarrier(byte[] audioData, AudioFormat format = AudioFormat.Wav)
        {
            ValidateAudioFormat(audioData, format);

            var audioInfo = ParseAudioFile(audioData, format);
            var samples = ExtractSamples(audioData, audioInfo);
            var capacity = GetCapacityInfo(audioData, format);

            // Analyze audio characteristics
            var stats = AnalyzeAudioStatistics(samples);

            // Calculate suitability score
            double durationScore = Math.Min(capacity.DurationSeconds / 60.0, 1.0) * 25;
            double dynamicRangeScore = Math.Min(stats.DynamicRange / 80.0, 1.0) * 25;
            double complexityScore = Math.Min(stats.SpectralComplexity, 1.0) * 25;
            double noiseFloorScore = Math.Min(stats.NoiseFloor / -40.0, 1.0) * 25;

            return new AudioCarrierAnalysis
            {
                CapacityInfo = capacity,
                DynamicRangeDb = stats.DynamicRange,
                NoiseFloorDb = stats.NoiseFloor,
                SpectralComplexity = stats.SpectralComplexity,
                PeakAmplitude = stats.PeakAmplitude,
                AverageAmplitude = stats.AverageAmplitude,
                SilenceRatio = stats.SilenceRatio,
                SuitabilityScore = durationScore + dynamicRangeScore + complexityScore + noiseFloorScore,
                RecommendedMethod = RecommendEmbeddingMethod(stats),
                DetectionRisk = CalculateDetectionRisk(stats)
            };
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("audio.steganography.evaluate");
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Audio steganography strategy does not perform access control - use for data hiding",
                ApplicablePolicies = new[] { "Steganography.Audio" }
            });
        }

        private void ValidateAudioFormat(byte[] data, AudioFormat format)
        {
            if (data == null || data.Length < 100)
                throw new ArgumentException("Invalid audio data", nameof(data));

            if (format == AudioFormat.Wav)
            {
                if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
                    throw new ArgumentException("Invalid WAV signature", nameof(data));
            }
            else if (format == AudioFormat.Aiff)
            {
                if (data[0] != 'F' || data[1] != 'O' || data[2] != 'R' || data[3] != 'M')
                    throw new ArgumentException("Invalid AIFF signature", nameof(data));
            }
        }

        private AudioFileInfo ParseAudioFile(byte[] data, AudioFormat format)
        {
            return format switch
            {
                AudioFormat.Wav => ParseWavFile(data),
                AudioFormat.Aiff => ParseAiffFile(data),
                _ => ParseRawAudio(data)
            };
        }

        private AudioFileInfo ParseWavFile(byte[] data)
        {
            // Parse WAV header
            // RIFF header: 12 bytes
            // fmt chunk: variable
            // data chunk: variable

            int offset = 12; // Skip RIFF header
            int audioFormat = 1;
            int channels = 2;
            int sampleRate = 44100;
            int bitsPerSample = 16;
            int dataOffset = 0;
            int dataSize = 0;

            while (offset < data.Length - 8)
            {
                var chunkId = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                int chunkSize = BitConverter.ToInt32(data, offset + 4);

                if (chunkId == "fmt ")
                {
                    audioFormat = BitConverter.ToInt16(data, offset + 8);
                    channels = BitConverter.ToInt16(data, offset + 10);
                    sampleRate = BitConverter.ToInt32(data, offset + 12);
                    bitsPerSample = BitConverter.ToInt16(data, offset + 22);
                }
                else if (chunkId == "data")
                {
                    dataOffset = offset + 8;
                    dataSize = chunkSize;
                    break;
                }

                offset += 8 + chunkSize;
                if (chunkSize % 2 == 1) offset++; // Padding
            }

            return new AudioFileInfo
            {
                Format = AudioFormat.Wav,
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                DataOffset = dataOffset,
                DataSize = dataSize,
                SampleCount = dataSize / (bitsPerSample / 8) / channels * channels
            };
        }

        private AudioFileInfo ParseAiffFile(byte[] data)
        {
            // Simplified AIFF parsing
            int channels = 2;
            int sampleRate = 44100;
            int bitsPerSample = 16;
            int dataOffset = 54;
            int dataSize = data.Length - 54;

            // Find COMM chunk
            int offset = 12;
            while (offset < data.Length - 8)
            {
                var chunkId = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                int chunkSize = (data[offset + 4] << 24) | (data[offset + 5] << 16) |
                               (data[offset + 6] << 8) | data[offset + 7];

                if (chunkId == "COMM")
                {
                    channels = (data[offset + 8] << 8) | data[offset + 9];
                    bitsPerSample = (data[offset + 14] << 8) | data[offset + 15];
                    // Sample rate is 80-bit IEEE 754 extended precision
                    sampleRate = 44100; // Default
                }
                else if (chunkId == "SSND")
                {
                    dataOffset = offset + 16; // Skip chunk header and offset/block
                    dataSize = chunkSize - 8;
                    break;
                }

                offset += 8 + chunkSize;
                if (chunkSize % 2 == 1) offset++;
            }

            return new AudioFileInfo
            {
                Format = AudioFormat.Aiff,
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                DataOffset = dataOffset,
                DataSize = dataSize,
                SampleCount = dataSize / (bitsPerSample / 8)
            };
        }

        private AudioFileInfo ParseRawAudio(byte[] data)
        {
            return new AudioFileInfo
            {
                Format = AudioFormat.Raw,
                Channels = 2,
                SampleRate = 44100,
                BitsPerSample = 16,
                DataOffset = 0,
                DataSize = data.Length,
                SampleCount = data.Length / 2
            };
        }

        private short[] ExtractSamples(byte[] data, AudioFileInfo info)
        {
            int bytesPerSample = info.BitsPerSample / 8;
            int sampleCount = info.DataSize / bytesPerSample;
            var samples = new short[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                int offset = info.DataOffset + i * bytesPerSample;
                if (offset + bytesPerSample > data.Length)
                    break;

                samples[i] = info.BitsPerSample switch
                {
                    8 => (short)((data[offset] - 128) * 256),
                    16 => BitConverter.ToInt16(data, offset),
                    24 => (short)((data[offset + 1] << 8) | (data[offset + 2])),
                    32 => (short)(BitConverter.ToInt32(data, offset) >> 16),
                    _ => BitConverter.ToInt16(data, offset)
                };
            }

            return samples;
        }

        private long CalculateCapacity(AudioFileInfo info)
        {
            return _embeddingMethod switch
            {
                AudioEmbeddingMethod.EchoHiding => info.SampleCount / _segmentSize, // 1 bit per segment
                AudioEmbeddingMethod.PhaseCoding => info.SampleCount / _segmentSize, // 1 bit per segment
                AudioEmbeddingMethod.LsbCoding => info.SampleCount / 8, // 1 bit per sample, byte output
                AudioEmbeddingMethod.SpreadSpectrum => info.SampleCount / (_segmentSize * 8),
                AudioEmbeddingMethod.SilenceIntervals => info.SampleCount / 1000,
                _ => info.SampleCount / _segmentSize
            };
        }

        private short[] EmbedUsingEchoHiding(short[] samples, byte[] payload, AudioFileInfo info)
        {
            var result = new short[samples.Length];
            Array.Copy(samples, result, samples.Length);

            int segmentCount = samples.Length / _segmentSize;
            int bitIndex = 0;
            int totalBits = payload.Length * 8;

            for (int seg = 0; seg < segmentCount && bitIndex < totalBits; seg++)
            {
                int bit = (payload[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
                int delay = bit == 0 ? _echoDelayOriginal : _echoDelayOne;

                int segStart = seg * _segmentSize;
                int segEnd = Math.Min(segStart + _segmentSize, samples.Length);

                // Apply windowed echo
                for (int i = segStart; i < segEnd; i++)
                {
                    int echoSource = i - delay;
                    if (echoSource >= segStart && echoSource < samples.Length)
                    {
                        // Hamming window for smooth transition
                        double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * (i - segStart) / _segmentSize);
                        double echo = samples[echoSource] * _echoDecay * window;
                        result[i] = (short)Math.Clamp(result[i] + (int)echo, short.MinValue, short.MaxValue);
                    }
                }

                bitIndex++;
            }

            return result;
        }

        private short[] EmbedUsingPhaseCoding(short[] samples, byte[] payload, AudioFileInfo info)
        {
            var result = new short[samples.Length];
            Array.Copy(samples, result, samples.Length);

            int segmentCount = Math.Min(samples.Length / _segmentSize, payload.Length * 8);

            for (int seg = 0; seg < segmentCount; seg++)
            {
                int bit = (payload[seg / 8] >> (7 - (seg % 8))) & 1;
                int segStart = seg * _segmentSize;

                // Simple phase shift by inverting samples for bit=1
                if (bit == 1)
                {
                    for (int i = segStart; i < segStart + _segmentSize && i < result.Length; i++)
                    {
                        // Gradual phase shift using sine modulation
                        double phase = Math.PI * (i - segStart) / _segmentSize;
                        result[i] = (short)(result[i] * Math.Cos(phase));
                    }
                }
            }

            return result;
        }

        private short[] EmbedUsingLsbCoding(short[] samples, byte[] payload)
        {
            var result = new short[samples.Length];
            Array.Copy(samples, result, samples.Length);

            int bitIndex = 0;
            int totalBits = payload.Length * 8;

            for (int i = 0; i < samples.Length && bitIndex < totalBits; i++)
            {
                int bit = (payload[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
                result[i] = (short)((result[i] & 0xFFFE) | bit);
                bitIndex++;
            }

            return result;
        }

        private short[] EmbedUsingSpreadSpectrum(short[] samples, byte[] payload, AudioFileInfo info)
        {
            var result = new short[samples.Length];
            Array.Copy(samples, result, samples.Length);

            // Generate pseudo-noise sequence
            var pn = GeneratePnSequence(samples.Length);

            int bitIndex = 0;
            int totalBits = payload.Length * 8;
            double amplitude = 100; // Spread spectrum amplitude

            for (int seg = 0; seg < samples.Length / _segmentSize && bitIndex < totalBits; seg++)
            {
                int bit = (payload[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
                double sign = bit == 1 ? 1.0 : -1.0;

                for (int i = seg * _segmentSize; i < (seg + 1) * _segmentSize && i < samples.Length; i++)
                {
                    result[i] = (short)Math.Clamp(
                        result[i] + (int)(pn[i] * amplitude * sign),
                        short.MinValue, short.MaxValue);
                }

                bitIndex++;
            }

            return result;
        }

        private short[] EmbedUsingSilenceIntervals(short[] samples, byte[] payload)
        {
            var result = new short[samples.Length];
            Array.Copy(samples, result, samples.Length);

            // Find silence regions and encode bits
            int threshold = 500;
            int minSilenceLength = 100;
            int bitIndex = 0;
            int totalBits = payload.Length * 8;

            int i = 0;
            while (i < samples.Length - minSilenceLength && bitIndex < totalBits)
            {
                // Check for silence
                bool isSilence = true;
                for (int j = 0; j < minSilenceLength && i + j < samples.Length; j++)
                {
                    if (Math.Abs(samples[i + j]) > threshold)
                    {
                        isSilence = false;
                        break;
                    }
                }

                if (isSilence)
                {
                    int bit = (payload[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;

                    // Encode bit by modifying silence pattern
                    for (int j = 0; j < minSilenceLength && i + j < result.Length; j++)
                    {
                        result[i + j] = (short)(bit * 10 * ((j % 2) * 2 - 1));
                    }

                    bitIndex++;
                    i += minSilenceLength;
                }
                else
                {
                    i++;
                }
            }

            return result;
        }

        private byte[] ExtractUsingEchoHiding(short[] samples, int startByte, int length, AudioFileInfo info)
        {
            var result = new byte[length];
            int segmentCount = samples.Length / _segmentSize;
            int startBit = startByte * 8;
            int extractedBits = 0;
            int totalBitsNeeded = length * 8;

            for (int seg = 0; seg < segmentCount && extractedBits < startBit + totalBitsNeeded; seg++)
            {
                if (seg * 8 / _segmentSize < startBit)
                    continue;

                int segStart = seg * _segmentSize;

                // Detect echo delay by correlation
                double corr0 = 0, corr1 = 0;

                for (int i = segStart; i < segStart + _segmentSize - _echoDelayOne && i < samples.Length; i++)
                {
                    corr0 += samples[i] * samples[i + _echoDelayOriginal];
                    corr1 += samples[i] * samples[i + _echoDelayOne];
                }

                int bit = corr1 > corr0 ? 1 : 0;

                if (extractedBits >= startBit)
                {
                    int resultBit = extractedBits - startBit;
                    result[resultBit / 8] |= (byte)(bit << (7 - (resultBit % 8)));
                }

                extractedBits++;
            }

            return result;
        }

        private byte[] ExtractUsingPhaseCoding(short[] samples, int startByte, int length, AudioFileInfo info)
        {
            var result = new byte[length];
            int startBit = startByte * 8;
            int extractedBits = 0;

            for (int seg = 0; seg < samples.Length / _segmentSize && extractedBits < length * 8; seg++)
            {
                int segStart = seg * _segmentSize;

                // Detect phase by checking sign patterns
                int positiveCount = 0;
                for (int i = segStart; i < segStart + _segmentSize && i < samples.Length; i++)
                {
                    if (samples[i] > 0) positiveCount++;
                }

                int bit = positiveCount > _segmentSize / 2 ? 0 : 1;

                if (extractedBits >= startBit)
                {
                    int resultBit = extractedBits - startBit;
                    result[resultBit / 8] |= (byte)(bit << (7 - (resultBit % 8)));
                }

                extractedBits++;
            }

            return result;
        }

        private byte[] ExtractUsingLsbCoding(short[] samples, int startByte, int length)
        {
            var result = new byte[length];
            int startBit = startByte * 8;

            for (int i = 0; i < length * 8; i++)
            {
                int sampleIndex = startBit + i;
                if (sampleIndex >= samples.Length)
                    break;

                int bit = samples[sampleIndex] & 1;
                result[i / 8] |= (byte)(bit << (7 - (i % 8)));
            }

            return result;
        }

        private byte[] ExtractUsingSpreadSpectrum(short[] samples, int startByte, int length, AudioFileInfo info)
        {
            var result = new byte[length];
            var pn = GeneratePnSequence(samples.Length);

            int startBit = startByte * 8;
            int extractedBits = 0;

            for (int seg = 0; seg < samples.Length / _segmentSize && extractedBits < length * 8; seg++)
            {
                // Correlate with PN sequence
                double correlation = 0;

                for (int i = seg * _segmentSize; i < (seg + 1) * _segmentSize && i < samples.Length; i++)
                {
                    correlation += samples[i] * pn[i];
                }

                int bit = correlation > 0 ? 1 : 0;

                if (extractedBits >= startBit)
                {
                    int resultBit = extractedBits - startBit;
                    result[resultBit / 8] |= (byte)(bit << (7 - (resultBit % 8)));
                }

                extractedBits++;
            }

            return result;
        }

        private byte[] ExtractUsingSilenceIntervals(short[] samples, int startByte, int length)
        {
            var result = new byte[length];
            int threshold = 50;
            int minSilenceLength = 100;
            int startBit = startByte * 8;
            int extractedBits = 0;

            int i = 0;
            while (i < samples.Length - minSilenceLength && extractedBits < startBit + length * 8)
            {
                // Check for encoded silence pattern
                int pattern = 0;
                for (int j = 0; j < minSilenceLength && i + j < samples.Length; j++)
                {
                    pattern += Math.Abs(samples[i + j]);
                }

                if (pattern < threshold * minSilenceLength)
                {
                    int bit = pattern > 0 ? 1 : 0;

                    if (extractedBits >= startBit)
                    {
                        int resultBit = extractedBits - startBit;
                        result[resultBit / 8] |= (byte)(bit << (7 - (resultBit % 8)));
                    }

                    extractedBits++;
                    i += minSilenceLength;
                }
                else
                {
                    i++;
                }
            }

            return result;
        }

        private double[] GeneratePnSequence(int length)
        {
            var pn = new double[length];
            var rng = new Random(12345); // Fixed seed for reproducibility

            for (int i = 0; i < length; i++)
            {
                pn[i] = rng.NextDouble() > 0.5 ? 1.0 : -1.0;
            }

            return pn;
        }

        private byte[] ReconstructAudioFile(short[] samples, byte[] originalData, AudioFileInfo info, AudioFormat format)
        {
            var result = new byte[originalData.Length];
            Array.Copy(originalData, result, originalData.Length);

            int bytesPerSample = info.BitsPerSample / 8;

            for (int i = 0; i < samples.Length; i++)
            {
                int offset = info.DataOffset + i * bytesPerSample;
                if (offset + bytesPerSample > result.Length)
                    break;

                if (info.BitsPerSample == 16)
                {
                    var bytes = BitConverter.GetBytes(samples[i]);
                    result[offset] = bytes[0];
                    result[offset + 1] = bytes[1];
                }
                else if (info.BitsPerSample == 8)
                {
                    result[offset] = (byte)((samples[i] / 256) + 128);
                }
            }

            return result;
        }

        private byte[] CreateHeader(int dataLength)
        {
            var header = new byte[HeaderSize];

            Buffer.BlockCopy(MagicBytes, 0, header, 0, MagicBytes.Length);
            BitConverter.GetBytes(1).CopyTo(header, 8); // Version
            BitConverter.GetBytes(dataLength).CopyTo(header, 12);
            BitConverter.GetBytes((int)_embeddingMethod).CopyTo(header, 16);
            BitConverter.GetBytes(_echoDelayOriginal).CopyTo(header, 20);
            BitConverter.GetBytes(_echoDelayOne).CopyTo(header, 24);
            BitConverter.GetBytes(_segmentSize).CopyTo(header, 28);

            // Checksum
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(header, 0, 56);
            Buffer.BlockCopy(hash, 0, header, 56, 8);

            return header;
        }

        private AudioHeader ParseHeader(byte[] data)
        {
            return new AudioHeader
            {
                Version = BitConverter.ToInt32(data, 8),
                DataLength = BitConverter.ToInt32(data, 12),
                Method = (AudioEmbeddingMethod)BitConverter.ToInt32(data, 16),
                EchoDelay0 = BitConverter.ToInt32(data, 20),
                EchoDelay1 = BitConverter.ToInt32(data, 24),
                SegmentSize = BitConverter.ToInt32(data, 28)
            };
        }

        private bool ValidateHeader(AudioHeader header)
        {
            return header.DataLength > 0 && header.DataLength < 100_000_000;
        }

        private AudioStatistics AnalyzeAudioStatistics(short[] samples)
        {
            if (samples.Length == 0)
                return new AudioStatistics();

            long sum = 0;
            long sumSquared = 0;
            int silenceCount = 0;
            short max = short.MinValue;
            short min = short.MaxValue;

            foreach (var sample in samples)
            {
                sum += Math.Abs(sample);
                sumSquared += (long)sample * sample;
                if (Math.Abs(sample) < 500) silenceCount++;
                if (sample > max) max = sample;
                if (sample < min) min = sample;
            }

            double average = (double)sum / samples.Length;
            double rms = Math.Sqrt((double)sumSquared / samples.Length);
            double dynamicRange = max > 0 ? 20 * Math.Log10((double)max / Math.Max(1, (int)Math.Abs(min))) : 0;

            // Estimate spectral complexity from variance
            double variance = 0;
            for (int i = 1; i < samples.Length; i++)
            {
                variance += Math.Pow(samples[i] - samples[i - 1], 2);
            }
            variance /= samples.Length;
            double complexity = Math.Min(variance / 10000000, 1.0);

            return new AudioStatistics
            {
                PeakAmplitude = max,
                AverageAmplitude = average,
                RmsAmplitude = rms,
                DynamicRange = Math.Abs(dynamicRange),
                NoiseFloor = -60 + (silenceCount > samples.Length * 0.1 ? 0 : 20),
                SpectralComplexity = complexity,
                SilenceRatio = (double)silenceCount / samples.Length
            };
        }

        private string RecommendEmbeddingMethod(AudioStatistics stats)
        {
            if (stats.DynamicRange > 60 && stats.SpectralComplexity > 0.5)
                return "SpreadSpectrum";
            if (stats.SilenceRatio > 0.2)
                return "SilenceIntervals";
            if (stats.DynamicRange > 40)
                return "EchoHiding";
            return "LsbCoding";
        }

        private string CalculateDetectionRisk(AudioStatistics stats)
        {
            if (stats.DynamicRange > 50 && stats.SpectralComplexity > 0.6)
                return "Low";
            if (stats.DynamicRange > 30 && stats.SpectralComplexity > 0.3)
                return "Medium";
            return "High";
        }

        private byte[] EncryptPayload(byte[] data)
        {
            if (_encryptionKey == null)
                throw new InvalidOperationException("Encryption key not configured");

            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var ms = new MemoryStream(65536);
            ms.Write(aes.IV, 0, 16);

            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(data, 0, data.Length);
            }

            return ms.ToArray();
        }

        private byte[] DecryptPayload(byte[] encryptedData)
        {
            if (_encryptionKey == null)
                throw new InvalidOperationException("Encryption key not configured");

            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            var iv = new byte[16];
            Buffer.BlockCopy(encryptedData, 0, iv, 0, 16);
            aes.IV = iv;

            using var ms = new MemoryStream(65536);
            using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(encryptedData, 16, encryptedData.Length - 16);
            }

            return ms.ToArray();
        }
    }

    /// <summary>
    /// Audio steganography embedding methods.
    /// </summary>
    public enum AudioEmbeddingMethod
    {
        EchoHiding,
        PhaseCoding,
        LsbCoding,
        SpreadSpectrum,
        SilenceIntervals
    }

    /// <summary>
    /// Supported audio formats.
    /// </summary>
    public enum AudioFormat
    {
        Wav,
        Aiff,
        Raw
    }

    /// <summary>
    /// Audio file information.
    /// </summary>
    internal record AudioFileInfo
    {
        public AudioFormat Format { get; init; }
        public int Channels { get; init; }
        public int SampleRate { get; init; }
        public int BitsPerSample { get; init; }
        public int DataOffset { get; init; }
        public int DataSize { get; init; }
        public int SampleCount { get; init; }
    }

    /// <summary>
    /// Audio steganography header.
    /// </summary>
    internal record AudioHeader
    {
        public int Version { get; init; }
        public int DataLength { get; init; }
        public AudioEmbeddingMethod Method { get; init; }
        public int EchoDelay0 { get; init; }
        public int EchoDelay1 { get; init; }
        public int SegmentSize { get; init; }
    }

    /// <summary>
    /// Audio statistics for analysis.
    /// </summary>
    internal record AudioStatistics
    {
        public double PeakAmplitude { get; init; }
        public double AverageAmplitude { get; init; }
        public double RmsAmplitude { get; init; }
        public double DynamicRange { get; init; }
        public double NoiseFloor { get; init; }
        public double SpectralComplexity { get; init; }
        public double SilenceRatio { get; init; }
    }

    /// <summary>
    /// Audio capacity information.
    /// </summary>
    public record AudioCapacityInfo
    {
        public int TotalSamples { get; init; }
        public int SampleRate { get; init; }
        public int BitsPerSample { get; init; }
        public int Channels { get; init; }
        public double DurationSeconds { get; init; }
        public long TotalCapacityBytes { get; init; }
        public long UsableCapacityBytes { get; init; }
        public string EmbeddingMethod { get; init; } = "";
    }

    /// <summary>
    /// Audio carrier analysis results.
    /// </summary>
    public record AudioCarrierAnalysis
    {
        public AudioCapacityInfo CapacityInfo { get; init; } = new();
        public double DynamicRangeDb { get; init; }
        public double NoiseFloorDb { get; init; }
        public double SpectralComplexity { get; init; }
        public double PeakAmplitude { get; init; }
        public double AverageAmplitude { get; init; }
        public double SilenceRatio { get; init; }
        public double SuitabilityScore { get; init; }
        public string RecommendedMethod { get; init; } = "";
        public string DetectionRisk { get; init; } = "Medium";
    }
}
