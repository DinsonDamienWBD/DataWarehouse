// <copyright file="PosixPermissions.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;
using System.Text;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Handles POSIX file permissions including mode bits, owner/group IDs, and ACL support.
/// </summary>
public sealed class PosixPermissions
{
    /// <summary>
    /// File type mask for mode bits.
    /// </summary>
    public const uint S_IFMT = 0xF000;

    /// <summary>
    /// Socket file type.
    /// </summary>
    public const uint S_IFSOCK = 0xC000;

    /// <summary>
    /// Symbolic link file type.
    /// </summary>
    public const uint S_IFLNK = 0xA000;

    /// <summary>
    /// Regular file type.
    /// </summary>
    public const uint S_IFREG = 0x8000;

    /// <summary>
    /// Block device file type.
    /// </summary>
    public const uint S_IFBLK = 0x6000;

    /// <summary>
    /// Directory file type.
    /// </summary>
    public const uint S_IFDIR = 0x4000;

    /// <summary>
    /// Character device file type.
    /// </summary>
    public const uint S_IFCHR = 0x2000;

    /// <summary>
    /// FIFO (named pipe) file type.
    /// </summary>
    public const uint S_IFIFO = 0x1000;

    /// <summary>
    /// Set user ID on execution.
    /// </summary>
    public const uint S_ISUID = 0x800;

    /// <summary>
    /// Set group ID on execution.
    /// </summary>
    public const uint S_ISGID = 0x400;

    /// <summary>
    /// Sticky bit.
    /// </summary>
    public const uint S_ISVTX = 0x200;

    /// <summary>
    /// Owner read permission.
    /// </summary>
    public const uint S_IRUSR = 0x100;

    /// <summary>
    /// Owner write permission.
    /// </summary>
    public const uint S_IWUSR = 0x80;

    /// <summary>
    /// Owner execute permission.
    /// </summary>
    public const uint S_IXUSR = 0x40;

    /// <summary>
    /// Owner read/write/execute permissions.
    /// </summary>
    public const uint S_IRWXU = S_IRUSR | S_IWUSR | S_IXUSR;

    /// <summary>
    /// Group read permission.
    /// </summary>
    public const uint S_IRGRP = 0x20;

    /// <summary>
    /// Group write permission.
    /// </summary>
    public const uint S_IWGRP = 0x10;

    /// <summary>
    /// Group execute permission.
    /// </summary>
    public const uint S_IXGRP = 0x8;

    /// <summary>
    /// Group read/write/execute permissions.
    /// </summary>
    public const uint S_IRWXG = S_IRGRP | S_IWGRP | S_IXGRP;

    /// <summary>
    /// Other read permission.
    /// </summary>
    public const uint S_IROTH = 0x4;

    /// <summary>
    /// Other write permission.
    /// </summary>
    public const uint S_IWOTH = 0x2;

    /// <summary>
    /// Other execute permission.
    /// </summary>
    public const uint S_IXOTH = 0x1;

    /// <summary>
    /// Other read/write/execute permissions.
    /// </summary>
    public const uint S_IRWXO = S_IROTH | S_IWOTH | S_IXOTH;

    /// <summary>
    /// All permission bits (rwxrwxrwx).
    /// </summary>
    public const uint PERMISSION_MASK = 0x1FF;

    /// <summary>
    /// Gets or sets the file mode (type and permissions).
    /// </summary>
    public uint Mode { get; set; }

    /// <summary>
    /// Gets or sets the owner user ID.
    /// </summary>
    public uint Uid { get; set; }

    /// <summary>
    /// Gets or sets the owner group ID.
    /// </summary>
    public uint Gid { get; set; }

    /// <summary>
    /// Gets or sets the POSIX ACL entries for access control.
    /// </summary>
    public List<PosixAclEntry> Acl { get; set; } = new();

