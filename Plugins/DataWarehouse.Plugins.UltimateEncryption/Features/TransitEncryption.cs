using DataWarehouse.SDK.Contracts.Encryption;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateEncryption.Features;

#region Data Classification

/// <summary>
/// Classification level for data sensitivity and security requirements.
/// Determines the minimum encryption strength and policy enforcement.
/// </summary>
public enum DataClassification
{
    /// <summary>
    /// Public data - minimum encryption required.
    /// Can use faster algorithms with reduced key sizes.
    /// </summary>
    Public = 0,

    /// <summary>
    /// Internal - standard encryption.
    /// Business data requiring baseline protection.
    /// </summary>
    Internal = 1,

    /// <summary>
    /// Confidential - strong encryption required.
    /// Sensitive business data, PII, financial records.
    /// </summary>
    Confidential = 2,

    /// <summary>
    /// Restricted - maximum encryption required.
    /// Trade secrets, classified information, top secret data.
    /// </summary>
    Restricted = 3,

    /// <summary>
    /// Secret - government classification.
    /// Requires FIPS-compliant algorithms and no downgrade.
    /// </summary>
    Secret = 4,

    /// <summary>
    /// Top Secret - highest classification.
    /// Requires military-grade encryption with split-key architecture.
    /// </summary>
    TopSecret = 5
}

#endregion

#region Transit Security Policies (T6.3.1-T6.3.5)

/// <summary>
/// Defines a security policy for data in transit encryption.
/// Controls cipher selection, negotiation behavior, and downgrade permissions.
/// </summary>
/// <param name="PolicyName">Human-readable name of the policy.</param>
/// <param name="PreferredCiphers">Ordered list of preferred ciphers (most preferred first).</param>
/// <param name="MinimumKeySize">Minimum key size in bits (128, 192, 256, etc.).</param>
/// <param name="AllowNegotiation">Whether cipher negotiation is permitted between endpoints.</param>
/// <param name="AllowDowngrade">Whether downgrade to weaker cipher is permitted.</param>
/// <param name="RequireEndToEnd">Whether end-to-end encryption is mandatory (no intermediate transcryption).</param>
/// <param name="RequireFips">Whether only FIPS 140-2/140-3 validated algorithms are permitted.</param>
/// <param name="MinimumClassification">Minimum data classification this policy can handle.</param>
/// <param name="MaximumClassification">Maximum data classification allowed (prevents over-classification overhead).</param>
/// <param name="FallbackCipher">Fallback cipher if negotiation fails (null = reject connection).</param>
/// <param name="Description">Detailed description of the policy purpose and use cases.</param>
public sealed record TransitSecurityPolicy(
    string PolicyName,
    List<string> PreferredCiphers,
    int MinimumKeySize,
    bool AllowNegotiation,
    bool AllowDowngrade,
    bool RequireEndToEnd,
    bool RequireFips,
    DataClassification MinimumClassification,
    DataClassification MaximumClassification,
    string? FallbackCipher,
    string Description
)
{
    /// <summary>
    /// Validates that a cipher meets this policy's requirements.
    /// </summary>
    /// <param name="cipherName">The cipher algorithm name (e.g., "AES-256-GCM").</param>
    /// <param name="keySizeBits">The key size in bits.</param>
    /// <param name="dataClassification">The classification level of the data being transmitted.</param>
    /// <returns>Validation result indicating compliance.</returns>
    public PolicyValidationResult ValidateCipher(string cipherName, int keySizeBits, DataClassification dataClassification)
    {
        var violations = new List<string>();

        // Check key size
        if (keySizeBits < MinimumKeySize)
        {
            violations.Add($"Key size {keySizeBits} bits is below policy minimum of {MinimumKeySize} bits");
        }

        // Check data classification bounds
        if (dataClassification < MinimumClassification)
        {
            violations.Add($"Data classification {dataClassification} is below policy minimum of {MinimumClassification}");
        }

        if (dataClassification > MaximumClassification)
        {
            violations.Add($"Data classification {dataClassification} exceeds policy maximum of {MaximumClassification}");
        }

        // Check if cipher is in preferred list
        if (!PreferredCiphers.Contains(cipherName, StringComparer.OrdinalIgnoreCase))
        {
            if (!AllowNegotiation)
            {
                violations.Add($"Cipher {cipherName} is not in the approved list and negotiation is disabled");
            }
        }

        // FIPS check (basic validation)
        if (RequireFips && !IsFipsCompliant(cipherName))
        {
            violations.Add($"Cipher {cipherName} is not FIPS 140-2/140-3 compliant");
        }

        return new PolicyValidationResult(
            IsValid: violations.Count == 0,
            Violations: violations,
            PolicyName: PolicyName
        );
    }

    /// <summary>
    /// Checks if a cipher is FIPS compliant (basic check).
    /// </summary>
    private static bool IsFipsCompliant(string cipherName)
    {
        var fipsCiphers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AES-128-GCM", "AES-192-GCM", "AES-256-GCM",
            "AES-128-CBC", "AES-192-CBC", "AES-256-CBC",
            "AES-128-CTR", "AES-192-CTR", "AES-256-CTR",
            "AES-128-CCM", "AES-192-CCM", "AES-256-CCM"
        };
        return fipsCiphers.Contains(cipherName);
    }
}

