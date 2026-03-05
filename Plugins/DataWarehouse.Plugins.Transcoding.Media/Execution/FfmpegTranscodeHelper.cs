namespace DataWarehouse.Plugins.Transcoding.Media.Execution;

/// <summary>
/// Helper class providing common FFmpeg execution logic for codec strategies.
/// Encapsulates the pattern of "try FFmpeg execution, fall back to package generation".
/// </summary>
public static class FfmpegTranscodeHelper
{
    /// <summary>
    /// Attempts to execute FFmpeg transcoding directly. Falls back to package generation if FFmpeg is unavailable.
    /// </summary>
    /// <param name="ffmpegArgs">FFmpeg command-line arguments.</param>
    /// <param name="sourceBytes">Source media bytes to pipe to FFmpeg stdin.</param>
    /// <param name="packageWriter">
    /// Fallback function to write a transcode package when FFmpeg is not available.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A stream containing either:
    /// <list type="bullet">
    /// <item><description>Actual transcoded output from FFmpeg (when available)</description></item>
    /// <item><description>A transcode package with FFmpeg args + metadata (when FFmpeg is not installed)</description></item>
    /// </list>
    /// </returns>
    public static async Task<Stream> ExecuteOrPackageAsync(
        string ffmpegArgs,
        byte[] sourceBytes,
        Func<Task<Stream>> packageWriter,
        CancellationToken cancellationToken = default)
    {
        // Try to execute FFmpeg directly if available
        try
        {
            var executor = new FfmpegExecutor();
            if (executor.IsAvailable)
            {
                var result = await executor.ExecuteAsync(
                    ffmpegArgs,
                    sourceBytes,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    // Return actual transcoded output from FFmpeg
                    return new MemoryStream(result.OutputData);
                }

                // Cat 15 (finding 1063): log FFmpeg execution failure so callers can distinguish
                // real output from fallback package output.
                System.Diagnostics.Trace.TraceWarning(
                    $"[FfmpegTranscodeHelper] FFmpeg execution failed (non-fatal); falling back to package generation.");
            }
        }
        catch (FfmpegNotFoundException ex)
        {
            // Cat 15 (finding 1063): log so callers know FFmpeg is not installed.
            System.Diagnostics.Trace.TraceInformation(
                $"[FfmpegTranscodeHelper] FFmpeg not found ({ex.Message}); using package generation fallback.");
        }

        // Fallback: generate transcode package
        return await packageWriter().ConfigureAwait(false);
    }
}
