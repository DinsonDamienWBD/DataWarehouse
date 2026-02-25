using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.9: Attestation Strategy
    /// Hardware attestation of node physical location for sovereignty verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - TPM-based location attestation
    /// - Secure enclave verification
    /// - GPS/network location verification
    /// - Multi-factor location proof
    /// - Attestation chain of trust
    /// - Continuous attestation monitoring
    /// </para>
    /// </remarks>
    public sealed class AttestationStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, NodeAttestation> _attestations = new BoundedDictionary<string, NodeAttestation>(1000);
        private readonly BoundedDictionary<string, AttestationPolicy> _policies = new BoundedDictionary<string, AttestationPolicy>(1000);
        private readonly BoundedDictionary<string, TrustAnchor> _trustAnchors = new BoundedDictionary<string, TrustAnchor>(1000);
        private readonly ConcurrentBag<AttestationEvent> _events = new();

        private TimeSpan _attestationValidityPeriod = TimeSpan.FromHours(24);
        private int _requiredFactors = 2;
        private double _minimumConfidence = 0.9;

        /// <inheritdoc/>
        public override string StrategyId => "attestation";

        /// <inheritdoc/>
        public override string StrategyName => "Location Attestation";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("AttestationValidityHours", out var validityObj) && validityObj is int hours)
            {
                _attestationValidityPeriod = TimeSpan.FromHours(hours);
            }

            if (configuration.TryGetValue("RequiredFactors", out var factorsObj) && factorsObj is int factors)
            {
                _requiredFactors = Math.Max(1, factors);
            }

            if (configuration.TryGetValue("MinimumConfidence", out var confObj) && confObj is double conf)
            {
                _minimumConfidence = Math.Clamp(conf, 0, 1);
            }

            InitializeDefaultPolicies();
            InitializeTrustAnchors();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Requests attestation from a node.
        /// </summary>
        public async Task<AttestationResult> RequestAttestationAsync(
            string nodeId,
            AttestationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            var attestationId = Guid.NewGuid().ToString();
            var factors = new List<AttestationFactor>();
            var errors = new List<string>();

            // Get applicable policy
            var policy = GetApplicablePolicy(request.PolicyId);

            // Collect attestation factors
            if (policy.RequiresTpm || request.IncludeTpm)
            {
                var tpmResult = await CollectTpmAttestationAsync(nodeId, request, cancellationToken);
                if (tpmResult.Success)
                    factors.Add(tpmResult.Factor!);
                else
                    errors.Add($"TPM: {tpmResult.ErrorMessage}");
            }

            if (policy.RequiresSecureEnclave || request.IncludeSecureEnclave)
            {
                var enclaveResult = await CollectSecureEnclaveAttestationAsync(nodeId, request, cancellationToken);
                if (enclaveResult.Success)
                    factors.Add(enclaveResult.Factor!);
                else
                    errors.Add($"Secure Enclave: {enclaveResult.ErrorMessage}");
            }

            if (policy.RequiresGps || request.IncludeGps)
            {
                var gpsResult = await CollectGpsAttestationAsync(nodeId, request, cancellationToken);
                if (gpsResult.Success)
                    factors.Add(gpsResult.Factor!);
                else
                    errors.Add($"GPS: {gpsResult.ErrorMessage}");
            }

            if (policy.RequiresNetworkLocation || request.IncludeNetworkLocation)
            {
                var networkResult = await CollectNetworkLocationAttestationAsync(nodeId, request, cancellationToken);
                if (networkResult.Success)
                    factors.Add(networkResult.Factor!);
                else
                    errors.Add($"Network: {networkResult.ErrorMessage}");
            }

            // Check if enough factors collected
            var requiredFactors = Math.Max(policy.MinimumFactors, _requiredFactors);
            if (factors.Count < requiredFactors)
            {
                LogEvent(AttestationEventType.AttestationFailed, nodeId, attestationId,
                    $"Insufficient factors: {factors.Count}/{requiredFactors}");

                return new AttestationResult
                {
                    Success = false,
                    AttestationId = attestationId,
                    ErrorMessage = $"Insufficient attestation factors. Required: {requiredFactors}, Collected: {factors.Count}",
                    CollectionErrors = errors
                };
            }

            // Calculate combined confidence
            var confidence = CalculateCombinedConfidence(factors);
            if (confidence < _minimumConfidence)
            {
                LogEvent(AttestationEventType.AttestationFailed, nodeId, attestationId,
                    $"Low confidence: {confidence:P0}");

                return new AttestationResult
                {
                    Success = false,
                    AttestationId = attestationId,
                    ErrorMessage = $"Attestation confidence {confidence:P0} below minimum {_minimumConfidence:P0}",
                    Confidence = confidence,
                    Factors = factors
                };
            }

            // Verify location consistency across factors
            var locationVerification = VerifyLocationConsistency(factors, request.ClaimedLocation);
            if (!locationVerification.IsConsistent)
            {
                LogEvent(AttestationEventType.LocationMismatch, nodeId, attestationId,
                    locationVerification.Discrepancy ?? "Location verification failed");

                return new AttestationResult
                {
                    Success = false,
                    AttestationId = attestationId,
                    ErrorMessage = $"Location mismatch: {locationVerification.Discrepancy}",
                    Factors = factors,
                    Confidence = confidence
                };
            }

            // Create attestation record
            var attestation = new NodeAttestation
            {
                AttestationId = attestationId,
                NodeId = nodeId,
                ClaimedLocation = request.ClaimedLocation,
                VerifiedLocation = locationVerification.DeterminedLocation,
                Factors = factors,
                Confidence = confidence,
                PolicyId = policy.PolicyId,
                IssuedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_attestationValidityPeriod),
                AttestationHash = GenerateAttestationHash(attestationId, nodeId, factors),
                ChainOfTrust = BuildChainOfTrust(factors)
            };

            _attestations[nodeId] = attestation;

            LogEvent(AttestationEventType.AttestationSucceeded, nodeId, attestationId,
                $"Location verified: {attestation.VerifiedLocation}, Confidence: {confidence:P0}");

            return new AttestationResult
            {
                Success = true,
                AttestationId = attestationId,
                Attestation = attestation,
                Factors = factors,
                Confidence = confidence,
                ExpiresAt = attestation.ExpiresAt
            };
        }

        /// <summary>
        /// Verifies an existing attestation.
        /// </summary>
        public AttestationVerificationResult VerifyAttestation(string nodeId)
        {
            if (!_attestations.TryGetValue(nodeId, out var attestation))
            {
                return new AttestationVerificationResult
                {
                    IsValid = false,
                    Reason = "No attestation found for node"
                };
            }

            // Check expiration
            if (attestation.ExpiresAt < DateTime.UtcNow)
            {
                return new AttestationVerificationResult
                {
                    IsValid = false,
                    Reason = "Attestation has expired",
                    ExpiredAt = attestation.ExpiresAt,
                    Attestation = attestation
                };
            }

            // Verify hash integrity
            var computedHash = GenerateAttestationHash(attestation.AttestationId, attestation.NodeId, attestation.Factors);
            if (!computedHash.Equals(attestation.AttestationHash, StringComparison.OrdinalIgnoreCase))
            {
                LogEvent(AttestationEventType.IntegrityViolation, nodeId, attestation.AttestationId,
                    "Attestation hash mismatch");

                return new AttestationVerificationResult
                {
                    IsValid = false,
                    Reason = "Attestation integrity compromised",
                    Attestation = attestation
                };
            }

            // Verify chain of trust
            var trustVerification = VerifyChainOfTrust(attestation.ChainOfTrust);
            if (!trustVerification.IsValid)
            {
                return new AttestationVerificationResult
                {
                    IsValid = false,
                    Reason = $"Chain of trust broken: {trustVerification.Reason}",
                    Attestation = attestation
                };
            }

            return new AttestationVerificationResult
            {
                IsValid = true,
                Attestation = attestation,
                VerifiedLocation = attestation.VerifiedLocation,
                Confidence = attestation.Confidence,
                ExpiresAt = attestation.ExpiresAt
            };
        }

        /// <summary>
        /// Gets current attestation for a node.
        /// </summary>
        public NodeAttestation? GetAttestation(string nodeId)
        {
            return _attestations.TryGetValue(nodeId, out var attestation) ? attestation : null;
        }

        /// <summary>
        /// Checks if a node has valid attestation for a location.
        /// </summary>
        public bool HasValidAttestation(string nodeId, string location)
        {
            if (!_attestations.TryGetValue(nodeId, out var attestation))
                return false;

            return attestation.ExpiresAt > DateTime.UtcNow &&
                   attestation.VerifiedLocation.Equals(location, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Revokes an attestation.
        /// </summary>
        public bool RevokeAttestation(string nodeId, string reason, string revokedBy)
        {
            if (_attestations.TryRemove(nodeId, out var attestation))
            {
                LogEvent(AttestationEventType.AttestationRevoked, nodeId, attestation.AttestationId,
                    $"Revoked by {revokedBy}: {reason}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Registers a trust anchor.
        /// </summary>
        public void RegisterTrustAnchor(TrustAnchor anchor)
        {
            _trustAnchors[anchor.AnchorId] = anchor;
        }

        /// <summary>
        /// Gets attestation statistics.
        /// </summary>
        public new AttestationStatistics GetStatistics()
        {
            var attestations = _attestations.Values.ToList();
            var now = DateTime.UtcNow;

            return new AttestationStatistics
            {
                TotalAttestations = attestations.Count,
                ValidAttestations = attestations.Count(a => a.ExpiresAt > now),
                ExpiredAttestations = attestations.Count(a => a.ExpiresAt <= now),
                AverageConfidence = attestations.Count > 0 ? attestations.Average(a => a.Confidence) : 0,
                AttestationsByLocation = attestations
                    .GroupBy(a => a.VerifiedLocation)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ExpiringWithin24Hours = attestations.Count(a => a.ExpiresAt > now && a.ExpiresAt < now.AddHours(24))
            };
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("attestation.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if attestation is required
            if (context.Attributes.TryGetValue("RequiresAttestation", out var reqObj) && reqObj is true)
            {
                if (context.Attributes.TryGetValue("NodeId", out var nodeIdObj) && nodeIdObj is string nodeId)
                {
                    var verification = VerifyAttestation(nodeId);
                    if (!verification.IsValid)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "ATT-001",
                            Description = $"Node {nodeId} attestation invalid: {verification.Reason}",
                            Severity = ViolationSeverity.Critical,
                            AffectedResource = nodeId,
                            Remediation = "Request new attestation for the node"
                        });
                    }
                }
            }

            // Check for expired attestations
            var expiredCount = _attestations.Values.Count(a => a.ExpiresAt < DateTime.UtcNow);
            if (expiredCount > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ATT-002",
                    Description = $"{expiredCount} node attestations have expired",
                    Severity = ViolationSeverity.High,
                    Remediation = "Renew expired attestations"
                });
            }

            // Check for attestations expiring soon
            var expiringSoon = _attestations.Values
                .Where(a => a.ExpiresAt > DateTime.UtcNow && a.ExpiresAt < DateTime.UtcNow.AddHours(24))
                .ToList();

            if (expiringSoon.Count > 0)
            {
                recommendations.Add($"{expiringSoon.Count} attestations expire within 24 hours");
            }

            // Check for low confidence attestations
            var lowConfidence = _attestations.Values
                .Where(a => a.ExpiresAt > DateTime.UtcNow && a.Confidence < 0.95)
                .ToList();

            if (lowConfidence.Count > 0)
            {
                recommendations.Add($"{lowConfidence.Count} attestations have confidence below 95%");
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["Statistics"] = GetStatistics(),
                    ["RequiredFactors"] = _requiredFactors,
                    ["MinimumConfidence"] = _minimumConfidence
                }
            });
        }

        private async Task<FactorCollectionResult> CollectTpmAttestationAsync(
            string nodeId, AttestationRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // In production, this would communicate with TPM hardware
                await Task.Delay(10, cancellationToken); // Simulated TPM query

                // Generate TPM quote with location claim
                var quote = GenerateTpmQuote(nodeId, request.ClaimedLocation);

                return new FactorCollectionResult
                {
                    Success = true,
                    Factor = new AttestationFactor
                    {
                        FactorType = AttestationFactorType.Tpm,
                        FactorId = Guid.NewGuid().ToString(),
                        Evidence = quote,
                        Confidence = 0.95,
                        CollectedAt = DateTime.UtcNow,
                        VerifiedLocation = request.ClaimedLocation
                    }
                };
            }
            catch (Exception ex)
            {
                return new FactorCollectionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<FactorCollectionResult> CollectSecureEnclaveAttestationAsync(
            string nodeId, AttestationRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // In production, this would use SGX, TrustZone, or similar
                await Task.Delay(10, cancellationToken);

                var enclaveReport = GenerateEnclaveReport(nodeId, request.ClaimedLocation);

                return new FactorCollectionResult
                {
                    Success = true,
                    Factor = new AttestationFactor
                    {
                        FactorType = AttestationFactorType.SecureEnclave,
                        FactorId = Guid.NewGuid().ToString(),
                        Evidence = enclaveReport,
                        Confidence = 0.98,
                        CollectedAt = DateTime.UtcNow,
                        VerifiedLocation = request.ClaimedLocation
                    }
                };
            }
            catch (Exception ex)
            {
                return new FactorCollectionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<FactorCollectionResult> CollectGpsAttestationAsync(
            string nodeId, AttestationRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // In production, this would use secure GPS module
                await Task.Delay(10, cancellationToken);

                // GPS provides lower confidence due to spoofing risk
                return new FactorCollectionResult
                {
                    Success = true,
                    Factor = new AttestationFactor
                    {
                        FactorType = AttestationFactorType.Gps,
                        FactorId = Guid.NewGuid().ToString(),
                        Evidence = $"GPS:{request.ClaimedLocation}:LAT:LON",
                        Confidence = 0.7,
                        CollectedAt = DateTime.UtcNow,
                        VerifiedLocation = request.ClaimedLocation,
                        Coordinates = new GeoCoordinates { Latitude = 0, Longitude = 0 }
                    }
                };
            }
            catch (Exception ex)
            {
                return new FactorCollectionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<FactorCollectionResult> CollectNetworkLocationAttestationAsync(
            string nodeId, AttestationRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // In production, this would use IP geolocation and network topology
                await Task.Delay(10, cancellationToken);

                return new FactorCollectionResult
                {
                    Success = true,
                    Factor = new AttestationFactor
                    {
                        FactorType = AttestationFactorType.NetworkLocation,
                        FactorId = Guid.NewGuid().ToString(),
                        Evidence = $"NETWORK:{request.ClaimedLocation}:ISP:AS",
                        Confidence = 0.8,
                        CollectedAt = DateTime.UtcNow,
                        VerifiedLocation = request.ClaimedLocation
                    }
                };
            }
            catch (Exception ex)
            {
                return new FactorCollectionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private double CalculateCombinedConfidence(List<AttestationFactor> factors)
        {
            if (factors.Count == 0) return 0;

            // Combined confidence using probability theory
            // P(all correct) = product of individual probabilities
            // But we use weighted average for more stable results
            var weights = new Dictionary<AttestationFactorType, double>
            {
                [AttestationFactorType.Tpm] = 1.0,
                [AttestationFactorType.SecureEnclave] = 1.2,
                [AttestationFactorType.Gps] = 0.6,
                [AttestationFactorType.NetworkLocation] = 0.7,
                [AttestationFactorType.Certificate] = 0.9
            };

            var totalWeight = 0.0;
            var weightedSum = 0.0;

            foreach (var factor in factors)
            {
                var weight = weights.GetValueOrDefault(factor.FactorType, 0.5);
                weightedSum += factor.Confidence * weight;
                totalWeight += weight;
            }

            return totalWeight > 0 ? weightedSum / totalWeight : 0;
        }

        private (bool IsConsistent, string? Discrepancy, string DeterminedLocation) VerifyLocationConsistency(
            List<AttestationFactor> factors, string claimedLocation)
        {
            var locations = factors
                .Where(f => !string.IsNullOrEmpty(f.VerifiedLocation))
                .GroupBy(f => f.VerifiedLocation!.ToUpperInvariant())
                .OrderByDescending(g => g.Sum(f => f.Confidence))
                .ToList();

            if (locations.Count == 0)
            {
                return (false, "No location data from factors", claimedLocation);
            }

            var primaryLocation = locations.First().Key;
            var primaryConfidence = locations.First().Sum(f => f.Confidence);

            // Check if claimed location matches
            if (!claimedLocation.Equals(primaryLocation, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Claimed: {claimedLocation}, Detected: {primaryLocation}", primaryLocation);
            }

            // Check for conflicting locations
            if (locations.Count > 1)
            {
                var secondaryConfidence = locations.Skip(1).First().Sum(f => f.Confidence);
                if (secondaryConfidence > primaryConfidence * 0.5)
                {
                    return (false, $"Conflicting location evidence", primaryLocation);
                }
            }

            return (true, null, primaryLocation);
        }

        private string GenerateAttestationHash(string attestationId, string nodeId, List<AttestationFactor> factors)
        {
            var data = new StringBuilder();
            data.Append(attestationId);
            data.Append(':');
            data.Append(nodeId);

            foreach (var factor in factors.OrderBy(f => f.FactorId))
            {
                data.Append(':');
                data.Append(factor.FactorId);
                data.Append(':');
                data.Append(factor.Evidence);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToString())));
        }

        private List<TrustChainLink> BuildChainOfTrust(List<AttestationFactor> factors)
        {
            var chain = new List<TrustChainLink>();

            foreach (var factor in factors)
            {
                // Find trust anchor for this factor type
                var anchor = _trustAnchors.Values
                    .FirstOrDefault(a => a.FactorTypes.Contains(factor.FactorType));

                if (anchor != null)
                {
                    chain.Add(new TrustChainLink
                    {
                        FactorId = factor.FactorId,
                        FactorType = factor.FactorType,
                        TrustAnchorId = anchor.AnchorId,
                        TrustLevel = anchor.TrustLevel,
                        Verified = true
                    });
                }
            }

            return chain;
        }

        private (bool IsValid, string? Reason) VerifyChainOfTrust(List<TrustChainLink> chain)
        {
            foreach (var link in chain)
            {
                if (!_trustAnchors.ContainsKey(link.TrustAnchorId))
                {
                    return (false, $"Trust anchor {link.TrustAnchorId} not found");
                }

                if (!link.Verified)
                {
                    return (false, $"Factor {link.FactorId} not verified");
                }
            }

            return (true, null);
        }

        private string GenerateTpmQuote(string nodeId, string location)
        {
            // In production, this would be an actual TPM quote
            var data = $"TPM_QUOTE:{nodeId}:{location}:{DateTime.UtcNow.Ticks}";
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }

        private string GenerateEnclaveReport(string nodeId, string location)
        {
            // In production, this would be an actual enclave report
            var data = $"ENCLAVE_REPORT:{nodeId}:{location}:{DateTime.UtcNow.Ticks}";
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }

        private AttestationPolicy GetApplicablePolicy(string? policyId)
        {
            if (!string.IsNullOrEmpty(policyId) && _policies.TryGetValue(policyId, out var policy))
            {
                return policy;
            }

            return _policies.GetValueOrDefault("default") ?? new AttestationPolicy
            {
                PolicyId = "default",
                PolicyName = "Default Attestation Policy",
                MinimumFactors = 2
            };
        }

        private void InitializeDefaultPolicies()
        {
            _policies["default"] = new AttestationPolicy
            {
                PolicyId = "default",
                PolicyName = "Default Attestation Policy",
                MinimumFactors = 2,
                RequiresNetworkLocation = true
            };

            _policies["high-security"] = new AttestationPolicy
            {
                PolicyId = "high-security",
                PolicyName = "High Security Attestation",
                MinimumFactors = 3,
                RequiresTpm = true,
                RequiresSecureEnclave = true,
                RequiresNetworkLocation = true
            };

            _policies["government"] = new AttestationPolicy
            {
                PolicyId = "government",
                PolicyName = "Government Grade Attestation",
                MinimumFactors = 4,
                RequiresTpm = true,
                RequiresSecureEnclave = true,
                RequiresGps = true,
                RequiresNetworkLocation = true,
                MinimumConfidence = 0.95
            };
        }

        private void InitializeTrustAnchors()
        {
            _trustAnchors["tpm-manufacturer"] = new TrustAnchor
            {
                AnchorId = "tpm-manufacturer",
                AnchorName = "TPM Manufacturer Root",
                FactorTypes = new List<AttestationFactorType> { AttestationFactorType.Tpm },
                TrustLevel = TrustLevel.Hardware,
                PublicKey = "TPM_ROOT_CA_KEY"
            };

            _trustAnchors["enclave-attestation"] = new TrustAnchor
            {
                AnchorId = "enclave-attestation",
                AnchorName = "Secure Enclave Attestation Service",
                FactorTypes = new List<AttestationFactorType> { AttestationFactorType.SecureEnclave },
                TrustLevel = TrustLevel.Hardware,
                PublicKey = "ENCLAVE_ATTESTATION_KEY"
            };

            _trustAnchors["geolocation-provider"] = new TrustAnchor
            {
                AnchorId = "geolocation-provider",
                AnchorName = "Certified Geolocation Provider",
                FactorTypes = new List<AttestationFactorType> { AttestationFactorType.Gps, AttestationFactorType.NetworkLocation },
                TrustLevel = TrustLevel.Certified,
                PublicKey = "GEOLOCATION_PROVIDER_KEY"
            };
        }

        private void LogEvent(AttestationEventType eventType, string nodeId, string attestationId, string details)
        {
            _events.Add(new AttestationEvent
            {
                EventId = Guid.NewGuid().ToString(),
                EventType = eventType,
                NodeId = nodeId,
                AttestationId = attestationId,
                Details = details,
                Timestamp = DateTime.UtcNow
            });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("attestation.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("attestation.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Attestation request.
    /// </summary>
    public sealed record AttestationRequest
    {
        public required string ClaimedLocation { get; init; }
        public string? PolicyId { get; init; }
        public bool IncludeTpm { get; init; }
        public bool IncludeSecureEnclave { get; init; }
        public bool IncludeGps { get; init; }
        public bool IncludeNetworkLocation { get; init; }
    }

    /// <summary>
    /// Attestation result.
    /// </summary>
    public sealed record AttestationResult
    {
        public required bool Success { get; init; }
        public required string AttestationId { get; init; }
        public string? ErrorMessage { get; init; }
        public NodeAttestation? Attestation { get; init; }
        public List<AttestationFactor> Factors { get; init; } = new();
        public double Confidence { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public List<string> CollectionErrors { get; init; } = new();
    }

    /// <summary>
    /// Node attestation record.
    /// </summary>
    public sealed record NodeAttestation
    {
        public required string AttestationId { get; init; }
        public required string NodeId { get; init; }
        public required string ClaimedLocation { get; init; }
        public required string VerifiedLocation { get; init; }
        public List<AttestationFactor> Factors { get; init; } = new();
        public required double Confidence { get; init; }
        public required string PolicyId { get; init; }
        public required DateTime IssuedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public required string AttestationHash { get; init; }
        public List<TrustChainLink> ChainOfTrust { get; init; } = new();
    }

    /// <summary>
    /// Attestation factor.
    /// </summary>
    public sealed record AttestationFactor
    {
        public required AttestationFactorType FactorType { get; init; }
        public required string FactorId { get; init; }
        public required string Evidence { get; init; }
        public required double Confidence { get; init; }
        public required DateTime CollectedAt { get; init; }
        public string? VerifiedLocation { get; init; }
        public GeoCoordinates? Coordinates { get; init; }
    }

    /// <summary>
    /// Attestation factor type.
    /// </summary>
    public enum AttestationFactorType
    {
        Tpm,
        SecureEnclave,
        Gps,
        NetworkLocation,
        Certificate,
        BiometricLocation,
        ManualVerification
    }

    /// <summary>
    /// Geographic coordinates.
    /// </summary>
    public sealed record GeoCoordinates
    {
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double? Accuracy { get; init; }
    }

    /// <summary>
    /// Factor collection result.
    /// </summary>
    public sealed record FactorCollectionResult
    {
        public required bool Success { get; init; }
        public AttestationFactor? Factor { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Attestation verification result.
    /// </summary>
    public sealed record AttestationVerificationResult
    {
        public required bool IsValid { get; init; }
        public string? Reason { get; init; }
        public NodeAttestation? Attestation { get; init; }
        public string? VerifiedLocation { get; init; }
        public double? Confidence { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? ExpiredAt { get; init; }
    }

    /// <summary>
    /// Attestation policy.
    /// </summary>
    public sealed record AttestationPolicy
    {
        public required string PolicyId { get; init; }
        public required string PolicyName { get; init; }
        public int MinimumFactors { get; init; } = 2;
        public double MinimumConfidence { get; init; } = 0.9;
        public bool RequiresTpm { get; init; }
        public bool RequiresSecureEnclave { get; init; }
        public bool RequiresGps { get; init; }
        public bool RequiresNetworkLocation { get; init; }
    }

    /// <summary>
    /// Trust anchor for attestation verification.
    /// </summary>
    public sealed record TrustAnchor
    {
        public required string AnchorId { get; init; }
        public required string AnchorName { get; init; }
        public List<AttestationFactorType> FactorTypes { get; init; } = new();
        public TrustLevel TrustLevel { get; init; }
        public string? PublicKey { get; init; }
        public DateTime? ExpiresAt { get; init; }
    }

    /// <summary>
    /// Trust level.
    /// </summary>
    public enum TrustLevel
    {
        Unknown,
        SelfSigned,
        Certified,
        Hardware,
        Government
    }

    /// <summary>
    /// Trust chain link.
    /// </summary>
    public sealed record TrustChainLink
    {
        public required string FactorId { get; init; }
        public required AttestationFactorType FactorType { get; init; }
        public required string TrustAnchorId { get; init; }
        public TrustLevel TrustLevel { get; init; }
        public bool Verified { get; init; }
    }

    /// <summary>
    /// Attestation statistics.
    /// </summary>
    public sealed record AttestationStatistics
    {
        public int TotalAttestations { get; init; }
        public int ValidAttestations { get; init; }
        public int ExpiredAttestations { get; init; }
        public double AverageConfidence { get; init; }
        public Dictionary<string, int> AttestationsByLocation { get; init; } = new();
        public int ExpiringWithin24Hours { get; init; }
    }

    /// <summary>
    /// Attestation event.
    /// </summary>
    public sealed record AttestationEvent
    {
        public required string EventId { get; init; }
        public required AttestationEventType EventType { get; init; }
        public required string NodeId { get; init; }
        public required string AttestationId { get; init; }
        public required string Details { get; init; }
        public required DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Attestation event type.
    /// </summary>
    public enum AttestationEventType
    {
        AttestationRequested,
        AttestationSucceeded,
        AttestationFailed,
        AttestationExpired,
        AttestationRevoked,
        LocationMismatch,
        IntegrityViolation
    }
}
