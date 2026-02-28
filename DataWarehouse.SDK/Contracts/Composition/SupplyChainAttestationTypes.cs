using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Composition;

/// <summary>
/// Represents a single build artifact with cryptographic digest.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record AttestationSubject
{
    /// <summary>
    /// Name of the artifact (e.g., "DataWarehouse.SDK.dll").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Cryptographic digests of the artifact (algorithm -> hex value).
    /// </summary>
    public required IReadOnlyDictionary<string, string> Digests { get; init; }

    /// <summary>
    /// Size of the artifact in bytes.
    /// </summary>
    public long? SizeBytes { get; init; }
}

/// <summary>
/// Represents a single step in the build process.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record BuildStep
{
    /// <summary>
    /// Build command executed (e.g., "dotnet build -c Release").
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Working directory where the command was executed.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Exit code from the build command.
    /// </summary>
    public required int ExitCode { get; init; }

    /// <summary>
    /// When the build step started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the build step completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Relevant environment variables (filtered, no secrets).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// Information about the builder that produced the artifacts.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record BuilderInfo
{
    /// <summary>
    /// Builder identifier (e.g., "datawarehouse.build/dotnet@v1").
    /// </summary>
    public required string BuilderId { get; init; }

    /// <summary>
    /// Version of the builder.
    /// </summary>
    public required string BuilderVersion { get; init; }

    /// <summary>
    /// Platform where the build occurred (OS/architecture).
    /// </summary>
    public string? BuilderPlatform { get; init; }
}

/// <summary>
/// Source code information for the build.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record SourceInfo
{
    /// <summary>
    /// Source repository URI (e.g., "https://github.com/org/repo").
    /// </summary>
    public required string RepositoryUri { get; init; }

    /// <summary>
    /// Exact commit hash (SHA) that was built.
    /// </summary>
    public required string CommitHash { get; init; }

    /// <summary>
    /// Branch name if available.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Tag name if available.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Timestamp of the commit.
    /// </summary>
    public DateTimeOffset? CommitTimestamp { get; init; }
}

/// <summary>
/// SLSA provenance predicate describing the build process.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record AttestationPredicate
{
    /// <summary>
    /// Information about the builder.
    /// </summary>
    public required BuilderInfo Builder { get; init; }

    /// <summary>
    /// Source code information.
    /// </summary>
    public required SourceInfo Source { get; init; }

    /// <summary>
    /// Build steps executed (ordered).
    /// </summary>
    public required IReadOnlyList<BuildStep> BuildSteps { get; init; }

    /// <summary>
    /// When the build started.
    /// </summary>
    public required DateTimeOffset BuildStartedAt { get; init; }

    /// <summary>
    /// When the build completed.
    /// </summary>
    public required DateTimeOffset BuildCompletedAt { get; init; }

    /// <summary>
    /// Build configuration metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string>? BuildConfig { get; init; }
}

/// <summary>
/// Result of attestation document verification.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record AttestationVerificationResult
{
    /// <summary>
    /// Whether the verification passed.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Name of the matched subject if valid.
    /// </summary>
    public string? MatchedSubjectName { get; init; }

    /// <summary>
    /// Expected hash from the attestation.
    /// </summary>
    public string? ExpectedHash { get; init; }

    /// <summary>
    /// Actual hash computed from the binary.
    /// </summary>
    public string? ActualHash { get; init; }

    /// <summary>
    /// Error message if verification failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the attestation is blockchain-anchored.
    /// </summary>
    public required bool BlockchainAnchored { get; init; }

    /// <summary>
    /// Creates a successful verification result.
    /// </summary>
    public static AttestationVerificationResult CreateValid(string subjectName, string hash, bool anchored)
    {
        return new AttestationVerificationResult
        {
            IsValid = true,
            MatchedSubjectName = subjectName,
            ExpectedHash = hash,
            ActualHash = hash,
            BlockchainAnchored = anchored
        };
    }

    /// <summary>
    /// Creates a failed verification result.
    /// </summary>
    public static AttestationVerificationResult CreateInvalid(string expected, string actual, string error)
    {
        return new AttestationVerificationResult
        {
            IsValid = false,
            ExpectedHash = expected,
            ActualHash = actual,
            ErrorMessage = error,
            BlockchainAnchored = false
        };
    }
}

/// <summary>
/// SLSA Level 2 compatible attestation document for build provenance.
/// Follows the in-toto attestation specification.
/// </summary>
[SdkCompatibility("3.0.0")]
public sealed record AttestationDocument
{
    /// <summary>
    /// Unique identifier for this attestation.
    /// </summary>
    public required string AttestationId { get; init; }

    /// <summary>
    /// in-toto statement type URI.
    /// </summary>
    public string Type { get; init; } = "https://in-toto.io/Statement/v1";

    /// <summary>
    /// SLSA provenance predicate type URI.
    /// </summary>
    public string PredicateType { get; init; } = "https://slsa.dev/provenance/v1";

    /// <summary>
    /// Build output artifacts with cryptographic digests.
    /// </summary>
    public required IReadOnlyList<AttestationSubject> Subjects { get; init; }

    /// <summary>
    /// Build provenance information.
    /// </summary>
    public required AttestationPredicate Predicate { get; init; }

    /// <summary>
    /// When this attestation was issued.
    /// </summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// SHA256 hash of the canonical JSON representation.
    /// </summary>
    public required string DocumentHash { get; init; }

    /// <summary>
    /// Blockchain anchor ID if anchored (null otherwise).
    /// </summary>
    public string? BlockchainAnchorId { get; init; }

    /// <summary>
    /// Verifies that a binary file matches one of the subjects in this attestation.
    /// </summary>
    /// <param name="binaryPath">Path to the binary file to verify.</param>
    /// <returns>Verification result indicating success or failure.</returns>
    public async Task<AttestationVerificationResult> VerifyAsync(string binaryPath)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(binaryPath);

            // Sanitize path: resolve to full path and reject traversal attempts
            string fullPath = Path.GetFullPath(binaryPath);
            if (fullPath.Contains("..", StringComparison.Ordinal))
            {
                return AttestationVerificationResult.CreateInvalid("", "", "Path traversal detected in binary path.");
            }

            if (!File.Exists(fullPath))
            {
                return AttestationVerificationResult.CreateInvalid("", "", $"File not found: {binaryPath}");
            }

            // Compute SHA256 hash of the file
            byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
            byte[] hashBytes = SHA256.HashData(fileBytes);
            string actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            // Search subjects for matching hash
            foreach (var subject in Subjects)
            {
                if (subject.Digests.TryGetValue("sha256", out string? expectedHash))
                {
                    if (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return AttestationVerificationResult.CreateValid(
                            subject.Name,
                            actualHash,
                            BlockchainAnchorId != null
                        );
                    }
                }
            }

            // No match found
            return AttestationVerificationResult.CreateInvalid(
                Subjects.FirstOrDefault()?.Digests.GetValueOrDefault("sha256") ?? "",
                actualHash,
                "Binary hash does not match any subject in attestation"
            );
        }
        catch (Exception ex)
        {
            return AttestationVerificationResult.CreateInvalid("", "", $"Verification error: {ex.Message}");
        }
    }
}
