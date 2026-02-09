// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Message bus integration service for TamperProof plugin.
/// Enables communication with Ultimate plugins (T93-Encryption, T94-KeyManagement, T95-AccessControl).
/// </summary>
public class MessageBusIntegrationService
{
    private readonly IMessageBusClient? _messageBus;
    private readonly ILogger<MessageBusIntegrationService> _logger;
    private readonly TamperProofConfiguration _config;

    private const string EncryptionTopic = "ultimate.encryption";
    private const string KeyManagementTopic = "ultimate.keymanagement";
    private const string AccessControlTopic = "ultimate.accesscontrol";
    private const string TamperProofAlertsTopic = "tamperproof.alerts";
    private const string TamperProofIncidentsTopic = "tamperproof.incidents";

    /// <summary>
    /// Creates a new message bus integration service.
    /// </summary>
    /// <param name="messageBus">Optional message bus client.</param>
    /// <param name="config">TamperProof configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public MessageBusIntegrationService(
        IMessageBusClient? messageBus,
        TamperProofConfiguration config,
        ILogger<MessageBusIntegrationService> logger)
    {
        _messageBus = messageBus;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether message bus is available.
    /// </summary>
    public bool IsAvailable => _messageBus != null;

    /// <summary>
    /// Requests encryption of data via UltimateEncryption (T93).
    /// </summary>
    /// <param name="data">Data to encrypt.</param>
    /// <param name="keyId">Key identifier.</param>
    /// <param name="algorithmHint">Optional algorithm hint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Encrypted data or null if not available.</returns>
    public async Task<EncryptionResponse?> RequestEncryptionAsync(
        byte[] data,
        string keyId,
        string? algorithmHint = null,
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _logger.LogDebug("Message bus not available, encryption request skipped");
            return null;
        }

        try
        {
            var request = new EncryptionRequest
            {
                RequestId = Guid.NewGuid(),
                Data = data,
                KeyId = keyId,
                AlgorithmHint = algorithmHint,
                RequestedAt = DateTimeOffset.UtcNow
            };

            _logger.LogDebug("Sending encryption request {RequestId} to {Topic}",
                request.RequestId, EncryptionTopic);

            var response = await _messageBus.RequestAsync<EncryptionRequest, EncryptionResponse>(
                $"{EncryptionTopic}.encrypt",
                request,
                TimeSpan.FromSeconds(30),
                ct);

            if (response?.Success == true)
            {
                _logger.LogDebug("Encryption request {RequestId} completed successfully", request.RequestId);
            }
            else
            {
                _logger.LogWarning("Encryption request {RequestId} failed: {Error}",
                    request.RequestId, response?.ErrorMessage ?? "No response");
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Encryption request failed");
            return null;
        }
    }

    /// <summary>
    /// Requests decryption of data via UltimateEncryption (T93).
    /// </summary>
    /// <param name="encryptedData">Encrypted data.</param>
    /// <param name="keyId">Key identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Decrypted data or null if not available.</returns>
    public async Task<DecryptionResponse?> RequestDecryptionAsync(
        byte[] encryptedData,
        string keyId,
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _logger.LogDebug("Message bus not available, decryption request skipped");
            return null;
        }

        try
        {
            var request = new DecryptionRequest
            {
                RequestId = Guid.NewGuid(),
                EncryptedData = encryptedData,
                KeyId = keyId,
                RequestedAt = DateTimeOffset.UtcNow
            };

            _logger.LogDebug("Sending decryption request {RequestId} to {Topic}",
                request.RequestId, EncryptionTopic);

            var response = await _messageBus.RequestAsync<DecryptionRequest, DecryptionResponse>(
                $"{EncryptionTopic}.decrypt",
                request,
                TimeSpan.FromSeconds(30),
                ct);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decryption request failed");
            return null;
        }
    }

    /// <summary>
    /// Requests a new encryption key from UltimateKeyManagement (T94).
    /// </summary>
    /// <param name="keyPurpose">Purpose of the key.</param>
    /// <param name="keyType">Type of key to generate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Key generation response or null if not available.</returns>
    public async Task<KeyGenerationResponse?> RequestKeyGenerationAsync(
        string keyPurpose,
        string keyType = "AES256",
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _logger.LogDebug("Message bus not available, key generation request skipped");
            return null;
        }

        try
        {
            var request = new KeyGenerationRequest
            {
                RequestId = Guid.NewGuid(),
                Purpose = keyPurpose,
                KeyType = keyType,
                RequestedAt = DateTimeOffset.UtcNow
            };

            _logger.LogDebug("Sending key generation request {RequestId} to {Topic}",
                request.RequestId, KeyManagementTopic);

            var response = await _messageBus.RequestAsync<KeyGenerationRequest, KeyGenerationResponse>(
                $"{KeyManagementTopic}.generate",
                request,
                TimeSpan.FromSeconds(30),
                ct);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Key generation request failed");
            return null;
        }
    }

    /// <summary>
    /// Requests key retrieval from UltimateKeyManagement (T94).
    /// </summary>
    /// <param name="keyId">Key identifier to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Key retrieval response or null if not available.</returns>
    public async Task<KeyRetrievalResponse?> RequestKeyRetrievalAsync(
        string keyId,
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _logger.LogDebug("Message bus not available, key retrieval request skipped");
            return null;
        }

        try
        {
            var request = new KeyRetrievalRequest
            {
                RequestId = Guid.NewGuid(),
                KeyId = keyId,
                RequestedAt = DateTimeOffset.UtcNow
            };

            var response = await _messageBus.RequestAsync<KeyRetrievalRequest, KeyRetrievalResponse>(
                $"{KeyManagementTopic}.retrieve",
                request,
                TimeSpan.FromSeconds(30),
                ct);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Key retrieval request failed");
            return null;
        }
    }

    /// <summary>
    /// Validates WORM access via UltimateAccessControl (T95).
    /// </summary>
    /// <param name="objectId">Object ID to access.</param>
    /// <param name="principal">Principal requesting access.</param>
    /// <param name="accessType">Type of access requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Access validation response or null if not available.</returns>
    public async Task<WormAccessValidationResponse?> ValidateWormAccessAsync(
        Guid objectId,
        string principal,
        AccessType accessType,
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _logger.LogDebug("Message bus not available, WORM access validation skipped");
            return null;
        }

        try
        {
            var request = new WormAccessValidationRequest
            {
                RequestId = Guid.NewGuid(),
                ObjectId = objectId,
                Principal = principal,
                AccessType = accessType,
                RequestedAt = DateTimeOffset.UtcNow
            };

            var response = await _messageBus.RequestAsync<WormAccessValidationRequest, WormAccessValidationResponse>(
                $"{AccessControlTopic}.worm.validate",
                request,
                TimeSpan.FromSeconds(10),
                ct);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WORM access validation request failed");
            return null;
        }
    }

    /// <summary>
    /// Publishes a tamper alert to the message bus.
    /// </summary>
    /// <param name="incident">Tamper incident to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishTamperAlertAsync(TamperIncidentReport incident, CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _logger.LogWarning("Message bus not available, tamper alert not published");
            return;
        }

        if (!_config.Alerts.PublishToMessageBus)
        {
            _logger.LogDebug("Message bus alerts disabled, tamper alert not published");
            return;
        }

        try
        {
            var alert = new TamperAlert
            {
                AlertId = Guid.NewGuid(),
                IncidentId = incident.IncidentId,
                ObjectId = incident.ObjectId,
                Severity = DetermineAlertSeverity(incident),
                Message = $"Tampering detected on object {incident.ObjectId}",
                IncidentDetails = incident,
                PublishedAt = DateTimeOffset.UtcNow
            };

            await _messageBus.PublishAsync(
                _config.Alerts.MessageBusTopic,
                alert,
                ct);

            _logger.LogInformation("Published tamper alert {AlertId} for incident {IncidentId}",
                alert.AlertId, incident.IncidentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish tamper alert for incident {IncidentId}", incident.IncidentId);
        }
    }

    /// <summary>
    /// Publishes a recovery notification to the message bus.
    /// </summary>
    /// <param name="objectId">Object ID that was recovered.</param>
    /// <param name="recoverySource">Source of recovery.</param>
    /// <param name="success">Whether recovery succeeded.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishRecoveryNotificationAsync(
        Guid objectId,
        string recoverySource,
        bool success,
        CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            return;
        }

        try
        {
            var notification = new RecoveryNotification
            {
                NotificationId = Guid.NewGuid(),
                ObjectId = objectId,
                RecoverySource = recoverySource,
                Success = success,
                NotifiedAt = DateTimeOffset.UtcNow
            };

            await _messageBus.PublishAsync(
                $"{TamperProofIncidentsTopic}.recovery",
                notification,
                ct);

            _logger.LogDebug("Published recovery notification for object {ObjectId}", objectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish recovery notification for object {ObjectId}", objectId);
        }
    }

    /// <summary>
    /// Determines the severity of a tamper alert based on incident details.
    /// </summary>
    private static AlertSeverity DetermineAlertSeverity(TamperIncidentReport incident)
    {
        // Critical if recovery failed
        if (!incident.RecoverySucceeded)
            return AlertSeverity.Critical;

        // Error if attributed to a specific principal
        if (incident.AttributionConfidence >= AttributionConfidence.Likely)
            return AlertSeverity.Error;

        // Warning for recovered incidents
        return AlertSeverity.Warning;
    }
}

