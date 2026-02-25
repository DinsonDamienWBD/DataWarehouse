using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Flags representing permissions on a tag. Combinable via bitwise OR
/// to express compound permission sets.
/// </summary>
[Flags]
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag permission flags")]
public enum TagPermission
{
    /// <summary>No permissions granted.</summary>
    None = 0,

    /// <summary>Permission to read the tag value and metadata.</summary>
    Read = 1,

    /// <summary>Permission to modify the tag value.</summary>
    Write = 2,

    /// <summary>Permission to delete the tag.</summary>
    Delete = 4,

    /// <summary>Administrative permission: can modify ACL entries and manage the tag lifecycle.</summary>
    Admin = 8,

    /// <summary>All permissions combined.</summary>
    All = Read | Write | Delete | Admin
}

/// <summary>
/// An access-control entry granting a specific principal a set of tag permissions.
/// </summary>
/// <param name="PrincipalId">
/// The identifier of the principal (user ID, plugin ID, role name, etc.).
/// Matching is case-insensitive.
/// </param>
/// <param name="Permissions">The permissions granted to this principal.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag ACL entry")]
public sealed record TagAclEntry(string PrincipalId, TagPermission Permissions)
{
    /// <inheritdoc />
    public override string ToString() => $"{PrincipalId}: {Permissions}";
}

/// <summary>
/// Per-tag access control list. Contains explicit entries for known principals
/// and a default fallback permission for everyone else.
/// </summary>
/// <remarks>
/// Use <see cref="GetEffectivePermission"/> to resolve the actual permissions for a principal.
/// If an explicit entry exists for the principal, its permissions are returned;
/// otherwise <see cref="DefaultPermission"/> is used.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag ACL")]
public sealed record TagAcl
{
    /// <summary>
    /// Gets the explicit ACL entries. Each entry grants specific permissions to a named principal.
    /// </summary>
    public IReadOnlyList<TagAclEntry> Entries { get; init; } = [];

    /// <summary>
    /// Gets the fallback permission applied to principals without an explicit entry.
    /// Defaults to <see cref="TagPermission.Read"/>.
    /// </summary>
    public TagPermission DefaultPermission { get; init; } = TagPermission.Read;

    /// <summary>
    /// Returns the effective permission for the given principal.
    /// Checks explicit entries first (case-insensitive match), then falls back to
    /// <see cref="DefaultPermission"/>.
    /// </summary>
    /// <param name="principalId">The principal to resolve permissions for.</param>
    /// <returns>The effective <see cref="TagPermission"/> for the principal.</returns>
    public TagPermission GetEffectivePermission(string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var entry = Entries.FirstOrDefault(e =>
            string.Equals(e.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase));
        return entry?.Permissions ?? DefaultPermission;
    }

    /// <summary>
    /// A read-only ACL where the default permission is <see cref="TagPermission.Read"/>
    /// and there are no explicit entries.
    /// </summary>
    public static TagAcl ReadOnly { get; } = new() { DefaultPermission = TagPermission.Read };

    /// <summary>
    /// A full-access ACL where the default permission is <see cref="TagPermission.All"/>
    /// and there are no explicit entries.
    /// </summary>
    public static TagAcl FullAccess { get; } = new() { DefaultPermission = TagPermission.All };

    /// <inheritdoc />
    public override string ToString() =>
        Entries.Count > 0
            ? $"TagAcl({Entries.Count} entries, default={DefaultPermission})"
            : $"TagAcl(default={DefaultPermission})";
}
