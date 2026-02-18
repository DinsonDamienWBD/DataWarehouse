// <copyright file="BitLockerIntegration.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Provides BitLocker encrypted drive detection and management capabilities.
/// Integrates with WinFSP driver to support mounting on BitLocker-protected volumes.
/// </summary>
/// <remarks>
/// This class uses Windows Management Instrumentation (WMI) to query BitLocker status
/// and native Windows APIs to detect TPM availability. It supports:
/// <list type="bullet">
/// <item>BitLocker encryption status detection</item>
/// <item>Encryption method identification (AES-128, AES-256, XTS-AES)</item>
/// <item>Protection status (locked/unlocked)</item>
/// <item>Hardware TPM detection</item>
/// <item>Recovery key authentication support</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BitLockerIntegration : IDisposable
{
    private readonly BitLockerConfig _config;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the BitLocker configuration options.
    /// </summary>
    public BitLockerConfig Config => _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="BitLockerIntegration"/> class.
    /// </summary>
    /// <param name="config">BitLocker configuration options.</param>
    public BitLockerIntegration(BitLockerConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Checks if a volume is BitLocker encrypted.
    /// </summary>
    /// <param name="volumePath">Volume path (e.g., "C:", "D:\", or full mount point).</param>
    /// <returns>BitLocker status information for the volume.</returns>
    /// <exception cref="BitLockerException">Thrown when BitLocker query fails.</exception>
    public BitLockerStatus CheckVolumeEncryption(string volumePath)
    {
        if (string.IsNullOrEmpty(volumePath))
            throw new ArgumentNullException(nameof(volumePath));

        lock (_lock)
        {
            try
            {
                var driveLetter = ExtractDriveLetter(volumePath);
                if (string.IsNullOrEmpty(driveLetter))
                {
                    return new BitLockerStatus
                    {
                        IsEncrypted = false,
                        ProtectionStatus = ProtectionStatus.Unprotected,
                        ConversionStatus = ConversionStatus.FullyDecrypted,
                        EncryptionMethod = EncryptionMethod.None,
                        VolumeType = VolumeType.OperatingSystem
                    };
                }

                // Validate drive letter to prevent WMI injection
                if (driveLetter.Length != 1 || !IsValidDriveLetter(driveLetter[0]))
                {
                    throw new ArgumentException($"Invalid drive letter: {driveLetter}", nameof(volumePath));
                }

                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2\\Security\\MicrosoftVolumeEncryption",
                    $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}:'");

                var collection = searcher.Get();
                if (collection.Count == 0)
                {
                    // Volume exists but is not encryptable or BitLocker is not available
                    return new BitLockerStatus
                    {
                        IsEncrypted = false,
                        ProtectionStatus = ProtectionStatus.Unprotected,
                        ConversionStatus = ConversionStatus.FullyDecrypted,
                        EncryptionMethod = EncryptionMethod.None,
                        VolumeType = VolumeType.Data
                    };
                }

                foreach (ManagementObject volume in collection)
                {
                    var status = ParseBitLockerVolume(volume, driveLetter);
                    return status;
                }

                return new BitLockerStatus
                {
                    IsEncrypted = false,
                    ProtectionStatus = ProtectionStatus.Unprotected,
                    ConversionStatus = ConversionStatus.FullyDecrypted,
                    EncryptionMethod = EncryptionMethod.None,
                    VolumeType = VolumeType.Data
                };
            }
            catch (ManagementException ex)
            {
                throw new BitLockerException($"Failed to query BitLocker status for volume {volumePath}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new BitLockerException($"Access denied when querying BitLocker status for volume {volumePath}. Administrator privileges may be required.", ex);
            }
        }
    }

    /// <summary>
    /// Validates that a volume meets BitLocker requirements based on configuration.
    /// </summary>
    /// <param name="volumePath">Volume path to validate.</param>
    /// <returns>Validation result with success status and optional error message.</returns>
    public BitLockerValidationResult ValidateVolume(string volumePath)
    {
        if (!_config.EnableBitLockerSupport)
        {
            return new BitLockerValidationResult
            {
                IsValid = true,
                Message = "BitLocker support is disabled"
            };
        }

        try
        {
            var status = CheckVolumeEncryption(volumePath);

            if (_config.RequireEncryptedVolume && !status.IsEncrypted)
            {
                return new BitLockerValidationResult
                {
                    IsValid = false,
                    Message = $"Volume {volumePath} is not BitLocker encrypted, but encryption is required by configuration"
                };
            }

            if (status.IsEncrypted && status.ProtectionStatus == ProtectionStatus.Locked)
            {
                if (!_config.AllowRecoveryKeyUnlock)
                {
                    return new BitLockerValidationResult
                    {
                        IsValid = false,
                        Message = $"Volume {volumePath} is locked and recovery key unlock is not allowed"
                    };
                }

                return new BitLockerValidationResult
                {
                    IsValid = false,
                    RequiresUnlock = true,
                    Message = $"Volume {volumePath} is locked and requires unlocking with recovery key or password"
                };
            }

            if (status.IsEncrypted && status.ConversionStatus == ConversionStatus.EncryptionInProgress)
            {
                return new BitLockerValidationResult
                {
                    IsValid = false,
                    Message = $"Volume {volumePath} encryption is in progress. Please wait for completion."
                };
            }

            if (status.IsEncrypted && status.ConversionStatus == ConversionStatus.DecryptionInProgress)
            {
                return new BitLockerValidationResult
                {
                    IsValid = false,
                    Message = $"Volume {volumePath} decryption is in progress. Please wait for completion."
                };
            }

            return new BitLockerValidationResult
            {
                IsValid = true,
                BitLockerStatus = status,
                Message = status.IsEncrypted
                    ? $"Volume {volumePath} is BitLocker encrypted ({status.EncryptionMethod}) and unlocked"
                    : $"Volume {volumePath} is not encrypted"
            };
        }
        catch (BitLockerException ex)
        {
            return new BitLockerValidationResult
            {
                IsValid = !_config.RequireEncryptedVolume,
                Message = $"Failed to validate BitLocker status: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Detects if a hardware Trusted Platform Module (TPM) is available.
    /// </summary>
    /// <returns>TPM detection result with version and status information.</returns>
    public TpmStatus DetectTpm()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2\\Security\\MicrosoftTpm",
                "SELECT * FROM Win32_Tpm");

            var collection = searcher.Get();
            if (collection.Count == 0)
            {
                return new TpmStatus
                {
                    IsPresent = false,
                    IsEnabled = false,
                    IsActivated = false,
                    IsOwned = false,
                    SpecVersion = null
                };
            }

            foreach (ManagementObject tpm in collection)
            {
                var isPresent = true;
                var isEnabled = GetBoolProperty(tpm, "IsEnabled_InitialValue");
                var isActivated = GetBoolProperty(tpm, "IsActivated_InitialValue");
                var isOwned = GetBoolProperty(tpm, "IsOwned_InitialValue");

                var specVersionProp = tpm["SpecVersion"] as string;

                return new TpmStatus
                {
                    IsPresent = isPresent,
                    IsEnabled = isEnabled,
                    IsActivated = isActivated,
                    IsOwned = isOwned,
                    SpecVersion = specVersionProp,
                    ManufacturerId = tpm["ManufacturerId"] as string,
                    ManufacturerVersion = tpm["ManufacturerVersion"] as string
                };
            }

            return new TpmStatus
            {
                IsPresent = false,
                IsEnabled = false,
                IsActivated = false,
                IsOwned = false
            };
        }
        catch (ManagementException)
        {
            // TPM WMI namespace not available
            return new TpmStatus
            {
                IsPresent = false,
                IsEnabled = false,
                IsActivated = false,
                IsOwned = false
            };
        }
        catch (UnauthorizedAccessException)
        {
            // Access denied to TPM information
            return new TpmStatus
            {
                IsPresent = null, // Unknown
                IsEnabled = false,
                IsActivated = false,
                IsOwned = false
            };
        }
    }

    /// <summary>
    /// Gets detailed information about BitLocker protectors for a volume.
    /// </summary>
    /// <param name="volumePath">Volume path to query.</param>
    /// <returns>List of protector information.</returns>
    public List<BitLockerProtectorInfo> GetVolumeProtectors(string volumePath)
    {
        var protectors = new List<BitLockerProtectorInfo>();
        var driveLetter = ExtractDriveLetter(volumePath);

        if (string.IsNullOrEmpty(driveLetter))
            return protectors;

        // Validate drive letter to prevent WMI injection
        if (driveLetter.Length != 1 || !IsValidDriveLetter(driveLetter[0]))
        {
            throw new ArgumentException($"Invalid drive letter: {driveLetter}", nameof(volumePath));
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2\\Security\\MicrosoftVolumeEncryption",
                $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}:'");

            var collection = searcher.Get();
            foreach (ManagementObject volume in collection)
            {
                var getKeyProtectorsMethod = volume.GetMethodParameters("GetKeyProtectors");
                getKeyProtectorsMethod["KeyProtectorType"] = 0; // All types

                var result = volume.InvokeMethod("GetKeyProtectors", getKeyProtectorsMethod, null);
                if (result?["VolumeKeyProtectorID"] is string[] protectorIds)
                {
                    foreach (var protectorId in protectorIds)
                    {
                        var getTypeMethod = volume.GetMethodParameters("GetKeyProtectorType");
                        getTypeMethod["VolumeKeyProtectorID"] = protectorId;

                        var typeResult = volume.InvokeMethod("GetKeyProtectorType", getTypeMethod, null);
                        if (typeResult?["KeyProtectorType"] is uint protectorType)
                        {
                            protectors.Add(new BitLockerProtectorInfo
                            {
                                ProtectorId = protectorId,
                                ProtectorType = MapProtectorType(protectorType)
                            });
                        }
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Unable to query protectors
        }

        return protectors;
    }

    /// <summary>
    /// Performs a health check on BitLocker encryption status for logging.
    /// </summary>
    /// <param name="volumePath">Volume path to check.</param>
    /// <returns>Health check result with status and recommendations.</returns>
    public BitLockerHealthCheck PerformHealthCheck(string volumePath)
    {
        var healthCheck = new BitLockerHealthCheck
        {
            VolumeePath = volumePath,
            CheckedAt = DateTime.UtcNow,
            Issues = new List<string>(),
            Recommendations = new List<string>()
        };

        try
        {
            var status = CheckVolumeEncryption(volumePath);
            healthCheck.BitLockerStatus = status;

            if (!status.IsEncrypted && _config.RequireEncryptedVolume)
            {
                healthCheck.OverallStatus = HealthStatus.Critical;
                healthCheck.Issues.Add("Volume is not encrypted but encryption is required");
                healthCheck.Recommendations.Add("Enable BitLocker encryption on this volume");
                return healthCheck;
            }

            if (status.IsEncrypted)
            {
                if (status.ProtectionStatus == ProtectionStatus.Locked)
                {
                    healthCheck.OverallStatus = HealthStatus.Critical;
                    healthCheck.Issues.Add("Volume is locked");
                    healthCheck.Recommendations.Add("Unlock the volume using recovery key or password");
                    return healthCheck;
                }

                if (status.ProtectionStatus == ProtectionStatus.ProtectionSuspended)
                {
                    healthCheck.OverallStatus = HealthStatus.Warning;
                    healthCheck.Issues.Add("BitLocker protection is suspended");
                    healthCheck.Recommendations.Add("Resume BitLocker protection");
                }

                if (status.ConversionStatus == ConversionStatus.EncryptionInProgress ||
                    status.ConversionStatus == ConversionStatus.DecryptionInProgress)
                {
                    healthCheck.OverallStatus = HealthStatus.Warning;
                    healthCheck.Issues.Add($"Conversion in progress: {status.ConversionStatus}");
                    healthCheck.Recommendations.Add("Wait for encryption/decryption to complete");
                }

                // Check for weak encryption methods
                if (status.EncryptionMethod == EncryptionMethod.Aes128 ||
                    status.EncryptionMethod == EncryptionMethod.Aes128Diffuser)
                {
                    if (healthCheck.OverallStatus == HealthStatus.Healthy)
                        healthCheck.OverallStatus = HealthStatus.Warning;

                    healthCheck.Recommendations.Add("Consider upgrading to AES-256 or XTS-AES for stronger encryption");
                }

                // Check protectors
                var protectors = GetVolumeProtectors(volumePath);
                if (protectors.Count == 0)
                {
                    healthCheck.OverallStatus = HealthStatus.Warning;
                    healthCheck.Issues.Add("No key protectors found");
                }
                else
                {
                    healthCheck.Recommendations.Add($"Volume has {protectors.Count} key protector(s) configured");
                }
            }

            if (healthCheck.OverallStatus == HealthStatus.Unknown)
            {
                healthCheck.OverallStatus = HealthStatus.Healthy;
            }
        }
        catch (BitLockerException ex)
        {
            healthCheck.OverallStatus = HealthStatus.Error;
            healthCheck.Issues.Add($"BitLocker health check failed: {ex.Message}");
        }

        return healthCheck;
    }

    #region Private Methods

    private static string ExtractDriveLetter(string volumePath)
    {
        if (string.IsNullOrEmpty(volumePath))
            return string.Empty;

        volumePath = volumePath.Trim();

        // Handle drive letter formats: "C", "C:", "C:\", "C:\path"
        if (volumePath.Length >= 1 && char.IsLetter(volumePath[0]))
        {
            return volumePath[0].ToString().ToUpperInvariant();
        }

        return string.Empty;
    }

    /// <summary>
    /// Validates that a character is a valid drive letter (A-Z, case insensitive).
    /// </summary>
    /// <param name="letter">Character to validate.</param>
    /// <returns>True if the character is a valid drive letter; otherwise, false.</returns>
    private static bool IsValidDriveLetter(char letter) => letter is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static BitLockerStatus ParseBitLockerVolume(ManagementObject volume, string driveLetter)
    {
        var protectionStatus = GetUInt32Property(volume, "ProtectionStatus");
        var conversionStatus = GetUInt32Property(volume, "ConversionStatus");
        var encryptionMethod = GetUInt32Property(volume, "EncryptionMethod");
        var volumeType = GetUInt32Property(volume, "VolumeType");
        var encryptionPercentage = GetUInt32Property(volume, "EncryptionPercentage");

        return new BitLockerStatus
        {
            DriveLetter = driveLetter,
            IsEncrypted = protectionStatus > 0,
            ProtectionStatus = MapProtectionStatus(protectionStatus),
            ConversionStatus = MapConversionStatus(conversionStatus),
            EncryptionMethod = MapEncryptionMethod(encryptionMethod),
            VolumeType = MapVolumeType(volumeType),
            EncryptionPercentage = encryptionPercentage
        };
    }

    private static uint GetUInt32Property(ManagementObject obj, string propertyName)
    {
        try
        {
            var value = obj[propertyName];
            if (value == null)
                return 0;

            return Convert.ToUInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static bool GetBoolProperty(ManagementObject obj, string propertyName)
    {
        try
        {
            var value = obj[propertyName];
            if (value == null)
                return false;

            return Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static ProtectionStatus MapProtectionStatus(uint status)
    {
        return status switch
        {
            0 => ProtectionStatus.Unprotected,
            1 => ProtectionStatus.Protected,
            2 => ProtectionStatus.ProtectionSuspended,
            3 => ProtectionStatus.Locked,
            _ => ProtectionStatus.Unknown
        };
    }

    private static ConversionStatus MapConversionStatus(uint status)
    {
        return status switch
        {
            0 => ConversionStatus.FullyDecrypted,
            1 => ConversionStatus.FullyEncrypted,
            2 => ConversionStatus.EncryptionInProgress,
            3 => ConversionStatus.DecryptionInProgress,
            4 => ConversionStatus.EncryptionPaused,
            5 => ConversionStatus.DecryptionPaused,
            _ => ConversionStatus.Unknown
        };
    }

    private static EncryptionMethod MapEncryptionMethod(uint method)
    {
        return method switch
        {
            0 => EncryptionMethod.None,
            1 => EncryptionMethod.Aes128Diffuser,
            2 => EncryptionMethod.Aes256Diffuser,
            3 => EncryptionMethod.Aes128,
            4 => EncryptionMethod.Aes256,
            6 => EncryptionMethod.XtsAes128,
            7 => EncryptionMethod.XtsAes256,
            _ => EncryptionMethod.Unknown
        };
    }

    private static VolumeType MapVolumeType(uint type)
    {
        return type switch
        {
            0 => VolumeType.OperatingSystem,
            1 => VolumeType.FixedData,
            2 => VolumeType.Portable,
            _ => VolumeType.Data
        };
    }

    private static ProtectorType MapProtectorType(uint type)
    {
        return type switch
        {
            0 => ProtectorType.Unknown,
            1 => ProtectorType.TpmProtector,
            2 => ProtectorType.ExternalKeyProtector,
            3 => ProtectorType.NumericalPasswordProtector,
            4 => ProtectorType.TpmPinProtector,
            5 => ProtectorType.TpmStartupKeyProtector,
            6 => ProtectorType.TpmPinStartupKeyProtector,
            7 => ProtectorType.PublicKeyProtector,
            8 => ProtectorType.PassphraseProtector,
            9 => ProtectorType.TpmCertificateProtector,
            10 => ProtectorType.CryptoApiNextGenerationCertificateProtector,
            _ => ProtectorType.Unknown
        };
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the BitLocker integration resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    #endregion
}

/// <summary>
/// Configuration options for BitLocker integration.
/// </summary>
public sealed class BitLockerConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether BitLocker support is enabled.
    /// When disabled, BitLocker checks are skipped.
    /// </summary>
    public bool EnableBitLockerSupport { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether encrypted volumes are required.
    /// When true, mounting will fail on non-encrypted volumes.
    /// </summary>
    public bool RequireEncryptedVolume { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether recovery key unlock is allowed.
    /// When false, locked volumes cannot be mounted even with recovery key.
    /// </summary>
    public bool AllowRecoveryKeyUnlock { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to log BitLocker status during mount operations.
    /// </summary>
    public bool LogEncryptionStatus { get; set; } = true;
}

/// <summary>
/// Represents the BitLocker encryption status of a volume.
/// </summary>
public sealed class BitLockerStatus
{
    /// <summary>
    /// Gets or sets the drive letter (e.g., "C").
    /// </summary>
    public string? DriveLetter { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the volume is encrypted.
    /// </summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Gets or sets the protection status of the volume.
    /// </summary>
    public ProtectionStatus ProtectionStatus { get; set; }

    /// <summary>
    /// Gets or sets the conversion status (encryption/decryption progress).
    /// </summary>
    public ConversionStatus ConversionStatus { get; set; }

    /// <summary>
    /// Gets or sets the encryption method used.
    /// </summary>
    public EncryptionMethod EncryptionMethod { get; set; }

    /// <summary>
    /// Gets or sets the volume type.
    /// </summary>
    public VolumeType VolumeType { get; set; }

    /// <summary>
    /// Gets or sets the encryption percentage (0-100).
    /// </summary>
    public uint EncryptionPercentage { get; set; }
}

/// <summary>
/// Protection status of a BitLocker volume.
/// </summary>
public enum ProtectionStatus
{
    /// <summary>Unknown status.</summary>
    Unknown,

    /// <summary>Volume is not protected.</summary>
    Unprotected,

    /// <summary>Volume is protected and unlocked.</summary>
    Protected,

    /// <summary>Protection is temporarily suspended.</summary>
    ProtectionSuspended,

    /// <summary>Volume is locked.</summary>
    Locked
}

/// <summary>
/// Conversion status indicating encryption/decryption progress.
/// </summary>
public enum ConversionStatus
{
    /// <summary>Unknown status.</summary>
    Unknown,

    /// <summary>Volume is fully decrypted.</summary>
    FullyDecrypted,

    /// <summary>Volume is fully encrypted.</summary>
    FullyEncrypted,

    /// <summary>Encryption is in progress.</summary>
    EncryptionInProgress,

    /// <summary>Decryption is in progress.</summary>
    DecryptionInProgress,

    /// <summary>Encryption is paused.</summary>
    EncryptionPaused,

    /// <summary>Decryption is paused.</summary>
    DecryptionPaused
}

/// <summary>
/// Encryption method used by BitLocker.
/// </summary>
public enum EncryptionMethod
{
    /// <summary>No encryption.</summary>
    None,

    /// <summary>Unknown encryption method.</summary>
    Unknown,

    /// <summary>AES-128 with Diffuser (deprecated).</summary>
    Aes128Diffuser,

    /// <summary>AES-256 with Diffuser (deprecated).</summary>
    Aes256Diffuser,

    /// <summary>AES-128 (CBC mode).</summary>
    Aes128,

    /// <summary>AES-256 (CBC mode).</summary>
    Aes256,

    /// <summary>XTS-AES-128 (recommended for Windows 10+).</summary>
    XtsAes128,

    /// <summary>XTS-AES-256 (recommended for Windows 10+).</summary>
    XtsAes256
}

/// <summary>
/// Type of BitLocker volume.
/// </summary>
public enum VolumeType
{
    /// <summary>Operating system volume.</summary>
    OperatingSystem,

    /// <summary>Fixed data volume.</summary>
    FixedData,

    /// <summary>Portable/removable volume.</summary>
    Portable,

    /// <summary>Generic data volume.</summary>
    Data
}

/// <summary>
/// TPM (Trusted Platform Module) status information.
/// </summary>
public sealed class TpmStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a TPM is present.
    /// Null indicates unknown status.
    /// </summary>
    public bool? IsPresent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the TPM is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the TPM is activated.
    /// </summary>
    public bool IsActivated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the TPM is owned.
    /// </summary>
    public bool IsOwned { get; set; }

    /// <summary>
    /// Gets or sets the TPM specification version (e.g., "1.2", "2.0").
    /// </summary>
    public string? SpecVersion { get; set; }

    /// <summary>
    /// Gets or sets the TPM manufacturer ID.
    /// </summary>
    public string? ManufacturerId { get; set; }

    /// <summary>
    /// Gets or sets the TPM manufacturer version.
    /// </summary>
    public string? ManufacturerVersion { get; set; }
}

/// <summary>
/// Result of BitLocker volume validation.
/// </summary>
public sealed class BitLockerValidationResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the volume is valid for mounting.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the volume requires manual unlock.
    /// </summary>
    public bool RequiresUnlock { get; set; }

    /// <summary>
    /// Gets or sets the validation message.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the BitLocker status information.
    /// </summary>
    public BitLockerStatus? BitLockerStatus { get; set; }
}

/// <summary>
/// Information about a BitLocker key protector.
/// </summary>
public sealed class BitLockerProtectorInfo
{
    /// <summary>
    /// Gets or sets the unique protector ID.
    /// </summary>
    public string? ProtectorId { get; set; }

    /// <summary>
    /// Gets or sets the type of key protector.
    /// </summary>
    public ProtectorType ProtectorType { get; set; }
}

/// <summary>
/// Type of BitLocker key protector.
/// </summary>
public enum ProtectorType
{
    /// <summary>Unknown protector type.</summary>
    Unknown,

    /// <summary>TPM protector.</summary>
    TpmProtector,

    /// <summary>External key protector (USB key).</summary>
    ExternalKeyProtector,

    /// <summary>Numerical password (recovery key).</summary>
    NumericalPasswordProtector,

    /// <summary>TPM and PIN.</summary>
    TpmPinProtector,

    /// <summary>TPM and startup key.</summary>
    TpmStartupKeyProtector,

    /// <summary>TPM, PIN, and startup key.</summary>
    TpmPinStartupKeyProtector,

    /// <summary>Public key protector.</summary>
    PublicKeyProtector,

    /// <summary>Passphrase protector.</summary>
    PassphraseProtector,

    /// <summary>TPM certificate protector.</summary>
    TpmCertificateProtector,

    /// <summary>CNG certificate protector.</summary>
    CryptoApiNextGenerationCertificateProtector
}

/// <summary>
/// Health check result for BitLocker encryption.
/// </summary>
public sealed class BitLockerHealthCheck
{
    /// <summary>
    /// Gets or sets the volume path that was checked.
    /// </summary>
    public string? VolumeePath { get; set; }

    /// <summary>
    /// Gets or sets when the check was performed.
    /// </summary>
    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// Gets or sets the overall health status.
    /// </summary>
    public HealthStatus OverallStatus { get; set; } = HealthStatus.Unknown;

    /// <summary>
    /// Gets or sets the BitLocker status.
    /// </summary>
    public BitLockerStatus? BitLockerStatus { get; set; }

    /// <summary>
    /// Gets or sets the list of issues found.
    /// </summary>
    public List<string> Issues { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of recommendations.
    /// </summary>
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// Overall health status classification.
/// </summary>
public enum HealthStatus
{
    /// <summary>Unknown health status.</summary>
    Unknown,

    /// <summary>Healthy - no issues.</summary>
    Healthy,

    /// <summary>Warning - minor issues or recommendations.</summary>
    Warning,

    /// <summary>Critical - major issues requiring attention.</summary>
    Critical,

    /// <summary>Error - health check failed.</summary>
    Error
}

/// <summary>
/// Exception thrown when BitLocker operations fail.
/// </summary>
public sealed class BitLockerException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BitLockerException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public BitLockerException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BitLockerException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public BitLockerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