    /// <summary>
    /// Gets or sets the default POSIX ACL entries for directories.
    /// </summary>
    public List<PosixAclEntry> DefaultAcl { get; set; } = new();

    /// <summary>
    /// Gets or sets the NFSv4 ACL entries for extended access control.
    /// </summary>
    public List<Nfsv4AclEntry> Nfsv4Acl { get; set; } = new();

    /// <summary>
    /// Gets the file type from the mode.
    /// </summary>
    public FileType Type => (FileType)(Mode & S_IFMT);

    /// <summary>
    /// Gets a value indicating whether the file is a regular file.
    /// </summary>
    public bool IsRegularFile => (Mode & S_IFMT) == S_IFREG;

    /// <summary>
    /// Gets a value indicating whether the file is a directory.
    /// </summary>
    public bool IsDirectory => (Mode & S_IFMT) == S_IFDIR;

    /// <summary>
    /// Gets a value indicating whether the file is a symbolic link.
    /// </summary>
    public bool IsSymlink => (Mode & S_IFMT) == S_IFLNK;

    /// <summary>
    /// Gets a value indicating whether the setuid bit is set.
    /// </summary>
    public bool IsSetUid => (Mode & S_ISUID) != 0;

    /// <summary>
    /// Gets a value indicating whether the setgid bit is set.
    /// </summary>
    public bool IsSetGid => (Mode & S_ISGID) != 0;

    /// <summary>
    /// Gets a value indicating whether the sticky bit is set.
    /// </summary>
    public bool IsSticky => (Mode & S_ISVTX) != 0;

    /// <summary>
    /// Gets the permission bits (lower 9 bits).
    /// </summary>
    public uint PermissionBits => Mode & PERMISSION_MASK;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosixPermissions"/> class.
    /// </summary>
    public PosixPermissions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PosixPermissions"/> class with the specified mode.
    /// </summary>
    /// <param name="mode">The file mode.</param>
    /// <param name="uid">The owner user ID.</param>
    /// <param name="gid">The owner group ID.</param>
    public PosixPermissions(uint mode, uint uid = 0, uint gid = 0)
    {
        Mode = mode;
        Uid = uid;
        Gid = gid;
    }

    /// <summary>
    /// Creates permissions for a regular file with the specified mode.
    /// </summary>
    /// <param name="mode">The permission bits (e.g., 0644).</param>
    /// <param name="uid">The owner user ID.</param>
    /// <param name="gid">The owner group ID.</param>
    /// <returns>A new <see cref="PosixPermissions"/> instance.</returns>
    public static PosixPermissions CreateFile(uint mode = 0x1A4, uint uid = 0, uint gid = 0)
    {
        return new PosixPermissions(S_IFREG | (mode & PERMISSION_MASK), uid, gid);
    }

    /// <summary>
    /// Creates permissions for a directory with the specified mode.
    /// </summary>
    /// <param name="mode">The permission bits (e.g., 0755).</param>
    /// <param name="uid">The owner user ID.</param>
    /// <param name="gid">The owner group ID.</param>
    /// <returns>A new <see cref="PosixPermissions"/> instance.</returns>
    public static PosixPermissions CreateDirectory(uint mode = 0x1ED, uint uid = 0, uint gid = 0)
    {
        return new PosixPermissions(S_IFDIR | (mode & PERMISSION_MASK), uid, gid);
    }

    /// <summary>
    /// Creates permissions for a symbolic link.
    /// </summary>
    /// <param name="uid">The owner user ID.</param>
    /// <param name="gid">The owner group ID.</param>
    /// <returns>A new <see cref="PosixPermissions"/> instance.</returns>
    public static PosixPermissions CreateSymlink(uint uid = 0, uint gid = 0)
    {
        // Symlinks always have 0777 permissions
        return new PosixPermissions(S_IFLNK | 0x1FF, uid, gid);
    }

