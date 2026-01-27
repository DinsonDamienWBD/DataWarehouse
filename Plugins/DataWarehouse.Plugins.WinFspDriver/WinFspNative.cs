using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Native WinFSP interop definitions and P/Invoke declarations.
/// Provides low-level access to the WinFSP user-mode filesystem API.
/// </summary>
public static class WinFspNative
{
    /// <summary>
    /// WinFSP DLL name.
    /// </summary>
    private const string WinFspDll = "winfsp-x64.dll";

    #region Status Codes

    /// <summary>
    /// WinFSP status codes (compatible with NTSTATUS).
    /// </summary>
    public static class Status
    {
        public const int Success = 0;
        public const int Pending = 0x00000103;
        public const int BufferOverflow = unchecked((int)0x80000005);
        public const int NoMoreFiles = unchecked((int)0x80000006);
        public const int InvalidParameter = unchecked((int)0xC000000D);
        public const int NoSuchFile = unchecked((int)0xC000000F);
        public const int EndOfFile = unchecked((int)0xC0000011);
        public const int MoreProcessingRequired = unchecked((int)0xC0000016);
        public const int AccessDenied = unchecked((int)0xC0000022);
        public const int BufferTooSmall = unchecked((int)0xC0000023);
        public const int ObjectNameInvalid = unchecked((int)0xC0000033);
        public const int ObjectNameNotFound = unchecked((int)0xC0000034);
        public const int ObjectNameCollision = unchecked((int)0xC0000035);
        public const int ObjectPathNotFound = unchecked((int)0xC000003A);
        public const int SharingViolation = unchecked((int)0xC0000043);
        public const int DeletePending = unchecked((int)0xC0000056);
        public const int FileClosed = unchecked((int)0xC0000128);
        public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
        public const int NotADirectory = unchecked((int)0xC0000103);
        public const int FileIsADirectory = unchecked((int)0xC00000BA);
        public const int CannotDelete = unchecked((int)0xC0000121);
        public const int NotImplemented = unchecked((int)0xC0000002);
        public const int InternalError = unchecked((int)0xC00000E5);
        public const int DiskFull = unchecked((int)0xC000007F);
        public const int MediaWriteProtected = unchecked((int)0xC00000A2);
        public const int NotSupported = unchecked((int)0xC00000BB);
        public const int Timeout = unchecked((int)0x00000102);
        public const int Cancelled = unchecked((int)0xC0000120);
    }

    #endregion

    #region File Attributes and Flags

    /// <summary>
    /// File attributes (compatible with Windows FILE_ATTRIBUTE_*).
    /// </summary>
    [Flags]
    public enum FileAttributes : uint
    {
        None = 0,
        ReadOnly = 0x00000001,
        Hidden = 0x00000002,
        System = 0x00000004,
        Directory = 0x00000010,
        Archive = 0x00000020,
        Device = 0x00000040,
        Normal = 0x00000080,
        Temporary = 0x00000100,
        SparseFile = 0x00000200,
        ReparsePoint = 0x00000400,
        Compressed = 0x00000800,
        Offline = 0x00001000,
        NotContentIndexed = 0x00002000,
        Encrypted = 0x00004000,
        IntegrityStream = 0x00008000,
        Virtual = 0x00010000,
        NoScrubData = 0x00020000,
        Ea = 0x00040000,
        Pinned = 0x00080000,
        Unpinned = 0x00100000,
        RecallOnOpen = 0x00040000,
        RecallOnDataAccess = 0x00400000
    }

    /// <summary>
    /// File creation disposition options.
    /// </summary>
    public enum CreateDisposition : uint
    {
        Supersede = 0,
        Open = 1,
        Create = 2,
        OpenIf = 3,
        Overwrite = 4,
        OverwriteIf = 5
    }

