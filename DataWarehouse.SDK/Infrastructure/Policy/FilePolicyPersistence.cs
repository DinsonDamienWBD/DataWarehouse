using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// File-based implementation of <see cref="PolicyPersistenceBase"/> that stores policies as
    /// individual JSON sidecar files alongside VDE data. Each policy is written to a separate
    /// <c>{hash}.json</c> file within a <c>policies/</c> subdirectory, enabling per-policy
    /// atomic updates without full-file rewrites.
    /// <para>
    /// File naming uses a truncated SHA-256 hash of the composite key for filesystem-safe,
    /// deterministic filenames. The full composite key is stored inside the JSON payload for
    /// reverse lookup and debugging. All writes use an atomic temp-file-then-rename pattern
    /// to prevent partial writes from corrupting stored data.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: File policy persistence (PERS-03)")]
    public sealed class FilePolicyPersistence : PolicyPersistenceBase
    {
        private readonly string _baseDirectory;
        private readonly string _policiesDirectory;
        private readonly string _profilePath;

        private static readonly JsonSerializerOptions s_fileOptions = CreateFileOptions();

        /// <summary>
        /// Initializes a new instance of <see cref="FilePolicyPersistence"/> with the specified base directory.
        /// </summary>
        /// <param name="baseDirectory">
        /// The root directory where policy sidecar files are stored. Typically the same directory
        /// that contains the <c>.dwvd</c> data files. Must not be null or empty.
        /// </param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="baseDirectory"/> is null or empty.</exception>
        public FilePolicyPersistence(string baseDirectory)
        {
            if (string.IsNullOrEmpty(baseDirectory))
                throw new ArgumentException("Base directory must not be null or empty.", nameof(baseDirectory));

            _baseDirectory = baseDirectory;
            _policiesDirectory = Path.Combine(baseDirectory, "policies");
            _profilePath = Path.Combine(baseDirectory, "profile.json");
        }

        /// <inheritdoc />
        protected override async Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllCoreAsync(CancellationToken ct)
        {
            var result = new List<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>();

            if (!Directory.Exists(_policiesDirectory))
                return result;

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(_policiesDirectory, "*.json"))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var bytes = await ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
                        var entry = JsonSerializer.Deserialize<PolicyFileEntry>(bytes, s_fileOptions);
                        if (entry == null) continue;

                        var policy = PolicySerializationHelper.DeserializePolicy(entry.PolicyData);
                        result.Add((entry.FeatureId, entry.Level, entry.Path, policy));
                    }
                    catch (JsonException)
                    {
                        // Skip malformed files rather than failing the entire load
                    }
                    catch (IOException)
                    {
                        // Skip files that cannot be read (locked, permissions)
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Directory was removed between exists check and enumeration
                return result;
            }

            return result;
        }

        /// <inheritdoc />
        protected override async Task SaveCoreAsync(string key, string featureId, PolicyLevel level, string path, byte[] serializedPolicy, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(_policiesDirectory);

                var hash = ComputeKeyHash(key);
                var filePath = Path.Combine(_policiesDirectory, $"{hash}.json");

                var entry = new PolicyFileEntry
                {
                    Key = key,
                    FeatureId = featureId,
                    Level = level,
                    Path = path,
                    PolicyData = serializedPolicy
                };

                var json = JsonSerializer.SerializeToUtf8Bytes(entry, s_fileOptions);
                await AtomicWriteAsync(filePath, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Failed to save policy '{key}' to '{_policiesDirectory}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        protected override async Task DeleteCoreAsync(string key, CancellationToken ct)
        {
            try
            {
                var hash = ComputeKeyHash(key);
                var filePath = Path.Combine(_policiesDirectory, $"{hash}.json");

                // Use Task.Run to avoid blocking the thread pool thread for file I/O
                await Task.Run(() =>
                {
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Failed to delete policy '{key}' from '{_policiesDirectory}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        protected override async Task SaveProfileCoreAsync(byte[] serializedProfile, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(_baseDirectory);
                await AtomicWriteAsync(_profilePath, serializedProfile, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Failed to save profile to '{_profilePath}': {ex.Message}", ex);
            }
        }

        /// <inheritdoc />
        protected override async Task<byte[]?> LoadProfileCoreAsync(CancellationToken ct)
        {
            if (!File.Exists(_profilePath))
                return null;

            try
            {
                return await ReadAllBytesAsync(_profilePath, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Failed to load profile from '{_profilePath}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Computes a truncated SHA-256 hash of the composite key for use as a filesystem-safe filename.
        /// Returns the first 16 hex characters (64 bits) which provides sufficient uniqueness
        /// for per-VDE policy collections.
        /// </summary>
        /// <param name="key">The composite key to hash.</param>
        /// <returns>A 16-character hex string suitable for use as a filename.</returns>
        private static string ComputeKeyHash(string key)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hashBytes)[..16];
        }

        /// <summary>
        /// Writes data to a file atomically using a temporary file and rename pattern.
        /// Ensures that readers never see a partially written file.
        /// </summary>
        /// <param name="targetPath">The final destination file path.</param>
        /// <param name="data">The data to write.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        private static async Task AtomicWriteAsync(string targetPath, byte[] data, CancellationToken ct)
        {
            var tempPath = targetPath + ".tmp";

            await File.WriteAllBytesAsync(tempPath, data, ct).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }

        /// <summary>
        /// Reads all bytes from a file asynchronously with cancellation support.
        /// </summary>
        /// <param name="filePath">The path to the file to read.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The file contents as a byte array.</returns>
        private static async Task<byte[]> ReadAllBytesAsync(string filePath, CancellationToken ct)
        {
            return await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates the <see cref="JsonSerializerOptions"/> used for policy file serialization.
        /// Uses camelCase naming and string-based enum serialization consistent with
        /// <see cref="PolicySerializationHelper"/>.
        /// </summary>
        private static JsonSerializerOptions CreateFileOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            return options;
        }

        /// <summary>
        /// Internal DTO for clean JSON serialization of individual policy files.
        /// Stores the composite key alongside the policy identity components and serialized data
        /// so that files can be correlated back to their logical keys.
        /// </summary>
        private sealed record PolicyFileEntry
        {
            /// <summary>The composite key generated by <see cref="PolicyPersistenceBase.GenerateKey"/>.</summary>
            public string Key { get; init; } = string.Empty;

            /// <summary>Unique identifier of the feature.</summary>
            public string FeatureId { get; init; } = string.Empty;

            /// <summary>The hierarchy level at which this policy applies.</summary>
            public PolicyLevel Level { get; init; }

            /// <summary>The VDE path at the specified level.</summary>
            public string Path { get; init; } = string.Empty;

            /// <summary>The serialized policy bytes (stored as base64 in JSON).</summary>
            public byte[] PolicyData { get; init; } = Array.Empty<byte>();
        }
    }
}
