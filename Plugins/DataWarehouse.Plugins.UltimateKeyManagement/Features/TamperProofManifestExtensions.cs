// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features;

/// <summary>
/// Extension methods for TamperProofManifest to handle encryption metadata.
/// Provides helper methods for encryption config storage/retrieval.
/// </summary>
/// <remarks>
/// These extensions enable the TamperProof system to work seamlessly with
/// UltimateKeyManagement for encryption configuration persistence.
/// </remarks>
public static class TamperProofManifestExtensions
{
    /// <summary>
    /// The key used to store encryption metadata in UserMetadata dictionary.
    /// </summary>
    public const string EncryptionMetadataKey = "encryption.metadata";

    /// <summary>
    /// The key used to store encryption config mode in UserMetadata dictionary.
    /// </summary>
    public const string EncryptionConfigModeKey = "encryption.configMode";

    /// <summary>
    /// The key used to store the manifest format version for encryption metadata.
    /// </summary>
    public const string EncryptionMetadataVersionKey = "encryption.metadataVersion";

    /// <summary>
    /// Current version of the encryption metadata format.
    /// </summary>
    public const int CurrentEncryptionMetadataVersion = 1;

    /// <summary>
    /// JSON serialization options for encryption metadata.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    #region Encryption Metadata Extraction

    /// <summary>
    /// Extracts encryption metadata from the manifest's UserMetadata.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to extract from.</param>
    /// <returns>The encryption metadata, or null if not present.</returns>
    public static EncryptionMetadata? GetEncryptionMetadata(this TamperProofManifest manifest)
    {
        if (manifest?.UserMetadata == null)
        {
            return null;
        }

        if (!manifest.UserMetadata.TryGetValue(EncryptionMetadataKey, out var metadataObj))
        {
            return null;
        }

        return DeserializeEncryptionMetadata(metadataObj);
    }

    /// <summary>
    /// Extracts encryption metadata from the manifest, throwing if not present.
    /// Use this when encryption metadata is required for decryption.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to extract from.</param>
    /// <returns>The encryption metadata.</returns>
    /// <exception cref="InvalidOperationException">If encryption metadata is not present.</exception>
    public static EncryptionMetadata GetRequiredEncryptionMetadata(this TamperProofManifest manifest)
    {
        var metadata = manifest.GetEncryptionMetadata();
        if (metadata == null)
        {
            throw new InvalidOperationException(
                $"Manifest for object {manifest.ObjectId} does not contain encryption metadata. " +
                "The object may not be encrypted or was written without encryption metadata storage.");
        }

        return metadata;
    }

    /// <summary>
    /// Checks if the manifest contains encryption metadata.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to check.</param>
    /// <returns>True if encryption metadata is present.</returns>
    public static bool HasEncryptionMetadata(this TamperProofManifest manifest)
    {
        return manifest?.UserMetadata != null &&
               manifest.UserMetadata.ContainsKey(EncryptionMetadataKey);
    }

    /// <summary>
    /// Gets the encryption config mode from the manifest.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to extract from.</param>
    /// <returns>The config mode, or null if not stored.</returns>
    public static EncryptionConfigMode? GetEncryptionConfigMode(this TamperProofManifest manifest)
    {
        if (manifest?.UserMetadata == null)
        {
            return null;
        }

        if (!manifest.UserMetadata.TryGetValue(EncryptionConfigModeKey, out var modeObj))
        {
            return null;
        }

        if (modeObj is EncryptionConfigMode mode)
        {
            return mode;
        }

        if (modeObj is string modeStr && Enum.TryParse<EncryptionConfigMode>(modeStr, out var parsedMode))
        {
            return parsedMode;
        }

        if (modeObj is int modeInt)
        {
            return (EncryptionConfigMode)modeInt;
        }

        return null;
    }

    /// <summary>
    /// Gets the encryption metadata version from the manifest.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to extract from.</param>
    /// <returns>The metadata version, or 0 if not present.</returns>
    public static int GetEncryptionMetadataVersion(this TamperProofManifest manifest)
    {
        if (manifest?.UserMetadata == null)
        {
            return 0;
        }

        if (!manifest.UserMetadata.TryGetValue(EncryptionMetadataVersionKey, out var versionObj))
        {
            return 0;
        }

        return versionObj switch
        {
            int v => v,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 0
        };
    }