    /// <summary>
    /// Checks if the specified user/group has read access.
    /// </summary>
    /// <param name="uid">The user ID to check.</param>
    /// <param name="gid">The group ID to check.</param>
    /// <param name="supplementaryGroups">Additional group IDs the user belongs to.</param>
    /// <returns>True if read access is permitted.</returns>
    public bool CanRead(uint uid, uint gid, IEnumerable<uint>? supplementaryGroups = null)
    {
        // Check ACL first if present
        if (Acl.Count > 0)
        {
            var aclResult = CheckAclAccess(uid, gid, supplementaryGroups, AclPermission.Read);
            if (aclResult.HasValue)
                return aclResult.Value;
        }

        // Root can always read
        if (uid == 0)
            return true;

        // Owner check
        if (uid == Uid)
            return (Mode & S_IRUSR) != 0;

        // Group check
        if (IsInGroup(gid, supplementaryGroups))
            return (Mode & S_IRGRP) != 0;

        // Other
        return (Mode & S_IROTH) != 0;
    }

    /// <summary>
    /// Checks if the specified user/group has write access.
    /// </summary>
    /// <param name="uid">The user ID to check.</param>
    /// <param name="gid">The group ID to check.</param>
    /// <param name="supplementaryGroups">Additional group IDs the user belongs to.</param>
    /// <returns>True if write access is permitted.</returns>
    public bool CanWrite(uint uid, uint gid, IEnumerable<uint>? supplementaryGroups = null)
    {
        // Check ACL first if present
        if (Acl.Count > 0)
        {
            var aclResult = CheckAclAccess(uid, gid, supplementaryGroups, AclPermission.Write);
            if (aclResult.HasValue)
                return aclResult.Value;
        }

        // Root can always write
        if (uid == 0)
            return true;

        // Owner check
        if (uid == Uid)
            return (Mode & S_IWUSR) != 0;

        // Group check
        if (IsInGroup(gid, supplementaryGroups))
            return (Mode & S_IWGRP) != 0;

        // Other
        return (Mode & S_IWOTH) != 0;
    }

    /// <summary>
    /// Checks if the specified user/group has execute access.
    /// </summary>
    /// <param name="uid">The user ID to check.</param>
    /// <param name="gid">The group ID to check.</param>
    /// <param name="supplementaryGroups">Additional group IDs the user belongs to.</param>
    /// <returns>True if execute access is permitted.</returns>
    public bool CanExecute(uint uid, uint gid, IEnumerable<uint>? supplementaryGroups = null)
    {
        // Check ACL first if present
        if (Acl.Count > 0)
        {
            var aclResult = CheckAclAccess(uid, gid, supplementaryGroups, AclPermission.Execute);
            if (aclResult.HasValue)
                return aclResult.Value;
        }

        // Root can execute if any execute bit is set
        if (uid == 0)
            return (Mode & (S_IXUSR | S_IXGRP | S_IXOTH)) != 0;

        // Owner check
        if (uid == Uid)
            return (Mode & S_IXUSR) != 0;

        // Group check
        if (IsInGroup(gid, supplementaryGroups))
            return (Mode & S_IXGRP) != 0;

        // Other
        return (Mode & S_IXOTH) != 0;
    }

    /// <summary>
    /// Sets the permission bits, preserving the file type.
    /// </summary>
    /// <param name="permissions">The permission bits to set (lower 12 bits).</param>
    public void SetPermissions(uint permissions)
    {
        Mode = (Mode & S_IFMT) | (permissions & 0xFFF);
    }

    /// <summary>
    /// Applies a umask to the permission bits.
    /// </summary>
    /// <param name="umask">The umask value.</param>
    public void ApplyUmask(uint umask)
    {
        Mode = (Mode & S_IFMT) | ((Mode & PERMISSION_MASK) & ~umask);
    }

