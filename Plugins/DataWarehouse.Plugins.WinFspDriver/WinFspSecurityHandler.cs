using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Handles Windows security descriptors, ACLs, and permission management for the WinFSP filesystem.
/// Provides translation between DataWarehouse permissions and Windows security model.
/// </summary>
public sealed class WinFspSecurityHandler : IDisposable
{
    private readonly SecurityConfig _config;
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _securityDescriptorCache;
    private byte[]? _defaultSecurityDescriptor;
    private byte[]? _defaultDirectorySecurityDescriptor;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the security handler.
    /// </summary>
    /// <param name="config">Security configuration.</param>
    public WinFspSecurityHandler(SecurityConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _securityDescriptorCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        InitializeDefaultSecurityDescriptors();
    }

    /// <summary>
    /// Gets whether ACL enforcement is enabled.
    /// </summary>
    public bool EnforceAcls => _config.EnforceAcls;

    #region Security Descriptor Operations

    /// <summary>
    /// Gets the security descriptor for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns>Security descriptor as byte array, or null if not found.</returns>
    public byte[]? GetSecurityDescriptor(string path, bool isDirectory)
    {
        if (string.IsNullOrEmpty(path))
            return isDirectory ? _defaultDirectorySecurityDescriptor : _defaultSecurityDescriptor;

        lock (_lock)
        {
            if (_securityDescriptorCache.TryGetValue(path, out var cached))
                return cached;
        }

        // Return default if no cached entry
        return isDirectory ? _defaultDirectorySecurityDescriptor : _defaultSecurityDescriptor;
    }

    /// <summary>
    /// Sets the security descriptor for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <param name="securityDescriptor">The security descriptor bytes.</param>
    public void SetSecurityDescriptor(string path, byte[] securityDescriptor)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (securityDescriptor == null || securityDescriptor.Length == 0)
            throw new ArgumentNullException(nameof(securityDescriptor));

