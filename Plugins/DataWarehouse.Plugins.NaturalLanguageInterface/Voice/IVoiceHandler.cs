// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Models;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Voice;

/// <summary>
/// Interface for voice command handlers.
/// Supports integration with Alexa, Google Assistant, Siri, and other voice platforms.
/// </summary>
public interface IVoiceHandler
{
    /// <summary>Voice platform identifier.</summary>
    string PlatformId { get; }

    /// <summary>Platform display name.</summary>
    string PlatformName { get; }

    /// <summary>Whether this handler is properly configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Handle an incoming voice request.</summary>
    Task<VoiceResponse> HandleRequestAsync(VoiceRequest request, CancellationToken ct = default);

    /// <summary>Validate a webhook signature.</summary>
    bool ValidateSignature(string body, string signature, IDictionary<string, string> headers);

    /// <summary>Get skill/action configuration for deployment.</summary>
    object GetSkillConfiguration();
}

/// <summary>
/// Voice request from a platform.
/// </summary>
public class VoiceRequest
{
    /// <summary>Request identifier.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Request type (launch, intent, session_ended).</summary>
    public VoiceRequestType Type { get; init; }

    /// <summary>User identifier.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Session/conversation identifier.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Intent name if type is Intent.</summary>
    public string? IntentName { get; init; }

    /// <summary>Slot/parameter values.</summary>
    public Dictionary<string, string> Slots { get; init; } = new();

    /// <summary>Raw transcript if available.</summary>
    public string? RawTranscript { get; init; }

    /// <summary>Locale (e.g., "en-US").</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>Device capabilities.</summary>
    public VoiceDeviceCapabilities Capabilities { get; init; } = new();

    /// <summary>Session attributes for multi-turn.</summary>
    public Dictionary<string, object> SessionAttributes { get; init; } = new();

    /// <summary>Whether this is a new session.</summary>
    public bool IsNewSession { get; init; }

    /// <summary>Platform-specific raw request.</summary>
    public object? RawRequest { get; init; }
}

/// <summary>
/// Voice response to a platform.
/// </summary>
public class VoiceResponse
{
    /// <summary>Spoken response text (SSML supported).</summary>
    public string Speech { get; init; } = string.Empty;

    /// <summary>Display text (for devices with screens).</summary>
    public string? DisplayText { get; init; }

    /// <summary>Reprompt if expecting user response.</summary>
    public string? Reprompt { get; init; }

    /// <summary>Whether to end the session.</summary>
    public bool ShouldEndSession { get; init; }

    /// <summary>Session attributes to persist.</summary>
    public Dictionary<string, object> SessionAttributes { get; init; } = new();

    /// <summary>Card/visual content for display.</summary>
    public VoiceCard? Card { get; init; }

    /// <summary>Directives for the device.</summary>
    public List<VoiceDirective>? Directives { get; init; }

    /// <summary>Platform-specific response data.</summary>
    public Dictionary<string, object> PlatformData { get; init; } = new();
}

/// <summary>
/// Voice request types.
/// </summary>
public enum VoiceRequestType
{
    Launch,
    Intent,
    SessionEnded,
    CanFulfillIntent,
    AccountLinking,
    Permission,
    Unknown
}

/// <summary>
/// Voice device capabilities.
/// </summary>
public class VoiceDeviceCapabilities
{
    /// <summary>Can display text/images.</summary>
    public bool HasDisplay { get; init; }

    /// <summary>Can play audio.</summary>
    public bool HasAudioPlayer { get; init; }

    /// <summary>Can play video.</summary>
    public bool HasVideoPlayer { get; init; }

    /// <summary>Display dimensions if available.</summary>
    public (int Width, int Height)? DisplaySize { get; init; }
}

/// <summary>
/// Visual card for voice response.
/// </summary>
public class VoiceCard
{
    /// <summary>Card type.</summary>
    public VoiceCardType Type { get; init; }

    /// <summary>Card title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Card content/body.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Image URL if applicable.</summary>
    public string? ImageUrl { get; init; }
}

/// <summary>
/// Voice card types.
/// </summary>
public enum VoiceCardType
{
    Simple,
    Standard,
    LinkAccount,
    AskForPermission
}

/// <summary>
/// Voice directive for device actions.
/// </summary>
public class VoiceDirective
{
    /// <summary>Directive type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Directive payload.</summary>
    public object? Payload { get; init; }
}
