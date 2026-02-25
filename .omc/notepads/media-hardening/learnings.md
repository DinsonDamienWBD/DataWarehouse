# Media/Transcoding Hardening - Learnings

## Architectural Patterns Discovered

### 1. Package Generation Pattern
**Pattern**: Strategies generate structured packages containing FFmpeg arguments and metadata when FFmpeg is unavailable.

**Strength**: Allows deferred execution - packages can be transferred to systems with FFmpeg installed.

**Limitation**: Not "real" transcoding until FFmpeg executes.

**Learning**: This is actually sophisticated for distributed/offline scenarios, but task required real execution readiness.

### 2. TranscodeOptions Extension via CustomMetadata
**Challenge**: Adding new parameters (watermarking, HDR, rotation) without breaking API.

**Solution**: Added `IReadOnlyDictionary<string, string>? CustomMetadata` parameter to `TranscodeOptions`.

**Benefit**:
- Backward compatible (optional parameter)
- Extensible for future features
- Type-safe at runtime via TryGetValue
- No complex inheritance hierarchy

### 3. Magic Byte Format Detection
**Pattern**: Centralized `MediaFormatDetector` class with static methods checking first 12-64 bytes.

**Coverage**:
- Video: MP4/MOV (ftyp box), WebM/MKV (EBML), AVI (RIFF), FLV, MPEG-TS
- Image: JPEG, PNG, WebP, AVIF

**Learning**: Format detection should be centralized, not duplicated across strategies.

### 4. Progress Reporting via Callback
**Pattern**: `Action<double>?` callback passed through `TranscodeOptions` and `FfmpegExecutor`.

**Implementation**: Parse FFmpeg stderr for `time=HH:MM:SS.ms` pattern.

**Limitation**: Percentage calculation requires knowing total duration upfront.

**Learning**: Reporting raw seconds is safer than calculating percentages without metadata.

## FFmpeg Integration Best Practices

### 1. Pipe-Based I/O
**Pattern**: `pipe:0` for stdin, `pipe:1` for stdout.

**Benefit**: No temp file management, works in sandboxed environments.

**Requirement**: Async stdin write + async stdout read to prevent deadlocks.

### 2. Stderr Capture for Progress
**Pattern**: `BeginErrorReadLine()` with event handler capturing each line.

**Learning**: FFmpeg writes all progress/diagnostics to stderr, not stdout.

### 3. Process Cleanup
**Pattern**: Try/catch around `process.Kill()` because process may already exit.

**Learning**: Empty catch blocks are legitimate here - process state is unpredictable during timeout/cancellation.

### 4. Two-Pass Encoding
**Pattern**: Pass 1 writes stats to file, pass 2 uses stats for bitrate optimization.

**Challenge**: Requires chaining commands with `&&` in arguments string.

**Learning**: Two-pass significantly improves quality for VP9/AV1 but doubles encoding time.

## Input Validation Patterns

### 1. Stream Validation
```csharp
if (inputStream == null || !inputStream.CanRead)
    throw new ArgumentException("Input stream must be readable.");
```

### 2. Size Limits
```csharp
if (sourceBytes.Length > 500_000_000) // 500 MB
    throw new ArgumentException("File too large for in-memory processing.");
```

**Learning**: 500 MB is safe for in-memory image processing; video should stream.

### 3. Range Validation
```csharp
if (targetResolution.Width < 64 || targetResolution.Height < 64)
    throw new ArgumentException("Resolution must be at least 64x64.");
```

**Learning**: FFmpeg has practical limits; validate early to provide clear errors.

## HDR Video Handling

### 1. Color Spaces
- **BT.709**: SDR (HD/Full HD)
- **BT.2020**: HDR (UHD/4K/8K)

### 2. Transfer Functions
- **smpte2084**: PQ (Perceptual Quantizer) - HDR10
- **arib-std-b67**: HLG (Hybrid Log-Gamma) - BBC/NHK HDR
- **bt2020-10**: 10-bit without HDR metadata

### 3. Tone Mapping Algorithms
- **Hable**: Filmic curve, preserves highlights
- **Mobius**: Linear highlights, natural roll-off
- **Reinhard**: Classic tone mapping, can crush shadows

**Learning**: HDR to SDR requires `zscale` filter chain with format conversions.

## Codec-Specific Learnings

### H.264
- **10-bit**: `high10` profile + `yuv420p10le` pixel format
- **Hardware 10-bit**: `p010le` for NVENC/QSV
- **Profiles**: baseline (mobile) < main (streaming) < high (broadcast) < high10 (10-bit)

### H.265
- **Default 10-bit**: `yuv420p10le` is production standard for HEVC
- **HDR10 metadata**: Requires master display + max CLL parameters
- **Tag**: `hvc1` tag for Apple/browser compatibility

### VP9
- **Two-pass recommended**: Quality improvement is significant
- **Tile columns**: `2^N` columns for parallel encoding
- **Row multi-threading**: `-row-mt 1` for CPU efficiency

### AV1
- **SVT-AV1 vs libaom**: SVT is 5-20x faster, competitive quality
- **Film grain**: Preserves perceptual quality at lower bitrates
- **Preset range**: SVT 0-13, libaom cpu-used 0-8

## Image Processing Insights

### JPEG
- **Quality range**: 1-100 (IJG scale)
- **CRF mapping**: Quality < 50 uses different scale factor formula
- **Progressive**: Multi-scan for web delivery (larger file, better UX)
- **Subsampling**: 4:4:4 (best) vs 4:2:0 (smallest) affects quality

### PNG
- **Lossless**: Compression level affects speed/size, not quality
- **Interlacing**: Adam7 for progressive rendering
- **CRC32**: Required for chunk integrity per PNG spec

## Streaming (HLS/DASH) Insights

### HLS
- **Segment duration**: 6 seconds is Apple recommendation (live/VOD balance)
- **Variant ladder**: 360p/480p/720p/1080p with specific bitrates
- **m3u8 format**: Master playlist + variant playlists

### DASH
- **MPD**: XML manifest (vs HLS's text-based m3u8)
- **Timescale**: 90,000 ticks/second is standard for video
- **Segment timeline**: Enables precise seeking

## SDK Extension Pattern

**Challenge**: Extend `TranscodeOptions` without breaking existing code.

**Solution**: Added optional parameter `IReadOnlyDictionary<string, string>? CustomMetadata = null`.

**Benefits**:
- Existing calls compile without changes
- New features opt-in via metadata keys
- No version fragmentation
- Documentation-driven (key names are strings)

**Trade-off**: Not strongly typed, but extremely flexible.

## Production Readiness Criteria Met

1. ✓ Real process execution (not just package generation)
2. ✓ Comprehensive parameter support (10-bit, HDR, watermarking, image ops)
3. ✓ Input validation everywhere
4. ✓ Memory management (size limits, streaming patterns)
5. ✓ Timeout protection
6. ✓ Progress reporting
7. ✓ Error handling with clear messages
8. ✓ Format detection via magic bytes
9. ✓ No placeholders/stubs/TODOs
10. ✓ Build verification passed

## Key Takeaway

**Package generation is clever for distributed systems, but production-ready means real execution capability.** The strategies now support both:
1. **Package mode**: When FFmpeg unavailable (transfer/offline)
2. **Execution mode**: When FFmpeg available (immediate transcoding)

This dual-mode approach is **actually better** than pure execution because it supports heterogeneous environments (e.g., edge device captures, cloud transcodes).
