using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.DeviceLevel;

#region GF(2^8) Arithmetic

/// <summary>
/// Pre-computed Galois Field GF(2^8) arithmetic using primitive polynomial
/// 0x11D (x^8 + x^4 + x^3 + x^2 + 1). All operations are O(1) via lookup tables.
/// Thread-safe: all state is static readonly.
/// </summary>
internal static class GaloisField
{
    /// <summary>Primitive polynomial for GF(2^8): x^8 + x^4 + x^3 + x^2 + 1 = 0x11D.</summary>
    private const int Polynomial = 0x11D;

    /// <summary>Exponential (antilog) table: ExpTable[i] = alpha^i mod polynomial.</summary>
    internal static readonly byte[] ExpTable = new byte[512];

    /// <summary>Logarithm table: LogTable[x] = i where alpha^i = x. LogTable[0] is undefined.</summary>
    internal static readonly byte[] LogTable = new byte[256];

    static GaloisField()
    {
        // Generate exp and log tables
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= Polynomial;
        }

        // Duplicate for wraparound (avoids modulo in multiply)
        for (int i = 255; i < 512; i++)
        {
            ExpTable[i] = ExpTable[i - 255];
        }
    }

    /// <summary>
    /// Multiplies two elements in GF(2^8) using log/exp tables.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return ExpTable[LogTable[a] + LogTable[b]];
    }

    /// <summary>
    /// Divides element <paramref name="a"/> by element <paramref name="b"/> in GF(2^8).
    /// </summary>
    /// <exception cref="DivideByZeroException">Thrown when <paramref name="b"/> is zero.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Divide(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("Division by zero in GF(2^8).");
        if (a == 0) return 0;
        int diff = LogTable[a] - LogTable[b];
        if (diff < 0) diff += 255;
        return ExpTable[diff];
    }

    /// <summary>
    /// Returns the multiplicative inverse of <paramref name="a"/> in GF(2^8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Inverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException("Zero has no inverse in GF(2^8).");
        return ExpTable[255 - LogTable[a]];
    }

    /// <summary>
    /// Raises <paramref name="a"/> to the power <paramref name="n"/> in GF(2^8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Power(byte a, int n)
    {
        if (a == 0) return 0;
        if (n == 0) return 1;
        int logA = LogTable[a];
        int result = (logA * n) % 255;
        if (result < 0) result += 255;
        return ExpTable[result];
    }
}

#endregion

#region Reed-Solomon Strategy

