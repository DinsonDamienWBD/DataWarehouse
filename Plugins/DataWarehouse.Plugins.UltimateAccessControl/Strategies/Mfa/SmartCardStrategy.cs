using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Mfa
{
    /// <summary>
    /// Smart card and PIV (Personal Identity Verification) MFA strategy.
    /// Provides certificate-based authentication using smart cards, CAC (Common Access Card),
    /// and PIV cards with PKCS#11 session management.
    /// </summary>
    public sealed class SmartCardStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, List<RegisteredCard>> _userCards = new();
        private readonly ConcurrentDictionary<string, SmartCardSession> _activeSessions = new();
        private const int SessionTimeoutMinutes = 15;
        private const int MaxCardsPerUser = 5;
        private const int PinMaxAttempts = 3;

        public override string StrategyId => "smart-card-mfa";
        public override string StrategyName => "Smart Card/PIV Multi-Factor Authentication";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 1000
        };

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(
            AccessContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                // Extract certificate from SubjectAttributes
                if (!context.SubjectAttributes.TryGetValue("certificate", out var certObj) ||
                    certObj is not byte[] certificateBytes)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Smart card certificate not provided in SubjectAttributes['certificate']",
                        ApplicablePolicies = new[] { "smart-card-required" }
                    };
                }

                // Extract signature (challenge response)
                if (!context.SubjectAttributes.TryGetValue("signature", out var sigObj) ||
                    sigObj is not byte[] signature)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Smart card signature not provided in SubjectAttributes['signature']",
                        ApplicablePolicies = new[] { "smart-card-signature-required" }
                    };
                }

                // Extract challenge
                if (!context.SubjectAttributes.TryGetValue("challenge", out var challengeObj) ||
                    challengeObj is not byte[] challenge)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Challenge not provided in SubjectAttributes['challenge']",
                        ApplicablePolicies = new[] { "smart-card-challenge-required" }
                    };
                }

                // Parse certificate
                X509Certificate2 certificate;
                try
                {
                    certificate = new X509Certificate2(certificateBytes);
                }
                catch (Exception ex)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Invalid certificate format: {ex.Message}",
                        ApplicablePolicies = new[] { "smart-card-invalid-certificate" }
                    };
                }

                // Validate certificate chain
                var chainValidation = ValidateCertificateChain(certificate);
                if (!chainValidation.IsValid)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Certificate chain validation failed: {chainValidation.Error}",
                        ApplicablePolicies = new[] { "smart-card-invalid-chain" }
                    };
                }

                // Check if certificate is registered to user
                if (!_userCards.TryGetValue(context.SubjectId, out var cards))
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "No smart cards registered for this user",
                        ApplicablePolicies = new[] { "smart-card-not-registered" }
                    };
                }

                var card = cards.FirstOrDefault(c => c.Thumbprint == certificate.Thumbprint);
                if (card == null)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Certificate not registered to this user",
                        ApplicablePolicies = new[] { "smart-card-cert-not-registered" }
                    };
                }

                // Verify signature (challenge-response)
                var signatureValid = VerifySignature(certificate, challenge, signature);
                if (!signatureValid)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Smart card signature verification failed",
                        ApplicablePolicies = new[] { "smart-card-invalid-signature" }
                    };
                }

                // Check card status
                if (card.IsRevoked)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Smart card has been revoked",
                        ApplicablePolicies = new[] { "smart-card-revoked" }
                    };
                }

                // Update card usage
                card.LastUsed = DateTime.UtcNow;
                card.UseCount++;

                // Extract PIV data if available
                var pivData = ExtractPivData(certificate);

                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Smart card authentication successful",
                    ApplicablePolicies = new[] { "smart-card-validated" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["mfa_method"] = "smart_card",
                        ["certificate_subject"] = certificate.Subject,
                        ["certificate_issuer"] = certificate.Issuer,
                        ["certificate_thumbprint"] = certificate.Thumbprint,
                        ["certificate_expiry"] = certificate.NotAfter,
                        ["card_type"] = card.CardType.ToString(),
                        ["piv_data"] = pivData
                    }
                };
            }
            catch (Exception ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Smart card validation error: {ex.Message}",
                    ApplicablePolicies = new[] { "smart-card-error" }
                };
            }
        }

        /// <summary>
        /// Registers a smart card certificate for a user.
        /// </summary>
        public CardRegistrationResult RegisterCard(
            string userId,
            byte[] certificateBytes,
            SmartCardType cardType,
            string? cardName = null)
        {
            try
            {
                // Parse certificate
                var certificate = new X509Certificate2(certificateBytes);

                // Validate certificate
                var chainValidation = ValidateCertificateChain(certificate);
                if (!chainValidation.IsValid)
                {
                    return new CardRegistrationResult
                    {
                        Success = false,
                        Message = $"Certificate validation failed: {chainValidation.Error}"
                    };
                }

                // Check expiration
                if (certificate.NotAfter < DateTime.UtcNow)
                {
                    return new CardRegistrationResult
                    {
                        Success = false,
                        Message = "Certificate has expired"
                    };
                }

                if (certificate.NotBefore > DateTime.UtcNow)
                {
                    return new CardRegistrationResult
                    {
                        Success = false,
                        Message = "Certificate is not yet valid"
                    };
                }

                // Get or create user cards list
                var userCards = _userCards.GetOrAdd(userId, _ => new List<RegisteredCard>());

                // Check card limit
                if (userCards.Count >= MaxCardsPerUser)
                {
                    return new CardRegistrationResult
                    {
                        Success = false,
                        Message = $"Maximum {MaxCardsPerUser} cards per user"
                    };
                }

                // Check for duplicate
                if (userCards.Any(c => c.Thumbprint == certificate.Thumbprint))
                {
                    return new CardRegistrationResult
                    {
                        Success = false,
                        Message = "Certificate already registered"
                    };
                }

                // Create card registration
                var card = new RegisteredCard
                {
                    CardId = GenerateCardId(),
                    CardType = cardType,
                    Name = cardName ?? $"{cardType} Card",
                    Thumbprint = certificate.Thumbprint,
                    Subject = certificate.Subject,
                    Issuer = certificate.Issuer,
                    NotBefore = certificate.NotBefore,
                    NotAfter = certificate.NotAfter,
                    RegisteredAt = DateTime.UtcNow,
                    UseCount = 0,
                    IsRevoked = false
                };

                lock (userCards)
                {
                    userCards.Add(card);
                }

                return new CardRegistrationResult
                {
                    Success = true,
                    Message = $"{cardType} card registered successfully",
                    CardId = card.CardId
                };
            }
            catch (Exception ex)
            {
                return new CardRegistrationResult
                {
                    Success = false,
                    Message = $"Card registration failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Revokes a smart card.
        /// </summary>
        public bool RevokeCard(string userId, string cardId)
        {
            if (!_userCards.TryGetValue(userId, out var cards))
            {
                return false;
            }

            lock (cards)
            {
                var card = cards.FirstOrDefault(c => c.CardId == cardId);
                if (card != null)
                {
                    card.IsRevoked = true;
                    card.RevokedAt = DateTime.UtcNow;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Validates X.509 certificate chain.
        /// </summary>
        private CertificateChainValidationResult ValidateCertificateChain(X509Certificate2 certificate)
        {
            try
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

                var isValid = chain.Build(certificate);

                if (!isValid)
                {
                    var errors = chain.ChainStatus
                        .Select(s => s.StatusInformation)
                        .ToList();

                    return new CertificateChainValidationResult
                    {
                        IsValid = false,
                        Error = string.Join("; ", errors)
                    };
                }

                return new CertificateChainValidationResult { IsValid = true };
            }
            catch (Exception ex)
            {
                return new CertificateChainValidationResult
                {
                    IsValid = false,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Verifies digital signature using certificate's public key.
        /// </summary>
        private bool VerifySignature(X509Certificate2 certificate, byte[] data, byte[] signature)
        {
            try
            {
                using var publicKey = certificate.GetRSAPublicKey() ?? certificate.GetECDsaPublicKey() as AsymmetricAlgorithm;
                if (publicKey == null)
                {
                    return false;
                }

                if (publicKey is RSA rsa)
                {
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
                else if (publicKey is ECDsa ecdsa)
                {
                    return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Extracts PIV (Personal Identity Verification) data from certificate.
        /// </summary>
        private Dictionary<string, string> ExtractPivData(X509Certificate2 certificate)
        {
            var pivData = new Dictionary<string, string>();

            // Extract common PIV fields from subject
            var subjectParts = certificate.Subject.Split(',');
            foreach (var part in subjectParts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2)
                {
                    pivData[kv[0].Trim()] = kv[1].Trim();
                }
            }

            // Extract PIV-specific OIDs if present
            foreach (var extension in certificate.Extensions)
            {
                if (extension.Oid?.Value?.StartsWith("2.16.840.1.101.3.6") == true) // PIV OID prefix
                {
                    pivData[$"PIV_{extension.Oid.FriendlyName ?? extension.Oid.Value}"] =
                        Convert.ToBase64String(extension.RawData);
                }
            }

            return pivData;
        }

        /// <summary>
        /// Generates cryptographically secure card ID.
        /// </summary>
        private string GenerateCardId()
        {
            var buffer = new byte[16];
            RandomNumberGenerator.Fill(buffer);
            return Convert.ToHexString(buffer);
        }

        /// <summary>
        /// Registered smart card data.
        /// </summary>
        private sealed class RegisteredCard
        {
            public required string CardId { get; init; }
            public required SmartCardType CardType { get; init; }
            public required string Name { get; init; }
            public required string Thumbprint { get; init; }
            public required string Subject { get; init; }
            public required string Issuer { get; init; }
            public required DateTime NotBefore { get; init; }
            public required DateTime NotAfter { get; init; }
            public required DateTime RegisteredAt { get; init; }
            public DateTime? LastUsed { get; set; }
            public int UseCount { get; set; }
            public bool IsRevoked { get; set; }
            public DateTime? RevokedAt { get; set; }
        }

        /// <summary>
        /// Active smart card session (for PIN caching).
        /// </summary>
        private sealed class SmartCardSession
        {
            public required string SessionId { get; init; }
            public required string UserId { get; init; }
            public required DateTime CreatedAt { get; init; }
            public required DateTime ExpiresAt { get; init; }
            public int PinAttempts { get; set; }
        }

        /// <summary>
        /// Certificate chain validation result.
        /// </summary>
        private sealed class CertificateChainValidationResult
        {
            public required bool IsValid { get; init; }
            public string? Error { get; init; }
        }
    }

    /// <summary>
    /// Smart card type enumeration.
    /// </summary>
    public enum SmartCardType
    {
        /// <summary>
        /// Generic smart card.
        /// </summary>
        Generic,

        /// <summary>
        /// Personal Identity Verification (PIV) card (FIPS 201).
        /// </summary>
        PIV,

        /// <summary>
        /// Common Access Card (US DoD).
        /// </summary>
        CAC,

        /// <summary>
        /// European Citizen Card.
        /// </summary>
        EuropeanCitizenCard,

        /// <summary>
        /// National ID card with certificate.
        /// </summary>
        NationalID
    }

    /// <summary>
    /// Smart card registration result.
    /// </summary>
    public sealed class CardRegistrationResult
    {
        /// <summary>
        /// Whether registration succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Result message.
        /// </summary>
        public required string Message { get; init; }

        /// <summary>
        /// Card ID (if successful).
        /// </summary>
        public string? CardId { get; init; }
    }
}