        lock (_lock)
        {
            _securityDescriptorCache[path] = securityDescriptor;
        }
    }

    /// <summary>
    /// Removes the security descriptor for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool RemoveSecurityDescriptor(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        lock (_lock)
        {
            return _securityDescriptorCache.Remove(path);
        }
    }

    /// <summary>
    /// Copies security descriptor from source to destination path.
    /// </summary>
    /// <param name="sourcePath">Source path.</param>
    /// <param name="destinationPath">Destination path.</param>
    public void CopySecurityDescriptor(string sourcePath, string destinationPath)
    {
        var sd = GetSecurityDescriptor(sourcePath, false);
        if (sd != null)
        {
            SetSecurityDescriptor(destinationPath, sd);
        }
    }

    /// <summary>
    /// Creates a security descriptor from SDDL string.
    /// </summary>
    /// <param name="sddl">The SDDL string.</param>
    /// <returns>Security descriptor bytes.</returns>
    public byte[] CreateSecurityDescriptorFromSddl(string sddl)
    {
        if (string.IsNullOrEmpty(sddl))
            throw new ArgumentNullException(nameof(sddl));

        var rawSd = new RawSecurityDescriptor(sddl);
        var bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// Converts security descriptor bytes to SDDL string.
    /// </summary>
    /// <param name="securityDescriptor">The security descriptor bytes.</param>
    /// <returns>SDDL string representation.</returns>
    public string GetSddlFromSecurityDescriptor(byte[] securityDescriptor)
    {
        if (securityDescriptor == null || securityDescriptor.Length == 0)
            throw new ArgumentNullException(nameof(securityDescriptor));

        var rawSd = new RawSecurityDescriptor(securityDescriptor, 0);
        return rawSd.GetSddlForm(AccessControlSections.All);
    }

    #endregion

    #region Access Check Operations

    /// <summary>
    /// Checks if the specified access is allowed for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <param name="identity">The identity requesting access.</param>
    /// <param name="accessMask">The requested access mask.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns>True if access is allowed.</returns>
    public bool CheckAccess(string path, WindowsIdentity identity, uint accessMask, bool isDirectory)
    {
        if (!_config.EnforceAcls)
            return true;

        var sd = GetSecurityDescriptor(path, isDirectory);
        if (sd == null)
            return true; // No security descriptor = allow all

        try
        {
            var rawSd = new RawSecurityDescriptor(sd, 0);
            if (rawSd.DiscretionaryAcl == null)
                return true;

            var principal = new WindowsPrincipal(identity);
            var groups = identity.Groups?.Select(g => g.Value).ToArray() ?? Array.Empty<string>();

            foreach (var ace in rawSd.DiscretionaryAcl.Cast<CommonAce>())
            {
                var sidString = ace.SecurityIdentifier.Value;

                // Check if ACE applies to this user or any of their groups
                var applies = sidString == identity.User?.Value ||
                              groups.Contains(sidString) ||
                              IsWellKnownSidMatch(ace.SecurityIdentifier, identity, principal);

                if (!applies)
                    continue;

                var aceAccessMask = (uint)ace.AccessMask;

                if (ace.AceType == AceType.AccessAllowed)
                {
                    if ((aceAccessMask & accessMask) == accessMask)
                        return true;
                }
                else if (ace.AceType == AceType.AccessDenied)
                {
                    if ((aceAccessMask & accessMask) != 0)
                        return false;
                }
            }

            // Default deny if no explicit allow
            return false;
        }
        catch
        {
            // On error, allow access (fail-open for usability)
            return true;
        }
    }

    /// <summary>
    /// Gets the effective access mask for a path and identity.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <param name="identity">The identity to check.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns>The effective access mask.</returns>
    public uint GetEffectiveAccess(string path, WindowsIdentity identity, bool isDirectory)
    {
        if (!_config.EnforceAcls)
            return uint.MaxValue; // All access

        var sd = GetSecurityDescriptor(path, isDirectory);
        if (sd == null)
            return uint.MaxValue;

        try
        {
            var rawSd = new RawSecurityDescriptor(sd, 0);
            if (rawSd.DiscretionaryAcl == null)
                return uint.MaxValue;

            var principal = new WindowsPrincipal(identity);
            var groups = identity.Groups?.Select(g => g.Value).ToArray() ?? Array.Empty<string>();

            uint allowedMask = 0;
            uint deniedMask = 0;

            foreach (var ace in rawSd.DiscretionaryAcl.Cast<CommonAce>())
            {
                var sidString = ace.SecurityIdentifier.Value;
                var applies = sidString == identity.User?.Value ||
                              groups.Contains(sidString) ||
                              IsWellKnownSidMatch(ace.SecurityIdentifier, identity, principal);

                if (!applies)
                    continue;

                if (ace.AceType == AceType.AccessAllowed)
                {
                    allowedMask |= (uint)ace.AccessMask;
                }
                else if (ace.AceType == AceType.AccessDenied)
                {
                    deniedMask |= (uint)ace.AccessMask;
                }
            }

            return allowedMask & ~deniedMask;
        }
        catch
        {
            return uint.MaxValue;
        }
    }

    #endregion

    #region Owner and Group Operations

    /// <summary>
    /// Gets the owner SID for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <returns>Owner SID string.</returns>
    public string? GetOwner(string path)
    {
        var sd = GetSecurityDescriptor(path, false);
        if (sd == null)
            return null;

        try
        {
            var rawSd = new RawSecurityDescriptor(sd, 0);
            return rawSd.Owner?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the group SID for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <returns>Group SID string.</returns>
    public string? GetGroup(string path)
    {
        var sd = GetSecurityDescriptor(path, false);
        if (sd == null)
            return null;

        try
        {
            var rawSd = new RawSecurityDescriptor(sd, 0);
            return rawSd.Group?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the owner for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <param name="ownerSid">The new owner SID string.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    public void SetOwner(string path, string ownerSid, bool isDirectory)
    {
        var sd = GetSecurityDescriptor(path, isDirectory);
        if (sd == null)
        {
            sd = isDirectory ? _defaultDirectorySecurityDescriptor! : _defaultSecurityDescriptor!;
        }

        var rawSd = new RawSecurityDescriptor(sd, 0);
        rawSd.Owner = new SecurityIdentifier(ownerSid);

        var bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);
        SetSecurityDescriptor(path, bytes);
    }

    /// <summary>
    /// Sets the group for a path.
    /// </summary>
    /// <param name="path">The file or directory path.</param>
    /// <param name="groupSid">The new group SID string.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    public void SetGroup(string path, string groupSid, bool isDirectory)
    {
        var sd = GetSecurityDescriptor(path, isDirectory);
        if (sd == null)
        {
            sd = isDirectory ? _defaultDirectorySecurityDescriptor! : _defaultSecurityDescriptor!;
        }

        var rawSd = new RawSecurityDescriptor(sd, 0);
        rawSd.Group = new SecurityIdentifier(groupSid);

        var bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);
        SetSecurityDescriptor(path, bytes);
    }

    #endregion

    #region Inheritance Operations

    /// <summary>
    /// Creates a security descriptor for a new child based on parent inheritance.
    /// </summary>
    /// <param name="parentPath">The parent directory path.</param>
    /// <param name="isDirectory">Whether the new child is a directory.</param>
    /// <returns>Inherited security descriptor bytes.</returns>
    public byte[] CreateInheritedSecurityDescriptor(string? parentPath, bool isDirectory)
    {
        if (!_config.InheritPermissions || string.IsNullOrEmpty(parentPath))
        {
            return isDirectory ? _defaultDirectorySecurityDescriptor! : _defaultSecurityDescriptor!;
        }

        var parentSd = GetSecurityDescriptor(parentPath, true);
        if (parentSd == null)
        {
            return isDirectory ? _defaultDirectorySecurityDescriptor! : _defaultSecurityDescriptor!;
        }

        try
        {
            var parentRawSd = new RawSecurityDescriptor(parentSd, 0);
            var newDacl = new RawAcl(GenericAcl.AclRevision, 0);

            // Copy inheritable ACEs from parent
            if (parentRawSd.DiscretionaryAcl != null)
            {
                foreach (var ace in parentRawSd.DiscretionaryAcl.Cast<CommonAce>())
                {
                    var flags = ace.AceFlags;

                    // Check inheritance flags
                    bool inheritable;
                    if (isDirectory)
                    {
                        inheritable = (flags & AceFlags.ContainerInherit) != 0;
                    }
                    else
                    {
                        inheritable = (flags & AceFlags.ObjectInherit) != 0;
                    }

                    if (!inheritable)
                        continue;

                    // Create new ACE with inherited flag
                    var newFlags = AceFlags.Inherited;
                    if ((flags & AceFlags.NoPropagateInherit) == 0)
                    {
                        // Propagate inheritance to children
                        if (isDirectory)
                        {
                            if ((flags & AceFlags.ContainerInherit) != 0)
                                newFlags |= AceFlags.ContainerInherit;
                            if ((flags & AceFlags.ObjectInherit) != 0)
                                newFlags |= AceFlags.ObjectInherit;
                        }
                    }

                    var newAce = new CommonAce(newFlags, ace.AceQualifier, ace.AccessMask, ace.SecurityIdentifier, ace.IsCallback, ace.GetOpaque());
                    newDacl.InsertAce(newDacl.Count, newAce);
                }
            }

            // Create new security descriptor
            var owner = parentRawSd.Owner ?? GetCurrentUserSid();
            var group = parentRawSd.Group ?? GetCurrentGroupSid();

            var newRawSd = new RawSecurityDescriptor(
                ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
                owner,
                group,
                null, // No SACL
                newDacl);

            var bytes = new byte[newRawSd.BinaryLength];
            newRawSd.GetBinaryForm(bytes, 0);
            return bytes;
        }
        catch
        {
            return isDirectory ? _defaultDirectorySecurityDescriptor! : _defaultSecurityDescriptor!;
        }
    }

    #endregion

    #region Audit Operations

    /// <summary>
    /// Logs an access audit event.
    /// </summary>
    /// <param name="path">The accessed path.</param>
    /// <param name="identity">The identity accessing the path.</param>
    /// <param name="accessMask">The access mask requested.</param>
    /// <param name="success">Whether access was granted.</param>
    public void AuditAccess(string path, WindowsIdentity identity, uint accessMask, bool success)
    {
        if (!_config.AuditAccess || string.IsNullOrEmpty(_config.AuditLogPath))
            return;

        try
        {
            var entry = $"{DateTime.UtcNow:O}|{(success ? "SUCCESS" : "FAILURE")}|{identity.Name}|{path}|{accessMask:X8}";

            lock (_lock)
            {
                File.AppendAllLines(_config.AuditLogPath, new[] { entry });
            }
        }
        catch
        {
            // Audit logging should not throw
        }
    }

    #endregion

    #region Private Methods

    private void InitializeDefaultSecurityDescriptors()
    {
        try
        {
            var owner = GetConfiguredOwnerSid();
            var group = GetConfiguredGroupSid();
            var dacl = CreateDefaultDacl();

            // File security descriptor
            var fileSd = new RawSecurityDescriptor(
                ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
                owner,
                group,
                null,
                dacl);

            _defaultSecurityDescriptor = new byte[fileSd.BinaryLength];
            fileSd.GetBinaryForm(_defaultSecurityDescriptor, 0);

            // Directory security descriptor (with inheritance)
            var dirDacl = CreateDefaultDacl(isDirectory: true);
            var dirSd = new RawSecurityDescriptor(
                ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
                owner,
                group,
                null,
                dirDacl);

            _defaultDirectorySecurityDescriptor = new byte[dirSd.BinaryLength];
            dirSd.GetBinaryForm(_defaultDirectorySecurityDescriptor, 0);
        }
        catch
        {
            // Create minimal security descriptors on error
            _defaultSecurityDescriptor = CreateMinimalSecurityDescriptor();
            _defaultDirectorySecurityDescriptor = _defaultSecurityDescriptor;
        }
    }

    private SecurityIdentifier GetConfiguredOwnerSid()
    {
        if (!string.IsNullOrEmpty(_config.DefaultOwnerSid))
        {
            return new SecurityIdentifier(_config.DefaultOwnerSid);
        }
        return GetCurrentUserSid();
    }

    private SecurityIdentifier GetConfiguredGroupSid()
    {
        if (!string.IsNullOrEmpty(_config.DefaultGroupSid))
        {
            return new SecurityIdentifier(_config.DefaultGroupSid);
        }
        return GetCurrentGroupSid();
    }

    private static SecurityIdentifier GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    }

    private static SecurityIdentifier GetCurrentGroupSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        // Get the primary group from the groups collection if available
        var groups = identity.Groups;
        if (groups != null && groups.Count > 0)
        {
            var firstGroup = groups[0];
            if (firstGroup != null)
            {
                return firstGroup.Translate(typeof(SecurityIdentifier)) as SecurityIdentifier
                       ?? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            }
        }
        return new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    }

    private RawAcl CreateDefaultDacl(bool isDirectory = false)
    {
        if (!string.IsNullOrEmpty(_config.DefaultDacl))
        {
            var rawSd = new RawSecurityDescriptor(_config.DefaultDacl);
            if (rawSd.DiscretionaryAcl != null)
                return rawSd.DiscretionaryAcl;
        }

        var dacl = new RawAcl(GenericAcl.AclRevision, 0);

        // Full control for SYSTEM
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var systemFlags = isDirectory
            ? AceFlags.ContainerInherit | AceFlags.ObjectInherit
            : AceFlags.None;
        dacl.InsertAce(0, new CommonAce(systemFlags, AceQualifier.AccessAllowed, 0x1F01FF, systemSid, false, null));

        // Full control for Administrators
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var adminFlags = isDirectory
            ? AceFlags.ContainerInherit | AceFlags.ObjectInherit
            : AceFlags.None;
        dacl.InsertAce(1, new CommonAce(adminFlags, AceQualifier.AccessAllowed, 0x1F01FF, adminSid, false, null));

        // Read/Write/Execute for Users
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var usersFlags = isDirectory
            ? AceFlags.ContainerInherit | AceFlags.ObjectInherit
            : AceFlags.None;
        dacl.InsertAce(2, new CommonAce(usersFlags, AceQualifier.AccessAllowed, 0x1301BF, usersSid, false, null));

        return dacl;
    }

    private static byte[] CreateMinimalSecurityDescriptor()
    {
        // O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)
        var sddl = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)";
        var rawSd = new RawSecurityDescriptor(sddl);
        var bytes = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static bool IsWellKnownSidMatch(SecurityIdentifier sid, WindowsIdentity identity, WindowsPrincipal principal)
    {
        // Check for well-known SIDs
        if (sid.IsWellKnown(WellKnownSidType.WorldSid))
            return true; // Everyone

        if (sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
            return identity.IsAuthenticated;

        if (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            return principal.IsInRole(WindowsBuiltInRole.Administrator);

        if (sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid))
            return principal.IsInRole(WindowsBuiltInRole.User);

        return false;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the security handler.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            _securityDescriptorCache.Clear();
        }

        _disposed = true;
    }

    #endregion
}