    #endregion

    #region Encryption Metadata Storage

    /// <summary>
    /// Creates a UserMetadata dictionary with encryption metadata.
    /// Used when building a new manifest.
    /// </summary>
    /// <param name="encryptionMetadata">The encryption metadata to store.</param>
    /// <param name="configMode">The encryption config mode.</param>
    /// <param name="existingMetadata">Optional existing user metadata to merge with.</param>
    /// <returns>A UserMetadata dictionary ready for manifest creation.</returns>
    public static Dictionary<string, object> CreateUserMetadataWithEncryption(
        EncryptionMetadata encryptionMetadata,
        EncryptionConfigMode configMode,
        Dictionary<string, object>? existingMetadata = null)
    {
        var userMetadata = existingMetadata != null
            ? new Dictionary<string, object>(existingMetadata)
            : new Dictionary<string, object>();

        // Store encryption metadata as serialized JSON
        userMetadata[EncryptionMetadataKey] = SerializeEncryptionMetadata(encryptionMetadata);
        userMetadata[EncryptionConfigModeKey] = configMode.ToString();
        userMetadata[EncryptionMetadataVersionKey] = CurrentEncryptionMetadataVersion;

        return userMetadata;
    }

    /// <summary>
    /// Merges encryption metadata into an existing UserMetadata dictionary.
    /// </summary>
    /// <param name="userMetadata">The existing user metadata dictionary (modified in place).</param>
    /// <param name="encryptionMetadata">The encryption metadata to add.</param>
    /// <param name="configMode">The encryption config mode.</param>
    public static void AddEncryptionMetadata(
        Dictionary<string, object> userMetadata,
        EncryptionMetadata encryptionMetadata,
        EncryptionConfigMode configMode)
    {
        userMetadata[EncryptionMetadataKey] = SerializeEncryptionMetadata(encryptionMetadata);
        userMetadata[EncryptionConfigModeKey] = configMode.ToString();
        userMetadata[EncryptionMetadataVersionKey] = CurrentEncryptionMetadataVersion;
    }

    #endregion

    #region Version Compatibility

    /// <summary>
    /// Checks if the manifest's encryption metadata version is compatible with current code.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to check.</param>
    /// <returns>True if compatible, false if requires migration.</returns>
    public static bool IsEncryptionMetadataVersionCompatible(this TamperProofManifest manifest)
    {
        var version = manifest.GetEncryptionMetadataVersion();

        // Version 0 means no version info - assume compatible
        if (version == 0)
        {
            return true;
        }

        // Current version is always compatible
        if (version == CurrentEncryptionMetadataVersion)
        {
            return true;
        }

        // Future versions are not compatible (downgrade scenario)
        if (version > CurrentEncryptionMetadataVersion)
        {
            return false;
        }

        // Older versions may need migration but are readable
        return true;
    }

    /// <summary>
    /// Migrates encryption metadata from an older format if necessary.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to migrate.</param>
    /// <returns>Migrated encryption metadata, or null if no migration needed.</returns>
    public static EncryptionMetadata? MigrateEncryptionMetadataIfNeeded(this TamperProofManifest manifest)
    {
        var version = manifest.GetEncryptionMetadataVersion();
        var metadata = manifest.GetEncryptionMetadata();

        if (metadata == null)
        {
            return null;
        }

        // No migration needed for current version
        if (version >= CurrentEncryptionMetadataVersion)
        {
            return null;
        }

        // Perform version-specific migrations
        // Currently no migrations needed (v1 is first version)

        return null;
    }

    #endregion

    #region Pipeline Stage Integration