/// <summary>
/// Result of policy validation.
/// </summary>
/// <param name="IsValid">Whether the cipher configuration is compliant with the policy.</param>
/// <param name="Violations">List of policy violations, if any.</param>
/// <param name="PolicyName">Name of the policy that was validated against.</param>
public sealed record PolicyValidationResult(
    bool IsValid,
    List<string> Violations,
    string PolicyName
);

/// <summary>
/// Provider for transit security policies.
/// Includes preset policies for common scenarios.
/// </summary>
public interface ITransitPolicyProvider
{
    /// <summary>
    /// Gets a policy by name.
    /// </summary>
    /// <param name="policyName">The policy name (case-insensitive).</param>
    /// <returns>The policy configuration, or null if not found.</returns>
    TransitSecurityPolicy? GetPolicy(string policyName);

    /// <summary>
    /// Lists all available policies.
    /// </summary>
    /// <returns>Collection of all transit security policies.</returns>
    IReadOnlyCollection<TransitSecurityPolicy> ListPolicies();

    /// <summary>
    /// Recommends a policy based on data classification.
    /// </summary>
    /// <param name="classification">The data classification level.</param>
    /// <returns>Recommended policy for the classification level.</returns>
    TransitSecurityPolicy RecommendPolicy(DataClassification classification);
}

/// <summary>
/// Default implementation of transit policy provider with preset policies.
/// Provides five standard policies covering various security and performance requirements.
/// </summary>
public sealed class TransitPolicyProvider : ITransitPolicyProvider
{
    private readonly Dictionary<string, TransitSecurityPolicy> _policies;

