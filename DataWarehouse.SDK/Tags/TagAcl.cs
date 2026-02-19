using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Flags representing permissions on a tag.
    /// </summary>
    [Flags]
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public enum TagPermission
    {
        None = 0,
        Read = 1,
        Write = 2,
        Delete = 4,
        Admin = 8,
        All = Read | Write | Delete | Admin
    }

    /// <summary>
    /// An access-control entry granting a principal specific tag permissions.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed record TagAclEntry(string PrincipalId, TagPermission Permissions);

    /// <summary>
    /// Per-tag access control list with explicit entries and a default fallback.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag types")]
    public sealed record TagAcl
    {
        public IReadOnlyList<TagAclEntry> Entries { get; init; } = Array.Empty<TagAclEntry>();

        public TagPermission DefaultPermission { get; init; } = TagPermission.Read;

        /// <summary>
        /// Returns the effective permission for the given principal.
        /// If an explicit entry exists, its permissions are returned; otherwise <see cref="DefaultPermission"/>.
        /// </summary>
        public TagPermission GetEffectivePermission(string principalId)
        {
            var entry = Entries.FirstOrDefault(e =>
                string.Equals(e.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase));
            return entry?.Permissions ?? DefaultPermission;
        }

        /// <summary>Read-only ACL: everyone can read, no other permissions.</summary>
        public static TagAcl ReadOnly { get; } = new() { DefaultPermission = TagPermission.Read };

        /// <summary>Full-access ACL: everyone gets all permissions.</summary>
        public static TagAcl FullAccess { get; } = new() { DefaultPermission = TagPermission.All };
    }
}
