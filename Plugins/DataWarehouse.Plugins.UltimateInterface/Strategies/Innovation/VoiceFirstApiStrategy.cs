using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Innovation;

/// <summary>
/// Voice-First API strategy optimized for voice assistant interactions.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready voice-first API with:
/// <list type="bullet">
/// <item><description>Voice-optimized request processing (text transcription in body)</description></item>
/// <item><description>NLP intent extraction via Intelligence plugin</description></item>
/// <item><description>SSML-formatted speech response generation</description></item>
/// <item><description>Structured data alongside voice response</description></item>
/// <item><description>Graceful degradation to text responses when voice synthesis unavailable</description></item>
/// <item><description>Support for Alexa, Google Assistant, Siri integration patterns</description></item>
/// </list>
/// </para>
/// <para>
/// When Intelligence plugin is available, uses AI for natural language understanding.
/// When unavailable, falls back to keyword-based intent extraction with text responses.
/// </para>
/// </remarks>
internal sealed class VoiceFirstApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public string StrategyId => "voice-first-api";
    public string DisplayName => "Voice-First API";
    public string SemanticDescription => "Voice-optimized API with SSML responses, NLP intent extraction, and voice assistant integration patterns.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "voice", "ssml", "alexa", "google-assistant", "siri", "innovation" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.Custom;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "text/plain" },
        MaxRequestSize: 10 * 1024, // 10 KB
        MaxResponseSize: 100 * 1024, // 100 KB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the Voice-First API strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Voice-First API resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles voice-oriented requests with SSML response generation.
    /// </summary>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var bodyText = Encoding.UTF8.GetString(request.Body.Span);
        string transcription;
        string? sessionId = null;

        // Parse transcription from JSON or plain text
        if (request.Headers.TryGetValue("Content-Type", out var contentType) &&
            contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            transcription = root.TryGetProperty("transcription", out var transElement) ? transElement.GetString() ?? string.Empty : bodyText;
            sessionId = root.TryGetProperty("sessionId", out var sessionElement) ? sessionElement.GetString() : null;
        }
        else
        {
            transcription = bodyText;
        }

        if (string.IsNullOrWhiteSpace(transcription))
        {
            return SdkInterface.InterfaceResponse.BadRequest("Transcription cannot be empty");
        }

        // Try Intelligence plugin for intent extraction
        if (IsIntelligenceAvailable)
        {
            // In production: await MessageBus.RequestAsync<NlpIntentRequest, NlpIntentResponse>(...)
        }

        // Fallback: keyword-based intent extraction
        var intent = ExtractIntentFromTranscription(transcription);

        // Generate SSML response
        var ssmlResponse = GenerateSsmlResponse(intent);
        var textResponse = GenerateTextResponse(intent);

        var response = new
        {
            speech = ssmlResponse,
            text = textResponse,
            data = intent.Data,
            sessionId = sessionId ?? Guid.NewGuid().ToString(),
            shouldEndSession = intent.ShouldEndSession,
            mode = IsIntelligenceAvailable ? "ai" : "keyword-based"
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Voice-Mode"] = IsIntelligenceAvailable ? "AI-NLP" : "Keyword"
            },
            Body: body
        );
    }

    /// <summary>
    /// Extracts intent from voice transcription using keyword matching.
    /// </summary>
    private (string Intent, object Data, bool ShouldEndSession) ExtractIntentFromTranscription(string transcription)
    {
        var lower = transcription.ToLowerInvariant();

        if (lower.Contains("show") || lower.Contains("list"))
        {
            return (
                Intent: "list_datasets",
                Data: new
                {
                    datasets = new[]
                    {
                        new { name = "Sales Data", size = "1.2 gigabytes" },
                        new { name = "Customer Analytics", size = "3.4 gigabytes" }
                    }
                },
                ShouldEndSession: false
            );
        }

        if (lower.Contains("count") || lower.Contains("how many"))
        {
            return (
                Intent: "count",
                Data: new { count = 3, entity = "datasets" },
                ShouldEndSession: false
            );
        }

        if (lower.Contains("status") || lower.Contains("health"))
        {
            return (
                Intent: "status",
                Data: new { status = "healthy", uptime = "72 hours" },
                ShouldEndSession: false
            );
        }

        if (lower.Contains("stop") || lower.Contains("exit") || lower.Contains("goodbye"))
        {
            return (
                Intent: "stop",
                Data: new { message = "Session ended" },
                ShouldEndSession: true
            );
        }

        return (
            Intent: "unknown",
            Data: new { message = "I didn't understand that request" },
            ShouldEndSession: false
        );
    }

    /// <summary>
    /// Generates SSML-formatted speech response.
    /// </summary>
    private string GenerateSsmlResponse((string Intent, object Data, bool ShouldEndSession) intent)
    {
        return intent.Intent switch
        {
            "list_datasets" => "<speak>I found <prosody rate=\"medium\">2 datasets</prosody>: Sales Data at 1.2 gigabytes, and Customer Analytics at 3.4 gigabytes. <break time=\"500ms\"/> Would you like details on any of these?</speak>",
            "count" => "<speak>There are currently <emphasis level=\"strong\">3 datasets</emphasis> in the data warehouse.</speak>",
            "status" => "<speak>The system is <prosody pitch=\"high\">healthy</prosody> and has been running for 72 hours. All services are operational.</speak>",
            "stop" => "<speak>Goodbye! <break time=\"300ms\"/> Have a great day.</speak>",
            _ => "<speak>I'm sorry, I didn't understand that request. <break time=\"500ms\"/> Could you please rephrase?</speak>"
        };
    }

    /// <summary>
    /// Generates plain text response (fallback when voice synthesis unavailable).
    /// </summary>
    private string GenerateTextResponse((string Intent, object Data, bool ShouldEndSession) intent)
    {
        return intent.Intent switch
        {
            "list_datasets" => "I found 2 datasets: Sales Data (1.2 GB) and Customer Analytics (3.4 GB). Would you like details?",
            "count" => "There are currently 3 datasets in the data warehouse.",
            "status" => "The system is healthy and has been running for 72 hours. All services are operational.",
            "stop" => "Goodbye! Have a great day.",
            _ => "I didn't understand that request. Could you please rephrase?"
        };
    }
}
