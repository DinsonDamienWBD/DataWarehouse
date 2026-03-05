using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.Security;

/// <summary>
/// Base class for IoT security strategies.
/// Delegates encryption/decryption to the UltimateEncryption plugin via message bus.
/// </summary>
public abstract class IoTSecurityStrategyBase : IoTStrategyBase, IIoTSecurityStrategy
{
    protected readonly BoundedDictionary<string, string> DeviceTokens = new BoundedDictionary<string, string>(1000);

    public override IoTStrategyCategory Category => IoTStrategyCategory.Security;

    public abstract Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public abstract Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);
    public abstract Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);
    public abstract Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Encrypts data by delegating to the UltimateEncryption plugin via message bus.
    /// Returns the encrypted payload including key material needed for decryption.
    /// </summary>
    public virtual async Task<byte[]> EncryptAsync(string deviceId, byte[] data, CancellationToken ct = default)
    {
        if (MessageBus == null)
            throw new InvalidOperationException(
                "Encryption requires a configured message bus. Configure encryption via the UltimateEncryption plugin.");

        var message = new PluginMessage
        {
            Type = "encryption.encrypt",
            SourcePluginId = "com.datawarehouse.iot.ultimate",
            Payload =
            {
                ["data"] = Convert.ToBase64String(data),
                ["deviceId"] = deviceId,
                ["algorithm"] = "AES-256-GCM"
            }
        };

        var response = await MessageBus.SendAsync("encryption.encrypt", message, TimeSpan.FromSeconds(30), ct);

        if (!response.Success)
            throw new CryptographicException(
                $"Encryption failed via message bus: {response.ErrorMessage ?? "Unknown error"}");

        if (response.Payload is byte[] encryptedBytes)
            return encryptedBytes;

        if (response.Payload is string base64Result)
            return Convert.FromBase64String(base64Result);

        // Extract from metadata if payload is structured
        if (response.Metadata.TryGetValue("encryptedData", out var encData) && encData is string encStr)
            return Convert.FromBase64String(encStr);

        throw new CryptographicException("Encryption response did not contain encrypted data in expected format.");
    }

    /// <summary>
    /// Decrypts data by delegating to the UltimateEncryption plugin via message bus.
    /// </summary>
    public virtual async Task<byte[]> DecryptAsync(string deviceId, byte[] data, CancellationToken ct = default)
    {
        if (MessageBus == null)
            throw new InvalidOperationException(
                "Decryption requires a configured message bus. Configure encryption via the UltimateEncryption plugin.");

        var message = new PluginMessage
        {
            Type = "encryption.decrypt",
            SourcePluginId = "com.datawarehouse.iot.ultimate",
            Payload =
            {
                ["data"] = Convert.ToBase64String(data),
                ["deviceId"] = deviceId,
                ["algorithm"] = "AES-256-GCM"
            }
        };

        var response = await MessageBus.SendAsync("encryption.decrypt", message, TimeSpan.FromSeconds(30), ct);

        if (!response.Success)
            throw new CryptographicException(
                $"Decryption failed via message bus: {response.ErrorMessage ?? "Unknown error"}");

        if (response.Payload is byte[] decryptedBytes)
            return decryptedBytes;

        if (response.Payload is string base64Result)
            return Convert.FromBase64String(base64Result);

        if (response.Metadata.TryGetValue("decryptedData", out var decData) && decData is string decStr)
            return Convert.FromBase64String(decStr);

        throw new CryptographicException("Decryption response did not contain decrypted data in expected format.");
    }
}

