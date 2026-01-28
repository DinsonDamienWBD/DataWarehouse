// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Audio;

/// <summary>
/// Handles audio processing capabilities including speech-to-text, text-to-speech,
/// audio analysis, and voice command parsing.
/// </summary>
/// <remarks>
/// <para>
/// This handler implements the Audio capability domain with the following sub-capabilities:
/// </para>
/// <list type="bullet">
/// <item><description>SpeechToText - Transcribe audio to text (Whisper, etc.)</description></item>
/// <item><description>TextToSpeech - Generate speech from text</description></item>
/// <item><description>AudioAnalysis - Analyze audio content (music, environmental sounds)</description></item>
/// <item><description>VoiceCommandParsing - Parse and interpret voice commands</description></item>
/// </list>
/// <para>
/// Note: Audio capabilities require providers with specific audio support (OpenAI, Google, Azure).
/// </para>
/// </remarks>
public sealed class AudioCapabilityHandler : ICapabilityHandler
{
    private readonly Func<string, IExtendedAIProvider?> _providerResolver;
    private readonly Func<string, IAudioProvider?>? _audioProviderResolver;
    private readonly AudioConfig _config;

    /// <summary>
    /// Speech-to-text transcription capability identifier.
    /// </summary>
    public const string SpeechToText = "SpeechToText";

    /// <summary>
    /// Text-to-speech synthesis capability identifier.
    /// </summary>
    public const string TextToSpeech = "TextToSpeech";

    /// <summary>
    /// Audio analysis capability identifier.
    /// </summary>
    public const string AudioAnalysis = "AudioAnalysis";

    /// <summary>
    /// Voice command parsing capability identifier.
    /// </summary>
    public const string VoiceCommandParsing = "VoiceCommandParsing";

    /// <inheritdoc/>
    public string CapabilityDomain => "Audio";

