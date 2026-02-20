using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features
{
    /// <summary>
    /// D1: Envelope verification service for validating IEnvelopeKeyStore implementations.
    /// Provides runtime validation that envelope key stores implement required methods
    /// and capability checking helpers for envelope encryption operations.
    /// </summary>
    public sealed class EnvelopeVerification
    {
        private readonly BoundedDictionary<string, EnvelopeCapabilityReport> _capabilityCache = new BoundedDictionary<string, EnvelopeCapabilityReport>(1000);
        private readonly IKeyStoreRegistry _registry;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Creates a new envelope verification service.
        /// </summary>
        /// <param name="registry">The key store registry to verify against.</param>
        public EnvelopeVerification(IKeyStoreRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// Verifies that all registered IEnvelopeKeyStore implementations have WrapKey/UnwrapKey methods.
        /// Returns a comprehensive verification report.
        /// </summary>
        public async Task<EnvelopeVerificationReport> VerifyAllEnvelopeKeyStoresAsync(CancellationToken ct = default)
        {
            var report = new EnvelopeVerificationReport
            {
                VerifiedAt = DateTime.UtcNow
            };

            var envelopeIds = _registry.GetRegisteredEnvelopeKeyStoreIds();

            foreach (var storeId in envelopeIds)
            {
                if (ct.IsCancellationRequested)
                    break;

                var keyStore = _registry.GetEnvelopeKeyStore(storeId);
                if (keyStore == null)
                {
                    report.Results.Add(new EnvelopeStoreVerificationResult
                    {
                        StoreId = storeId,
                        IsValid = false,
                        ErrorMessage = "Envelope key store not found in registry."
                    });
                    continue;
                }

                var result = await VerifyEnvelopeKeyStoreAsync(storeId, keyStore, ct);
                report.Results.Add(result);
            }

            report.TotalStores = report.Results.Count;
            report.ValidStores = report.Results.Count(r => r.IsValid);
            report.InvalidStores = report.Results.Count(r => !r.IsValid);

            return report;
        }

        /// <summary>
        /// Verifies a specific IEnvelopeKeyStore implementation.
        /// </summary>
        public async Task<EnvelopeStoreVerificationResult> VerifyEnvelopeKeyStoreAsync(
            string storeId,
            IEnvelopeKeyStore keyStore,
            CancellationToken ct = default)
        {
            var result = new EnvelopeStoreVerificationResult
            {
                StoreId = storeId,
                StoreType = keyStore.GetType().FullName ?? keyStore.GetType().Name
            };

            try
            {
                // Check interface implementation
                var interfaceCheck = VerifyInterfaceImplementation(keyStore);
                result.HasWrapKeyMethod = interfaceCheck.HasWrapKey;
                result.HasUnwrapKeyMethod = interfaceCheck.HasUnwrapKey;
                result.HasSupportedAlgorithms = interfaceCheck.HasSupportedAlgorithms;
                result.HasHsmSupport = interfaceCheck.HasHsmSupport;

                // Validate method signatures
                result.WrapKeySignatureValid = ValidateWrapKeySignature(keyStore);
                result.UnwrapKeySignatureValid = ValidateUnwrapKeySignature(keyStore);

                // Check supported algorithms
                result.SupportedAlgorithms = keyStore.SupportedWrappingAlgorithms.ToList();
                result.SupportsHsmKeyGeneration = keyStore.SupportsHsmKeyGeneration;

                // Attempt a capability probe (non-destructive)
                var capabilityProbe = await ProbeCapabilitiesAsync(storeId, keyStore, ct);
                result.CapabilityProbeResult = capabilityProbe;

                // Determine overall validity
                result.IsValid = result.HasWrapKeyMethod &&
                                result.HasUnwrapKeyMethod &&
                                result.WrapKeySignatureValid &&
                                result.UnwrapKeySignatureValid &&
                                result.SupportedAlgorithms.Count > 0;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Verification failed: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Checks if a key store supports envelope encryption operations.
        /// </summary>
        public bool SupportsEnvelopeEncryption(string storeId)
        {
            var keyStore = _registry.GetEnvelopeKeyStore(storeId);
            if (keyStore == null)
                return false;

            // Check if it's a strategy with capabilities
            if (_registry.GetKeyStore(storeId) is IKeyStoreStrategy strategy)
            {
                return strategy.Capabilities.SupportsEnvelope;
            }

            // Fall back to interface check
            return VerifyInterfaceImplementation(keyStore).HasWrapKey &&
                   VerifyInterfaceImplementation(keyStore).HasUnwrapKey;
        }

        /// <summary>
        /// Gets the envelope capabilities for a specific key store.
        /// Results are cached for performance.
        /// </summary>
        public async Task<EnvelopeCapabilityReport> GetEnvelopeCapabilitiesAsync(
            string storeId,
            CancellationToken ct = default)
        {
            // Check cache first
            if (_capabilityCache.TryGetValue(storeId, out var cached) &&
                cached.CachedAt.Add(_cacheExpiration) > DateTime.UtcNow)
            {
                return cached;
            }

            var keyStore = _registry.GetEnvelopeKeyStore(storeId);
            if (keyStore == null)
            {
                return new EnvelopeCapabilityReport
                {
                    StoreId = storeId,
                    IsAvailable = false,
                    ErrorMessage = "Envelope key store not found."
                };
            }

            var report = new EnvelopeCapabilityReport
            {
                StoreId = storeId,
                StoreType = keyStore.GetType().FullName ?? keyStore.GetType().Name,
                IsAvailable = true,
                SupportedAlgorithms = keyStore.SupportedWrappingAlgorithms.ToList(),
                SupportsHsmKeyGeneration = keyStore.SupportsHsmKeyGeneration,
                CachedAt = DateTime.UtcNow
            };

            // Get strategy capabilities if available
            if (_registry.GetKeyStore(storeId) is IKeyStoreStrategy strategy)
            {
                report.SupportsRotation = strategy.Capabilities.SupportsRotation;
                report.SupportsVersioning = strategy.Capabilities.SupportsVersioning;
                report.SupportsAuditLogging = strategy.Capabilities.SupportsAuditLogging;
                report.SupportsReplication = strategy.Capabilities.SupportsReplication;
                report.Metadata = new Dictionary<string, object>(strategy.Capabilities.Metadata);
            }

            // Cache the result
            _capabilityCache[storeId] = report;

            return report;
        }

        /// <summary>
        /// Validates that the required WrapKey/UnwrapKey operations can be performed.
        /// This is a quick check without actually performing cryptographic operations.
        /// </summary>
        public EnvelopeReadinessCheck CheckEnvelopeReadiness(string storeId, string kekId)
        {
            var check = new EnvelopeReadinessCheck
            {
                StoreId = storeId,
                KekId = kekId,
                CheckedAt = DateTime.UtcNow
            };

            // Check store registration
            var keyStore = _registry.GetEnvelopeKeyStore(storeId);
            if (keyStore == null)
            {
                check.IsReady = false;
                check.Issues.Add("Envelope key store is not registered.");
                return check;
            }

            check.StoreRegistered = true;

            // Check interface implementation
            var interfaceCheck = VerifyInterfaceImplementation(keyStore);
            check.HasRequiredMethods = interfaceCheck.HasWrapKey && interfaceCheck.HasUnwrapKey;

            if (!check.HasRequiredMethods)
            {
                check.Issues.Add("Key store does not implement required WrapKey/UnwrapKey methods.");
            }

            // Check supported algorithms
            check.HasSupportedAlgorithms = keyStore.SupportedWrappingAlgorithms.Count > 0;
            if (!check.HasSupportedAlgorithms)
            {
                check.Issues.Add("Key store does not report any supported wrapping algorithms.");
            }

            // Check KEK ID validity
            check.KekIdProvided = !string.IsNullOrWhiteSpace(kekId);
            if (!check.KekIdProvided)
            {
                check.Issues.Add("No KEK ID provided for envelope operations.");
            }

            check.IsReady = check.StoreRegistered &&
                           check.HasRequiredMethods &&
                           check.HasSupportedAlgorithms &&
                           check.KekIdProvided;

            return check;
        }

        /// <summary>
        /// Gets all registered envelope key stores with their verification status.
        /// </summary>
        public IReadOnlyList<EnvelopeStoreInfo> GetRegisteredEnvelopeStores()
        {
            var stores = new List<EnvelopeStoreInfo>();
            var envelopeIds = _registry.GetRegisteredEnvelopeKeyStoreIds();

            foreach (var storeId in envelopeIds)
            {
                var keyStore = _registry.GetEnvelopeKeyStore(storeId);
                if (keyStore == null)
                    continue;

                var info = new EnvelopeStoreInfo
                {
                    StoreId = storeId,
                    StoreType = keyStore.GetType().FullName ?? keyStore.GetType().Name,
                    SupportedAlgorithms = keyStore.SupportedWrappingAlgorithms.ToList(),
                    SupportsHsmKeyGeneration = keyStore.SupportsHsmKeyGeneration
                };

                // Add strategy info if available
                if (_registry.GetKeyStore(storeId) is IKeyStoreStrategy strategy)
                {
                    info.SupportsEnvelope = strategy.Capabilities.SupportsEnvelope;
                    info.SupportsRotation = strategy.Capabilities.SupportsRotation;
                    info.SupportsHsm = strategy.Capabilities.SupportsHsm;
                }
                else
                {
                    info.SupportsEnvelope = true; // It's registered as envelope, so assume true
                }

                stores.Add(info);
            }

            return stores.AsReadOnly();
        }

        /// <summary>
        /// Clears the capability cache.
        /// </summary>
        public void ClearCache()
        {
            _capabilityCache.Clear();
        }

        #region Private Methods

        private InterfaceVerification VerifyInterfaceImplementation(IEnvelopeKeyStore keyStore)
        {
            var type = keyStore.GetType();
            var verification = new InterfaceVerification();

            // Check for WrapKeyAsync method
            var wrapMethod = type.GetMethod("WrapKeyAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(byte[]), typeof(ISecurityContext) },
                null);
            verification.HasWrapKey = wrapMethod != null;

            // Check for UnwrapKeyAsync method
            var unwrapMethod = type.GetMethod("UnwrapKeyAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(byte[]), typeof(ISecurityContext) },
                null);
            verification.HasUnwrapKey = unwrapMethod != null;

            // Check for SupportedWrappingAlgorithms property
            var algorithmsProperty = type.GetProperty("SupportedWrappingAlgorithms",
                BindingFlags.Public | BindingFlags.Instance);
            verification.HasSupportedAlgorithms = algorithmsProperty != null;

            // Check for SupportsHsmKeyGeneration property
            var hsmProperty = type.GetProperty("SupportsHsmKeyGeneration",
                BindingFlags.Public | BindingFlags.Instance);
            verification.HasHsmSupport = hsmProperty != null;

            return verification;
        }

        private bool ValidateWrapKeySignature(IEnvelopeKeyStore keyStore)
        {
            var type = keyStore.GetType();
            var method = type.GetMethod("WrapKeyAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(byte[]), typeof(ISecurityContext) },
                null);

            if (method == null)
                return false;

            // Validate return type is Task<byte[]>
            return method.ReturnType == typeof(Task<byte[]>);
        }

        private bool ValidateUnwrapKeySignature(IEnvelopeKeyStore keyStore)
        {
            var type = keyStore.GetType();
            var method = type.GetMethod("UnwrapKeyAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(byte[]), typeof(ISecurityContext) },
                null);

            if (method == null)
                return false;

            // Validate return type is Task<byte[]>
            return method.ReturnType == typeof(Task<byte[]>);
        }

        private async Task<CapabilityProbeResult> ProbeCapabilitiesAsync(
            string storeId,
            IEnvelopeKeyStore keyStore,
            CancellationToken ct)
        {
            var result = new CapabilityProbeResult
            {
                ProbeTime = DateTime.UtcNow
            };

            try
            {
                // Check if we can get current key ID (basic connectivity test)
                var currentKeyId = await keyStore.GetCurrentKeyIdAsync();
                result.CanGetCurrentKey = !string.IsNullOrEmpty(currentKeyId);
                result.CurrentKeyId = currentKeyId;

                // Check if it's a strategy and probe health
                if (_registry.GetKeyStore(storeId) is IKeyStoreStrategy strategy)
                {
                    result.HealthCheckPassed = await strategy.HealthCheckAsync(ct);
                }
                else
                {
                    result.HealthCheckPassed = true; // Assume healthy if no health check available
                }

                result.ProbeSuccessful = true;
            }
            catch (Exception ex)
            {
                result.ProbeSuccessful = false;
                result.ProbeError = ex.Message;
            }

            return result;
        }

        #endregion

        #region Private Types

        private class InterfaceVerification
        {
            public bool HasWrapKey { get; set; }
            public bool HasUnwrapKey { get; set; }
            public bool HasSupportedAlgorithms { get; set; }
            public bool HasHsmSupport { get; set; }
        }

        #endregion
    }

    #region Result Types

    /// <summary>
    /// Comprehensive verification report for all envelope key stores.
    /// </summary>
    public class EnvelopeVerificationReport
    {
        public DateTime VerifiedAt { get; set; }
        public int TotalStores { get; set; }
        public int ValidStores { get; set; }
        public int InvalidStores { get; set; }
        public List<EnvelopeStoreVerificationResult> Results { get; set; } = new();

        public bool AllValid => InvalidStores == 0 && TotalStores > 0;
    }

    /// <summary>
    /// Verification result for a single envelope key store.
    /// </summary>
    public class EnvelopeStoreVerificationResult
    {
        public string StoreId { get; set; } = "";
        public string StoreType { get; set; } = "";
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }

        // Interface verification
        public bool HasWrapKeyMethod { get; set; }
        public bool HasUnwrapKeyMethod { get; set; }
        public bool HasSupportedAlgorithms { get; set; }
        public bool HasHsmSupport { get; set; }

        // Signature validation
        public bool WrapKeySignatureValid { get; set; }
        public bool UnwrapKeySignatureValid { get; set; }

        // Capabilities
        public List<string> SupportedAlgorithms { get; set; } = new();
        public bool SupportsHsmKeyGeneration { get; set; }

        // Capability probe
        public CapabilityProbeResult? CapabilityProbeResult { get; set; }
    }

    /// <summary>
    /// Result of probing envelope key store capabilities.
    /// </summary>
    public class CapabilityProbeResult
    {
        public DateTime ProbeTime { get; set; }
        public bool ProbeSuccessful { get; set; }
        public string? ProbeError { get; set; }
        public bool CanGetCurrentKey { get; set; }
        public string? CurrentKeyId { get; set; }
        public bool HealthCheckPassed { get; set; }
    }

    /// <summary>
    /// Envelope capability report for a key store.
    /// </summary>
    public class EnvelopeCapabilityReport
    {
        public string StoreId { get; set; } = "";
        public string StoreType { get; set; } = "";
        public bool IsAvailable { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> SupportedAlgorithms { get; set; } = new();
        public bool SupportsHsmKeyGeneration { get; set; }
        public bool SupportsRotation { get; set; }
        public bool SupportsVersioning { get; set; }
        public bool SupportsAuditLogging { get; set; }
        public bool SupportsReplication { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public DateTime CachedAt { get; set; }
    }

    /// <summary>
    /// Readiness check for envelope encryption operations.
    /// </summary>
    public class EnvelopeReadinessCheck
    {
        public string StoreId { get; set; } = "";
        public string KekId { get; set; } = "";
        public DateTime CheckedAt { get; set; }
        public bool IsReady { get; set; }
        public bool StoreRegistered { get; set; }
        public bool HasRequiredMethods { get; set; }
        public bool HasSupportedAlgorithms { get; set; }
        public bool KekIdProvided { get; set; }
        public List<string> Issues { get; set; } = new();
    }

    /// <summary>
    /// Information about a registered envelope key store.
    /// </summary>
    public class EnvelopeStoreInfo
    {
        public string StoreId { get; set; } = "";
        public string StoreType { get; set; } = "";
        public bool SupportsEnvelope { get; set; }
        public bool SupportsRotation { get; set; }
        public bool SupportsHsm { get; set; }
        public bool SupportsHsmKeyGeneration { get; set; }
        public List<string> SupportedAlgorithms { get; set; } = new();
    }

    #endregion
}