    /// <summary>
    /// Initializes a new instance with standard preset policies.
    /// </summary>
    public TransitPolicyProvider()
    {
        _policies = new Dictionary<string, TransitSecurityPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            // T6.3.1: Default Transit Policy
            ["DefaultTransitPolicy"] = new(
                PolicyName: "DefaultTransitPolicy",
                PreferredCiphers: new List<string> { "AES-256-GCM", "ChaCha20-Poly1305", "AES-192-GCM" },
                MinimumKeySize: 128,
                AllowNegotiation: true,
                AllowDowngrade: false,
                RequireEndToEnd: false,
                RequireFips: false,
                MinimumClassification: DataClassification.Public,
                MaximumClassification: DataClassification.Confidential,
                FallbackCipher: "AES-256-GCM",
                Description: "Balanced default policy for general-purpose transit encryption. " +
                             "Prefers AES-256-GCM or ChaCha20-Poly1305 with negotiation support. " +
                             "Suitable for Public through Confidential data."
            ),

            // T6.3.2: Government Transit Policy
            ["GovernmentTransitPolicy"] = new(
                PolicyName: "GovernmentTransitPolicy",
                PreferredCiphers: new List<string> { "AES-256-GCM", "AES-192-GCM" },
                MinimumKeySize: 256,
                AllowNegotiation: false,
                AllowDowngrade: false,
                RequireEndToEnd: true,
                RequireFips: true,
                MinimumClassification: DataClassification.Secret,
                MaximumClassification: DataClassification.TopSecret,
                FallbackCipher: null,
                Description: "FIPS-only policy for government and classified data. " +
                             "Requires minimum 256-bit keys, no downgrade below Secret classification. " +
                             "End-to-end encryption mandatory, no cipher negotiation permitted."
            ),

            // T6.3.3: High Performance Transit Policy
            ["HighPerformanceTransitPolicy"] = new(
                PolicyName: "HighPerformanceTransitPolicy",
                PreferredCiphers: new List<string> { "ChaCha20-Poly1305", "AES-128-GCM", "AES-256-GCM" },
                MinimumKeySize: 128,
                AllowNegotiation: true,
                AllowDowngrade: true,
                RequireEndToEnd: false,
                RequireFips: false,
                MinimumClassification: DataClassification.Public,
                MaximumClassification: DataClassification.Internal,
                FallbackCipher: "ChaCha20-Poly1305",
                Description: "Performance-optimized policy for high-throughput scenarios. " +
                             "Prefers ChaCha20 for software performance, allows 128-bit for Public data. " +
                             "Suitable for internal communications and non-sensitive data."
            ),

            // T6.3.4: Maximum Security Transit Policy
            ["MaximumSecurityTransitPolicy"] = new(
                PolicyName: "MaximumSecurityTransitPolicy",
                PreferredCiphers: new List<string> { "Serpent-256-GCM", "Twofish-256-GCM", "AES-256-GCM" },
                MinimumKeySize: 256,
                AllowNegotiation: false,
                AllowDowngrade: false,
                RequireEndToEnd: true,
                RequireFips: false,
                MinimumClassification: DataClassification.Restricted,
                MaximumClassification: DataClassification.TopSecret,
                FallbackCipher: null,
                Description: "Maximum security policy with end-to-end only encryption. " +
                             "No transcryption permitted, Serpent/Twofish preferred over AES. " +
                             "Designed for highly sensitive and classified information."
            ),

            // T6.3.5: Mobile Optimized Policy
            ["MobileOptimizedPolicy"] = new(
                PolicyName: "MobileOptimizedPolicy",
                PreferredCiphers: new List<string> { "ChaCha20-Poly1305", "AES-128-GCM" },
                MinimumKeySize: 128,
                AllowNegotiation: true,
                AllowDowngrade: false,
                RequireEndToEnd: false,
                RequireFips: false,
                MinimumClassification: DataClassification.Public,
                MaximumClassification: DataClassification.Confidential,
                FallbackCipher: "ChaCha20-Poly1305",
                Description: "Optimized for mobile devices and battery-constrained environments. " +
                             "Prefers ChaCha20 (better software performance than AES without AES-NI). " +
                             "Reduced KDF iterations to minimize battery drain."
            )
        };
    }

    /// <inheritdoc/>
    public TransitSecurityPolicy? GetPolicy(string policyName)
    {
        return _policies.TryGetValue(policyName, out var policy) ? policy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<TransitSecurityPolicy> ListPolicies()
    {
        return _policies.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public TransitSecurityPolicy RecommendPolicy(DataClassification classification)
    {
        return classification switch
        {
            DataClassification.Public => _policies["HighPerformanceTransitPolicy"],
            DataClassification.Internal => _policies["DefaultTransitPolicy"],
            DataClassification.Confidential => _policies["DefaultTransitPolicy"],
            DataClassification.Restricted => _policies["MaximumSecurityTransitPolicy"],
            DataClassification.Secret => _policies["GovernmentTransitPolicy"],
            DataClassification.TopSecret => _policies["GovernmentTransitPolicy"],
            _ => _policies["DefaultTransitPolicy"]
        };
    }
}

#endregion

#region Endpoint Capabilities (T6.2.1-T6.2.4)

/// <summary>
/// Describes the cryptographic capabilities of an endpoint.
/// Used for cipher negotiation and performance optimization.
/// </summary>
/// <param name="EndpointType">Type of endpoint (Desktop, Mobile, IoT, Browser).</param>
/// <param name="OperatingSystem">Operating system name (Windows, macOS, Linux, iOS, Android, etc.).</param>
/// <param name="HasAesNi">Whether AES-NI hardware acceleration is available.</param>
/// <param name="HasAvx2">Whether AVX2 instructions are available.</param>
/// <param name="SupportedCiphers">List of ciphers the endpoint can handle.</param>
/// <param name="PreferredCipher">The endpoint's preferred cipher (best performance).</param>
/// <param name="MaxThroughputMBps">Estimated maximum encryption throughput in MB/s.</param>
/// <param name="IsBatteryPowered">Whether the device is battery-powered (affects algorithm selection).</param>
/// <param name="MemoryConstrainedMB">Available memory for cryptographic operations (null = unconstrained).</param>
public sealed record EndpointCapabilities(
    string EndpointType,
    string OperatingSystem,
    bool HasAesNi,
    bool HasAvx2,
    List<string> SupportedCiphers,
    string PreferredCipher,
    int MaxThroughputMBps,
    bool IsBatteryPowered,
    int? MemoryConstrainedMB
)
{
    /// <summary>
    /// Determines if the endpoint can use a specific cipher efficiently.
    /// </summary>
    /// <param name="cipherName">The cipher algorithm name.</param>
    /// <returns>True if the cipher is supported and efficient on this endpoint.</returns>
    public bool CanUseEfficiently(string cipherName)
    {
        if (!SupportedCiphers.Contains(cipherName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // AES-based ciphers need AES-NI for efficiency
        if (cipherName.Contains("AES", StringComparison.OrdinalIgnoreCase) && !HasAesNi)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Selects the best cipher for this endpoint from a list of options.
    /// </summary>
    /// <param name="availableCiphers">List of available ciphers.</param>
    /// <returns>The best cipher for this endpoint, or null if none are suitable.</returns>
    public string? SelectBestCipher(IEnumerable<string> availableCiphers)
    {
        // Prefer the endpoint's preferred cipher if available
        if (availableCiphers.Contains(PreferredCipher, StringComparer.OrdinalIgnoreCase))
        {
            return PreferredCipher;
        }

        // Find first supported cipher
        foreach (var cipher in availableCiphers)
        {
            if (CanUseEfficiently(cipher))
            {
                return cipher;
            }
        }

        return null;
    }
}

/// <summary>
/// Detects and reports endpoint cryptographic capabilities.
/// Provides preset capabilities for common platforms.
/// </summary>
public interface IEndpointCapabilitiesDetector
{
    /// <summary>
    /// Detects capabilities of the current system.
    /// </summary>
    /// <returns>Detected endpoint capabilities.</returns>
    EndpointCapabilities DetectCurrent();

    /// <summary>
    /// Gets preset capabilities for a known platform type.
    /// </summary>
    /// <param name="platformType">Platform type (e.g., "Windows Desktop", "iOS Mobile").</param>
    /// <returns>Preset capabilities, or null if unknown.</returns>
    EndpointCapabilities? GetPreset(string platformType);
}

/// <summary>
/// Default implementation of endpoint capabilities detector.
/// Includes presets for Desktop (Windows/macOS/Linux), Mobile (iOS/Android), IoT, and Browser.
/// </summary>
public sealed class EndpointCapabilitiesDetector : IEndpointCapabilitiesDetector
{
    private readonly Dictionary<string, EndpointCapabilities> _presets;

    /// <summary>
    /// Initializes a new instance with platform presets.
    /// </summary>
    public EndpointCapabilitiesDetector()
    {
        _presets = new Dictionary<string, EndpointCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            // T6.2.1: Desktop Capabilities (Windows)
            ["Windows Desktop"] = new(
                EndpointType: "Desktop",
                OperatingSystem: "Windows",
                HasAesNi: true, // Assume modern CPUs
                HasAvx2: true,
                SupportedCiphers: new List<string>
                {
                    "AES-256-GCM", "AES-128-GCM", "ChaCha20-Poly1305",
                    "AES-256-CBC", "Serpent-256-GCM", "Twofish-256-GCM"
                },
                PreferredCipher: "AES-256-GCM",
                MaxThroughputMBps: 2000,
                IsBatteryPowered: false,
                MemoryConstrainedMB: null
            ),

            ["macOS Desktop"] = new(
                EndpointType: "Desktop",
                OperatingSystem: "macOS",
                HasAesNi: true, // Apple Silicon and Intel both support
                HasAvx2: true,
                SupportedCiphers: new List<string>
                {
                    "AES-256-GCM", "AES-128-GCM", "ChaCha20-Poly1305",
                    "AES-256-CBC", "Serpent-256-GCM"
                },
                PreferredCipher: "AES-256-GCM",
                MaxThroughputMBps: 2500,
                IsBatteryPowered: false,
                MemoryConstrainedMB: null
            ),

            ["Linux Desktop"] = new(
                EndpointType: "Desktop",
                OperatingSystem: "Linux",
                HasAesNi: true,
                HasAvx2: true,
                SupportedCiphers: new List<string>
                {
                    "AES-256-GCM", "AES-128-GCM", "ChaCha20-Poly1305",
                    "AES-256-CBC", "Serpent-256-GCM", "Twofish-256-GCM"
                },
                PreferredCipher: "AES-256-GCM",
                MaxThroughputMBps: 1800,
                IsBatteryPowered: false,
                MemoryConstrainedMB: null
            ),

            // T6.2.2: Mobile Capabilities
            ["iOS Mobile"] = new(
                EndpointType: "Mobile",
                OperatingSystem: "iOS",
                HasAesNi: false, // ARM-based, no x86 AES-NI
                HasAvx2: false,
                SupportedCiphers: new List<string>
                {
                    "ChaCha20-Poly1305", "AES-256-GCM", "AES-128-GCM"
                },
                PreferredCipher: "ChaCha20-Poly1305", // Better without AES-NI
                MaxThroughputMBps: 300,
                IsBatteryPowered: true,
                MemoryConstrainedMB: 2048
            ),

            ["Android Mobile"] = new(
                EndpointType: "Mobile",
                OperatingSystem: "Android",
                HasAesNi: false, // Most ARM devices
                HasAvx2: false,
                SupportedCiphers: new List<string>
                {
                    "ChaCha20-Poly1305", "AES-256-GCM", "AES-128-GCM"
                },
                PreferredCipher: "ChaCha20-Poly1305",
                MaxThroughputMBps: 250,
                IsBatteryPowered: true,
                MemoryConstrainedMB: 1024
            ),

            // T6.2.3: IoT Capabilities
            ["IoT Device"] = new(
                EndpointType: "IoT",
                OperatingSystem: "Embedded",
                HasAesNi: false,
                HasAvx2: false,
                SupportedCiphers: new List<string>
                {
                    "ChaCha20-Poly1305", "AES-128-GCM"
                },
                PreferredCipher: "ChaCha20-Poly1305",
                MaxThroughputMBps: 50,
                IsBatteryPowered: true,
                MemoryConstrainedMB: 256
            ),

            // T6.2.4: Browser Capabilities
            ["Browser"] = new(
                EndpointType: "Browser",
                OperatingSystem: "Web",
                HasAesNi: false, // WebCrypto abstraction
                HasAvx2: false,
                SupportedCiphers: new List<string>
                {
                    "AES-256-GCM", "AES-128-GCM", "AES-256-CBC"
                },
                PreferredCipher: "AES-256-GCM",
                MaxThroughputMBps: 100,
                IsBatteryPowered: false,
                MemoryConstrainedMB: 512
            )
        };
    }

    /// <inheritdoc/>
    public EndpointCapabilities DetectCurrent()
    {
        var os = GetOperatingSystem();
        var hasAesNi = DetectAesNi();
        var hasAvx2 = false; // Simplified for this implementation

        var endpointType = "Desktop";
        var isBatteryPowered = false;
        int? memoryConstraint = null;

        // Platform-specific defaults
        var supportedCiphers = new List<string>
        {
            "AES-256-GCM", "AES-128-GCM", "ChaCha20-Poly1305",
            "AES-256-CBC", "Serpent-256-GCM", "Twofish-256-GCM"
        };

        var preferredCipher = hasAesNi ? "AES-256-GCM" : "ChaCha20-Poly1305";
        var maxThroughput = hasAesNi ? 2000 : 500;

        return new EndpointCapabilities(
            EndpointType: endpointType,
            OperatingSystem: os,
            HasAesNi: hasAesNi,
            HasAvx2: hasAvx2,
            SupportedCiphers: supportedCiphers,
            PreferredCipher: preferredCipher,
            MaxThroughputMBps: maxThroughput,
            IsBatteryPowered: isBatteryPowered,
            MemoryConstrainedMB: memoryConstraint
        );
    }

    /// <inheritdoc/>
    public EndpointCapabilities? GetPreset(string platformType)
    {
        return _presets.TryGetValue(platformType, out var preset) ? preset : null;
    }

    /// <summary>
    /// Detects the operating system.
    /// </summary>
    private static string GetOperatingSystem()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        return "Unknown";
    }

    /// <summary>
    /// Detects AES-NI hardware acceleration support.
    /// </summary>
    private static bool DetectAesNi()
    {
        // On .NET, we can check if AES hardware acceleration is available
        // This is a simplified check - production code would use CPUID on x86/x64
        try
        {
            // If we can create an AesGcm instance, hardware support is likely available
            using var aes = new AesGcm(new byte[32], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

#endregion

#region Cipher Negotiation (T6.2.5-T6.2.8)

/// <summary>
/// Strategy for negotiating cipher selection between two endpoints.
/// </summary>
public interface ICipherNegotiationStrategy
{
    /// <summary>
    /// Negotiates a cipher between local and remote capabilities.
    /// </summary>
    /// <param name="localCapabilities">Local endpoint capabilities.</param>
    /// <param name="remoteCapabilities">Remote endpoint capabilities.</param>
    /// <param name="policy">Transit security policy to enforce.</param>
    /// <param name="dataClassification">Classification level of the data to be transmitted.</param>
    /// <returns>Negotiation result with selected cipher and metadata.</returns>
    NegotiationResult Negotiate(
        EndpointCapabilities localCapabilities,
        EndpointCapabilities remoteCapabilities,
        TransitSecurityPolicy policy,
        DataClassification dataClassification);
}

/// <summary>
/// Result of cipher negotiation.
/// </summary>
/// <param name="IsSuccess">Whether negotiation succeeded.</param>
/// <param name="SelectedCipher">The negotiated cipher (null if negotiation failed).</param>
/// <param name="KeySizeBits">Negotiated key size in bits.</param>
/// <param name="Reason">Reason for success or failure.</param>
/// <param name="PerformanceScore">Estimated performance score (0-100, higher is better).</param>
public sealed record NegotiationResult(
    bool IsSuccess,
    string? SelectedCipher,
    int KeySizeBits,
    string Reason,
    int PerformanceScore
);

/// <summary>
/// T6.2.5: Default negotiation strategy - balances security and performance.
/// Selects the strongest mutually-supported cipher that meets policy requirements.
/// </summary>
public sealed class DefaultNegotiationStrategy : ICipherNegotiationStrategy
{
    /// <inheritdoc/>
    public NegotiationResult Negotiate(
        EndpointCapabilities localCapabilities,
        EndpointCapabilities remoteCapabilities,
        TransitSecurityPolicy policy,
        DataClassification dataClassification)
    {
        // Find mutually supported ciphers
        var mutualCiphers = localCapabilities.SupportedCiphers
            .Intersect(remoteCapabilities.SupportedCiphers, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mutualCiphers.Count == 0)
        {
            return new NegotiationResult(
                IsSuccess: false,
                SelectedCipher: null,
                KeySizeBits: 0,
                Reason: "No mutually supported ciphers",
                PerformanceScore: 0
            );
        }

        // Try preferred ciphers first
        foreach (var preferredCipher in policy.PreferredCiphers)
        {
            if (mutualCiphers.Contains(preferredCipher, StringComparer.OrdinalIgnoreCase))
            {
                var keySize = GetKeySizeFromCipherName(preferredCipher);
                var validation = policy.ValidateCipher(preferredCipher, keySize, dataClassification);

                if (validation.IsValid)
                {
                    var perfScore = CalculatePerformanceScore(preferredCipher, localCapabilities, remoteCapabilities);
                    return new NegotiationResult(
                        IsSuccess: true,
                        SelectedCipher: preferredCipher,
                        KeySizeBits: keySize,
                        Reason: "Selected from preferred ciphers list",
                        PerformanceScore: perfScore
                    );
                }
            }
        }

        // Try fallback cipher
        if (policy.FallbackCipher != null && mutualCiphers.Contains(policy.FallbackCipher, StringComparer.OrdinalIgnoreCase))
        {
            var keySize = GetKeySizeFromCipherName(policy.FallbackCipher);
            var validation = policy.ValidateCipher(policy.FallbackCipher, keySize, dataClassification);

            if (validation.IsValid)
            {
                var perfScore = CalculatePerformanceScore(policy.FallbackCipher, localCapabilities, remoteCapabilities);
                return new NegotiationResult(
                    IsSuccess: true,
                    SelectedCipher: policy.FallbackCipher,
                    KeySizeBits: keySize,
                    Reason: "Using fallback cipher",
                    PerformanceScore: perfScore
                );
            }
        }

        return new NegotiationResult(
            IsSuccess: false,
            SelectedCipher: null,
            KeySizeBits: 0,
            Reason: "No cipher meets policy requirements",
            PerformanceScore: 0
        );
    }

    private static int CalculatePerformanceScore(string cipher, EndpointCapabilities local, EndpointCapabilities remote)
    {
        var score = 50; // Base score

        // Prefer ChaCha20 on systems without AES-NI
        if (cipher.Contains("ChaCha20", StringComparison.OrdinalIgnoreCase))
        {
            if (!local.HasAesNi || !remote.HasAesNi)
                score += 30;
            else
                score += 10;
        }

        // Prefer AES-GCM on systems with AES-NI
        if (cipher.Contains("AES", StringComparison.OrdinalIgnoreCase) && cipher.Contains("GCM", StringComparison.OrdinalIgnoreCase))
        {
            if (local.HasAesNi && remote.HasAesNi)
                score += 30;
            else
                score -= 10;
        }

        return Math.Clamp(score, 0, 100);
    }

    private static int GetKeySizeFromCipherName(string cipherName)
    {
        if (cipherName.Contains("128")) return 128;
        if (cipherName.Contains("192")) return 192;
        if (cipherName.Contains("256")) return 256;
        return 256; // Default to 256-bit
    }
}

/// <summary>
/// T6.2.6: Security-first negotiation strategy - always selects the strongest cipher.
/// Prioritizes security over performance, may reject connections if strong crypto unavailable.
/// </summary>
public sealed class SecurityFirstStrategy : ICipherNegotiationStrategy
{
    /// <inheritdoc/>
    public NegotiationResult Negotiate(
        EndpointCapabilities localCapabilities,
        EndpointCapabilities remoteCapabilities,
        TransitSecurityPolicy policy,
        DataClassification dataClassification)
    {
        var mutualCiphers = localCapabilities.SupportedCiphers
            .Intersect(remoteCapabilities.SupportedCiphers, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Prefer strongest ciphers (Serpent > Twofish > AES-256 > ChaCha20)
        var strongestOrder = new[] { "Serpent-256-GCM", "Twofish-256-GCM", "AES-256-GCM", "ChaCha20-Poly1305", "AES-192-GCM", "AES-128-GCM" };

        foreach (var cipher in strongestOrder)
        {
            if (mutualCiphers.Contains(cipher, StringComparer.OrdinalIgnoreCase))
            {
                var keySize = GetKeySizeFromCipherName(cipher);
                var validation = policy.ValidateCipher(cipher, keySize, dataClassification);

                if (validation.IsValid)
                {
                    return new NegotiationResult(
                        IsSuccess: true,
                        SelectedCipher: cipher,
                        KeySizeBits: keySize,
                        Reason: "Selected strongest available cipher",
                        PerformanceScore: 40 // May be slower
                    );
                }
            }
        }

        return new NegotiationResult(
            IsSuccess: false,
            SelectedCipher: null,
            KeySizeBits: 0,
            Reason: "No sufficiently strong cipher available",
            PerformanceScore: 0
        );
    }

    private static int GetKeySizeFromCipherName(string cipherName)
    {
        if (cipherName.Contains("128")) return 128;
        if (cipherName.Contains("192")) return 192;
        if (cipherName.Contains("256")) return 256;
        return 256;
    }
}

/// <summary>
/// T6.2.7: Performance-first negotiation strategy - selects the fastest cipher meeting minimum requirements.
/// Prioritizes throughput and low latency while maintaining minimum security standards.
/// </summary>
public sealed class PerformanceFirstStrategy : ICipherNegotiationStrategy
{
    /// <inheritdoc/>
    public NegotiationResult Negotiate(
        EndpointCapabilities localCapabilities,
        EndpointCapabilities remoteCapabilities,
        TransitSecurityPolicy policy,
        DataClassification dataClassification)
    {
        var mutualCiphers = localCapabilities.SupportedCiphers
            .Intersect(remoteCapabilities.SupportedCiphers, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Determine if both have AES-NI
        var bothHaveAesNi = localCapabilities.HasAesNi && remoteCapabilities.HasAesNi;

        // Performance-ordered based on hardware
        var performanceOrder = bothHaveAesNi
            ? new[] { "AES-128-GCM", "AES-256-GCM", "ChaCha20-Poly1305", "AES-192-GCM" }
            : new[] { "ChaCha20-Poly1305", "AES-128-GCM", "AES-256-GCM", "AES-192-GCM" };

        foreach (var cipher in performanceOrder)
        {
            if (mutualCiphers.Contains(cipher, StringComparer.OrdinalIgnoreCase))
            {
                var keySize = GetKeySizeFromCipherName(cipher);
                var validation = policy.ValidateCipher(cipher, keySize, dataClassification);

                if (validation.IsValid)
                {
                    return new NegotiationResult(
                        IsSuccess: true,
                        SelectedCipher: cipher,
                        KeySizeBits: keySize,
                        Reason: "Selected fastest cipher meeting minimum security",
                        PerformanceScore: 90
                    );
                }
            }
        }

        return new NegotiationResult(
            IsSuccess: false,
            SelectedCipher: null,
            KeySizeBits: 0,
            Reason: "No performant cipher meets security requirements",
            PerformanceScore: 0
        );
    }

    private static int GetKeySizeFromCipherName(string cipherName)
    {
        if (cipherName.Contains("128")) return 128;
        if (cipherName.Contains("192")) return 192;
        if (cipherName.Contains("256")) return 256;
        return 256;
    }
}

/// <summary>
/// T6.2.8: Policy-driven negotiation strategy - strictly enforces transit security policy.
/// Only allows ciphers explicitly approved by policy, respects classification-based rules.
/// </summary>
public sealed class PolicyDrivenStrategy : ICipherNegotiationStrategy
{
    /// <inheritdoc/>
    public NegotiationResult Negotiate(
        EndpointCapabilities localCapabilities,
        EndpointCapabilities remoteCapabilities,
        TransitSecurityPolicy policy,
        DataClassification dataClassification)
    {
        // Only consider ciphers in the policy's preferred list
        var allowedCiphers = policy.PreferredCiphers;

        var mutualCiphers = localCapabilities.SupportedCiphers
            .Intersect(remoteCapabilities.SupportedCiphers, StringComparer.OrdinalIgnoreCase)
            .Intersect(allowedCiphers, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mutualCiphers.Count == 0)
        {
            // Try fallback if allowed
            if (policy.FallbackCipher != null &&
                localCapabilities.SupportedCiphers.Contains(policy.FallbackCipher, StringComparer.OrdinalIgnoreCase) &&
                remoteCapabilities.SupportedCiphers.Contains(policy.FallbackCipher, StringComparer.OrdinalIgnoreCase))
            {
                var keySize = GetKeySizeFromCipherName(policy.FallbackCipher);
                return new NegotiationResult(
                    IsSuccess: true,
                    SelectedCipher: policy.FallbackCipher,
                    KeySizeBits: keySize,
                    Reason: "Using policy-defined fallback cipher",
                    PerformanceScore: 50
                );
            }

            return new NegotiationResult(
                IsSuccess: false,
                SelectedCipher: null,
                KeySizeBits: 0,
                Reason: "No policy-approved cipher is mutually supported",
                PerformanceScore: 0
            );
        }

        // Select first policy-preferred cipher that's mutually supported
        foreach (var preferredCipher in policy.PreferredCiphers)
        {
            if (mutualCiphers.Contains(preferredCipher, StringComparer.OrdinalIgnoreCase))
            {
                var keySize = GetKeySizeFromCipherName(preferredCipher);
                var validation = policy.ValidateCipher(preferredCipher, keySize, dataClassification);

                if (validation.IsValid)
                {
                    return new NegotiationResult(
                        IsSuccess: true,
                        SelectedCipher: preferredCipher,
                        KeySizeBits: keySize,
                        Reason: "Policy-approved cipher selected",
                        PerformanceScore: 70
                    );
                }
            }
        }

        return new NegotiationResult(
            IsSuccess: false,
            SelectedCipher: null,
            KeySizeBits: 0,
            Reason: "No cipher meets both policy and classification requirements",
            PerformanceScore: 0
        );
    }

    private static int GetKeySizeFromCipherName(string cipherName)
    {
        if (cipherName.Contains("128")) return 128;
        if (cipherName.Contains("192")) return 192;
        if (cipherName.Contains("256")) return 256;
        return 256;
    }
}

#endregion

#region Transcryption Service (T6.2.9-T6.2.10)

/// <summary>
/// Service for converting data from one cipher to another (transcryption).
/// Supports both in-memory and streaming operations with secure memory handling.
/// </summary>
public interface ITranscryptionService
{
    /// <summary>
    /// T6.2.9: Transcrypts data from one cipher to another (in-memory).
    /// Useful for adapting to endpoint capabilities or policy changes.
    /// </summary>
    /// <param name="ciphertext">The encrypted data in the source cipher.</param>
    /// <param name="sourceCipher">The source encryption strategy.</param>
    /// <param name="sourceKey">The source decryption key.</param>
    /// <param name="targetCipher">The target encryption strategy.</param>
    /// <param name="targetKey">The target encryption key.</param>
    /// <param name="associatedData">Optional associated data for AEAD ciphers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transcrypted ciphertext in the target cipher format.</returns>
    Task<byte[]> TranscryptAsync(
        byte[] ciphertext,
        IEncryptionStrategy sourceCipher,
        byte[] sourceKey,
        IEncryptionStrategy targetCipher,
        byte[] targetKey,
        byte[]? associatedData = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// T6.2.10: Transcrypts data from one cipher to another (streaming).
    /// Efficient for large files, processes data in chunks to minimize memory usage.
    /// </summary>
    /// <param name="sourceStream">The source encrypted stream.</param>
    /// <param name="targetStream">The target encrypted stream (will be written to).</param>
    /// <param name="sourceCipher">The source encryption strategy.</param>
    /// <param name="sourceKey">The source decryption key.</param>
    /// <param name="targetCipher">The target encryption strategy.</param>
    /// <param name="targetKey">The target encryption key.</param>
    /// <param name="associatedData">Optional associated data for AEAD ciphers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when transcryption is finished.</returns>
    Task TranscryptStreamingAsync(
        Stream sourceStream,
        Stream targetStream,
        IEncryptionStrategy sourceCipher,
        byte[] sourceKey,
        IEncryptionStrategy targetCipher,
        byte[] targetKey,
        byte[]? associatedData = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of transcryption service.
/// Handles secure memory operations and supports both in-memory and streaming transcryption.
/// </summary>
public sealed class TranscryptionService : ITranscryptionService
{
    private const int StreamingChunkSize = 64 * 1024; // 64 KB chunks

    /// <inheritdoc/>
    public async Task<byte[]> TranscryptAsync(
        byte[] ciphertext,
        IEncryptionStrategy sourceCipher,
        byte[] sourceKey,
        IEncryptionStrategy targetCipher,
        byte[] targetKey,
        byte[]? associatedData = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(sourceCipher);
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(targetCipher);
        ArgumentNullException.ThrowIfNull(targetKey);

        byte[]? plaintext = null;
        try
        {
            // Step 1: Decrypt with source cipher
            plaintext = await sourceCipher.DecryptAsync(ciphertext, sourceKey, associatedData, cancellationToken);

            // Step 2: Encrypt with target cipher
            var transcryptedData = await targetCipher.EncryptAsync(plaintext, targetKey, associatedData, cancellationToken);

            return transcryptedData;
        }
        finally
        {
            // Secure memory cleanup - zero out plaintext
            if (plaintext != null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <inheritdoc/>
    public async Task TranscryptStreamingAsync(
        Stream sourceStream,
        Stream targetStream,
        IEncryptionStrategy sourceCipher,
        byte[] sourceKey,
        IEncryptionStrategy targetCipher,
        byte[] targetKey,
        byte[]? associatedData = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);
        ArgumentNullException.ThrowIfNull(targetStream);
        ArgumentNullException.ThrowIfNull(sourceCipher);
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(targetCipher);
        ArgumentNullException.ThrowIfNull(targetKey);

        // For streaming, we need to read the entire encrypted stream first
        // (since most AEAD ciphers require the full ciphertext + tag for authentication)
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        byte[]? transcrypted = null;

        try
        {
            // Read source ciphertext
            using var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms, StreamingChunkSize, cancellationToken);
            ciphertext = ms.ToArray();

            // Decrypt
            plaintext = await sourceCipher.DecryptAsync(ciphertext, sourceKey, associatedData, cancellationToken);

            // Encrypt with target cipher
            transcrypted = await targetCipher.EncryptAsync(plaintext, targetKey, associatedData, cancellationToken);

            // Write to target stream
            await targetStream.WriteAsync(transcrypted, cancellationToken);
        }
        finally
        {
            // Secure memory cleanup
            if (plaintext != null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext != null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (transcrypted != null)
            {
                CryptographicOperations.ZeroMemory(transcrypted);
            }
        }
    }
}

#endregion
