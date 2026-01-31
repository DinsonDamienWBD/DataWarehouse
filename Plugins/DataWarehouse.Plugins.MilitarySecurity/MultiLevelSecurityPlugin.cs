using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.MilitarySecurity;

/// <summary>
/// Multi-Level Security (MLS) plugin for handling multiple classification levels.
/// Provides trusted downgrade and sanitization capabilities for systems
/// accredited to process multiple security levels.
/// </summary>
public partial class MultiLevelSecurityPlugin : MultiLevelSecurityPluginBase
{
    private readonly Dictionary<string, string> _authorizationCodes = new();
    private readonly List<DowngradeAuditEntry> _auditLog = new();
    private ClassificationLevel _systemHigh = ClassificationLevel.TopSecret;
    private readonly List<ClassificationLevel> _supportedLevels;

    /// <inheritdoc />
    public override string Id => "datawarehouse.milsec.mls";

    /// <inheritdoc />
    public override string Name => "Multi-Level Security";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override IReadOnlyList<ClassificationLevel> SupportedLevels => _supportedLevels;

    /// <inheritdoc />
    public override ClassificationLevel SystemHighLevel => _systemHigh;

    /// <summary>
    /// Creates a new instance of the MLS plugin with default supported levels.
    /// </summary>
    public MultiLevelSecurityPlugin()
    {
        _supportedLevels = new List<ClassificationLevel>
        {
            ClassificationLevel.Unclassified,
            ClassificationLevel.Cui,
            ClassificationLevel.Confidential,
            ClassificationLevel.Secret,
            ClassificationLevel.TopSecret
        };
    }

    /// <summary>
    /// Sets the system high classification level.
    /// </summary>
    /// <param name="level">Maximum classification level the system can process.</param>
    public void SetSystemHigh(ClassificationLevel level)
    {
        if (!_supportedLevels.Contains(level))
        {
            throw new ArgumentException($"Level {level} is not supported by this system");
        }
        _systemHigh = level;
    }

    /// <summary>
    /// Registers an authorization code for downgrade operations.
    /// </summary>
    /// <param name="code">Authorization code.</param>
    /// <param name="authorityId">ID of the classification authority.</param>
    public void RegisterAuthorizationCode(string code, string authorityId)
    {
        _authorizationCodes[code] = authorityId;
    }

    /// <inheritdoc />
    protected override async Task<byte[]> PerformDowngradeAsync(
        byte[] data,
        SecurityLabel current,
        ClassificationLevel target,
        string authCode)
    {
        // Validate authorization code
        if (!_authorizationCodes.TryGetValue(authCode, out var authorityId))
        {
            throw new UnauthorizedAccessException("Invalid authorization code for downgrade operation");
        }

        // Create new classification marking header
        var header = CreateClassificationHeader(target, authorityId);

        // Prepend header to data
        var headerBytes = Encoding.UTF8.GetBytes(header);
        var result = new byte[headerBytes.Length + data.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(data, 0, result, headerBytes.Length, data.Length);

        // Record audit entry
        var auditEntry = new DowngradeAuditEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OriginalLevel = current.Level,
            TargetLevel = target,
            AuthorityId = authorityId,
            Timestamp = DateTimeOffset.UtcNow,
            DataHash = SHA256.HashData(data)
        };
        _auditLog.Add(auditEntry);

        return await Task.FromResult(result);
    }

    /// <inheritdoc />
    protected override async Task<byte[]> PerformSanitizationAsync(
        byte[] data,
        SecurityLabel source,
        ClassificationLevel target)
    {
        // Convert data to text for analysis (assumes text data)
        var text = Encoding.UTF8.GetString(data);
        var sanitized = text;

        // Redact content based on classification level patterns
        if (source.Level > target)
        {
            // Remove classification markings above target level
            sanitized = RemoveHigherClassificationMarkings(sanitized, target);

            // Redact sensitive keywords
            sanitized = RedactSensitiveKeywords(sanitized, target);

            // Remove compartment references if target is below Secret
            if (target < ClassificationLevel.Secret)
            {
                sanitized = RemoveCompartmentReferences(sanitized);
            }
        }

        // Add sanitization notice
        var notice = $"\n[SANITIZED: Downgraded from {source.Level} to {target} on {DateTimeOffset.UtcNow:O}]\n";
        sanitized = notice + sanitized;

        return await Task.FromResult(Encoding.UTF8.GetBytes(sanitized));
    }