#region Message Bus Contracts

/// <summary>
/// Interface for message bus client.
/// </summary>
public interface IMessageBusClient
{
    /// <summary>
    /// Sends a request and waits for a response.
    /// </summary>
    Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string topic,
        TRequest request,
        TimeSpan timeout,
        CancellationToken ct = default);

    /// <summary>
    /// Publishes a message to a topic.
    /// </summary>
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default);
}

/// <summary>
/// Request to encrypt data via UltimateEncryption.
/// </summary>
public class EncryptionRequest
{
    public required Guid RequestId { get; init; }
    public required byte[] Data { get; init; }
    public required string KeyId { get; init; }
    public string? AlgorithmHint { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// Response from encryption request.
/// </summary>
public class EncryptionResponse
{
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public byte[]? EncryptedData { get; init; }
    public string? Algorithm { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to decrypt data via UltimateEncryption.
/// </summary>
public class DecryptionRequest
{
    public required Guid RequestId { get; init; }
    public required byte[] EncryptedData { get; init; }
    public required string KeyId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// Response from decryption request.
/// </summary>
public class DecryptionResponse
{
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public byte[]? DecryptedData { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to generate a new key via UltimateKeyManagement.
/// </summary>
public class KeyGenerationRequest
{
    public required Guid RequestId { get; init; }
    public required string Purpose { get; init; }
    public required string KeyType { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// Response from key generation request.
/// </summary>
public class KeyGenerationResponse
{
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public string? KeyId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to retrieve a key via UltimateKeyManagement.
/// </summary>
public class KeyRetrievalRequest
{
    public required Guid RequestId { get; init; }
    public required string KeyId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// Response from key retrieval request.
/// </summary>
public class KeyRetrievalResponse
{
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public byte[]? KeyMaterial { get; init; }
    public string? KeyType { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to validate WORM access via UltimateAccessControl.
/// </summary>
public class WormAccessValidationRequest
{
    public required Guid RequestId { get; init; }
    public required Guid ObjectId { get; init; }
    public required string Principal { get; init; }
    public required AccessType AccessType { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// Response from WORM access validation request.
/// </summary>
public class WormAccessValidationResponse
{
    public required Guid RequestId { get; init; }
    public required bool Allowed { get; init; }
    public string? DenialReason { get; init; }
    public DateTimeOffset? RetentionExpiresAt { get; init; }
    public bool? HasLegalHold { get; init; }
}

/// <summary>
/// Tamper alert for message bus publication.
/// </summary>
public class TamperAlert
{
    public required Guid AlertId { get; init; }
    public required Guid IncidentId { get; init; }
    public required Guid ObjectId { get; init; }
    public required AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public TamperIncidentReport? IncidentDetails { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>
/// Recovery notification for message bus publication.
/// </summary>
public class RecoveryNotification
{
    public required Guid NotificationId { get; init; }
    public required Guid ObjectId { get; init; }
    public required string RecoverySource { get; init; }
    public required bool Success { get; init; }
    public required DateTimeOffset NotifiedAt { get; init; }
}

#endregion
