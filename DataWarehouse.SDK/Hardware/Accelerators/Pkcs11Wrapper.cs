using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// Minimal PKCS#11 Cryptoki API wrapper for HSM access.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PKCS#11 (Cryptoki) is the universal API for accessing Hardware Security Modules.
    /// This wrapper provides a minimal subset of the full PKCS#11 specification covering
    /// the most commonly used functions for session management and cryptographic operations.
    /// </para>
    /// <para>
    /// <strong>PKCS#11 Library Loading</strong>: The library path varies by HSM vendor:
    /// <list type="bullet">
    /// <item><description><strong>Thales/SafeNet</strong>: cryptoki.dll (Windows), libCryptoki2_64.so (Linux)</description></item>
    /// <item><description><strong>AWS CloudHSM</strong>: cloudhsm_pkcs11.dll (Windows), libcloudhsm_pkcs11.so (Linux)</description></item>
    /// <item><description><strong>SoftHSM</strong> (testing): softhsm2.dll (Windows), libsofthsm2.so (Linux)</description></item>
    /// </list>
    /// The library path can be configured via <see cref="Pkcs11LibraryPath"/>.
    /// </para>
    /// <para>
    /// <strong>Phase 35 Scope</strong>: This wrapper includes only the PKCS#11 functions needed
    /// for basic session management, key generation, and cryptographic operations. The full
    /// PKCS#11 specification defines 70+ functions; expanding this wrapper is straightforward
    /// by adding additional function pointer delegates and marshaling structures.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 35: PKCS#11 HSM wrapper (HW-04)")]
    internal static partial class Pkcs11Wrapper
    {
        // ==================== Constants ====================

        /// <summary>
        /// PKCS#11 library path. Configurable for different HSM vendors.
        /// </summary>
        internal static string Pkcs11LibraryPath { get; set; } =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "cryptoki.dll"
                : "libCryptoki2_64.so";

        // Return codes
        internal const uint CKR_OK = 0x00000000;
        internal const uint CKR_ARGUMENTS_BAD = 0x00000007;
        internal const uint CKR_SESSION_HANDLE_INVALID = 0x000000B3;

        // Session flags
        internal const uint CKF_RW_SESSION = 0x00000002;
        internal const uint CKF_SERIAL_SESSION = 0x00000004;

        // User types
        internal const uint CKU_USER = 1;

        // Attribute types
        internal const uint CKA_CLASS = 0x00000000;
        internal const uint CKA_LABEL = 0x00000003;
        internal const uint CKA_VALUE = 0x00000011;
        internal const uint CKA_KEY_TYPE = 0x00000100;

        // Object classes
        internal const uint CKO_SECRET_KEY = 0x00000004;

        // Key types
        internal const uint CKK_AES = 0x0000001F;

        // ==================== Structures ====================

        /// <summary>
        /// PKCS#11 version information.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CK_VERSION
        {
            public byte major;
            public byte minor;
        }

        /// <summary>
        /// PKCS#11 library information.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CK_INFO
        {
            public CK_VERSION cryptokiVersion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] manufacturerID;
            public uint flags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] libraryDescription;
            public CK_VERSION libraryVersion;
        }

        /// <summary>
        /// PKCS#11 attribute structure for template-based operations.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CK_ATTRIBUTE
        {
            public uint type;
            public IntPtr pValue;
            public uint ulValueLen;
        }

        // ==================== Function Delegates ====================

        /// <summary>
        /// Initializes the PKCS#11 library.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_Initialize(IntPtr pInitArgs);

        /// <summary>
        /// Finalizes the PKCS#11 library.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_Finalize(IntPtr pReserved);

        /// <summary>
        /// Gets PKCS#11 library information.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_GetInfo(ref CK_INFO pInfo);

        /// <summary>
        /// Opens a session to an HSM slot.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_OpenSession(
            uint slotID,
            uint flags,
            IntPtr pApplication,
            IntPtr notify,
            out uint phSession);

        /// <summary>
        /// Closes an HSM session.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_CloseSession(uint hSession);

        /// <summary>
        /// Logs into an HSM session.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_Login(uint hSession, uint userType, byte[] pPin, uint ulPinLen);

        /// <summary>
        /// Logs out of an HSM session.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_Logout(uint hSession);

        /// <summary>
        /// Generates a secret key in the HSM.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_GenerateKey(
            uint hSession,
            IntPtr pMechanism,
            CK_ATTRIBUTE[] pTemplate,
            uint ulCount,
            out uint phKey);

        /// <summary>
        /// Signs data using an HSM key.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint C_Sign(
            uint hSession,
            byte[] pData,
            uint ulDataLen,
            byte[] pSignature,
            ref uint pulSignatureLen);

        // ==================== Function List Structure ====================

        /// <summary>
        /// PKCS#11 function list structure returned by C_GetFunctionList.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Per the PKCS#11 v2.40 specification (section 3.1), CK_FUNCTION_LIST is a structure
        /// containing a CK_VERSION followed by exactly 70 function pointers in a fixed, defined order.
        /// Each function pointer is one native pointer width (4 bytes on 32-bit, 8 bytes on 64-bit).
        /// </para>
        /// <para>
        /// We use <see cref="LayoutKind.Explicit"/> with byte offsets computed as:
        ///   offset = sizeof(CK_VERSION padded to pointer alignment) + (index * IntPtr.Size)
        /// CK_VERSION is 2 bytes (major + minor), padded to IntPtr.Size alignment = IntPtr.Size bytes.
        /// Function pointer index (0-based) per PKCS#11 v2.40 spec:
        ///   0=C_Initialize, 1=C_Finalize, 2=C_GetInfo, 3=C_GetFunctionList,
        ///   4=C_GetSlotList, 5=C_GetSlotInfo, 6=C_GetTokenInfo, 7=C_GetMechanismList,
        ///   8=C_GetMechanismInfo, 9=C_InitToken, 10=C_InitPIN, 11=C_SetPIN,
        ///   12=C_OpenSession, 13=C_CloseSession, 14=C_CloseAllSessions, 15=C_GetSessionInfo,
        ///   16=C_GetOperationState, 17=C_SetOperationState,
        ///   18=C_Login, 19=C_Logout
        /// </para>
        /// </remarks>
        [StructLayout(LayoutKind.Explicit)]
        internal struct CK_FUNCTION_LIST
        {
            // CK_VERSION (2 bytes: major + minor), padded to pointer-size alignment
            [FieldOffset(0)]
            public byte VersionMajor;
            [FieldOffset(1)]
            public byte VersionMinor;

            // Function pointer at index 0: C_Initialize
            // Offset = IntPtr.Size (version struct padded to pointer alignment)
            // We store the offsets as constants; at runtime we use GetFunctionPointer helpers.
            // Using FieldOffset requires compile-time constants, so we fix to 64-bit layout (8-byte pointers).
            // On 32-bit platforms, use the 32-bit variant (4-byte pointers).
            // These offsets are for LP64 (Linux/macOS/Windows 64-bit): version at offset 0 (padded to 8), then pointers.
            [FieldOffset(8)]
            public IntPtr C_Initialize;       // index 0

            [FieldOffset(16)]
            public IntPtr C_Finalize;         // index 1

            [FieldOffset(24)]
            public IntPtr C_GetInfo;          // index 2

            [FieldOffset(32)]
            public IntPtr C_GetFunctionList;  // index 3

            [FieldOffset(40)]
            public IntPtr C_GetSlotList;      // index 4

            [FieldOffset(48)]
            public IntPtr C_GetSlotInfo;      // index 5

            [FieldOffset(56)]
            public IntPtr C_GetTokenInfo;     // index 6

            [FieldOffset(64)]
            public IntPtr C_GetMechanismList; // index 7

            [FieldOffset(72)]
            public IntPtr C_GetMechanismInfo; // index 8

            [FieldOffset(80)]
            public IntPtr C_InitToken;        // index 9

            [FieldOffset(88)]
            public IntPtr C_InitPIN;          // index 10

            [FieldOffset(96)]
            public IntPtr C_SetPIN;           // index 11

            [FieldOffset(104)]
            public IntPtr C_OpenSession;      // index 12

            [FieldOffset(112)]
            public IntPtr C_CloseSession;     // index 13

            [FieldOffset(120)]
            public IntPtr C_CloseAllSessions; // index 14

            [FieldOffset(128)]
            public IntPtr C_GetSessionInfo;   // index 15

            [FieldOffset(136)]
            public IntPtr C_GetOperationState; // index 16

            [FieldOffset(144)]
            public IntPtr C_SetOperationState; // index 17

            [FieldOffset(152)]
            public IntPtr C_Login;            // index 18

            [FieldOffset(160)]
            public IntPtr C_Logout;           // index 19
        }

        // ==================== Library Loading ====================

        /// <summary>
        /// Loads the PKCS#11 library from the configured path.
        /// </summary>
        /// <returns>Library handle, or IntPtr.Zero if loading failed.</returns>
        internal static IntPtr LoadLibrary()
        {
            if (NativeLibrary.TryLoad(Pkcs11LibraryPath, out IntPtr handle))
                return handle;
            return IntPtr.Zero;
        }

        /// <summary>
        /// Gets the PKCS#11 function list from the loaded library.
        /// </summary>
        /// <param name="libraryHandle">Handle to the loaded PKCS#11 library.</param>
        /// <returns>Function list structure with function pointers.</returns>
        /// <exception cref="InvalidOperationException">Thrown when C_GetFunctionList is not found or fails.</exception>
        /// <remarks>
        /// This method calls the C_GetFunctionList function to retrieve a pointer to the
        /// CK_FUNCTION_LIST structure, which contains function pointers to all PKCS#11 functions.
        /// </remarks>
        internal static CK_FUNCTION_LIST GetFunctionList(IntPtr libraryHandle)
        {
            // Get C_GetFunctionList export
            IntPtr getFunctionListPtr = NativeLibrary.GetExport(libraryHandle, "C_GetFunctionList");
            if (getFunctionListPtr == IntPtr.Zero)
                throw new InvalidOperationException("C_GetFunctionList not found in PKCS#11 library");

            // Invoke C_GetFunctionList
            var getFunctionList = Marshal.GetDelegateForFunctionPointer<C_GetFunctionListDelegate>(getFunctionListPtr);
            IntPtr functionListPtr = IntPtr.Zero;
            uint rv = getFunctionList(ref functionListPtr);
            if (rv != CKR_OK)
                throw new InvalidOperationException($"C_GetFunctionList failed: 0x{rv:X8}");

            // Marshal function list structure
            return Marshal.PtrToStructure<CK_FUNCTION_LIST>(functionListPtr);
        }

        /// <summary>
        /// Delegate for C_GetFunctionList.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint C_GetFunctionListDelegate(ref IntPtr ppFunctionList);
    }
}