    private string CreateClassificationHeader(ClassificationLevel level, string authority)
    {
        var marking = level switch
        {
            ClassificationLevel.Unclassified => "UNCLASSIFIED",
            ClassificationLevel.Cui => "CUI",
            ClassificationLevel.Confidential => "CONFIDENTIAL",
            ClassificationLevel.Secret => "SECRET",
            ClassificationLevel.TopSecret => "TOP SECRET",
            ClassificationLevel.TsSci => "TOP SECRET//SCI",
            _ => "UNKNOWN"
        };

        return $"//CLASSIFICATION: {marking}\n" +
               $"//DOWNGRADE AUTHORITY: {authority}\n" +
               $"//DOWNGRADE DATE: {DateTimeOffset.UtcNow:O}\n" +
               "//END HEADER\n";
    }

    private string RemoveHigherClassificationMarkings(string text, ClassificationLevel target)
    {
        var result = text;

        // Patterns to remove based on target level
        if (target <= ClassificationLevel.Secret)
        {
            // Remove TOP SECRET markings
            result = TopSecretPattern().Replace(result, "[REDACTED]");
        }

        if (target <= ClassificationLevel.Confidential)
        {
            // Remove SECRET markings
            result = SecretPattern().Replace(result, "[REDACTED]");
        }

        if (target <= ClassificationLevel.Cui)
        {
            // Remove CONFIDENTIAL markings
            result = ConfidentialPattern().Replace(result, "[REDACTED]");
        }

        return result;
    }

    private string RedactSensitiveKeywords(string text, ClassificationLevel target)
    {
        var result = text;

        // Keyword lists by classification level
        var topSecretKeywords = new[] { "SIGINT", "HUMINT", "MASINT", "IMINT", "TECHINT" };
        var secretKeywords = new[] { "OPERATIONAL PLAN", "CONTINGENCY", "OPLAN" };

        if (target < ClassificationLevel.TopSecret)
        {
            foreach (var keyword in topSecretKeywords)
            {
                result = Regex.Replace(result, $@"\b{keyword}\b", "[REDACTED]", RegexOptions.IgnoreCase);
            }
        }

        if (target < ClassificationLevel.Secret)
        {
            foreach (var keyword in secretKeywords)
            {
                result = Regex.Replace(result, $@"\b{keyword}\b", "[REDACTED]", RegexOptions.IgnoreCase);
            }
        }

        return result;
    }

    private string RemoveCompartmentReferences(string text)
    {
        // Remove SCI compartment markings
        var compartmentPatterns = new[]
        {
            @"//SI[/-]?[A-Z]*",
            @"//TK[/-]?[A-Z]*",
            @"//G[/-]?[A-Z]*",
            @"//HCS[/-]?[A-Z]*",
            @"\bSCI\b",
            @"\bCOMINT\b",
            @"\bGAMMA\b"
        };

        var result = text;
        foreach (var pattern in compartmentPatterns)
        {
            result = Regex.Replace(result, pattern, "[COMPARTMENT REDACTED]", RegexOptions.IgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Gets the audit log of all downgrade operations.
    /// </summary>
    public IReadOnlyList<DowngradeAuditEntry> GetAuditLog() => _auditLog;

    [GeneratedRegex(@"\bTOP SECRET\b|\bTS\b(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex TopSecretPattern();

    [GeneratedRegex(@"\bSECRET\b(?![/])", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\bCONFIDENTIAL\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidentialPattern();
}

/// <summary>
/// Audit entry for downgrade operations.
/// </summary>
public class DowngradeAuditEntry
{
    /// <summary>
    /// Unique entry identifier.
    /// </summary>
    public string EntryId { get; set; } = "";

    /// <summary>
    /// Original classification level.
    /// </summary>
    public ClassificationLevel OriginalLevel { get; set; }

    /// <summary>
    /// Target classification level after downgrade.
    /// </summary>
    public ClassificationLevel TargetLevel { get; set; }

    /// <summary>
    /// ID of the authority who authorized the downgrade.
    /// </summary>
    public string AuthorityId { get; set; } = "";

    /// <summary>
    /// Timestamp of the downgrade operation.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// SHA-256 hash of the original data.
    /// </summary>
    public byte[] DataHash { get; set; } = Array.Empty<byte>();
}
