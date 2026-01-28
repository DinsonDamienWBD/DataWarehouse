// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Models;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;

/// <summary>
/// Interface for external platform integrations.
/// Supports chat platforms, AI assistants, and custom integrations.
/// </summary>
public interface IIntegrationProvider
{
    /// <summary>Platform identifier.</summary>
    string PlatformId { get; }

    /// <summary>Platform display name.</summary>
    string PlatformName { get; }

    /// <summary>Whether this integration is properly configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Handle an incoming webhook event.</summary>
    Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default);

    /// <summary>Validate a webhook signature.</summary>
    bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers);

    /// <summary>Get the webhook endpoint URL pattern.</summary>
    string GetWebhookEndpoint();

    /// <summary>Get integration configuration/manifest.</summary>
    object GetConfiguration();
}

/// <summary>
/// Base implementation for integration providers.
/// </summary>
public abstract class IntegrationProviderBase : IIntegrationProvider
{
    public abstract string PlatformId { get; }
    public abstract string PlatformName { get; }
    public abstract bool IsConfigured { get; }

    public abstract Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default);
    public abstract bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers);
    public abstract object GetConfiguration();

    public virtual string GetWebhookEndpoint() => $"/api/integrations/{PlatformId}/webhook";

    /// <summary>Helper to create a success response.</summary>
    protected IntegrationResponse SuccessResponse(object? body = null, string contentType = "application/json")
    {
        return new IntegrationResponse
        {
            StatusCode = 200,
            Body = body,
            ContentType = contentType
        };
    }

    /// <summary>Helper to create an error response.</summary>
    protected IntegrationResponse ErrorResponse(int statusCode, string message)
    {
        return new IntegrationResponse
        {
            StatusCode = statusCode,
            Body = new { error = message },
            ContentType = "application/json"
        };
    }

    /// <summary>Helper to create a verification response (for webhook setup).</summary>
    protected IntegrationResponse VerificationResponse(string challenge)
    {
        return new IntegrationResponse
        {
            StatusCode = 200,
            Body = challenge,
            ContentType = "text/plain"
        };
    }
}
