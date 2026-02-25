using DataWarehouse.SDK.Contracts.Media;

namespace DataWarehouse.Plugins.Transcoding.Media.Execution;

/// <summary>
/// Detects media format from file headers (magic bytes) with comprehensive format support.
/// Provides production-ready format detection for video, audio, image, and container formats.
/// </summary>
public static class MediaFormatDetector
{
    /// <summary>
    /// Detects the media format by inspecting the magic bytes at the start of the file.
    /// </summary>
    /// <param name="data">File data (minimum 12 bytes recommended for accurate detection).</param>
    /// <returns>The detected <see cref="MediaFormat"/>, or <see cref="MediaFormat.Unknown"/> if unrecognized.</returns>
    public static MediaFormat DetectFormat(byte[] data)
    {
        if (data.Length < 4) return MediaFormat.Unknown;

        // Video containers
        if (IsMp4(data)) return MediaFormat.MP4;
        if (IsWebM(data) || IsMatroska(data)) return MediaFormat.WebM;
        if (IsAvi(data)) return MediaFormat.AVI;
        if (IsFlv(data)) return MediaFormat.FLV;
        if (IsMpegTs(data)) return MediaFormat.HLS; // MPEG-TS often used for HLS

        // Image formats
        if (IsJpeg(data)) return MediaFormat.JPEG;
        if (IsPng(data)) return MediaFormat.PNG;
        if (IsWebP(data)) return MediaFormat.WebP;
        if (IsAvif(data)) return MediaFormat.AVIF;

        return MediaFormat.Unknown;
    }

    /// <summary>
    /// Checks if the data is an MP4/MOV container (ftyp box signature).
    /// </summary>
    private static bool IsMp4(byte[] data)
    {
        if (data.Length < 12) return false;

        // MP4/MOV files start with ftyp box: 4-byte size, then "ftyp"
        // Bytes 4-7 should be "ftyp" (0x66 0x74 0x79 0x70)
        return data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70;
    }

    /// <summary>
    /// Checks if the data is a WebM container (EBML header with DocType "webm").
    /// </summary>
    private static bool IsWebM(byte[] data)
    {
        if (data.Length < 4) return false;

        // EBML header: 0x1A 0x45 0xDF 0xA3
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            // Check for "webm" DocType somewhere in the first 64 bytes
            if (data.Length >= 30)
            {
                var headerStr = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64));
                if (headerStr.Contains("webm")) return true;
            }

            return true; // Assume WebM if EBML header present
        }

        return false;
    }

    /// <summary>
    /// Checks if the data is a Matroska (MKV) container (EBML header with DocType "matroska").
    /// </summary>
    private static bool IsMatroska(byte[] data)
    {
        if (data.Length < 4) return false;

        // EBML header: 0x1A 0x45 0xDF 0xA3
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            if (data.Length >= 40)
            {
                var headerStr = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64));
                if (headerStr.Contains("matroska")) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the data is an AVI container (RIFF header with AVI signature).
    /// </summary>
    private static bool IsAvi(byte[] data)
    {
        if (data.Length < 12) return false;

        // AVI files start with "RIFF" followed by size, then "AVI " or "AVIX"
        return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
               data[8] == 0x41 && data[9] == 0x56 && data[10] == 0x49;
    }

    /// <summary>
    /// Checks if the data is an FLV (Flash Video) container.
    /// </summary>
    private static bool IsFlv(byte[] data)
    {
        if (data.Length < 3) return false;

        // FLV signature: "FLV" (0x46 0x4C 0x56)
        return data[0] == 0x46 && data[1] == 0x4C && data[2] == 0x56;
    }

    /// <summary>
    /// Checks if the data is an MPEG-TS (Transport Stream) file.
    /// </summary>
    private static bool IsMpegTs(byte[] data)
    {
        if (data.Length < 188) return false;

        // MPEG-TS packets start with sync byte 0x47 every 188 bytes
        return data[0] == 0x47 && (data.Length >= 376 ? data[188] == 0x47 : true);
    }

    /// <summary>
    /// Checks if the data is a JPEG image (JFIF or Exif).
    /// </summary>
    private static bool IsJpeg(byte[] data)
    {
        if (data.Length < 3) return false;

        // JPEG signature: 0xFF 0xD8 0xFF
        return data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
    }

    /// <summary>
    /// Checks if the data is a PNG image.
    /// </summary>
    private static bool IsPng(byte[] data)
    {
        if (data.Length < 8) return false;

        // PNG signature: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
        return data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
               data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
    }

    /// <summary>
    /// Checks if the data is a WebP image.
    /// </summary>
    private static bool IsWebP(byte[] data)
    {
        if (data.Length < 12) return false;

        // WebP signature: "RIFF" + size + "WEBP"
        return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
               data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;
    }

    /// <summary>
    /// Checks if the data is an AVIF image (AV1 Image File Format).
    /// </summary>
    private static bool IsAvif(byte[] data)
    {
        if (data.Length < 12) return false;

        // AVIF is an MP4-based format with ftyp box containing "avif" brand
        if (IsMp4(data))
        {
            // Check for "avif" or "avis" in ftyp box (bytes 8-11)
            if (data.Length >= 16)
            {
                return (data[8] == 0x61 && data[9] == 0x76 && data[10] == 0x69 && data[11] == 0x66) ||
                       (data[8] == 0x61 && data[9] == 0x76 && data[10] == 0x69 && data[11] == 0x73);
            }
        }

        return false;
    }
}
