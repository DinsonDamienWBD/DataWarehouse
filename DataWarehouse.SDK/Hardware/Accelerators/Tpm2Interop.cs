using DataWarehouse.SDK.Contracts;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// Platform-specific TPM 2.0 interop layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides low-level access to TPM 2.0 devices via platform-specific APIs:
    /// <list type="bullet">
    /// <item><description><strong>Windows</strong>: TPM Base Services (TBS) API via Tbsi.dll</description></item>
    /// <item><description><strong>Linux</strong>: Direct /dev/tpmrm0 device file access</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The TPM Resource Manager (tpmrm0 on Linux, TBS on Windows) handles:
    /// <list type="bullet">
    /// <item><description>Session management and context swapping</description></item>
    /// <item><description>Resource allocation and cleanup</description></item>
    /// <item><description>Access control and multi-process coordination</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Windows TBS</strong>: Tbsi.dll provides managed access to TPM 2.0. Applications
    /// create a TBS context, submit TPM commands, and receive responses. TBS handles all
    /// low-level TPM communication and resource management.
    /// </para>
    /// <para>
    /// <strong>Linux /dev/tpmrm0</strong>: The kernel TPM Resource Manager device provides
    /// similar functionality via standard file I/O. Applications open the device, write
    /// TPM command buffers, and read response buffers.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 35: TPM2 platform interop (HW-03)")]
    internal static partial class Tpm2Interop
    {
        // ==================== Windows TBS API ====================

        /// <summary>
        /// Creates a TBS context for TPM 2.0 access on Windows.
        /// </summary>
        /// <param name="contextParams">TBS context parameters (version = 2 for TPM 2.0).</param>
        /// <param name="context">Output: TBS context handle.</param>
        /// <returns>TBS result code (0 = success).</returns>
        [LibraryImport("Tbsi.dll", EntryPoint = "Tbsi_Context_Create")]
        internal static partial uint TbsiContextCreate(ref TBS_CONTEXT_PARAMS2 contextParams, out IntPtr context);

        /// <summary>
        /// Closes a TBS context on Windows.
        /// </summary>
        /// <param name="context">TBS context handle to close.</param>
        /// <returns>TBS result code.</returns>
        [LibraryImport("Tbsi.dll", EntryPoint = "Tbsip_Context_Close")]
        internal static partial uint TbsipContextClose(IntPtr context);

        /// <summary>
        /// Submits a TPM command to the TPM via TBS on Windows.
        /// </summary>
        /// <param name="context">TBS context handle.</param>
        /// <param name="locality">TPM locality (typically 0).</param>
        /// <param name="priority">Command priority.</param>
        /// <param name="commandBuffer">TPM command buffer.</param>
        /// <param name="commandSize">Size of command buffer.</param>
        /// <param name="responseBuffer">Buffer to receive TPM response.</param>
        /// <param name="responseSize">In: size of response buffer. Out: actual response size.</param>
        /// <returns>TBS result code.</returns>
        [LibraryImport("Tbsi.dll", EntryPoint = "Tbsip_Submit_Command")]
        internal static partial uint TbsipSubmitCommand(
            IntPtr context,
            uint locality,
            uint priority,
            byte[] commandBuffer,
            uint commandSize,
            byte[] responseBuffer,
            ref uint responseSize);

        /// <summary>
        /// TBS context parameters for TPM 2.0.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TBS_CONTEXT_PARAMS2
        {
            /// <summary>TBS context version. Use 2 for TPM 2.0.</summary>
            public uint Version;
        }

        // ==================== Linux TPM Device ====================

        /// <summary>
        /// Linux TPM Resource Manager device path.
        /// </summary>
        internal static class LinuxTpmAccess
        {
            /// <summary>
            /// TPM Resource Manager device path on Linux.
            /// </summary>
            /// <remarks>
            /// The /dev/tpmrm0 device provides managed access to TPM 2.0 on Linux.
            /// It handles resource management, session context swapping, and access control.
            /// Applications open this device with read/write access and perform I/O via
            /// standard read()/write() system calls.
            /// </remarks>
            internal const string TpmDevice = "/dev/tpmrm0";

            /// <summary>
            /// Checks if TPM 2.0 is available on Linux.
            /// </summary>
            /// <returns>True if /dev/tpmrm0 exists and is accessible.</returns>
            internal static bool IsAvailable()
            {
                return File.Exists(TpmDevice);
            }
        }

        // ==================== Detection Helper ====================

        /// <summary>
        /// Detects whether TPM 2.0 is available on the current platform.
        /// </summary>
        /// <returns>True if TPM 2.0 is present and accessible.</returns>
        /// <remarks>
        /// <para>
        /// Detection strategy:
        /// <list type="bullet">
        /// <item><description><strong>Windows</strong>: Attempt to create a TBS context. If successful, TPM 2.0 is present.</description></item>
        /// <item><description><strong>Linux</strong>: Check if /dev/tpmrm0 exists.</description></item>
        /// <item><description><strong>Other platforms</strong>: Return false (TPM 2.0 not supported).</description></item>
        /// </list>
        /// </para>
        /// </remarks>
        internal static bool IsTpm2Available()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Try to create TBS context
                var p = new TBS_CONTEXT_PARAMS2 { Version = 2 };
                uint result = TbsiContextCreate(ref p, out IntPtr ctx);
                if (result == 0 && ctx != IntPtr.Zero)
                {
                    TbsipContextClose(ctx);
                    return true;
                }
                return false;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return LinuxTpmAccess.IsAvailable();
            }

            return false;
        }
    }
}