    /// <inheritdoc/>
    public string DisplayName => "Audio Processing";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => new[]
    {
        SpeechToText,
        TextToSpeech,
        AudioAnalysis,
        VoiceCommandParsing
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve AI providers by name.</param>
    /// <param name="audioProviderResolver">Optional function to resolve audio-specific providers.</param>
    /// <param name="config">Optional configuration settings.</param>
    public AudioCapabilityHandler(
        Func<string, IExtendedAIProvider?> providerResolver,
        Func<string, IAudioProvider?>? audioProviderResolver = null,
        AudioConfig? config = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _audioProviderResolver = audioProviderResolver;
        _config = config ?? new AudioConfig();
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check full domain.capability
        var fullCapability = $"{CapabilityDomain}.{capability}";
        if (instanceConfig.DisabledCapabilities.Contains(fullCapability))
            return false;

        // Check quota tier restrictions
        var limits = UsageLimits.GetDefaultLimits(instanceConfig.QuotaTier);

        // Audio features are typically part of the broader feature set
        // Only enabled for Pro tier and above where Vision is enabled
        // (as a proxy for advanced features)
        if (!limits.VisionEnabled && capability != SpeechToText)
            return false;

        return true;
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var provider))
            return provider;

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out provider))
            return provider;

        // Default providers based on capability
        return capability switch
        {
            SpeechToText => "openai", // Whisper
            TextToSpeech => "openai", // TTS
            AudioAnalysis => "openai",
            VoiceCommandParsing => "openai",
            _ => _config.DefaultProvider
        };
    }

    /// <summary>
    /// Transcribes audio to text.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The transcription request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transcription result.</returns>
    public async Task<CapabilityResult<TranscriptionResult>> TranscribeAsync(
        InstanceCapabilityConfig instanceConfig,
        TranscriptionRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, SpeechToText))
            return CapabilityResult<TranscriptionResult>.Disabled(SpeechToText);

        if (request.AudioData == null && string.IsNullOrEmpty(request.AudioUrl))
            return CapabilityResult<TranscriptionResult>.Fail("Either AudioData or AudioUrl must be provided.", "NO_AUDIO");

        var providerName = GetProviderForCapability(instanceConfig, SpeechToText);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<TranscriptionResult>.NoProvider(SpeechToText);

        // Try audio-specific provider first
        if (_audioProviderResolver != null)
        {
            var audioProvider = _audioProviderResolver(providerName);
            if (audioProvider != null)
            {
                return await TranscribeWithAudioProviderAsync(audioProvider, providerName, request, ct);
            }
        }

        // Fall back to general AI provider if it supports audio
        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<TranscriptionResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        // Check if provider is an audio provider
        if (provider is IAudioProvider directAudioProvider)
        {
            return await TranscribeWithAudioProviderAsync(directAudioProvider, providerName, request, ct);
        }

        return CapabilityResult<TranscriptionResult>.Fail($"Provider '{providerName}' does not support audio transcription.", "TRANSCRIPTION_NOT_SUPPORTED");
    }

    /// <summary>
    /// Generates speech from text.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The text-to-speech request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The synthesized speech result.</returns>
    public async Task<CapabilityResult<SpeechSynthesisResult>> SynthesizeSpeechAsync(
        InstanceCapabilityConfig instanceConfig,
        SpeechSynthesisRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, TextToSpeech))
            return CapabilityResult<SpeechSynthesisResult>.Disabled(TextToSpeech);

        if (string.IsNullOrWhiteSpace(request.Text))
            return CapabilityResult<SpeechSynthesisResult>.Fail("Text cannot be empty.", "NO_TEXT");

        var providerName = GetProviderForCapability(instanceConfig, TextToSpeech);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<SpeechSynthesisResult>.NoProvider(TextToSpeech);

        // Try audio-specific provider first
        if (_audioProviderResolver != null)
        {
            var audioProvider = _audioProviderResolver(providerName);
            if (audioProvider != null)
            {
                return await SynthesizeWithAudioProviderAsync(audioProvider, providerName, request, ct);
            }
        }

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<SpeechSynthesisResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (provider is IAudioProvider directAudioProvider)
        {
            return await SynthesizeWithAudioProviderAsync(directAudioProvider, providerName, request, ct);
        }

        return CapabilityResult<SpeechSynthesisResult>.Fail($"Provider '{providerName}' does not support text-to-speech.", "TTS_NOT_SUPPORTED");
    }

    /// <summary>
    /// Analyzes audio content (music, environmental sounds, etc.).
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The audio analysis request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The audio analysis result.</returns>
    public async Task<CapabilityResult<AudioAnalysisResult>> AnalyzeAudioAsync(
        InstanceCapabilityConfig instanceConfig,
        AudioAnalysisRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, AudioAnalysis))
            return CapabilityResult<AudioAnalysisResult>.Disabled(AudioAnalysis);

        if (request.AudioData == null && string.IsNullOrEmpty(request.AudioUrl))
            return CapabilityResult<AudioAnalysisResult>.Fail("Either AudioData or AudioUrl must be provided.", "NO_AUDIO");

        var providerName = GetProviderForCapability(instanceConfig, AudioAnalysis);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<AudioAnalysisResult>.NoProvider(AudioAnalysis);

        // For audio analysis, we typically transcribe first then analyze with LLM
        // This is a multi-step process

        try
        {
            var sw = Stopwatch.StartNew();

            // Step 1: Transcribe the audio
            var transcriptionRequest = new TranscriptionRequest
            {
                AudioData = request.AudioData,
                AudioUrl = request.AudioUrl,
                Language = request.Language,
                IncludeTimestamps = true
            };

            var transcriptionResult = await TranscribeAsync(instanceConfig, transcriptionRequest, ct);
            if (!transcriptionResult.IsSuccess)
            {
                return CapabilityResult<AudioAnalysisResult>.Fail(
                    $"Failed to transcribe audio: {transcriptionResult.ErrorMessage}",
                    transcriptionResult.ErrorCode ?? "TRANSCRIPTION_FAILED");
            }

            // Step 2: Analyze the transcription with an LLM
            var provider = _providerResolver(providerName);
            if (provider == null)
            {
                return CapabilityResult<AudioAnalysisResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");
            }

            var analysisPrompt = BuildAudioAnalysisPrompt(
                transcriptionResult.Value!.Text,
                transcriptionResult.Value!.Segments,
                request.AnalysisType);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = "You are an audio content analyst. Analyze the provided transcription and audio metadata." },
                    new() { Role = "user", Content = analysisPrompt }
                },
                MaxTokens = 2048
            };

            var chatResponse = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            var analysis = ParseAudioAnalysisResponse(chatResponse.Content, request.AnalysisType);
            analysis.Transcription = transcriptionResult.Value.Text;
            analysis.Duration = transcriptionResult.Value.Duration;

            return CapabilityResult<AudioAnalysisResult>.Ok(
                analysis,
                providerName,
                chatRequest.Model,
                new TokenUsage
                {
                    InputTokens = chatResponse.InputTokens,
                    OutputTokens = chatResponse.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<AudioAnalysisResult>.Fail(ex.Message, "AUDIO_ANALYSIS_ERROR");
        }
    }

    /// <summary>
    /// Parses a voice command and extracts intent and parameters.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The voice command parsing request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed voice command result.</returns>
    public async Task<CapabilityResult<VoiceCommandResult>> ParseVoiceCommandAsync(
        InstanceCapabilityConfig instanceConfig,
        VoiceCommandRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, VoiceCommandParsing))
            return CapabilityResult<VoiceCommandResult>.Disabled(VoiceCommandParsing);

        // Either transcription or audio data is required
        if (string.IsNullOrWhiteSpace(request.Transcription) &&
            request.AudioData == null &&
            string.IsNullOrEmpty(request.AudioUrl))
        {
            return CapabilityResult<VoiceCommandResult>.Fail(
                "Either Transcription, AudioData, or AudioUrl must be provided.",
                "NO_INPUT");
        }

        var providerName = GetProviderForCapability(instanceConfig, VoiceCommandParsing);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<VoiceCommandResult>.NoProvider(VoiceCommandParsing);

        try
        {
            var sw = Stopwatch.StartNew();

            // Transcribe audio if not provided as text
            var transcription = request.Transcription;
            if (string.IsNullOrEmpty(transcription))
            {
                var transcriptionRequest = new TranscriptionRequest
                {
                    AudioData = request.AudioData,
                    AudioUrl = request.AudioUrl,
                    Language = request.Language
                };

                var transcriptionResult = await TranscribeAsync(instanceConfig, transcriptionRequest, ct);
                if (!transcriptionResult.IsSuccess)
                {
                    return CapabilityResult<VoiceCommandResult>.Fail(
                        $"Failed to transcribe audio: {transcriptionResult.ErrorMessage}",
                        transcriptionResult.ErrorCode ?? "TRANSCRIPTION_FAILED");
                }

                transcription = transcriptionResult.Value!.Text;
            }

            // Parse the command using an LLM
            var provider = _providerResolver(providerName);
            if (provider == null)
            {
                return CapabilityResult<VoiceCommandResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");
            }

            var parsePrompt = BuildVoiceCommandParsePrompt(transcription!, request.AvailableCommands, request.Context);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = GetVoiceCommandSystemPrompt(request.AvailableCommands) },
                    new() { Role = "user", Content = parsePrompt }
                },
                MaxTokens = 1024
            };

            var chatResponse = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            var commandResult = ParseVoiceCommandResponse(chatResponse.Content, transcription!);

            return CapabilityResult<VoiceCommandResult>.Ok(
                commandResult,
                providerName,
                chatRequest.Model,
                new TokenUsage
                {
                    InputTokens = chatResponse.InputTokens,
                    OutputTokens = chatResponse.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<VoiceCommandResult>.Fail(ex.Message, "VOICE_COMMAND_ERROR");
        }
    }

    #region Private Helpers

    private async Task<CapabilityResult<TranscriptionResult>> TranscribeWithAudioProviderAsync(
        IAudioProvider audioProvider,
        string providerName,
        TranscriptionRequest request,
        CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            var result = await audioProvider.TranscribeAsync(
                request.AudioData,
                request.AudioUrl,
                new TranscriptionOptions
                {
                    Language = request.Language,
                    IncludeTimestamps = request.IncludeTimestamps,
                    IncludeWordLevelTimestamps = request.IncludeWordLevelTimestamps,
                    Model = request.Model ?? _config.DefaultTranscriptionModel
                },
                ct);

            sw.Stop();

            return CapabilityResult<TranscriptionResult>.Ok(
                result,
                providerName,
                request.Model ?? _config.DefaultTranscriptionModel,
                duration: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<TranscriptionResult>.Fail(ex.Message, "TRANSCRIPTION_ERROR");
        }
    }

    private async Task<CapabilityResult<SpeechSynthesisResult>> SynthesizeWithAudioProviderAsync(
        IAudioProvider audioProvider,
        string providerName,
        SpeechSynthesisRequest request,
        CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            var result = await audioProvider.SynthesizeSpeechAsync(
                request.Text,
                new SpeechSynthesisOptions
                {
                    Voice = request.Voice ?? _config.DefaultVoice,
                    Speed = request.Speed ?? 1.0,
                    Pitch = request.Pitch ?? 1.0,
                    OutputFormat = request.OutputFormat ?? AudioFormat.Mp3,
                    Model = request.Model ?? _config.DefaultTTSModel
                },
                ct);

            sw.Stop();

            return CapabilityResult<SpeechSynthesisResult>.Ok(
                result,
                providerName,
                request.Model ?? _config.DefaultTTSModel,
                duration: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<SpeechSynthesisResult>.Fail(ex.Message, "TTS_ERROR");
        }
    }

    private static string BuildAudioAnalysisPrompt(
        string transcription,
        List<TranscriptionSegment>? segments,
        AudioAnalysisType analysisType)
    {
        var prompt = $"Transcription:\n{transcription}\n\n";

        if (segments != null && segments.Count > 0)
        {
            prompt += "Timestamps:\n";
            foreach (var segment in segments.Take(20)) // Limit to avoid token overflow
            {
                prompt += $"[{segment.StartTime:F1}s - {segment.EndTime:F1}s]: {segment.Text}\n";
            }
            prompt += "\n";
        }

        prompt += analysisType switch
        {
            AudioAnalysisType.Sentiment => "Analyze the sentiment of this speech. Identify emotional tone, mood changes, and overall sentiment.",
            AudioAnalysisType.Speaker => "Identify and characterize different speakers. Note speaking styles and roles.",
            AudioAnalysisType.Topic => "Identify the main topics discussed. List key themes and subject areas.",
            AudioAnalysisType.Summary => "Provide a comprehensive summary of the audio content.",
            AudioAnalysisType.KeyPoints => "Extract the key points and main takeaways from this content.",
            AudioAnalysisType.ActionItems => "Extract any action items, tasks, or follow-ups mentioned.",
            AudioAnalysisType.Questions => "Identify any questions asked and whether they were answered.",
            _ => "Analyze this audio transcription and provide relevant insights."
        };

        prompt += "\n\nProvide your analysis in JSON format.";
        return prompt;
    }

    private static AudioAnalysisResult ParseAudioAnalysisResponse(string response, AudioAnalysisType analysisType)
    {
        try
        {
            return JsonSerializer.Deserialize<AudioAnalysisResult>(response)
                ?? new AudioAnalysisResult { Summary = response };
        }
        catch
        {
            return new AudioAnalysisResult
            {
                Summary = response,
                AnalysisType = analysisType
            };
        }
    }

    private static string GetVoiceCommandSystemPrompt(List<CommandDefinition>? availableCommands)
    {
        var prompt = @"You are a voice command parser. Your job is to extract the user's intent and parameters from spoken commands.

Respond in JSON format with the following structure:
{
  ""intent"": ""command_name"",
  ""parameters"": { ""param1"": ""value1"", ... },
  ""confidence"": 0.95,
  ""alternateInterpretations"": []
}

If the command is unclear or doesn't match any known command, set intent to ""unknown"" and explain in the ""clarification"" field.";

        if (availableCommands != null && availableCommands.Count > 0)
        {
            prompt += "\n\nAvailable commands:\n";
            foreach (var cmd in availableCommands)
            {
                prompt += $"- {cmd.Name}: {cmd.Description}";
                if (cmd.Parameters != null && cmd.Parameters.Count > 0)
                {
                    prompt += $" (parameters: {string.Join(", ", cmd.Parameters.Select(p => p.Name))})";
                }
                prompt += "\n";
            }
        }

        return prompt;
    }

    private static string BuildVoiceCommandParsePrompt(
        string transcription,
        List<CommandDefinition>? availableCommands,
        Dictionary<string, object>? context)
    {
        var prompt = $"Parse this voice command: \"{transcription}\"";

        if (context != null && context.Count > 0)
        {
            prompt += $"\n\nContext: {JsonSerializer.Serialize(context)}";
        }

        return prompt;
    }

    private static VoiceCommandResult ParseVoiceCommandResponse(string response, string originalTranscription)
    {
        try
        {
            var result = JsonSerializer.Deserialize<VoiceCommandResult>(response);
            if (result != null)
            {
                result.OriginalTranscription = originalTranscription;
                return result;
            }
        }
        catch
        {
            // Fall through
        }

        return new VoiceCommandResult
        {
            OriginalTranscription = originalTranscription,
            Intent = "unknown",
            Confidence = 0.0,
            Clarification = response
        };
    }

    #endregion
}