/// <summary>
/// Device-level Reed-Solomon erasure coding strategy using Vandermonde matrix encoding
/// in GF(2^8). Supports arbitrary k+m configurations where k+m &lt;= 255.
/// </summary>
/// <remarks>
/// <para>
/// The encoding matrix is a (k+m) x k Vandermonde matrix where the first k rows
/// form an identity matrix (data passes through unchanged) and the last m rows
/// contain Vandermonde coefficients for parity generation.
/// </para>
/// <para>
/// Decoding selects any k available rows from the encoding matrix, inverts the
/// resulting k x k submatrix, and multiplies by the available chunks to recover
/// all data chunks. Decoding matrices are cached by availability pattern.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public sealed class DeviceReedSolomonStrategy : RaidStrategyBase, IDeviceErasureCodingStrategy
{
    private readonly int _dataDevices;
    private readonly int _parityDevices;
    private readonly byte[,] _encodingMatrix;
    private readonly ConcurrentDictionary<string, byte[,]> _decodingMatrixCache = new();

    /// <inheritdoc/>
    public override string StrategyId => $"device-rs-{_dataDevices}+{_parityDevices}";

    /// <inheritdoc/>
    public override string StrategyName => $"Device Reed-Solomon ({_dataDevices}+{_parityDevices})";

    /// <inheritdoc/>
    public override int RaidLevel => 60; // Custom: device-level RS

    /// <inheritdoc/>
    public override string Category => "DeviceErasureCoding";

    /// <inheritdoc/>
    public override int MinimumDisks => _dataDevices + _parityDevices;

    /// <inheritdoc/>
    public override int FaultTolerance => _parityDevices;

    /// <inheritdoc/>
    public override double StorageEfficiency => _dataDevices / (double)(_dataDevices + _parityDevices);

    /// <inheritdoc/>
    public override double ReadPerformanceMultiplier => _dataDevices * 0.95;

    /// <inheritdoc/>
    public override double WritePerformanceMultiplier => _dataDevices * 0.7;

    /// <summary>
    /// Gets the erasure coding scheme (always <see cref="ErasureCodingScheme.ReedSolomon"/>).
    /// </summary>
    public ErasureCodingScheme Scheme => ErasureCodingScheme.ReedSolomon;

    /// <summary>
    /// Gets the maximum number of simultaneous device failures tolerable (m).
    /// </summary>
    public int MaxTolerableFailures => _parityDevices;

    /// <summary>
    /// Initializes a new device-level Reed-Solomon strategy.
    /// </summary>
    /// <param name="dataDevices">Number of data devices (k). Must be at least 1.</param>
    /// <param name="parityDevices">Number of parity devices (m). Must be at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when k or m is less than 1 or k+m exceeds 255 (GF(2^8) limit).
    /// </exception>
    public DeviceReedSolomonStrategy(int dataDevices, int parityDevices)
    {
        if (dataDevices < 1)
            throw new ArgumentOutOfRangeException(nameof(dataDevices), "Must have at least 1 data device.");
        if (parityDevices < 1)
            throw new ArgumentOutOfRangeException(nameof(parityDevices), "Must have at least 1 parity device.");
        if (dataDevices + parityDevices > 255)
            throw new ArgumentOutOfRangeException(nameof(dataDevices), "Total devices (k+m) cannot exceed 255 for GF(2^8).");

        _dataDevices = dataDevices;
        _parityDevices = parityDevices;
        _encodingMatrix = GenerateVandermondeMatrix(dataDevices + parityDevices, dataDevices);
    }

    /// <summary>Creates an 8+3 configuration: 72.7% efficiency, tolerates 3 failures.</summary>
    public static DeviceReedSolomonStrategy Create8Plus3() => new(8, 3);

    /// <summary>Creates a 16+4 configuration: 80% efficiency, tolerates 4 failures.</summary>
    public static DeviceReedSolomonStrategy Create16Plus4() => new(16, 4);

    #region IDeviceErasureCodingStrategy

    /// <inheritdoc/>
    public Task<CompoundBlockDevice> CreateCompoundDeviceAsync(
        ErasureCodingConfiguration config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        ValidateConfig(config);

        var compoundConfig = new CompoundDeviceConfiguration(DeviceLayoutType.Parity)
        {
            StripeSizeBlocks = config.StripeSizeBlocks,
            ParityDeviceCount = config.ParityDeviceCount
        };

        var compound = new CompoundBlockDevice(config.Devices, compoundConfig);
        return Task.FromResult(compound);
    }

    /// <inheritdoc/>
    public Task<ErasureCodingHealth> CheckHealthAsync(
        ErasureCodingConfiguration config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        int available = 0;
        int failed = 0;
        for (int i = 0; i < config.Devices.Count; i++)
        {
            if (config.Devices[i].IsOnline)
                available++;
            else
                failed++;
        }

        var health = new ErasureCodingHealth
        {
            IsHealthy = failed == 0,
            AvailableDevices = available,
            MinimumRequiredDevices = config.DataDeviceCount,
            TotalDevices = config.Devices.Count,
            FailedDevices = failed,
            CanRecoverData = available >= config.DataDeviceCount,
            StorageEfficiency = config.DataDeviceCount / (double)config.Devices.Count
        };

        return Task.FromResult(health);
    }

    /// <inheritdoc/>
    public async Task RebuildDeviceAsync(
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        ErasureCodingConfiguration config,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(config);

        if (failedDeviceIndex < 0 || failedDeviceIndex >= config.Devices.Count)
            throw new ArgumentOutOfRangeException(nameof(failedDeviceIndex));

        // Check we have enough surviving devices
        int availableCount = 0;
        var deviceAvailable = new bool[config.Devices.Count];
        for (int i = 0; i < config.Devices.Count; i++)
        {
            bool isAvail = i != failedDeviceIndex && config.Devices[i].IsOnline;
            deviceAvailable[i] = isAvail;
            if (isAvail) availableCount++;
        }

        if (availableCount < _dataDevices)
            throw new InvalidOperationException(
                $"Cannot rebuild: only {availableCount} devices available, need at least {_dataDevices}.");

        int blockSize = replacement.BlockSize;
        long totalStripes = replacement.BlockCount;
        const int batchSize = 256;
        long completedStripes = 0;

        for (long stripe = 0; stripe < totalStripes; stripe += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            long batchEnd = Math.Min(stripe + batchSize, totalStripes);
            for (long s = stripe; s < batchEnd; s++)
            {
                // Decode the full data for this stripe
                int dataLength = _dataDevices * blockSize;
                var decoded = await DecodeStripeAsync(config.Devices, deviceAvailable, s, dataLength, ct)
                    .ConfigureAwait(false);

                // Re-encode to get the specific chunk for the failed device
                var allChunks = EncodeStripeToChunks(decoded, blockSize);

                // Write the failed device's chunk to the replacement
                if (failedDeviceIndex < allChunks.Length && allChunks[failedDeviceIndex].Length > 0)
                {
                    await replacement.WriteBlockAsync(s, allChunks[failedDeviceIndex], ct)
                        .ConfigureAwait(false);
                }
            }

            completedStripes = batchEnd;
            progress?.Report((double)completedStripes / totalStripes);
        }
    }

    /// <inheritdoc/>
    public async Task EncodeStripeAsync(
        ReadOnlyMemory<byte> data,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        long stripeOffset,
        CancellationToken ct = default)
    {
        if (devices.Count != _dataDevices + _parityDevices)
            throw new ArgumentException(
                $"Expected {_dataDevices + _parityDevices} devices, got {devices.Count}.", nameof(devices));

        int blockSize = devices[0].BlockSize;
        int expectedDataLength = _dataDevices * blockSize;

        // Pad data if needed
        byte[] paddedData;
        if (data.Length >= expectedDataLength)
        {
            paddedData = data.Slice(0, expectedDataLength).ToArray();
        }
        else
        {
            paddedData = new byte[expectedDataLength];
            data.Span.CopyTo(paddedData.AsSpan());
        }

        // Encode to get all chunks (k data + m parity)
        var chunks = EncodeStripeToChunks(paddedData.AsMemory(), blockSize);

        // Write all chunks in parallel
        var writeTasks = new Task[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            writeTasks[i] = devices[i].WriteBlockAsync(stripeOffset, chunks[i], ct);
        }

        await Task.WhenAll(writeTasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ReadOnlyMemory<byte>> DecodeStripeAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        bool[] deviceAvailable,
        long stripeOffset,
        int dataLength,
        CancellationToken ct = default)
    {
        int totalDevices = _dataDevices + _parityDevices;
        if (devices.Count != totalDevices)
            throw new ArgumentException(
                $"Expected {totalDevices} devices, got {devices.Count}.", nameof(devices));
        if (deviceAvailable.Length != totalDevices)
            throw new ArgumentException(
                $"Expected {totalDevices} availability flags, got {deviceAvailable.Length}.", nameof(deviceAvailable));

        int blockSize = devices[0].BlockSize;

        // Count available devices
        int availCount = 0;
        for (int i = 0; i < totalDevices; i++)
        {
            if (deviceAvailable[i]) availCount++;
        }

        if (availCount < _dataDevices)
            throw new InvalidOperationException(
                $"Insufficient devices for decoding: {availCount} available, need {_dataDevices}.");

        // Fast path: all data devices available
        bool allDataAvailable = true;
        for (int i = 0; i < _dataDevices; i++)
        {
            if (!deviceAvailable[i])
            {
                allDataAvailable = false;
                break;
            }
        }

        if (allDataAvailable)
        {
            var result = new byte[dataLength];
            var readTasks = new Task[_dataDevices];
            var buffers = new byte[_dataDevices][];

            for (int i = 0; i < _dataDevices; i++)
            {
                buffers[i] = new byte[blockSize];
                readTasks[i] = devices[i].ReadBlockAsync(stripeOffset, buffers[i].AsMemory(), ct);
            }

            await Task.WhenAll(readTasks).ConfigureAwait(false);

            for (int i = 0; i < _dataDevices; i++)
            {
                int offset = i * blockSize;
                int copyLen = Math.Min(blockSize, dataLength - offset);
                if (copyLen > 0)
                    buffers[i].AsSpan(0, copyLen).CopyTo(result.AsSpan(offset));
            }

            return result.AsMemory(0, dataLength);
        }

        // Degraded path: select any k available devices
        var selectedIndices = new int[_dataDevices];
        int selected = 0;
        for (int i = 0; i < totalDevices && selected < _dataDevices; i++)
        {
            if (deviceAvailable[i])
            {
                selectedIndices[selected++] = i;
            }
        }

        // Read from selected devices
        var selectedChunks = new byte[_dataDevices][];
        var degradedReadTasks = new Task[_dataDevices];
        for (int i = 0; i < _dataDevices; i++)
        {
            selectedChunks[i] = new byte[blockSize];
            degradedReadTasks[i] = devices[selectedIndices[i]].ReadBlockAsync(
                stripeOffset, selectedChunks[i].AsMemory(), ct);
        }

        await Task.WhenAll(degradedReadTasks).ConfigureAwait(false);

        // Build submatrix from encoding matrix rows corresponding to selected devices
        var cacheKey = string.Join(",", selectedIndices);

        if (!_decodingMatrixCache.TryGetValue(cacheKey, out byte[,]? decodingMatrix))
        {
            var subMatrix = new byte[_dataDevices, _dataDevices];
            for (int i = 0; i < _dataDevices; i++)
            {
                for (int j = 0; j < _dataDevices; j++)
                {
                    subMatrix[i, j] = _encodingMatrix[selectedIndices[i], j];
                }
            }

            decodingMatrix = InvertMatrixGF(subMatrix);
            _decodingMatrixCache.TryAdd(cacheKey, decodingMatrix);
        }

        // Multiply inverse matrix by available chunks to recover data
        var decoded = new byte[dataLength];
        for (int i = 0; i < _dataDevices; i++)
        {
            int offset = i * blockSize;
            int copyLen = Math.Min(blockSize, dataLength - offset);
            if (copyLen <= 0) continue;

            for (int j = 0; j < _dataDevices; j++)
            {
                byte coeff = decodingMatrix[i, j];
                if (coeff == 0) continue;

                for (int b = 0; b < copyLen; b++)
                {
                    decoded[offset + b] ^= GaloisField.Multiply(selectedChunks[j][b], coeff);
                }
            }
        }

        return decoded.AsMemory(0, dataLength);
    }

    #endregion

    #region RaidStrategyBase Overrides

    /// <inheritdoc/>
    public override Task WriteAsync(long logicalBlockAddress, byte[] data, CancellationToken ct = default)
    {
        // Device-level RS uses EncodeStripeAsync directly; this is for RAID strategy compatibility
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<byte[]> ReadAsync(long logicalBlockAddress, int length, CancellationToken ct = default)
    {
        // Device-level RS uses DecodeStripeAsync directly; this is for RAID strategy compatibility
        return Task.FromResult(Array.Empty<byte>());
    }

    /// <inheritdoc/>
    public override Task RebuildAsync(int failedDiskIndex, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Device-level RS uses RebuildDeviceAsync directly
        return Task.CompletedTask;
    }

    #endregion

    #region Private Helpers

    private void ValidateConfig(ErasureCodingConfiguration config)
    {
        if (config.Scheme != ErasureCodingScheme.ReedSolomon)
            throw new ArgumentException(
                $"Expected ReedSolomon scheme, got {config.Scheme}.", nameof(config));
        if (config.DataDeviceCount != _dataDevices)
            throw new ArgumentException(
                $"Expected {_dataDevices} data devices, got {config.DataDeviceCount}.", nameof(config));
        if (config.ParityDeviceCount != _parityDevices)
            throw new ArgumentException(
                $"Expected {_parityDevices} parity devices, got {config.ParityDeviceCount}.", nameof(config));
    }

    /// <summary>
    /// Generates a (k+m) x k Vandermonde encoding matrix in GF(2^8).
    /// First k rows form identity (data pass-through), last m rows are Vandermonde coefficients.
    /// </summary>
    private static byte[,] GenerateVandermondeMatrix(int rows, int cols)
    {
        var matrix = new byte[rows, cols];

        // First k rows: identity matrix (data devices store data unchanged)
        for (int i = 0; i < cols; i++)
        {
            matrix[i, i] = 1;
        }

        // Last m rows: Vandermonde coefficients for parity generation
        // Row i (i >= cols): matrix[i, j] = alpha^((i - cols) * j) where alpha is the evaluation point
        for (int i = cols; i < rows; i++)
        {
            int parityIndex = i - cols;
            // Use alpha = parityIndex + 1 as evaluation point (avoid 0)
            byte alpha = (byte)(parityIndex + 1);

            for (int j = 0; j < cols; j++)
            {
                matrix[i, j] = GaloisField.Power(alpha, j);
            }
        }

        return matrix;
    }

    /// <summary>
    /// Encodes data into k data chunks + m parity chunks.
    /// Returns an array of ReadOnlyMemory where index 0..k-1 are data and k..k+m-1 are parity.
    /// </summary>
    private ReadOnlyMemory<byte>[] EncodeStripeToChunks(ReadOnlyMemory<byte> data, int chunkSize)
    {
        int total = _dataDevices + _parityDevices;
        var chunks = new ReadOnlyMemory<byte>[total];

        // Split into k data chunks
        var dataSpan = data.Span;
        for (int i = 0; i < _dataDevices; i++)
        {
            int offset = i * chunkSize;
            var chunk = new byte[chunkSize];
            int copyLen = Math.Min(chunkSize, dataSpan.Length - offset);
            if (copyLen > 0)
                dataSpan.Slice(offset, copyLen).CopyTo(chunk);
            chunks[i] = chunk;
        }

        // Generate m parity chunks using matrix multiplication in GF(2^8)
        for (int p = 0; p < _parityDevices; p++)
        {
            int parityRow = _dataDevices + p;
            var parityChunk = new byte[chunkSize];

            for (int d = 0; d < _dataDevices; d++)
            {
                byte coeff = _encodingMatrix[parityRow, d];
                if (coeff == 0) continue;

                var dataChunkSpan = chunks[d].Span;
                for (int b = 0; b < chunkSize; b++)
                {
                    parityChunk[b] ^= GaloisField.Multiply(dataChunkSpan[b], coeff);
                }
            }

            chunks[parityRow] = parityChunk;
        }

        return chunks;
    }

    /// <summary>
    /// Inverts a square matrix in GF(2^8) using Gauss-Jordan elimination.
    /// </summary>
    private static byte[,] InvertMatrixGF(byte[,] matrix)
    {
        int n = matrix.GetLength(0);
        var augmented = new byte[n, 2 * n];

        // Build [A | I]
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                augmented[i, j] = matrix[i, j];
            }
            augmented[i, n + i] = 1;
        }

        // Gauss-Jordan elimination
        for (int col = 0; col < n; col++)
        {
            // Find pivot
            if (augmented[col, col] == 0)
            {
                bool found = false;
                for (int row = col + 1; row < n; row++)
                {
                    if (augmented[row, col] != 0)
                    {
                        // Swap rows
                        for (int j = 0; j < 2 * n; j++)
                        {
                            (augmented[col, j], augmented[row, j]) = (augmented[row, j], augmented[col, j]);
                        }
                        found = true;
                        break;
                    }
                }

                if (!found)
                    throw new InvalidOperationException(
                        $"Reed-Solomon submatrix is singular at column {col}: cannot invert.");
            }

            // Scale pivot row so pivot becomes 1
            byte pivotInverse = GaloisField.Inverse(augmented[col, col]);
            for (int j = 0; j < 2 * n; j++)
            {
                augmented[col, j] = GaloisField.Multiply(augmented[col, j], pivotInverse);
            }

            // Eliminate column in all other rows
            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                byte factor = augmented[row, col];
                if (factor == 0) continue;

                for (int j = 0; j < 2 * n; j++)
                {
                    augmented[row, j] ^= GaloisField.Multiply(augmented[col, j], factor);
                }
            }
        }

        // Extract right half (inverse)
        var result = new byte[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                result[i, j] = augmented[i, n + j];
            }
        }

        return result;
    }

    #endregion
}