    /// <summary>
    /// Converts the mode to a string representation (e.g., "-rwxr-xr-x").
    /// </summary>
    /// <returns>The mode string.</returns>
    public string ToModeString()
    {
        var sb = new StringBuilder(10);

        // File type character
        sb.Append(Type switch
        {
            FileType.Directory => 'd',
            FileType.SymbolicLink => 'l',
            FileType.BlockDevice => 'b',
            FileType.CharacterDevice => 'c',
            FileType.Fifo => 'p',
            FileType.Socket => 's',
            _ => '-'
        });

        // Owner permissions
        sb.Append((Mode & S_IRUSR) != 0 ? 'r' : '-');
        sb.Append((Mode & S_IWUSR) != 0 ? 'w' : '-');
        if (IsSetUid)
            sb.Append((Mode & S_IXUSR) != 0 ? 's' : 'S');
        else
            sb.Append((Mode & S_IXUSR) != 0 ? 'x' : '-');

        // Group permissions
        sb.Append((Mode & S_IRGRP) != 0 ? 'r' : '-');
        sb.Append((Mode & S_IWGRP) != 0 ? 'w' : '-');
        if (IsSetGid)
            sb.Append((Mode & S_IXGRP) != 0 ? 's' : 'S');
        else
            sb.Append((Mode & S_IXGRP) != 0 ? 'x' : '-');

        // Other permissions
        sb.Append((Mode & S_IROTH) != 0 ? 'r' : '-');
        sb.Append((Mode & S_IWOTH) != 0 ? 'w' : '-');
        if (IsSticky)
            sb.Append((Mode & S_IXOTH) != 0 ? 't' : 'T');
        else
            sb.Append((Mode & S_IXOTH) != 0 ? 'x' : '-');

        return sb.ToString();
    }

    /// <summary>
    /// Parses a mode string (e.g., "rwxr-xr-x") to permission bits.
    /// </summary>
    /// <param name="modeString">The mode string to parse.</param>
    /// <returns>The permission bits.</returns>
    public static uint ParseModeString(string modeString)
    {
        if (string.IsNullOrEmpty(modeString))
            return 0;

        var s = modeString.TrimStart('-', 'd', 'l', 'b', 'c', 'p', 's');
        if (s.Length < 9)
            s = s.PadRight(9, '-');

        uint mode = 0;

        // Owner permissions
        if (s[0] == 'r') mode |= S_IRUSR;
        if (s[1] == 'w') mode |= S_IWUSR;
        if (s[2] == 'x' || s[2] == 's') mode |= S_IXUSR;
        if (s[2] == 's' || s[2] == 'S') mode |= S_ISUID;

        // Group permissions
        if (s[3] == 'r') mode |= S_IRGRP;
        if (s[4] == 'w') mode |= S_IWGRP;
        if (s[5] == 'x' || s[5] == 's') mode |= S_IXGRP;
        if (s[5] == 's' || s[5] == 'S') mode |= S_ISGID;

        // Other permissions
        if (s[6] == 'r') mode |= S_IROTH;
        if (s[7] == 'w') mode |= S_IWOTH;
        if (s[8] == 'x' || s[8] == 't') mode |= S_IXOTH;
        if (s[8] == 't' || s[8] == 'T') mode |= S_ISVTX;

        return mode;
    }

    /// <summary>
    /// Adds a POSIX ACL entry.
    /// </summary>
    /// <param name="tag">The ACL tag type.</param>
    /// <param name="id">The user or group ID (or -1 for USER_OBJ, GROUP_OBJ, OTHER, MASK).</param>
    /// <param name="permissions">The permissions to grant.</param>
    public void AddAclEntry(AclTag tag, int id, AclPermission permissions)
    {
        Acl.Add(new PosixAclEntry
        {
            Tag = tag,
            Id = id,
            Permissions = permissions
        });
    }

