using System.Globalization;

namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Standard context record included in all platform-routed messages.
/// Contains the authenticated application identity, service token metadata,
/// allowed scopes, and timing information for every forwarded request.
/// </summary>
/// <remarks>
/// <para>
/// The AppContextRouter creates an <see cref="AppRequestContext"/> for each
/// validated incoming request and embeds it in the forwarded <c>PluginMessage.Payload</c>
/// so that downstream plugins can identify the originating application and enforce
/// per-app policies.
/// </para>
/// <para>
/// Use <see cref="ToDictionary"/> to serialize the context for embedding in a message payload.
/// Use <see cref="FromPayload"/> to reconstruct the context from a received payload.
/// </para>
/// </remarks>
public sealed record AppRequestContext
{
    /// <summary>
    /// Identifier of the application making the request.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Identifier of the service token used to authenticate the request.
    /// </summary>
    public required string TokenId { get; init; }

    /// <summary>
    /// Service scopes authorized for this request, as validated from the service token.
    /// </summary>
    public required string[] AllowedScopes { get; init; }

    /// <summary>
    /// UTC timestamp when the request arrived at the platform router.
    /// </summary>
    public required DateTime RequestTimestamp { get; init; }

    /// <summary>
    /// Optional correlation identifier for request tracing. Defaults to a new GUID.
    /// </summary>
    public string? RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Arbitrary additional context key-value pairs for extensibility.
    /// </summary>
    public Dictionary<string, object> AdditionalContext { get; init; } = new();

    /// <summary>
    /// Serializes this context into a <see cref="Dictionary{TKey, TValue}"/>
    /// suitable for embedding in <c>PluginMessage.Payload</c>.
    /// </summary>
    /// <returns>A dictionary containing all fields of this context.</returns>
    public Dictionary<string, object> ToDictionary()
    {
        var dict = new Dictionary<string, object>
        {
            ["AppId"] = AppId,
            ["TokenId"] = TokenId,
            ["AllowedScopes"] = AllowedScopes,
            ["RequestTimestamp"] = RequestTimestamp.ToString("O", CultureInfo.InvariantCulture),
            ["RequestId"] = RequestId ?? Guid.NewGuid().ToString("N"),
            ["AdditionalContext"] = AdditionalContext
        };

        return dict;
    }

    /// <summary>
    /// Reconstructs an <see cref="AppRequestContext"/> from a message payload dictionary.
    /// </summary>
    /// <param name="payload">The payload dictionary containing serialized context fields.</param>
    /// <returns>A reconstructed <see cref="AppRequestContext"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when required fields are missing from the payload.</exception>
    public static AppRequestContext FromPayload(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("AppId", out var appIdObj) || appIdObj is not string appId)
            throw new ArgumentException("Missing required field 'AppId' in payload.", nameof(payload));

        if (!payload.TryGetValue("TokenId", out var tokenIdObj) || tokenIdObj is not string tokenId)
            throw new ArgumentException("Missing required field 'TokenId' in payload.", nameof(payload));

        var allowedScopes = Array.Empty<string>();
        if (payload.TryGetValue("AllowedScopes", out var scopesObj))
        {
            allowedScopes = scopesObj switch
            {
                string[] arr => arr,
                object[] objs => objs.Select(o => o?.ToString() ?? string.Empty).ToArray(),
                _ => []
            };
        }

        var requestTimestamp = DateTime.UtcNow;
        if (payload.TryGetValue("RequestTimestamp", out var tsObj))
        {
            requestTimestamp = tsObj switch
            {
                DateTime dt => dt,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
                _ => DateTime.UtcNow
            };
        }

        string? requestId = null;
        if (payload.TryGetValue("RequestId", out var ridObj) && ridObj is string rid)
        {
            requestId = rid;
        }

        var additionalContext = new Dictionary<string, object>();
        if (payload.TryGetValue("AdditionalContext", out var acObj) && acObj is Dictionary<string, object> ac)
        {
            additionalContext = ac;
        }

        return new AppRequestContext
        {
            AppId = appId,
            TokenId = tokenId,
            AllowedScopes = allowedScopes,
            RequestTimestamp = requestTimestamp,
            RequestId = requestId,
            AdditionalContext = additionalContext
        };
    }
}