    /// <summary>
    /// Finds the encryption stage in the manifest's pipeline stages.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to search.</param>
    /// <returns>The encryption pipeline stage record, or null if not found.</returns>
    public static PipelineStageRecord? GetEncryptionStage(this TamperProofManifest manifest)
    {
        return manifest?.PipelineStages?.FirstOrDefault(
            s => s.StageType.Equals("encryption", StringComparison.OrdinalIgnoreCase) ||
                 s.StageType.StartsWith("encryption.", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the manifest indicates the content is encrypted.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to check.</param>
    /// <returns>True if encryption stage is present or encryption metadata exists.</returns>
    public static bool IsContentEncrypted(this TamperProofManifest manifest)
    {
        return manifest.HasEncryptionMetadata() || manifest.GetEncryptionStage() != null;
    }

    /// <summary>
    /// Extracts encryption parameters from the encryption pipeline stage.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to extract from.</param>
    /// <returns>Dictionary of encryption parameters, or empty if not present.</returns>
    public static IReadOnlyDictionary<string, object> GetEncryptionStageParameters(
        this TamperProofManifest manifest)
    {
        var stage = manifest.GetEncryptionStage();
        if (stage?.Parameters == null)
        {
            return new Dictionary<string, object>();
        }

        return stage.Parameters;
    }

    #endregion

    #region Validation

    /// <summary>
    /// Validates that the manifest's encryption metadata is complete and consistent.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to validate.</param>
    /// <returns>List of validation errors, empty if valid.</returns>
    public static IReadOnlyList<string> ValidateEncryptionMetadata(this TamperProofManifest manifest)
    {
        var errors = new List<string>();

        if (!manifest.HasEncryptionMetadata())
        {
            // No encryption metadata - nothing to validate
            return errors;
        }

        var metadata = manifest.GetEncryptionMetadata();
        if (metadata == null)
        {
            errors.Add("Encryption metadata key is present but value could not be deserialized.");
            return errors;
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(metadata.EncryptionPluginId))
        {
            errors.Add("EncryptionMetadata.EncryptionPluginId is required but missing.");
        }

        // Validate mode-specific fields
        switch (metadata.KeyMode)
        {
            case KeyManagementMode.Direct:
                if (string.IsNullOrWhiteSpace(metadata.KeyId))
                {
                    errors.Add("EncryptionMetadata.KeyId is required for Direct mode.");
                }
                break;

            case KeyManagementMode.Envelope:
                if (metadata.WrappedDek == null || metadata.WrappedDek.Length == 0)
                {
                    errors.Add("EncryptionMetadata.WrappedDek is required for Envelope mode.");
                }
                if (string.IsNullOrWhiteSpace(metadata.KekId))
                {
                    errors.Add("EncryptionMetadata.KekId is required for Envelope mode.");
                }
                break;
        }

        // Validate version compatibility
        if (!manifest.IsEncryptionMetadataVersionCompatible())
        {
            var version = manifest.GetEncryptionMetadataVersion();
            errors.Add($"Encryption metadata version {version} is not compatible with current version {CurrentEncryptionMetadataVersion}.");
        }

        return errors;
    }

    #endregion

    #region Serialization Helpers

    /// <summary>
    /// Serializes encryption metadata to a JSON-compatible dictionary.
    /// </summary>
    private static Dictionary<string, object?> SerializeEncryptionMetadata(EncryptionMetadata metadata)
    {
        // Create a dictionary representation for JSON storage
        var dict = new Dictionary<string, object?>
        {
            ["encryptionPluginId"] = metadata.EncryptionPluginId,
            ["keyMode"] = metadata.KeyMode.ToString(),
            ["keyId"] = metadata.KeyId,
            ["wrappedDek"] = metadata.WrappedDek != null ? Convert.ToBase64String(metadata.WrappedDek) : null,
            ["kekId"] = metadata.KekId,
            ["keyStorePluginId"] = metadata.KeyStorePluginId,
            ["algorithmParams"] = metadata.AlgorithmParams,
            ["encryptedAt"] = metadata.EncryptedAt.ToString("O"),
            ["encryptedBy"] = metadata.EncryptedBy,
            ["version"] = metadata.Version
        };

        return dict;
    }

    /// <summary>
    /// Deserializes encryption metadata from a stored object.
    /// </summary>
    private static EncryptionMetadata? DeserializeEncryptionMetadata(object stored)
    {
        try
        {
            // Handle JsonElement (from System.Text.Json deserialization)
            if (stored is JsonElement jsonElement)
            {
                return DeserializeFromJsonElement(jsonElement);
            }

            // Handle dictionary representation
            if (stored is IDictionary<string, object> dict)
            {
                return DeserializeFromDictionary(dict);
            }

            // Handle string (JSON)
            if (stored is string json)
            {
                return JsonSerializer.Deserialize<EncryptionMetadata>(json, SerializerOptions);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deserializes encryption metadata from a JsonElement.
    /// </summary>
    private static EncryptionMetadata? DeserializeFromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var algorithmParams = new Dictionary<string, object>();
        if (element.TryGetProperty("algorithmParams", out var paramsElement) &&
            paramsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in paramsElement.EnumerateObject())
            {
                algorithmParams[prop.Name] = ConvertJsonElement(prop.Value) ?? null!; // Dictionary<string, object> can store null
            }
        }

        return new EncryptionMetadata
        {
            EncryptionPluginId = element.TryGetProperty("encryptionPluginId", out var pluginId)
                ? pluginId.GetString() ?? ""
                : "",
            KeyMode = element.TryGetProperty("keyMode", out var keyMode) &&
                      Enum.TryParse<KeyManagementMode>(keyMode.GetString(), out var parsedMode)
                ? parsedMode
                : KeyManagementMode.Direct,
            KeyId = element.TryGetProperty("keyId", out var keyId)
                ? keyId.GetString()
                : null,
            WrappedDek = element.TryGetProperty("wrappedDek", out var wrappedDek) &&
                         wrappedDek.ValueKind == JsonValueKind.String
                ? Convert.FromBase64String(wrappedDek.GetString()!)
                : null,
            KekId = element.TryGetProperty("kekId", out var kekId)
                ? kekId.GetString()
                : null,
            KeyStorePluginId = element.TryGetProperty("keyStorePluginId", out var keyStoreId)
                ? keyStoreId.GetString()
                : null,
            AlgorithmParams = algorithmParams,
            EncryptedAt = element.TryGetProperty("encryptedAt", out var encryptedAt) &&
                          DateTime.TryParse(encryptedAt.GetString(), out var parsedTime)
                ? parsedTime
                : DateTime.UtcNow,
            EncryptedBy = element.TryGetProperty("encryptedBy", out var encryptedBy)
                ? encryptedBy.GetString()
                : null,
            Version = element.TryGetProperty("version", out var version)
                ? version.GetInt32()
                : 1
        };
    }

    /// <summary>
    /// Deserializes encryption metadata from a dictionary.
    /// </summary>
    private static EncryptionMetadata? DeserializeFromDictionary(IDictionary<string, object> dict)
    {
        var algorithmParams = new Dictionary<string, object>();
        if (dict.TryGetValue("algorithmParams", out var paramsObj) &&
            paramsObj is IDictionary<string, object> paramsDict)
        {
            foreach (var kvp in paramsDict)
            {
                algorithmParams[kvp.Key] = kvp.Value;
            }
        }

        return new EncryptionMetadata
        {
            EncryptionPluginId = dict.TryGetValue("encryptionPluginId", out var pluginId)
                ? pluginId?.ToString() ?? ""
                : "",
            KeyMode = dict.TryGetValue("keyMode", out var keyMode) &&
                      Enum.TryParse<KeyManagementMode>(keyMode?.ToString(), out var parsedMode)
                ? parsedMode
                : KeyManagementMode.Direct,
            KeyId = dict.TryGetValue("keyId", out var keyId)
                ? keyId?.ToString()
                : null,
            WrappedDek = dict.TryGetValue("wrappedDek", out var wrappedDek) && wrappedDek is string wrappedStr
                ? Convert.FromBase64String(wrappedStr)
                : null,
            KekId = dict.TryGetValue("kekId", out var kekId)
                ? kekId?.ToString()
                : null,
            KeyStorePluginId = dict.TryGetValue("keyStorePluginId", out var keyStoreId)
                ? keyStoreId?.ToString()
                : null,
            AlgorithmParams = algorithmParams,
            EncryptedAt = dict.TryGetValue("encryptedAt", out var encryptedAt) &&
                          DateTime.TryParse(encryptedAt?.ToString(), out var parsedTime)
                ? parsedTime
                : DateTime.UtcNow,
            EncryptedBy = dict.TryGetValue("encryptedBy", out var encryptedBy)
                ? encryptedBy?.ToString()
                : null,
            Version = dict.TryGetValue("version", out var version) && version is int v
                ? v
                : 1
        };
    }

    /// <summary>
    /// Converts a JsonElement to a primitive CLR type.
    /// </summary>
    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString()
        };
    }

    #endregion
}