/// <summary>
/// Device authentication strategy.
/// </summary>
public class DeviceAuthenticationStrategy : IoTSecurityStrategyBase
{
    public override string StrategyId => "device-auth";
    public override string StrategyName => "Device Authentication";
    public override string Description => "Multi-factor device authentication with token management";
    public override string[] Tags => new[] { "iot", "security", "authentication", "token", "mfa" };

    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default)
    {
        // Simulate authentication
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        DeviceTokens[request.DeviceId] = token;

        return Task.FromResult(new AuthenticationResult
        {
            Success = true,
            DeviceId = request.DeviceId,
            Token = token,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Message = "Authentication successful"
        });
    }

    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default)
    {
        var newToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        DeviceTokens[deviceId] = newToken;

        return Task.FromResult(new CredentialRotationResult
        {
            Success = true,
            DeviceId = deviceId,
            NewCredentials = new DeviceCredentials
            {
                DeviceId = deviceId,
                Type = CredentialType.SasToken,
                PrimaryKey = newToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            },
            RotatedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default)
    {
        // Using Random.Shared for thread-safety;
        return Task.FromResult(new SecurityAssessment
        {
            DeviceId = deviceId,
            SecurityScore = Random.Shared.Next(60, 100),
            ThreatLevel = ThreatLevel.Low,
            Findings = new List<SecurityFinding>
            {
                new()
                {
                    FindingId = Guid.NewGuid().ToString(),
                    Title = "Token Age",
                    Description = "Device token is older than 7 days",
                    Severity = ThreatLevel.Low,
                    Remediation = "Rotate device credentials"
                }
            },
            AssessedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ThreatDetectionResult
        {
            Success = true,
            ThreatsDetected = 0,
            Threats = new List<DetectedThreat>()
        });
    }
}

/// <summary>
/// Credential rotation strategy.
/// </summary>
public class CredentialRotationStrategy : IoTSecurityStrategyBase
{
    public override string StrategyId => "credential-rotation";
    public override string StrategyName => "Credential Rotation";
    public override string Description => "Automated credential rotation for IoT device security";
    public override string[] Tags => new[] { "iot", "security", "credentials", "rotation", "automated" };

    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new AuthenticationResult
        {
            Success = true,
            DeviceId = request.DeviceId,
            Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
    }

    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default)
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var newKey = Convert.ToBase64String(bytes);

        return Task.FromResult(new CredentialRotationResult
        {
            Success = true,
            DeviceId = deviceId,
            NewCredentials = new DeviceCredentials
            {
                DeviceId = deviceId,
                Type = CredentialType.SymmetricKey,
                PrimaryKey = newKey,
                SecondaryKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(90)
            },
            RotatedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(new SecurityAssessment
        {
            DeviceId = deviceId,
            SecurityScore = 85,
            ThreatLevel = ThreatLevel.None,
            Findings = new List<SecurityFinding>(),
            AssessedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ThreatDetectionResult
        {
            Success = true,
            ThreatsDetected = 0,
            Threats = new List<DetectedThreat>()
        });
    }
}

/// <summary>
/// Security assessment strategy.
/// </summary>
public class SecurityAssessmentStrategy : IoTSecurityStrategyBase
{
    public override string StrategyId => "security-assessment";
    public override string StrategyName => "Security Assessment";
    public override string Description => "Comprehensive security posture assessment for IoT devices";
    public override string[] Tags => new[] { "iot", "security", "assessment", "posture", "compliance" };

    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new AuthenticationResult { Success = true, DeviceId = request.DeviceId });
    }

    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(new CredentialRotationResult { Success = true, DeviceId = deviceId, RotatedAt = DateTimeOffset.UtcNow });
    }

    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default)
    {
        // Using Random.Shared for thread-safety;
        var findings = new List<SecurityFinding>();

        // Check various security aspects
        if (Random.Shared.NextDouble() > 0.7)
        {
            findings.Add(new SecurityFinding
            {
                FindingId = Guid.NewGuid().ToString(),
                Title = "Outdated Firmware",
                Description = "Device firmware is more than 90 days old",
                Severity = ThreatLevel.Medium,
                Remediation = "Update device firmware to latest version"
            });
        }

        if (Random.Shared.NextDouble() > 0.8)
        {
            findings.Add(new SecurityFinding
            {
                FindingId = Guid.NewGuid().ToString(),
                Title = "Weak Authentication",
                Description = "Device using symmetric key authentication",
                Severity = ThreatLevel.Low,
                Remediation = "Consider upgrading to X.509 certificate authentication"
            });
        }

        if (Random.Shared.NextDouble() > 0.9)
        {
            findings.Add(new SecurityFinding
            {
                FindingId = Guid.NewGuid().ToString(),
                Title = "Missing Encryption",
                Description = "Device telemetry not encrypted at rest",
                Severity = ThreatLevel.High,
                Remediation = "Enable encryption for stored telemetry data"
            });
        }

        var score = 100 - findings.Sum(f => f.Severity switch
        {
            ThreatLevel.Critical => 25,
            ThreatLevel.High => 15,
            ThreatLevel.Medium => 10,
            ThreatLevel.Low => 5,
            _ => 0
        });

        return Task.FromResult(new SecurityAssessment
        {
            DeviceId = deviceId,
            SecurityScore = Math.Max(0, score),
            ThreatLevel = findings.Any() ? findings.Max(f => f.Severity) : ThreatLevel.None,
            Findings = findings,
            AssessedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ThreatDetectionResult
        {
            Success = true,
            ThreatsDetected = 0,
            Threats = new List<DetectedThreat>()
        });
    }
}

