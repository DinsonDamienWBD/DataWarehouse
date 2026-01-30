using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.MilitarySecurity;

/// <summary>
/// Bell-LaPadula Mandatory Access Control (MAC) plugin implementation.
/// Enforces the Bell-LaPadula security model:
/// - Simple Security Property (No Read Up): Subject clearance must dominate object classification
/// - *-Property (No Write Down): Object classification must dominate subject current level
/// Provides compartment-based access control and security label validation.
/// </summary>
public class BellLaPadulaMacPlugin : MandatoryAccessControlPluginBase
{
    private readonly Dictionary<string, SubjectClearance> _clearances = new();
    private readonly HashSet<string> _validCompartments = new()
    {
        "SI", "TK", "G", "HCS", "ORCON", "NOFORN", "REL", "FOUO"
    };
    private readonly HashSet<string> _validCaveats = new()
    {
        "NOFORN", "REL TO USA", "REL TO FVEY", "ORCON", "IMCON", "LACONIC",
        "FOUO", "LES", "PROPIN", "RELIDO", "RSEN", "FISA"
    };

    /// <inheritdoc />
    public override string Id => "datawarehouse.milsec.bell-lapadula-mac";

    /// <inheritdoc />
    public override string Name => "Bell-LaPadula MAC";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <summary>
    /// Registers a subject's clearance level and authorized compartments.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <param name="clearanceLevel">Maximum clearance level.</param>
    /// <param name="authorizedCompartments">Compartments the subject is authorized to access.</param>
    public void RegisterClearance(string subjectId, ClassificationLevel clearanceLevel, string[] authorizedCompartments)
    {
        _clearances[subjectId] = new SubjectClearance
        {
            SubjectId = subjectId,
            ClearanceLevel = clearanceLevel,
            AuthorizedCompartments = authorizedCompartments.ToHashSet(),
            GrantedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Revokes a subject's clearance.
    /// </summary>
    /// <param name="subjectId">Subject identifier to revoke.</param>
    /// <returns>True if clearance was revoked.</returns>
    public bool RevokeClearance(string subjectId)
    {
        return _clearances.Remove(subjectId);
    }

    /// <inheritdoc />
    protected override Task<SecurityLabel> ResolveEffectiveLabelAsync(string subjectId, string[] compartments)
    {
        if (!_clearances.TryGetValue(subjectId, out var clearance))
        {
            // No clearance - return Unclassified with no compartments
            return Task.FromResult(new SecurityLabel(
                Level: ClassificationLevel.Unclassified,
                Compartments: Array.Empty<string>(),
                Caveats: Array.Empty<string>(),
                ClassificationAuthority: "System",
                ClassifiedOn: DateTimeOffset.UtcNow,
                DeclassifyOn: null
            ));
        }

        // Filter requested compartments to only those the subject is authorized for
        var effectiveCompartments = compartments
            .Where(c => clearance.AuthorizedCompartments.Contains(c))
            .ToArray();

        return Task.FromResult(new SecurityLabel(
            Level: clearance.ClearanceLevel,
            Compartments: effectiveCompartments,
            Caveats: Array.Empty<string>(),
            ClassificationAuthority: "MAC System",
            ClassifiedOn: clearance.GrantedAt,
            DeclassifyOn: null
        ));
    }

    /// <inheritdoc />
    protected override Task<ValidationResult> PerformLabelValidationAsync(SecurityLabel label)
    {
        var errors = new List<string>();

        // Validate classification level
        if (!Enum.IsDefined(typeof(ClassificationLevel), label.Level))
        {
            errors.Add($"Invalid classification level: {label.Level}");
        }

        // Validate compartments
        foreach (var compartment in label.Compartments)
        {
            if (!_validCompartments.Contains(compartment))
            {
                errors.Add($"Unknown compartment code: {compartment}");
            }
        }

        // Validate caveats
        foreach (var caveat in label.Caveats)
        {
            // Check if caveat is valid or starts with valid prefix (e.g., "REL TO USA, GBR")
            var isValid = _validCaveats.Contains(caveat) ||
                          _validCaveats.Any(v => caveat.StartsWith(v));
            if (!isValid)
            {
                errors.Add($"Unknown caveat: {caveat}");
            }
        }

        // Validate classification authority
        if (string.IsNullOrWhiteSpace(label.ClassificationAuthority))
        {
            errors.Add("Classification authority is required");
        }

        // Validate dates
        if (label.DeclassifyOn.HasValue && label.DeclassifyOn < label.ClassifiedOn)
        {
            errors.Add("Declassification date cannot be before classification date");
        }

        // Compartments require at least Secret clearance
        if (label.Compartments.Length > 0 && label.Level < ClassificationLevel.Secret)
        {
            errors.Add("Compartmented information requires at least Secret classification");
        }

        return Task.FromResult(new ValidationResult(
            IsValid: errors.Count == 0,
            Errors: errors.ToArray()
        ));
    }

    /// <summary>
    /// Adds a custom compartment code to the valid compartments list.
    /// </summary>
    /// <param name="compartmentCode">Compartment code to add.</param>
    public void AddValidCompartment(string compartmentCode)
    {
        _validCompartments.Add(compartmentCode);
    }

    /// <summary>
    /// Adds a custom caveat to the valid caveats list.
    /// </summary>
    /// <param name="caveat">Caveat to add.</param>
    public void AddValidCaveat(string caveat)
    {
        _validCaveats.Add(caveat);
    }

    /// <summary>
    /// Gets all registered clearance subject IDs (for administrative purposes).
    /// </summary>
    public IReadOnlyList<string> GetAllClearanceSubjects()
    {
        return _clearances.Keys.ToList();
    }

    /// <summary>
    /// Gets clearance information for a specific subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>Clearance information if found.</returns>
    public ClearanceInfo? GetClearanceInfo(string subjectId)
    {
        if (!_clearances.TryGetValue(subjectId, out var clearance))
            return null;

        return new ClearanceInfo(
            clearance.SubjectId,
            clearance.ClearanceLevel,
            clearance.AuthorizedCompartments.ToArray(),
            clearance.GrantedAt
        );
    }

    private class SubjectClearance
    {
        public string SubjectId { get; set; } = "";
        public ClassificationLevel ClearanceLevel { get; set; }
        public HashSet<string> AuthorizedCompartments { get; set; } = new();
        public DateTimeOffset GrantedAt { get; set; }
    }
}

/// <summary>
/// Clearance information for a subject.
/// </summary>
/// <param name="SubjectId">Subject identifier.</param>
/// <param name="ClearanceLevel">Clearance level.</param>
/// <param name="AuthorizedCompartments">Authorized compartments.</param>
/// <param name="GrantedAt">When clearance was granted.</param>
public record ClearanceInfo(
    string SubjectId,
    ClassificationLevel ClearanceLevel,
    string[] AuthorizedCompartments,
    DateTimeOffset GrantedAt
);