    /// <summary>
    /// File access rights.
    /// </summary>
    [Flags]
    public enum AccessRights : uint
    {
        None = 0,
        Delete = 0x00010000,
        ReadControl = 0x00020000,
        WriteDac = 0x00040000,
        WriteOwner = 0x00080000,
        Synchronize = 0x00100000,
        StandardRightsRequired = Delete | ReadControl | WriteDac | WriteOwner,
        StandardRightsAll = StandardRightsRequired | Synchronize,
        GenericAll = 0x10000000,
        GenericExecute = 0x20000000,
        GenericWrite = 0x40000000,
        GenericRead = 0x80000000,
        FileReadData = 0x00000001,
        FileWriteData = 0x00000002,
        FileAppendData = 0x00000004,
        FileReadEa = 0x00000008,
        FileWriteEa = 0x00000010,
        FileExecute = 0x00000020,
        FileDeleteChild = 0x00000040,
        FileReadAttributes = 0x00000080,
        FileWriteAttributes = 0x00000100,
        FileAllAccess = StandardRightsRequired | Synchronize | 0x1FF
    }

    /// <summary>
    /// Create options for file operations.
    /// </summary>
    [Flags]
    public enum CreateOptions : uint
    {
        None = 0,
        DirectoryFile = 0x00000001,
        WriteThrough = 0x00000002,
        SequentialOnly = 0x00000004,
        NoIntermediateBuffering = 0x00000008,
        SynchronousIoAlert = 0x00000010,
        SynchronousIoNonAlert = 0x00000020,
        NonDirectoryFile = 0x00000040,
        CreateTreeConnection = 0x00000080,
        CompleteIfOplocked = 0x00000100,
        NoEaKnowledge = 0x00000200,
        OpenForRecovery = 0x00000400,
        RandomAccess = 0x00000800,
        DeleteOnClose = 0x00001000,
        OpenByFileId = 0x00002000,
        OpenForBackupIntent = 0x00004000,
        NoCompression = 0x00008000,
        OpenRequiringOplock = 0x00010000,
        DisallowExclusive = 0x00020000,
        ReserveOpfilter = 0x00100000,
        OpenReparsePoint = 0x00200000,
        OpenNoRecall = 0x00400000,
        OpenForFreeSpaceQuery = 0x00800000
    }

    /// <summary>
    /// Share access modes.
    /// </summary>
    [Flags]
    public enum ShareAccess : uint
    {
        None = 0,
        Read = 0x00000001,
        Write = 0x00000002,
        Delete = 0x00000004
    }

    #endregion

    #region Structures