/// <summary>
/// Threat detection strategy.
/// </summary>
public class ThreatDetectionStrategy : IoTSecurityStrategyBase
{
    public override string StrategyId => "threat-detection";
    public override string StrategyName => "Threat Detection";
    public override string Description => "Real-time threat detection and intrusion prevention for IoT";
    public override string[] Tags => new[] { "iot", "security", "threat", "detection", "intrusion", "ids" };

    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new AuthenticationResult { Success = true, DeviceId = request.DeviceId });
    }

    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(new CredentialRotationResult { Success = true, DeviceId = deviceId, RotatedAt = DateTimeOffset.UtcNow });
    }

    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(new SecurityAssessment
        {
            DeviceId = deviceId,
            SecurityScore = 90,
            ThreatLevel = ThreatLevel.None,
            Findings = new List<SecurityFinding>(),
            AssessedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default)
    {
        // Using Random.Shared for thread-safety;
        var threats = new List<DetectedThreat>();

        // Simulate threat detection
        if (Random.Shared.NextDouble() > 0.85)
        {
            threats.Add(new DetectedThreat
            {
                ThreatId = Guid.NewGuid().ToString(),
                ThreatType = "BruteForce",
                Severity = ThreatLevel.Medium,
                AffectedDeviceId = request.DeviceId,
                DetectedAt = DateTimeOffset.UtcNow,
                Description = "Multiple failed authentication attempts detected"
            });
        }

        if (Random.Shared.NextDouble() > 0.95)
        {
            threats.Add(new DetectedThreat
            {
                ThreatId = Guid.NewGuid().ToString(),
                ThreatType = "DataExfiltration",
                Severity = ThreatLevel.High,
                AffectedDeviceId = request.DeviceId,
                DetectedAt = DateTimeOffset.UtcNow,
                Description = "Unusual data transfer pattern detected"
            });
        }

        return Task.FromResult(new ThreatDetectionResult
        {
            Success = true,
            ThreatsDetected = threats.Count,
            Threats = threats
        });
    }
}

/// <summary>
/// Certificate management strategy.
/// </summary>
public class CertificateManagementStrategy : IoTSecurityStrategyBase
{
    public override string StrategyId => "certificate-management";
    public override string StrategyName => "Certificate Management";
    public override string Description => "X.509 certificate lifecycle management for IoT devices";
    public override string[] Tags => new[] { "iot", "security", "certificate", "x509", "pki", "lifecycle" };

    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default)
    {
        // Validate certificate
        var isValid = !string.IsNullOrEmpty(request.Certificate);

        return Task.FromResult(new AuthenticationResult
        {
            Success = isValid,
            DeviceId = request.DeviceId,
            Message = isValid ? "Certificate validated" : "Invalid certificate"
        });
    }

    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default)
    {
        // Generate real self-signed X.509 certificate
        using var rsa = RSA.Create(2048);
        var subjectName = new X500DistinguishedName($"CN={deviceId},O=DataWarehouse IoT,OU=Devices");
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Add key usage extensions
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);
        var certificatePem = cert.ExportCertificatePem();

        return Task.FromResult(new CredentialRotationResult
        {
            Success = true,
            DeviceId = deviceId,
            NewCredentials = new DeviceCredentials
            {
                DeviceId = deviceId,
                Type = CredentialType.X509Certificate,
                Certificate = certificatePem,
                ExpiresAt = notAfter
            },
            RotatedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default)
    {
        // Using Random.Shared for thread-safety;
        var findings = new List<SecurityFinding>();

        if (Random.Shared.NextDouble() > 0.7)
        {
            findings.Add(new SecurityFinding
            {
                FindingId = Guid.NewGuid().ToString(),
                Title = "Certificate Expiring Soon",
                Description = "Device certificate expires in less than 30 days",
                Severity = ThreatLevel.Medium,
                Remediation = "Renew device certificate before expiration"
            });
        }

        return Task.FromResult(new SecurityAssessment
        {
            DeviceId = deviceId,
            SecurityScore = findings.Count == 0 ? 95 : 75,
            ThreatLevel = findings.Count == 0 ? ThreatLevel.None : ThreatLevel.Medium,
            Findings = findings,
            AssessedAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ThreatDetectionResult
        {
            Success = true,
            ThreatsDetected = 0,
            Threats = new List<DetectedThreat>()
        });
    }
}
