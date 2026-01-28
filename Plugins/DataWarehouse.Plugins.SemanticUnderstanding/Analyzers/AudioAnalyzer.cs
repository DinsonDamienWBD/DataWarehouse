using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Text;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Analyzers
{
    /// <summary>
    /// Audio content analyzer using AI transcription and analysis capabilities.
    /// Extracts transcripts and semantic information from audio files.
    /// </summary>
    public sealed class AudioAnalyzer : IContentAnalyzer
    {
        private readonly IAIProvider? _aiProvider;

        public string[] SupportedContentTypes => new[]
        {
            "audio/mpeg",
            "audio/mp3",
            "audio/wav",
            "audio/x-wav",
            "audio/ogg",
            "audio/flac",
            "audio/m4a",
            "audio/aac",
            "audio/webm"
        };

        public AudioAnalyzer(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
        }

        public bool CanAnalyze(string contentType)
        {
            var normalizedType = NormalizeContentType(contentType);
            return SupportedContentTypes.Any(t =>
                normalizedType.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                normalizedType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<string> ExtractTextAsync(Stream content, string contentType, CancellationToken ct = default)
        {
            // Audio transcription requires AI provider
            if (_aiProvider == null || !_aiProvider.IsAvailable)
            {
                // Return basic audio metadata as fallback
                return await ExtractAudioMetadataAsync(content, contentType, ct);
            }

            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            var audioBytes = memoryStream.ToArray();

            // Use AI for transcription (assuming the provider supports audio)
            var base64Audio = Convert.ToBase64String(audioBytes);

            var request = new AIRequest
            {
                Prompt = "Please transcribe this audio file accurately. Include speaker labels if multiple speakers are detected. Format: [Speaker X]: text",
                SystemMessage = "You are a speech-to-text transcription system. Transcribe audio accurately, preserving punctuation and speaker changes.",
                ExtendedParameters = new Dictionary<string, object>
                {
                    ["audio"] = base64Audio,
                    ["audio_content_type"] = contentType
                }
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (response.Success)
            {
                return response.Content;
            }

            return await ExtractAudioMetadataAsync(content, contentType, ct);
        }

        public async Task<ContentAnalysisResult> AnalyzeAsync(
            Stream content,
            string contentType,
            ContentAnalysisOptions options,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Read audio into memory
                using var memoryStream = new MemoryStream();
                await content.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                var metadata = new Dictionary<string, object>
                {
                    ["contentType"] = contentType,
                    ["fileSize"] = memoryStream.Length
                };

                // Extract basic audio information
                var audioInfo = ExtractBasicAudioInfo(memoryStream.ToArray(), contentType);
                foreach (var (key, value) in audioInfo)
                {
                    metadata[key] = value;
                }

                string text = string.Empty;
                LanguageDetectionResult? languageResult = null;
                SummarizationResult? summaryResult = null;
                float[]? embeddings = null;

                // Transcribe using AI if available
                if (options.ExtractText && _aiProvider != null && _aiProvider.IsAvailable)
                {
                    memoryStream.Position = 0;
                    text = await ExtractTextAsync(memoryStream, contentType, ct);

                    if (!string.IsNullOrEmpty(text))
                    {
                        metadata["transcribed"] = true;
                        metadata["transcriptLength"] = text.Length;

                        // Perform text analysis on transcript
                        var textAnalyzer = new TextAnalyzer();
                        using var textStream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                        var textResult = await textAnalyzer.AnalyzeAsync(textStream, "text/plain", options, ct);

                        languageResult = textResult.LanguageResult;
                        summaryResult = textResult.SummaryResult;
                        embeddings = textResult.Embeddings;

                        // Copy keywords
                        foreach (var kw in textResult.Keywords)
                        {
                            metadata[$"keyword_{kw.Keyword}"] = kw.Weight;
                        }
                    }
                }

                // Generate audio-specific analysis if AI is available
                if (_aiProvider != null && _aiProvider.IsAvailable && !string.IsNullOrEmpty(text))
                {
                    memoryStream.Position = 0;
                    var audioAnalysis = await AnalyzeAudioContentAsync(text, ct);

                    if (audioAnalysis != null)
                    {
                        metadata["speakerCount"] = audioAnalysis.SpeakerCount;
                        metadata["topics"] = audioAnalysis.Topics;
                        metadata["sentiment"] = audioAnalysis.OverallSentiment;
                    }
                }

                sw.Stop();

                return new ContentAnalysisResult
                {
                    Text = text,
                    DetectedContentType = contentType,
                    LanguageResult = languageResult,
                    SummaryResult = summaryResult,
                    Embeddings = embeddings,
                    Success = true,
                    Duration = sw.Elapsed,
                    Metadata = metadata
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ContentAnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Audio analysis failed: {ex.Message}",
                    Duration = sw.Elapsed
                };
            }
        }

        private Task<string> ExtractAudioMetadataAsync(Stream content, string contentType, CancellationToken ct)
        {
            // Extract basic metadata without AI
            using var memoryStream = new MemoryStream();
            content.CopyTo(memoryStream);
            var data = memoryStream.ToArray();

            var info = ExtractBasicAudioInfo(data, contentType);
            var sb = new StringBuilder();

            sb.Append($"Audio file ({contentType})");

            if (info.TryGetValue("format", out var format))
                sb.Append($" Format: {format}");

            if (info.TryGetValue("duration", out var duration))
                sb.Append($" Duration: {duration}s");

            if (info.TryGetValue("sampleRate", out var sampleRate))
                sb.Append($" Sample rate: {sampleRate}Hz");

            return Task.FromResult(sb.ToString());
        }

        private Dictionary<string, object> ExtractBasicAudioInfo(byte[] data, string contentType)
        {
            var info = new Dictionary<string, object>
            {
                ["fileSize"] = data.Length
            };

            // Detect format from magic bytes
            if (data.Length >= 12)
            {
                // MP3 (ID3 tag or frame sync)
                if ((data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33) || // ID3
                    (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)) // Frame sync
                {
                    info["format"] = "MP3";
                    var mp3Info = ParseMP3Header(data);
                    foreach (var (key, value) in mp3Info)
                    {
                        info[key] = value;
                    }
                }
                // WAV
                else if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                         data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45)
                {
                    info["format"] = "WAV";
                    var wavInfo = ParseWAVHeader(data);
                    foreach (var (key, value) in wavInfo)
                    {
                        info[key] = value;
                    }
                }
                // OGG
                else if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
                {
                    info["format"] = "OGG";
                }
                // FLAC
                else if (data[0] == 0x66 && data[1] == 0x4C && data[2] == 0x61 && data[3] == 0x43)
                {
                    info["format"] = "FLAC";
                }
                // M4A/AAC (ftyp box)
                else if (data.Length >= 8 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
                {
                    info["format"] = "M4A/AAC";
                }
            }

            return info;
        }

        private Dictionary<string, object> ParseMP3Header(byte[] data)
        {
            var info = new Dictionary<string, object>();

            // Find first frame sync
            int offset = 0;
            if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
            {
                // Skip ID3 tag
                if (data.Length > 10)
                {
                    var tagSize = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) |
                                  ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
                    offset = 10 + tagSize;
                }
            }

            // Find frame sync
            while (offset < data.Length - 4)
            {
                if (data[offset] == 0xFF && (data[offset + 1] & 0xE0) == 0xE0)
                {
                    // Parse frame header
                    var header = (data[offset] << 24) | (data[offset + 1] << 16) |
                                 (data[offset + 2] << 8) | data[offset + 3];

                    var version = (header >> 19) & 3;
                    var layer = (header >> 17) & 3;
                    var bitrateIndex = (header >> 12) & 0xF;
                    var sampleRateIndex = (header >> 10) & 3;

                    // Sample rate table for MPEG1
                    int[] sampleRates = { 44100, 48000, 32000, 0 };
                    if (sampleRateIndex < 3)
                    {
                        info["sampleRate"] = sampleRates[sampleRateIndex];
                    }

                    // Estimate duration from file size (rough)
                    if (bitrateIndex > 0 && bitrateIndex < 15)
                    {
                        int[] bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
                        var bitrate = bitrates[bitrateIndex];
                        if (bitrate > 0)
                        {
                            info["bitrate"] = bitrate;
                            info["estimatedDuration"] = (data.Length * 8) / (bitrate * 1000);
                        }
                    }

                    break;
                }
                offset++;
            }

            return info;
        }

        private Dictionary<string, object> ParseWAVHeader(byte[] data)
        {
            var info = new Dictionary<string, object>();

            if (data.Length < 44)
                return info;

            // Parse fmt chunk
            int offset = 12;
            while (offset < data.Length - 8)
            {
                var chunkId = Encoding.ASCII.GetString(data, offset, 4);
                var chunkSize = BitConverter.ToInt32(data, offset + 4);

                if (chunkId == "fmt ")
                {
                    if (offset + 24 <= data.Length)
                    {
                        var channels = BitConverter.ToInt16(data, offset + 10);
                        var sampleRate = BitConverter.ToInt32(data, offset + 12);
                        var bitsPerSample = BitConverter.ToInt16(data, offset + 22);

                        info["channels"] = channels;
                        info["sampleRate"] = sampleRate;
                        info["bitsPerSample"] = bitsPerSample;
                    }
                }
                else if (chunkId == "data")
                {
                    info["dataSize"] = chunkSize;

                    // Calculate duration
                    if (info.ContainsKey("sampleRate") && info.ContainsKey("channels") && info.ContainsKey("bitsPerSample"))
                    {
                        var sampleRate = (int)info["sampleRate"];
                        var channels = (short)info["channels"];
                        var bitsPerSample = (short)info["bitsPerSample"];

                        if (sampleRate > 0 && channels > 0 && bitsPerSample > 0)
                        {
                            var bytesPerSecond = sampleRate * channels * (bitsPerSample / 8);
                            info["duration"] = (float)chunkSize / bytesPerSecond;
                        }
                    }
                    break;
                }

                offset += 8 + chunkSize;
                if (chunkSize % 2 == 1) offset++; // Padding byte
            }

            return info;
        }

        private async Task<AudioContentAnalysis?> AnalyzeAudioContentAsync(string transcript, CancellationToken ct)
        {
            if (_aiProvider == null || string.IsNullOrEmpty(transcript))
                return null;

            var request = new AIRequest
            {
                Prompt = $@"Analyze this transcript and provide:
1. Number of distinct speakers detected
2. Main topics discussed (up to 5)
3. Overall sentiment (positive/neutral/negative)

Transcript:
{transcript}

Format response as:
SPEAKERS: [number]
TOPICS: [comma-separated list]
SENTIMENT: [positive/neutral/negative]",
                SystemMessage = "You are a conversation analysis system. Analyze transcripts accurately and concisely.",
                MaxTokens = 200
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (!response.Success)
                return null;

            return ParseAudioAnalysisResponse(response.Content);
        }

        private AudioContentAnalysis ParseAudioAnalysisResponse(string response)
        {
            var result = new AudioContentAnalysis();

            var speakersMatch = System.Text.RegularExpressions.Regex.Match(response, @"SPEAKERS:\s*(\d+)");
            if (speakersMatch.Success && int.TryParse(speakersMatch.Groups[1].Value, out var speakers))
            {
                result.SpeakerCount = speakers;
            }

            var topicsMatch = System.Text.RegularExpressions.Regex.Match(response, @"TOPICS:\s*(.+?)(?:SENTIMENT:|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (topicsMatch.Success)
            {
                result.Topics = topicsMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
            }

            var sentimentMatch = System.Text.RegularExpressions.Regex.Match(response, @"SENTIMENT:\s*(\w+)");
            if (sentimentMatch.Success)
            {
                result.OverallSentiment = sentimentMatch.Groups[1].Value.Trim().ToLowerInvariant();
            }

            return result;
        }

        private static string NormalizeContentType(string contentType)
        {
            var idx = contentType.IndexOf(';');
            return idx >= 0 ? contentType[..idx].Trim().ToLowerInvariant() : contentType.Trim().ToLowerInvariant();
        }

        private sealed class AudioContentAnalysis
        {
            public int SpeakerCount { get; set; } = 1;
            public List<string> Topics { get; set; } = new();
            public string OverallSentiment { get; set; } = "neutral";
        }
    }
}