    /// <summary>
    /// File information structure used by WinFSP operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
        public ulong AllocationSize;
        public ulong FileSize;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public ulong ChangeTime;
        public ulong IndexNumber;
        public uint HardLinks;
        public uint EaSize;
    }

    /// <summary>
    /// Volume information structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct VolumeInfo
    {
        public ulong TotalSize;
        public ulong FreeSize;
        public ushort VolumeLabelLength;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string VolumeLabel;
    }

    /// <summary>
    /// Directory information entry.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DirInfo
    {
        public ushort Size;
        public FileInfo FileInfo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
    }

    /// <summary>
    /// Stream information entry for alternate data streams.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StreamInfo
    {
        public ushort Size;
        public ulong StreamSize;
        public ulong StreamAllocationSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string StreamName;
    }

    /// <summary>
    /// File system parameters for WinFSP initialization.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FspFileSystemParams
    {
        public uint Version;
        public ushort SectorSize;
        public ushort SectorsPerAllocationUnit;
        public ushort MaxComponentLength;
        public ulong VolumeCreationTime;
        public uint VolumeSerialNumber;
        public uint TransactTimeout;
        public uint IrpTimeout;
        public uint IrpCapacity;
        public uint FileInfoTimeout;
        public uint CaseSensitiveSearch;
        public uint CasePreservedNames;
        public uint UnicodeOnDisk;
        public uint PersistentAcls;
        public uint ReparsePoints;
        public uint ReparsePointsAccessCheck;
        public uint NamedStreams;
        public uint HardLinks;
        public uint ExtendedAttributes;
        public uint WslFeatures;
        public uint FlushAndPurgeOnCleanup;
        public uint DeviceControl;
        public uint UmFileContextIsUserContext2;
        public uint UmFileContextIsFullContext;
        public uint AllowOpenInKernelMode;
        public uint CasePreservedExtendedAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)]
        public string Prefix;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string FileSystemName;
    }

    /// <summary>
    /// Security descriptor information.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityDescriptorInfo
    {
        public IntPtr SecurityDescriptor;
        public uint SecurityDescriptorSize;
    }

    /// <summary>
    /// Open file information returned after create/open operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OpenFileInfo
    {
        public FileInfo FileInfo;
        public IntPtr NormalizedName;
        public ushort NormalizedNameSize;
    }

    #endregion

    #region Callback Delegates

    /// <summary>
    /// Delegate for GetVolumeInfo callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetVolumeInfoDelegate(IntPtr fileSystem, out VolumeInfo volumeInfo);

    /// <summary>
    /// Delegate for SetVolumeLabel callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int SetVolumeLabelDelegate(IntPtr fileSystem, string volumeLabel, out VolumeInfo volumeInfo);

    /// <summary>
    /// Delegate for GetSecurityByName callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int GetSecurityByNameDelegate(
        IntPtr fileSystem,
        string fileName,
        out uint fileAttributes,
        IntPtr securityDescriptor,
        ref uint securityDescriptorSize);

    /// <summary>
    /// Delegate for Create callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int CreateDelegate(
        IntPtr fileSystem,
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        IntPtr securityDescriptor,
        ulong allocationSize,
        out IntPtr fileContext,
        out OpenFileInfo openFileInfo);

    /// <summary>
    /// Delegate for Open callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int OpenDelegate(
        IntPtr fileSystem,
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out IntPtr fileContext,
        out OpenFileInfo openFileInfo);

    /// <summary>
    /// Delegate for Overwrite callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int OverwriteDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo);

    /// <summary>
    /// Delegate for Cleanup callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void CleanupDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        string fileName,
        uint flags);

    /// <summary>
    /// Delegate for Close callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void CloseDelegate(IntPtr fileSystem, IntPtr fileContext);

    /// <summary>
    /// Delegate for Read callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int ReadDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred);

    /// <summary>
    /// Delegate for Write callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int WriteDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo);

    /// <summary>
    /// Delegate for Flush callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FlushDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        out FileInfo fileInfo);

    /// <summary>
    /// Delegate for GetFileInfo callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetFileInfoDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        out FileInfo fileInfo);

    /// <summary>
    /// Delegate for SetBasicInfo callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetBasicInfoDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo);

    /// <summary>
    /// Delegate for SetFileSize callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetFileSizeDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo);

    /// <summary>
    /// Delegate for CanDelete callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CanDeleteDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        string fileName);

    /// <summary>
    /// Delegate for Rename callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int RenameDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        string fileName,
        string newFileName,
        bool replaceIfExists);

    /// <summary>
    /// Delegate for GetSecurity callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetSecurityDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        IntPtr securityDescriptor,
        ref uint securityDescriptorSize);

    /// <summary>
    /// Delegate for SetSecurity callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetSecurityDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        uint securityInformation,
        IntPtr modificationDescriptor);

    /// <summary>
    /// Delegate for ReadDirectory callback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int ReadDirectoryDelegate(
        IntPtr fileSystem,
        IntPtr fileContext,
        string pattern,
        string marker,
        IntPtr buffer,
        uint length,
        out uint bytesTransferred);

    #endregion

    #region Interface Structure

    /// <summary>
    /// WinFSP file system interface containing all operation callbacks.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FspFileSystemInterface
    {
        public GetVolumeInfoDelegate GetVolumeInfo;
        public SetVolumeLabelDelegate SetVolumeLabel;
        public GetSecurityByNameDelegate GetSecurityByName;
        public CreateDelegate Create;
        public OpenDelegate Open;
        public OverwriteDelegate Overwrite;
        public CleanupDelegate Cleanup;
        public CloseDelegate Close;
        public ReadDelegate Read;
        public WriteDelegate Write;
        public FlushDelegate Flush;
        public GetFileInfoDelegate GetFileInfo;
        public SetBasicInfoDelegate SetBasicInfo;
        public SetFileSizeDelegate SetFileSize;
        public CanDeleteDelegate CanDelete;
        public RenameDelegate Rename;
        public GetSecurityDelegate GetSecurity;
        public SetSecurityDelegate SetSecurity;
        public ReadDirectoryDelegate ReadDirectory;
        // Additional callbacks for extended functionality
        public IntPtr ResolveReparsePoints;
        public IntPtr GetReparsePoint;
        public IntPtr SetReparsePoint;
        public IntPtr DeleteReparsePoint;
        public IntPtr GetStreamInfo;
        public IntPtr GetDirInfoByName;
        public IntPtr Control;
        public IntPtr SetDelete;
    }

    #endregion

    #region P/Invoke Declarations

    /// <summary>
    /// Creates a WinFSP file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int FspFileSystemCreate(
        string devicePath,
        ref FspFileSystemParams volumeParams,
        ref FspFileSystemInterface interface_,
        out IntPtr pFileSystem);

    /// <summary>
    /// Deletes a WinFSP file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemDelete(IntPtr fileSystem);

    /// <summary>
    /// Sets the mount point for a file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern int FspFileSystemSetMountPoint(IntPtr fileSystem, string mountPoint);

    /// <summary>
    /// Removes the mount point from a file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemRemoveMountPoint(IntPtr fileSystem);

    /// <summary>
    /// Starts the file system dispatcher.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FspFileSystemStartDispatcher(IntPtr fileSystem, uint threadCount);

    /// <summary>
    /// Stops the file system dispatcher.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemStopDispatcher(IntPtr fileSystem);

    /// <summary>
    /// Gets the mount point of a file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FspFileSystemMountPoint(IntPtr fileSystem);

    /// <summary>
    /// Sends a response for an asynchronous operation.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemSendResponse(IntPtr fileSystem, IntPtr response);

    /// <summary>
    /// Gets the user context from a file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FspFileSystemGetUserContext(IntPtr fileSystem);

    /// <summary>
    /// Sets the user context for a file system.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void FspFileSystemSetUserContext(IntPtr fileSystem, IntPtr userContext);

    /// <summary>
    /// Converts an NTSTATUS code to a Win32 error code.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FspNtStatusFromWin32(uint error);

    /// <summary>
    /// Converts a Win32 error code to an NTSTATUS code.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FspWin32FromNtStatus(int status);

    /// <summary>
    /// Fills directory buffer with an entry.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FspFileSystemAddDirInfo(
        ref DirInfo dirInfo,
        IntPtr buffer,
        uint length,
        out uint bytesTransferred);

    /// <summary>
    /// Ends a directory enumeration.
    /// </summary>
    [DllImport(WinFspDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FspFileSystemEndDirInfo(
        IntPtr buffer,
        uint length,
        out uint bytesTransferred);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if a status code indicates success.
    /// </summary>
    /// <param name="status">The status code to check.</param>
    /// <returns>True if the status indicates success.</returns>
    public static bool IsSuccess(int status) => status >= 0;

    /// <summary>
    /// Converts a Windows FILETIME to a DateTime.
    /// </summary>
    /// <param name="fileTime">The FILETIME value (100-nanosecond intervals since Jan 1, 1601).</param>
    /// <returns>The corresponding DateTime in UTC.</returns>
    public static DateTime FileTimeToDateTime(ulong fileTime)
    {
        if (fileTime == 0)
            return DateTime.MinValue;
        return DateTime.FromFileTimeUtc((long)fileTime);
    }

    /// <summary>
    /// Converts a DateTime to a Windows FILETIME.
    /// </summary>
    /// <param name="dateTime">The DateTime to convert.</param>
    /// <returns>The corresponding FILETIME value.</returns>
    public static ulong DateTimeToFileTime(DateTime dateTime)
    {
        if (dateTime == DateTime.MinValue)
            return 0;
        return (ulong)dateTime.ToFileTimeUtc();
    }

    /// <summary>
    /// Gets the current time as a FILETIME.
    /// </summary>
    /// <returns>Current time as FILETIME.</returns>
    public static ulong GetCurrentFileTime() => DateTimeToFileTime(DateTime.UtcNow);

    #endregion
}
