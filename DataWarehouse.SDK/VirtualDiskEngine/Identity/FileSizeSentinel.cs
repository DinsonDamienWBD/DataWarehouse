using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Validates that a VDE file's actual size matches the expected size recorded in the superblock.
/// Detects truncation and unexpected extension of VDE files, which may indicate tampering,
/// storage corruption, or incomplete writes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity (VTMP-03, VTMP-05)")]
public static class FileSizeSentinel
{
    /// <summary>
    /// Validates that the actual file size matches the expected file size.
    /// </summary>
    /// <param name="actualFileSize">The actual file size in bytes.</param>
    /// <param name="expectedFileSize">The expected file size in bytes from the superblock.</param>
    /// <returns>True if the sizes match exactly; false otherwise.</returns>
    public static bool Validate(long actualFileSize, long expectedFileSize)
    {
        return actualFileSize == expectedFileSize;
    }

    /// <summary>
    /// Validates that the actual file size matches the <see cref="SuperblockV2.ExpectedFileSize"/>.
    /// </summary>
    /// <param name="actualFileSize">The actual file size in bytes.</param>
    /// <param name="superblock">The superblock containing the expected file size.</param>
    /// <returns>True if the sizes match exactly; false otherwise.</returns>
    public static bool Validate(long actualFileSize, in SuperblockV2 superblock)
    {
        return actualFileSize == superblock.ExpectedFileSize;
    }

    /// <summary>
    /// Validates the file size against the superblock, throwing <see cref="VdeTamperDetectedException"/>
    /// if there is a mismatch. The exception message includes the expected size, actual size,
    /// and the byte delta for diagnostic purposes.
    /// </summary>
    /// <param name="actualFileSize">The actual file size in bytes.</param>
    /// <param name="superblock">The superblock containing the expected file size.</param>
    /// <exception cref="VdeTamperDetectedException">The file size does not match the expected value.</exception>
    public static void ValidateOrThrow(long actualFileSize, in SuperblockV2 superblock)
    {
        if (actualFileSize != superblock.ExpectedFileSize)
        {
            long delta = actualFileSize - superblock.ExpectedFileSize;
            throw new VdeTamperDetectedException(
                $"File size sentinel failed: expected {superblock.ExpectedFileSize} bytes but file is " +
                $"{actualFileSize} bytes (delta: {delta} bytes). " +
                "The VDE file may have been truncated or extended.");
        }
    }

    /// <summary>
    /// Validates the file size at the specified path against the superblock's expected size.
    /// </summary>
    /// <param name="filePath">Path to the VDE file.</param>
    /// <param name="superblock">The superblock containing the expected file size.</param>
    /// <exception cref="VdeTamperDetectedException">The file size does not match the expected value.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static void ValidateOrThrow(string filePath, in SuperblockV2 superblock)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("VDE file not found.", filePath);

        ValidateOrThrow(fileInfo.Length, in superblock);
    }
}
