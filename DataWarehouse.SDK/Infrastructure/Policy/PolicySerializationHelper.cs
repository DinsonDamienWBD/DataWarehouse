using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Provides static helper methods for serializing and deserializing policy types
    /// using <see cref="System.Text.Json"/>. All policy persistence implementations use
    /// this helper to ensure consistent JSON representation across backends.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy serialization (PERS-01)")]
    public static class PolicySerializationHelper
    {
        private static readonly JsonSerializerOptions s_options = CreateOptions();

        /// <summary>
        /// Serializes a <see cref="FeaturePolicy"/> to a UTF-8 JSON byte array.
        /// </summary>
        /// <param name="policy">The feature policy to serialize. Must not be null.</param>
        /// <returns>A byte array containing the JSON representation of the policy.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> is null.</exception>
        public static byte[] SerializePolicy(FeaturePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return JsonSerializer.SerializeToUtf8Bytes(policy, s_options);
        }

        /// <summary>
        /// Deserializes a UTF-8 JSON byte array to a <see cref="FeaturePolicy"/>.
        /// </summary>
        /// <param name="data">The JSON byte array to deserialize. Must not be null or empty.</param>
        /// <returns>The deserialized <see cref="FeaturePolicy"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when deserialization produces a null result.</exception>
        public static FeaturePolicy DeserializePolicy(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return JsonSerializer.Deserialize<FeaturePolicy>(data, s_options)
                ?? throw new InvalidOperationException("Deserialization of FeaturePolicy produced null result.");
        }

        /// <summary>
        /// Serializes an <see cref="OperationalProfile"/> to a UTF-8 JSON byte array.
        /// </summary>
        /// <param name="profile">The operational profile to serialize. Must not be null.</param>
        /// <returns>A byte array containing the JSON representation of the profile.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is null.</exception>
        public static byte[] SerializeProfile(OperationalProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return JsonSerializer.SerializeToUtf8Bytes(profile, s_options);
        }

        /// <summary>
        /// Deserializes a UTF-8 JSON byte array to an <see cref="OperationalProfile"/>.
        /// </summary>
        /// <param name="data">The JSON byte array to deserialize. Must not be null or empty.</param>
        /// <returns>The deserialized <see cref="OperationalProfile"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when deserialization produces a null result.</exception>
        public static OperationalProfile DeserializeProfile(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return JsonSerializer.Deserialize<OperationalProfile>(data, s_options)
                ?? throw new InvalidOperationException("Deserialization of OperationalProfile produced null result.");
        }

        /// <summary>
        /// Serializes a collection of policies to a UTF-8 JSON byte array for bulk export.
        /// </summary>
        /// <param name="policies">The policies to serialize as (FeatureId, Level, Path, Policy) tuples.</param>
        /// <returns>A byte array containing the JSON representation of all policies.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policies"/> is null.</exception>
        public static byte[] SerializePolicies(IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)> policies)
        {
            if (policies == null) throw new ArgumentNullException(nameof(policies));

            var entries = new List<PolicyEntry>(policies.Count);
            for (int i = 0; i < policies.Count; i++)
            {
                var (featureId, level, path, policy) = policies[i];
                entries.Add(new PolicyEntry
                {
                    FeatureId = featureId,
                    Level = level,
                    Path = path,
                    Policy = policy
                });
            }

            return JsonSerializer.SerializeToUtf8Bytes(entries, s_options);
        }

        /// <summary>
        /// Deserializes a UTF-8 JSON byte array to a collection of policies for bulk import.
        /// </summary>
        /// <param name="data">The JSON byte array to deserialize. Must not be null or empty.</param>
        /// <returns>A read-only list of deserialized policies as (FeatureId, Level, Path, Policy) tuples.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when deserialization produces a null result.</exception>
        public static IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)> DeserializePolicies(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var entries = JsonSerializer.Deserialize<List<PolicyEntry>>(data, s_options)
                ?? throw new InvalidOperationException("Deserialization of policy entries produced null result.");

            var result = new List<(string, PolicyLevel, string, FeaturePolicy)>(entries.Count);
            foreach (var entry in entries)
            {
                result.Add((entry.FeatureId, entry.Level, entry.Path, entry.Policy));
            }

            return result;
        }

        /// <summary>
        /// Creates the shared <see cref="JsonSerializerOptions"/> configured for policy serialization.
        /// Uses camelCase naming, compact output, and string-based enum serialization.
        /// </summary>
        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            return options;
        }

        /// <summary>
        /// Internal DTO record for clean JSON serialization of policy tuples.
        /// Maps the value-tuple structure to named properties for readable JSON output.
        /// </summary>
        private sealed record PolicyEntry
        {
            /// <summary>Unique identifier of the feature.</summary>
            public string FeatureId { get; init; } = string.Empty;

            /// <summary>The hierarchy level at which this policy applies.</summary>
            public PolicyLevel Level { get; init; }

            /// <summary>The VDE path at the specified level.</summary>
            public string Path { get; init; } = string.Empty;

            /// <summary>The feature policy.</summary>
            public FeaturePolicy Policy { get; init; } = null!;
        }
    }
}