#region Audio Provider Interface

/// <summary>
/// Interface for audio-specific AI provider capabilities.
/// </summary>
public interface IAudioProvider
{
    /// <summary>
    /// Transcribes audio to text.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(
        byte[]? audioData,
        string? audioUrl,
        TranscriptionOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Synthesizes speech from text.
    /// </summary>
    Task<SpeechSynthesisResult> SynthesizeSpeechAsync(
        string text,
        SpeechSynthesisOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Options for transcription.
/// </summary>
public sealed class TranscriptionOptions
{
    /// <summary>Language code for transcription.</summary>
    public string? Language { get; init; }

    /// <summary>Whether to include segment timestamps.</summary>
    public bool IncludeTimestamps { get; init; }

    /// <summary>Whether to include word-level timestamps.</summary>
    public bool IncludeWordLevelTimestamps { get; init; }

    /// <summary>Specific transcription model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Options for speech synthesis.
/// </summary>
public sealed class SpeechSynthesisOptions
{
    /// <summary>Voice to use for synthesis.</summary>
    public string? Voice { get; init; }

    /// <summary>Speech speed (1.0 = normal).</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Voice pitch (1.0 = normal).</summary>
    public double Pitch { get; init; } = 1.0;

    /// <summary>Output audio format.</summary>
    public AudioFormat OutputFormat { get; init; } = AudioFormat.Mp3;

    /// <summary>Specific TTS model to use.</summary>
    public string? Model { get; init; }
}

#endregion

#region Configuration

/// <summary>
/// Configuration for the audio capability handler.
/// </summary>
public sealed class AudioConfig
{
    /// <summary>Default provider for audio operations.</summary>
    public string DefaultProvider { get; init; } = "openai";

    /// <summary>Default model for transcription (Whisper).</summary>
    public string DefaultTranscriptionModel { get; init; } = "whisper-1";

    /// <summary>Default model for text-to-speech.</summary>
    public string DefaultTTSModel { get; init; } = "tts-1";

    /// <summary>Default voice for text-to-speech.</summary>
    public string DefaultVoice { get; init; } = "alloy";

    /// <summary>Maximum audio duration in seconds.</summary>
    public int MaxAudioDurationSeconds { get; init; } = 3600; // 1 hour

    /// <summary>Maximum audio file size in bytes.</summary>
    public long MaxAudioFileSizeBytes { get; init; } = 25 * 1024 * 1024; // 25 MB
}

/// <summary>
/// Audio output format.
/// </summary>
public enum AudioFormat
{
    /// <summary>MP3 format.</summary>
    Mp3,
    /// <summary>Opus format.</summary>
    Opus,
    /// <summary>AAC format.</summary>
    Aac,
    /// <summary>FLAC format.</summary>
    Flac,
    /// <summary>WAV format.</summary>
    Wav,
    /// <summary>PCM format.</summary>
    Pcm
}

/// <summary>
/// Type of audio analysis to perform.
/// </summary>
public enum AudioAnalysisType
{
    /// <summary>General analysis.</summary>
    General,
    /// <summary>Sentiment analysis.</summary>
    Sentiment,
    /// <summary>Speaker identification.</summary>
    Speaker,
    /// <summary>Topic extraction.</summary>
    Topic,
    /// <summary>Content summary.</summary>
    Summary,
    /// <summary>Key points extraction.</summary>
    KeyPoints,
    /// <summary>Action items extraction.</summary>
    ActionItems,
    /// <summary>Question identification.</summary>
    Questions
}

#endregion

#region Request Types

/// <summary>
/// Request for audio transcription.
/// </summary>
public sealed class TranscriptionRequest
{
    /// <summary>Audio data as byte array.</summary>
    public byte[]? AudioData { get; init; }

    /// <summary>URL to audio file.</summary>
    public string? AudioUrl { get; init; }

    /// <summary>Expected language code (ISO 639-1).</summary>
    public string? Language { get; init; }

    /// <summary>Whether to include segment timestamps.</summary>
    public bool IncludeTimestamps { get; init; }

    /// <summary>Whether to include word-level timestamps.</summary>
    public bool IncludeWordLevelTimestamps { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Request for speech synthesis.
/// </summary>
public sealed class SpeechSynthesisRequest
{
    /// <summary>Text to synthesize.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Voice to use.</summary>
    public string? Voice { get; init; }

    /// <summary>Speech speed (0.25 to 4.0, 1.0 = normal).</summary>
    public double? Speed { get; init; }

    /// <summary>Voice pitch (0.5 to 2.0, 1.0 = normal).</summary>
    public double? Pitch { get; init; }

    /// <summary>Output audio format.</summary>
    public AudioFormat? OutputFormat { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Request for audio analysis.
/// </summary>
public sealed class AudioAnalysisRequest
{
    /// <summary>Audio data as byte array.</summary>
    public byte[]? AudioData { get; init; }

    /// <summary>URL to audio file.</summary>
    public string? AudioUrl { get; init; }

    /// <summary>Type of analysis to perform.</summary>
    public AudioAnalysisType AnalysisType { get; init; } = AudioAnalysisType.General;

    /// <summary>Expected language code.</summary>
    public string? Language { get; init; }

    /// <summary>Specific model to use for analysis.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Request for voice command parsing.
/// </summary>
public sealed class VoiceCommandRequest
{
    /// <summary>Pre-transcribed text (if already transcribed).</summary>
    public string? Transcription { get; init; }

    /// <summary>Audio data as byte array.</summary>
    public byte[]? AudioData { get; init; }

    /// <summary>URL to audio file.</summary>
    public string? AudioUrl { get; init; }

    /// <summary>Expected language code.</summary>
    public string? Language { get; init; }

    /// <summary>Available command definitions for matching.</summary>
    public List<CommandDefinition>? AvailableCommands { get; init; }

    /// <summary>Additional context for command interpretation.</summary>
    public Dictionary<string, object>? Context { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Definition of an available command.
/// </summary>
public sealed class CommandDefinition
{
    /// <summary>Command name/identifier.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Description of what the command does.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Example phrases that trigger this command.</summary>
    public List<string>? Examples { get; init; }

    /// <summary>Parameters the command accepts.</summary>
    public List<CommandParameter>? Parameters { get; init; }
}

/// <summary>
/// Definition of a command parameter.
/// </summary>
public sealed class CommandParameter
{
    /// <summary>Parameter name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Parameter type (string, number, date, etc.).</summary>
    public string Type { get; init; } = "string";

    /// <summary>Whether the parameter is required.</summary>
    public bool Required { get; init; }

    /// <summary>Description of the parameter.</summary>
    public string? Description { get; init; }
}

#endregion

#region Result Types

/// <summary>
/// Result of audio transcription.
/// </summary>
public sealed class TranscriptionResult
{
    /// <summary>Full transcribed text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Detected language.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Audio duration in seconds.</summary>
    public double? Duration { get; init; }

    /// <summary>Transcription segments with timestamps.</summary>
    public List<TranscriptionSegment>? Segments { get; init; }

    /// <summary>Word-level timestamps if requested.</summary>
    public List<TranscriptionWord>? Words { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// A segment of transcribed audio.
/// </summary>
public sealed class TranscriptionSegment
{
    /// <summary>Segment text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Start time in seconds.</summary>
    public double StartTime { get; init; }

    /// <summary>End time in seconds.</summary>
    public double EndTime { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// A transcribed word with timestamp.
/// </summary>
public sealed class TranscriptionWord
{
    /// <summary>The word.</summary>
    public string Word { get; init; } = string.Empty;

    /// <summary>Start time in seconds.</summary>
    public double StartTime { get; init; }

    /// <summary>End time in seconds.</summary>
    public double EndTime { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// Result of speech synthesis.
/// </summary>
public sealed class SpeechSynthesisResult
{
    /// <summary>Generated audio data.</summary>
    public byte[] AudioData { get; init; } = Array.Empty<byte>();

    /// <summary>Audio format.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Duration in seconds.</summary>
    public double? Duration { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Voice used.</summary>
    public string? Voice { get; init; }
}

/// <summary>
/// Result of audio analysis.
/// </summary>
public sealed class AudioAnalysisResult
{
    /// <summary>Type of analysis performed.</summary>
    public AudioAnalysisType AnalysisType { get; set; }

    /// <summary>Summary of the audio content.</summary>
    public string? Summary { get; init; }

    /// <summary>Original transcription.</summary>
    public string? Transcription { get; set; }

    /// <summary>Audio duration in seconds.</summary>
    public double? Duration { get; set; }

    /// <summary>Sentiment analysis results.</summary>
    public SentimentAnalysis? Sentiment { get; init; }

    /// <summary>Identified speakers.</summary>
    public List<SpeakerInfo>? Speakers { get; init; }

    /// <summary>Main topics discussed.</summary>
    public List<string>? Topics { get; init; }

    /// <summary>Key points extracted.</summary>
    public List<string>? KeyPoints { get; init; }

    /// <summary>Action items identified.</summary>
    public List<string>? ActionItems { get; init; }

    /// <summary>Questions identified.</summary>
    public List<QuestionInfo>? Questions { get; init; }
}

/// <summary>
/// Sentiment analysis result.
/// </summary>
public sealed class SentimentAnalysis
{
    /// <summary>Overall sentiment (positive, negative, neutral, mixed).</summary>
    public string Overall { get; init; } = "neutral";

    /// <summary>Sentiment score (-1 to 1).</summary>
    public double Score { get; init; }

    /// <summary>Emotion breakdown.</summary>
    public Dictionary<string, double>? Emotions { get; init; }
}

/// <summary>
/// Information about an identified speaker.
/// </summary>
public sealed class SpeakerInfo
{
    /// <summary>Speaker identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Speaker label/name if identified.</summary>
    public string? Label { get; init; }

    /// <summary>Speaking time in seconds.</summary>
    public double? SpeakingTime { get; init; }

    /// <summary>Speaker characteristics.</summary>
    public string? Characteristics { get; init; }
}

/// <summary>
/// Information about a question in the audio.
/// </summary>
public sealed class QuestionInfo
{
    /// <summary>The question asked.</summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>Whether the question was answered.</summary>
    public bool WasAnswered { get; init; }

    /// <summary>The answer if provided.</summary>
    public string? Answer { get; init; }

    /// <summary>Timestamp of the question.</summary>
    public double? Timestamp { get; init; }
}

/// <summary>
/// Result of voice command parsing.
/// </summary>
public sealed class VoiceCommandResult
{
    /// <summary>Original transcription.</summary>
    public string OriginalTranscription { get; set; } = string.Empty;

    /// <summary>Identified command intent.</summary>
    public string Intent { get; init; } = string.Empty;

    /// <summary>Extracted parameters.</summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Alternative interpretations.</summary>
    public List<AlternateInterpretation>? AlternateInterpretations { get; init; }

    /// <summary>Clarification needed (if intent is unclear).</summary>
    public string? Clarification { get; init; }
}

/// <summary>
/// An alternate interpretation of a voice command.
/// </summary>
public sealed class AlternateInterpretation
{
    /// <summary>Intent.</summary>
    public string Intent { get; init; } = string.Empty;

    /// <summary>Parameters.</summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>Confidence score.</summary>
    public double Confidence { get; init; }
}

#endregion