    /// <summary>
    /// Adds an NFSv4 ACL entry.
    /// </summary>
    /// <param name="type">The ACE type (allow/deny).</param>
    /// <param name="who">Who the ACE applies to.</param>
    /// <param name="whoId">The user or group ID.</param>
    /// <param name="accessMask">The access mask.</param>
    /// <param name="flags">The ACE flags.</param>
    public void AddNfsv4AclEntry(Nfsv4AceType type, Nfsv4AceWho who, int whoId,
        Nfsv4AccessMask accessMask, Nfsv4AceFlags flags = Nfsv4AceFlags.None)
    {
        Nfsv4Acl.Add(new Nfsv4AclEntry
        {
            Type = type,
            Who = who,
            WhoId = whoId,
            AccessMask = accessMask,
            Flags = flags
        });
    }

    /// <summary>
    /// Serializes the POSIX ACL to binary format for xattr storage.
    /// </summary>
    /// <returns>The serialized ACL bytes.</returns>
    public byte[] SerializePosixAcl()
    {
        if (Acl.Count == 0)
            return Array.Empty<byte>();

        using var ms = new MemoryStream(4096);
        using var writer = new BinaryWriter(ms);

        // POSIX ACL version
        writer.Write((uint)2);

        foreach (var entry in Acl)
        {
            writer.Write((ushort)entry.Tag);
            writer.Write((ushort)entry.Permissions);
            writer.Write(entry.Id);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a POSIX ACL from binary format.
    /// </summary>
    /// <param name="data">The serialized ACL bytes.</param>
    /// <returns>The list of ACL entries.</returns>
    public static List<PosixAclEntry> DeserializePosixAcl(byte[] data)
    {
        var entries = new List<PosixAclEntry>();

        if (data == null || data.Length < 8)
            return entries;

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var version = reader.ReadUInt32();
        if (version != 2)
            throw new InvalidOperationException($"Unsupported POSIX ACL version: {version}");

        while (ms.Position + 8 <= ms.Length)
        {
            entries.Add(new PosixAclEntry
            {
                Tag = (AclTag)reader.ReadUInt16(),
                Permissions = (AclPermission)reader.ReadUInt16(),
                Id = reader.ReadInt32()
            });
        }

        return entries;
    }

    private bool IsInGroup(uint gid, IEnumerable<uint>? supplementaryGroups)
    {
        if (gid == Gid)
            return true;

        if (supplementaryGroups != null)
        {
            foreach (var g in supplementaryGroups)
            {
                if (g == Gid)
                    return true;
            }
        }

        return false;
    }

    private bool? CheckAclAccess(uint uid, uint gid, IEnumerable<uint>? supplementaryGroups, AclPermission permission)
    {
        var mask = AclPermission.All;
        PosixAclEntry? userObjEntry = null;
        PosixAclEntry? groupObjEntry = null;
        PosixAclEntry? otherEntry = null;
        PosixAclEntry? userEntry = null;
        PosixAclEntry? groupEntry = null;

        foreach (var entry in Acl)
        {
            switch (entry.Tag)
            {
                case AclTag.UserObj:
                    userObjEntry = entry;
                    break;
                case AclTag.User:
                    if (entry.Id == (int)uid)
                        userEntry = entry;
                    break;
                case AclTag.GroupObj:
                    groupObjEntry = entry;
                    break;
                case AclTag.Group:
                    if (entry.Id == (int)gid || (supplementaryGroups?.Contains((uint)entry.Id) ?? false))
                        groupEntry = entry;
                    break;
                case AclTag.Mask:
                    mask = entry.Permissions;
                    break;
                case AclTag.Other:
                    otherEntry = entry;
                    break;
            }
        }

        // Owner match
        if (uid == Uid && userObjEntry.HasValue)
        {
            return (userObjEntry.Value.Permissions & permission) == permission;
        }

        // Named user match
        if (userEntry.HasValue)
        {
            return ((userEntry.Value.Permissions & mask) & permission) == permission;
        }

        // Group match
        if (IsInGroup(gid, supplementaryGroups))
        {
            if (groupEntry.HasValue)
            {
                return ((groupEntry.Value.Permissions & mask) & permission) == permission;
            }

            if (groupObjEntry.HasValue)
            {
                return ((groupObjEntry.Value.Permissions & mask) & permission) == permission;
            }
        }

        // Other
        if (otherEntry.HasValue)
        {
            return (otherEntry.Value.Permissions & permission) == permission;
        }

        return null; // Fall back to traditional permission check
    }
}

/// <summary>
/// File types for POSIX mode.
/// </summary>
public enum FileType : uint
{
    /// <summary>
    /// Regular file.
    /// </summary>
    Regular = PosixPermissions.S_IFREG,

    /// <summary>
    /// Directory.
    /// </summary>
    Directory = PosixPermissions.S_IFDIR,

    /// <summary>
    /// Symbolic link.
    /// </summary>
    SymbolicLink = PosixPermissions.S_IFLNK,

    /// <summary>
    /// Block device.
    /// </summary>
    BlockDevice = PosixPermissions.S_IFBLK,

    /// <summary>
    /// Character device.
    /// </summary>
    CharacterDevice = PosixPermissions.S_IFCHR,

    /// <summary>
    /// FIFO (named pipe).
    /// </summary>
    Fifo = PosixPermissions.S_IFIFO,

    /// <summary>
    /// Socket.
    /// </summary>
    Socket = PosixPermissions.S_IFSOCK
}

/// <summary>
/// POSIX ACL tag types.
/// </summary>
public enum AclTag : ushort
{
    /// <summary>
    /// Undefined ACL entry.
    /// </summary>
    Undefined = 0x00,

    /// <summary>
    /// Owner user permissions.
    /// </summary>
    UserObj = 0x01,

    /// <summary>
    /// Named user permissions.
    /// </summary>
    User = 0x02,

    /// <summary>
    /// Owning group permissions.
    /// </summary>
    GroupObj = 0x04,

    /// <summary>
    /// Named group permissions.
    /// </summary>
    Group = 0x08,

    /// <summary>
    /// Maximum permissions mask.
    /// </summary>
    Mask = 0x10,

    /// <summary>
    /// Other users permissions.
    /// </summary>
    Other = 0x20
}

/// <summary>
/// POSIX ACL permissions.
/// </summary>
[Flags]
public enum AclPermission : ushort
{
    /// <summary>
    /// No permissions.
    /// </summary>
    None = 0,

    /// <summary>
    /// Execute permission.
    /// </summary>
    Execute = 1,

    /// <summary>
    /// Write permission.
    /// </summary>
    Write = 2,

    /// <summary>
    /// Read permission.
    /// </summary>
    Read = 4,

    /// <summary>
    /// All permissions.
    /// </summary>
    All = Read | Write | Execute
}

/// <summary>
/// POSIX ACL entry.
/// </summary>
public struct PosixAclEntry
{
    /// <summary>
    /// The ACL tag type.
    /// </summary>
    public AclTag Tag { get; set; }

    /// <summary>
    /// The user or group ID (-1 for USER_OBJ, GROUP_OBJ, OTHER, MASK).
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The permissions granted.
    /// </summary>
    public AclPermission Permissions { get; set; }
}

/// <summary>
/// NFSv4 ACE types.
/// </summary>
public enum Nfsv4AceType : byte
{
    /// <summary>
    /// Allow access.
    /// </summary>
    Allow = 0,

    /// <summary>
    /// Deny access.
    /// </summary>
    Deny = 1,

    /// <summary>
    /// Audit successful access.
    /// </summary>
    Audit = 2,

    /// <summary>
    /// Alarm on access attempt.
    /// </summary>
    Alarm = 3
}

/// <summary>
/// NFSv4 ACE who types.
/// </summary>
public enum Nfsv4AceWho : byte
{
    /// <summary>
    /// Named user.
    /// </summary>
    User = 0,

    /// <summary>
    /// Named group.
    /// </summary>
    Group = 1,

    /// <summary>
    /// Special identifier (OWNER@, GROUP@, EVERYONE@).
    /// </summary>
    Special = 2
}

/// <summary>
/// NFSv4 ACE flags.
/// </summary>
[Flags]
public enum Nfsv4AceFlags : byte
{
    /// <summary>
    /// No flags.
    /// </summary>
    None = 0,

    /// <summary>
    /// ACE applies to directory itself.
    /// </summary>
    FileInherit = 0x01,

    /// <summary>
    /// ACE applies to subdirectories.
    /// </summary>
    DirectoryInherit = 0x02,

    /// <summary>
    /// Don't propagate inheritance.
    /// </summary>
    NoPropagateInherit = 0x04,

    /// <summary>
    /// ACE only affects inheritance.
    /// </summary>
    InheritOnly = 0x08,

    /// <summary>
    /// ACE was inherited.
    /// </summary>
    Inherited = 0x10,

    /// <summary>
    /// Principal is a group.
    /// </summary>
    Identifier = 0x40
}

/// <summary>
/// NFSv4 access mask.
/// </summary>
[Flags]
public enum Nfsv4AccessMask : uint
{
    /// <summary>
    /// Read file data.
    /// </summary>
    ReadData = 0x00000001,

    /// <summary>
    /// List directory contents.
    /// </summary>
    ListDirectory = 0x00000001,

    /// <summary>
    /// Write file data.
    /// </summary>
    WriteData = 0x00000002,

    /// <summary>
    /// Create files in directory.
    /// </summary>
    AddFile = 0x00000002,

    /// <summary>
    /// Append data to file.
    /// </summary>
    AppendData = 0x00000004,

    /// <summary>
    /// Create subdirectory.
    /// </summary>
    AddSubdirectory = 0x00000004,

    /// <summary>
    /// Read named attributes.
    /// </summary>
    ReadNamedAttrs = 0x00000008,

    /// <summary>
    /// Write named attributes.
    /// </summary>
    WriteNamedAttrs = 0x00000010,

    /// <summary>
    /// Execute file.
    /// </summary>
    Execute = 0x00000020,

    /// <summary>
    /// Traverse directory.
    /// </summary>
    Traverse = 0x00000020,

    /// <summary>
    /// Delete children in directory.
    /// </summary>
    DeleteChild = 0x00000040,

    /// <summary>
    /// Read file attributes.
    /// </summary>
    ReadAttributes = 0x00000080,

    /// <summary>
    /// Write file attributes.
    /// </summary>
    WriteAttributes = 0x00000100,

    /// <summary>
    /// Delete file.
    /// </summary>
    Delete = 0x00010000,

    /// <summary>
    /// Read ACL.
    /// </summary>
    ReadAcl = 0x00020000,

    /// <summary>
    /// Write ACL.
    /// </summary>
    WriteAcl = 0x00040000,

    /// <summary>
    /// Change owner.
    /// </summary>
    WriteOwner = 0x00080000,

    /// <summary>
    /// Synchronize access.
    /// </summary>
    Synchronize = 0x00100000,

    /// <summary>
    /// All permissions.
    /// </summary>
    All = 0x001F01FF
}

/// <summary>
/// NFSv4 ACL entry.
/// </summary>
public struct Nfsv4AclEntry
{
    /// <summary>
    /// The ACE type.
    /// </summary>
    public Nfsv4AceType Type { get; set; }

    /// <summary>
    /// The ACE flags.
    /// </summary>
    public Nfsv4AceFlags Flags { get; set; }

    /// <summary>
    /// Who the ACE applies to.
    /// </summary>
    public Nfsv4AceWho Who { get; set; }

    /// <summary>
    /// The user or group ID.
    /// </summary>
    public int WhoId { get; set; }

    /// <summary>
    /// The access mask.
    /// </summary>
    public Nfsv4AccessMask AccessMask { get; set; }
}