#endregion

#region Fountain Code Strategy

/// <summary>
/// Device-level fountain code (LT / Luby Transform) erasure coding strategy.
/// Uses rateless encoding where each encoded block is the XOR of a random subset
/// of source blocks, selected via Robust Soliton degree distribution.
/// </summary>
/// <remarks>
/// <para>
/// Fountain codes are "rateless": the encoder can produce an arbitrary number of
/// encoded blocks from k source blocks. Each encoded block is the XOR of d randomly
/// selected source blocks, where d follows the Robust Soliton distribution.
/// </para>
/// <para>
/// Decoding uses a peeling decoder: iteratively find encoded blocks of degree 1,
/// recover the corresponding source block, and reduce all other encoded blocks.
/// If peeling stalls, Gaussian elimination over GF(2) completes the decode.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public sealed class DeviceFountainCodeStrategy : RaidStrategyBase, IDeviceErasureCodingStrategy
{
    private readonly int _dataDevices;
    private readonly double _redundancyFactor;
    private readonly int _totalEncodedBlocks;
    private readonly double[] _solitonCdf;

    /// <inheritdoc/>
    public override string StrategyId => $"device-fountain-{_dataDevices}x{_redundancyFactor:F1}";

    /// <inheritdoc/>
    public override string StrategyName => $"Device Fountain Code ({_dataDevices}x{_redundancyFactor:F1})";

    /// <inheritdoc/>
    public override int RaidLevel => 61; // Custom: device-level fountain

    /// <inheritdoc/>
    public override string Category => "DeviceErasureCoding";

    /// <inheritdoc/>
    public override int MinimumDisks => _dataDevices + (int)Math.Ceiling(_dataDevices * (_redundancyFactor - 1.0));

    /// <inheritdoc/>
    public override int FaultTolerance => _totalEncodedBlocks - _dataDevices;

    /// <inheritdoc/>
    public override double StorageEfficiency => _dataDevices / (double)_totalEncodedBlocks;

    /// <inheritdoc/>
    public override double ReadPerformanceMultiplier => _dataDevices * 0.8;

    /// <inheritdoc/>
    public override double WritePerformanceMultiplier => _dataDevices * 0.5;

    /// <summary>
    /// Gets the erasure coding scheme (always <see cref="ErasureCodingScheme.FountainCode"/>).
    /// </summary>
    public ErasureCodingScheme Scheme => ErasureCodingScheme.FountainCode;

    /// <summary>
    /// Gets the maximum number of tolerable failures.
    /// </summary>
    public int MaxTolerableFailures => _totalEncodedBlocks - _dataDevices;

    /// <summary>
    /// Initializes a new device-level fountain code strategy.
    /// </summary>
    /// <param name="dataDevices">Number of source data devices (k). Must be at least 2.</param>
    /// <param name="redundancyFactor">
    /// Ratio of total encoded blocks to source blocks (e.g., 1.5 means 50% overhead).
    /// Must be >= 1.05 to ensure decodability.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when parameters are out of valid range.
    /// </exception>
    public DeviceFountainCodeStrategy(int dataDevices, double redundancyFactor = 1.5)
    {
        if (dataDevices < 2)
            throw new ArgumentOutOfRangeException(nameof(dataDevices), "Must have at least 2 data devices for fountain codes.");
        if (redundancyFactor < 1.05)
            throw new ArgumentOutOfRangeException(nameof(redundancyFactor), "Redundancy factor must be at least 1.05.");

        _dataDevices = dataDevices;
        _redundancyFactor = redundancyFactor;
        _totalEncodedBlocks = (int)Math.Ceiling(dataDevices * redundancyFactor);
        _solitonCdf = ComputeRobustSolitonCdf(dataDevices);
    }

    #region IDeviceErasureCodingStrategy

    /// <inheritdoc/>
    public Task<CompoundBlockDevice> CreateCompoundDeviceAsync(
        ErasureCodingConfiguration config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        ValidateConfig(config);

        // Use Striped layout to distribute encoded blocks round-robin across devices
        var compoundConfig = new CompoundDeviceConfiguration(DeviceLayoutType.Striped)
        {
            StripeSizeBlocks = config.StripeSizeBlocks
        };

        var compound = new CompoundBlockDevice(config.Devices, compoundConfig);
        return Task.FromResult(compound);
    }

    /// <inheritdoc/>
    public Task<ErasureCodingHealth> CheckHealthAsync(
        ErasureCodingConfiguration config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        int available = 0;
        int failed = 0;
        for (int i = 0; i < config.Devices.Count; i++)
        {
            if (config.Devices[i].IsOnline)
                available++;
            else
                failed++;
        }

        // For fountain codes, we need enough encoded blocks to decode
        // (at least k blocks, but peeling may need slightly more)
        int effectiveMinimum = _dataDevices;

        var health = new ErasureCodingHealth
        {
            IsHealthy = failed == 0,
            AvailableDevices = available,
            MinimumRequiredDevices = effectiveMinimum,
            TotalDevices = config.Devices.Count,
            FailedDevices = failed,
            CanRecoverData = available >= effectiveMinimum,
            StorageEfficiency = _dataDevices / (double)config.Devices.Count
        };

        return Task.FromResult(health);
    }

    /// <inheritdoc/>
    public async Task RebuildDeviceAsync(
        int failedDeviceIndex,
        IPhysicalBlockDevice replacement,
        ErasureCodingConfiguration config,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(config);

        if (failedDeviceIndex < 0 || failedDeviceIndex >= config.Devices.Count)
            throw new ArgumentOutOfRangeException(nameof(failedDeviceIndex));

        int blockSize = replacement.BlockSize;
        long totalStripes = replacement.BlockCount;
        const int batchSize = 256;
        long completedStripes = 0;

        var deviceAvailable = new bool[config.Devices.Count];
        for (int i = 0; i < config.Devices.Count; i++)
        {
            deviceAvailable[i] = i != failedDeviceIndex && config.Devices[i].IsOnline;
        }

        for (long stripe = 0; stripe < totalStripes; stripe += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            long batchEnd = Math.Min(stripe + batchSize, totalStripes);
            for (long s = stripe; s < batchEnd; s++)
            {
                // Decode original source data for this stripe
                int dataLength = _dataDevices * blockSize;
                var decoded = await DecodeStripeAsync(config.Devices, deviceAvailable, s, dataLength, ct)
                    .ConfigureAwait(false);

                // Re-encode to get the failed device's block
                var encodedBlocks = FountainEncode(decoded, blockSize);

                if (failedDeviceIndex < encodedBlocks.Length)
                {
                    await replacement.WriteBlockAsync(s, encodedBlocks[failedDeviceIndex], ct)
                        .ConfigureAwait(false);
                }
            }

            completedStripes = batchEnd;
            progress?.Report((double)completedStripes / totalStripes);
        }
    }

    /// <inheritdoc/>
    public async Task EncodeStripeAsync(
        ReadOnlyMemory<byte> data,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        long stripeOffset,
        CancellationToken ct = default)
    {
        if (devices.Count < _totalEncodedBlocks)
            throw new ArgumentException(
                $"Expected at least {_totalEncodedBlocks} devices, got {devices.Count}.", nameof(devices));

        int blockSize = devices[0].BlockSize;

        // Pad data if needed
        int expectedDataLength = _dataDevices * blockSize;
        byte[] paddedData;
        if (data.Length >= expectedDataLength)
        {
            paddedData = data.Slice(0, expectedDataLength).ToArray();
        }
        else
        {
            paddedData = new byte[expectedDataLength];
            data.Span.CopyTo(paddedData.AsSpan());
        }

        // Encode into n = totalEncodedBlocks encoded blocks
        var encodedBlocks = FountainEncode(paddedData.AsMemory(), blockSize);

        // Write encoded blocks round-robin across devices
        int writeCount = Math.Min(encodedBlocks.Length, devices.Count);
        var writeTasks = new Task[writeCount];
        for (int i = 0; i < writeCount; i++)
        {
            writeTasks[i] = devices[i].WriteBlockAsync(stripeOffset, encodedBlocks[i], ct);
        }

        await Task.WhenAll(writeTasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ReadOnlyMemory<byte>> DecodeStripeAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        bool[] deviceAvailable,
        long stripeOffset,
        int dataLength,
        CancellationToken ct = default)
    {
        if (deviceAvailable.Length != devices.Count)
            throw new ArgumentException(
                $"Availability array length ({deviceAvailable.Length}) must match device count ({devices.Count}).",
                nameof(deviceAvailable));

        int blockSize = devices[0].BlockSize;

        // Read available encoded blocks
        var availableBlocks = new List<(int encodedIndex, byte[] data)>();
        var readTasks = new List<(int index, Task<byte[]> task)>();

        for (int i = 0; i < devices.Count; i++)
        {
            if (!deviceAvailable[i]) continue;

            var buffer = new byte[blockSize];
            int idx = i;
            readTasks.Add((idx, ReadDeviceBlock(devices[idx], stripeOffset, buffer, ct)));
        }

        foreach (var (index, task) in readTasks)
        {
            var blockData = await task.ConfigureAwait(false);
            availableBlocks.Add((index, blockData));
        }

        if (availableBlocks.Count < _dataDevices)
            throw new InvalidOperationException(
                $"Insufficient encoded blocks for decoding: {availableBlocks.Count} available, need at least {_dataDevices}.");

        // Decode using peeling decoder with Gaussian elimination fallback
        return FountainDecode(availableBlocks, blockSize, dataLength);
    }

    #endregion

    #region RaidStrategyBase Overrides

    /// <inheritdoc/>
    public override Task WriteAsync(long logicalBlockAddress, byte[] data, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<byte[]> ReadAsync(long logicalBlockAddress, int length, CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<byte>());
    }

    /// <inheritdoc/>
    public override Task RebuildAsync(int failedDiskIndex, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Fountain Encoding/Decoding

    /// <summary>
    /// Returns the source block indices that encoded block <paramref name="encodedIndex"/>
    /// XORs together, determined by the degree from Robust Soliton and a seeded PRNG.
    /// </summary>
    private int[] GetSourceIndices(int encodedIndex)
    {
        var rng = new Random(encodedIndex * 31337 + 7919);
        int degree = SampleDegree(rng);

        // Select `degree` distinct source indices
        var indices = new HashSet<int>();
        while (indices.Count < degree)
        {
            indices.Add(rng.Next(_dataDevices));
        }

        return indices.OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// Samples a degree from the Robust Soliton distribution using the pre-computed CDF.
    /// </summary>
    private int SampleDegree(Random rng)
    {
        double u = rng.NextDouble();
        for (int d = 1; d <= _dataDevices; d++)
        {
            if (u <= _solitonCdf[d])
                return d;
        }
        return 1; // Fallback
    }

    /// <summary>
    /// Encodes source data into <see cref="_totalEncodedBlocks"/> encoded blocks
    /// using fountain (LT) coding.
    /// </summary>
    private ReadOnlyMemory<byte>[] FountainEncode(ReadOnlyMemory<byte> data, int blockSize)
    {
        // Split into k source blocks
        var sourceBlocks = new byte[_dataDevices][];
        var dataSpan = data.Span;
        for (int i = 0; i < _dataDevices; i++)
        {
            sourceBlocks[i] = new byte[blockSize];
            int offset = i * blockSize;
            int copyLen = Math.Min(blockSize, dataSpan.Length - offset);
            if (copyLen > 0)
                dataSpan.Slice(offset, copyLen).CopyTo(sourceBlocks[i]);
        }

        // Generate encoded blocks
        var encoded = new ReadOnlyMemory<byte>[_totalEncodedBlocks];
        for (int i = 0; i < _totalEncodedBlocks; i++)
        {
            var indices = GetSourceIndices(i);
            var block = new byte[blockSize];

            // XOR selected source blocks together
            foreach (int srcIdx in indices)
            {
                for (int b = 0; b < blockSize; b++)
                {
                    block[b] ^= sourceBlocks[srcIdx][b];
                }
            }

            encoded[i] = block;
        }

        return encoded;
    }

    /// <summary>
    /// Decodes fountain-encoded blocks using peeling decoder with Gaussian elimination fallback.
    /// </summary>
    private ReadOnlyMemory<byte> FountainDecode(
        List<(int encodedIndex, byte[] data)> availableBlocks,
        int blockSize,
        int dataLength)
    {
        // Build encoding relationships: for each available encoded block, track its source indices
        var equations = new List<(HashSet<int> sourceIndices, byte[] data)>();
        foreach (var (encodedIndex, data) in availableBlocks)
        {
            var indices = GetSourceIndices(encodedIndex);
            equations.Add((new HashSet<int>(indices), (byte[])data.Clone()));
        }

        // Recovered source blocks
        var recovered = new byte[_dataDevices][];
        var isRecovered = new bool[_dataDevices];
        int recoveredCount = 0;

        // Peeling decoder: iteratively find degree-1 equations
        bool progress = true;
        while (progress && recoveredCount < _dataDevices)
        {
            progress = false;

            for (int e = 0; e < equations.Count; e++)
            {
                var (srcIndices, eqData) = equations[e];
                if (srcIndices.Count == 1)
                {
                    int srcIdx = srcIndices.First();
                    if (isRecovered[srcIdx]) continue;

                    // Recover this source block
                    recovered[srcIdx] = (byte[])eqData.Clone();
                    isRecovered[srcIdx] = true;
                    recoveredCount++;
                    progress = true;

                    // XOR this source block out of all other equations that reference it
                    for (int other = 0; other < equations.Count; other++)
                    {
                        if (other == e) continue;
                        var (otherIndices, otherData) = equations[other];
                        if (otherIndices.Remove(srcIdx))
                        {
                            // XOR the recovered block out
                            for (int b = 0; b < blockSize; b++)
                            {
                                otherData[b] ^= recovered[srcIdx][b];
                            }
                        }
                    }
                }
            }
        }

        // If peeling didn't recover all blocks, fall back to Gaussian elimination over GF(2)
        if (recoveredCount < _dataDevices)
        {
            GaussianEliminationFallback(equations, recovered, isRecovered, blockSize, ref recoveredCount);
        }

        if (recoveredCount < _dataDevices)
            throw new InvalidOperationException(
                $"Fountain decode failed: recovered {recoveredCount}/{_dataDevices} source blocks. " +
                "Insufficient encoded blocks or unfavorable degree distribution.");

        // Assemble result
        var result = new byte[dataLength];
        for (int i = 0; i < _dataDevices; i++)
        {
            int offset = i * blockSize;
            int copyLen = Math.Min(blockSize, dataLength - offset);
            if (copyLen > 0 && recovered[i] != null)
                recovered[i].AsSpan(0, copyLen).CopyTo(result.AsSpan(offset));
        }

        return result.AsMemory(0, dataLength);
    }

    /// <summary>
    /// Gaussian elimination fallback over GF(2) for remaining unrecovered source blocks.
    /// </summary>
    private void GaussianEliminationFallback(
        List<(HashSet<int> sourceIndices, byte[] data)> equations,
        byte[][] recovered,
        bool[] isRecovered,
        int blockSize,
        ref int recoveredCount)
    {
        // Collect unrecovered source indices
        var unrecovered = new List<int>();
        for (int i = 0; i < _dataDevices; i++)
        {
            if (!isRecovered[i]) unrecovered.Add(i);
        }

        if (unrecovered.Count == 0) return;

        // Build binary matrix for remaining equations over remaining unknowns
        var relevantEquations = equations
            .Where(eq => eq.sourceIndices.Count > 0 && eq.sourceIndices.Any(idx => !isRecovered[idx]))
            .ToList();

        if (relevantEquations.Count < unrecovered.Count) return;

        int rows = relevantEquations.Count;
        int cols = unrecovered.Count;
        var indexMap = new Dictionary<int, int>();
        for (int i = 0; i < unrecovered.Count; i++)
        {
            indexMap[unrecovered[i]] = i;
        }

        // Binary coefficient matrix + data RHS
        var coeffMatrix = new bool[rows, cols];
        var rhsData = new byte[rows][];

        for (int r = 0; r < rows; r++)
        {
            var (indices, data) = relevantEquations[r];
            rhsData[r] = (byte[])data.Clone();
            foreach (int idx in indices)
            {
                if (indexMap.TryGetValue(idx, out int colIdx))
                {
                    coeffMatrix[r, colIdx] = true;
                }
            }
        }

        // Forward elimination
        for (int col = 0; col < cols && col < rows; col++)
        {
            // Find pivot
            int pivotRow = -1;
            for (int r = col; r < rows; r++)
            {
                if (coeffMatrix[r, col])
                {
                    pivotRow = r;
                    break;
                }
            }

            if (pivotRow < 0) continue;

            // Swap rows
            if (pivotRow != col)
            {
                for (int j = 0; j < cols; j++)
                {
                    (coeffMatrix[col, j], coeffMatrix[pivotRow, j]) =
                        (coeffMatrix[pivotRow, j], coeffMatrix[col, j]);
                }
                (rhsData[col], rhsData[pivotRow]) = (rhsData[pivotRow], rhsData[col]);
            }

            // Eliminate
            for (int r = 0; r < rows; r++)
            {
                if (r == col || !coeffMatrix[r, col]) continue;

                for (int j = 0; j < cols; j++)
                {
                    coeffMatrix[r, j] ^= coeffMatrix[col, j];
                }
                for (int b = 0; b < blockSize; b++)
                {
                    rhsData[r][b] ^= rhsData[col][b];
                }
            }
        }

        // Back-substitution: extract solutions
        for (int col = 0; col < cols && col < rows; col++)
        {
            if (!coeffMatrix[col, col]) continue;

            int sourceIdx = unrecovered[col];
            recovered[sourceIdx] = rhsData[col];
            isRecovered[sourceIdx] = true;
            recoveredCount++;
        }
    }

    /// <summary>
    /// Computes the CDF of the Robust Soliton distribution for k source blocks.
    /// </summary>
    private static double[] ComputeRobustSolitonCdf(int k)
    {
        // Ideal Soliton distribution
        var rho = new double[k + 1];
        rho[1] = 1.0 / k;
        for (int d = 2; d <= k; d++)
        {
            rho[d] = 1.0 / ((double)d * (d - 1));
        }

        // Robust Soliton: add spike at degree 1 and k/S
        double c = 0.1; // Tuning constant
        double delta = 0.05; // Failure probability
        double S = c * Math.Log(k / delta) * Math.Sqrt(k);

        var tau = new double[k + 1];
        int kOverS = Math.Max(1, (int)Math.Round(k / S));

        for (int d = 1; d < kOverS && d <= k; d++)
        {
            tau[d] = S / ((double)k * d);
        }
        if (kOverS <= k)
        {
            tau[kOverS] = S * Math.Log(S / delta) / k;
        }

        // Combine and normalize
        var mu = new double[k + 1];
        double sum = 0;
        for (int d = 1; d <= k; d++)
        {
            mu[d] = rho[d] + tau[d];
            sum += mu[d];
        }

        // Build CDF
        var cdf = new double[k + 1];
        double cumulative = 0;
        for (int d = 1; d <= k; d++)
        {
            cumulative += mu[d] / sum;
            cdf[d] = cumulative;
        }
        cdf[k] = 1.0; // Ensure exact 1.0 at end

        return cdf;
    }

    private void ValidateConfig(ErasureCodingConfiguration config)
    {
        if (config.Scheme != ErasureCodingScheme.FountainCode)
            throw new ArgumentException(
                $"Expected FountainCode scheme, got {config.Scheme}.", nameof(config));
        if (config.DataDeviceCount != _dataDevices)
            throw new ArgumentException(
                $"Expected {_dataDevices} data devices, got {config.DataDeviceCount}.", nameof(config));
    }

    private static async Task<byte[]> ReadDeviceBlock(
        IPhysicalBlockDevice device, long stripeOffset, byte[] buffer, CancellationToken ct)
    {
        await device.ReadBlockAsync(stripeOffset, buffer.AsMemory(), ct).ConfigureAwait(false);
        return buffer;
    }

    #endregion
}

#endregion

#region Registry

/// <summary>
/// Registry of pre-configured device-level erasure coding strategies.
/// Provides quick access to common configurations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device erasure coding (CBDV-03)")]
public static class DeviceErasureCodingRegistry
{
    private static readonly ConcurrentDictionary<string, IDeviceErasureCodingStrategy> _strategies = new();

    static DeviceErasureCodingRegistry()
    {
        Register(DeviceReedSolomonStrategy.Create8Plus3());
        Register(DeviceReedSolomonStrategy.Create16Plus4());
        Register(new DeviceFountainCodeStrategy(8, 1.5));
        Register(new DeviceFountainCodeStrategy(16, 1.5));
    }

    /// <summary>
    /// Registers a strategy in the registry.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    public static void Register(IDeviceErasureCodingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Gets a registered strategy by its ID.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public static IDeviceErasureCodingStrategy? Get(string strategyId)
    {
        _strategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public static IReadOnlyCollection<IDeviceErasureCodingStrategy> GetAll()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }
}

#endregion
