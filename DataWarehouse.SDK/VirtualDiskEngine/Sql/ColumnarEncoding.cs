using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Encoding type for columnar data compression.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Columnar VDE regions (VOPT-16)")]
public enum EncodingType : byte
{
    /// <summary>No encoding; raw values stored as-is.</summary>
    Plain = 0,

    /// <summary>Run-length encoding: consecutive equal values stored as (value, count) pairs.</summary>
    RunLength = 1,

    /// <summary>Dictionary encoding: unique values in dictionary, data replaced by index.</summary>
    Dictionary = 2,

    /// <summary>Bit-packed encoding for booleans and small integers.</summary>
    BitPacked = 3,
}

/// <summary>
/// Provides RLE, dictionary, and bit-packed encoding for columnar data.
/// All methods are stateless and operate on raw byte spans.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Columnar VDE regions (VOPT-16)")]
public static class ColumnarEncoding
{
    // ── Run-Length Encoding ──────────────────────────────────────────────

    /// <summary>
    /// Encodes consecutive equal values as (value, count) pairs.
    /// Each run is stored as: [value (valueWidth bytes)] [runLength (4 bytes LE)].
    /// </summary>
    /// <param name="values">Raw column data (rowCount * valueWidth bytes).</param>
    /// <param name="valueWidth">Width of each value in bytes.</param>
    /// <returns>RLE-encoded byte array.</returns>
    public static byte[] EncodeRunLength(ReadOnlySpan<byte> values, int valueWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueWidth);
        if (values.Length % valueWidth != 0) throw new ArgumentException("Values length must be a multiple of valueWidth.", nameof(values));

        int rowCount = values.Length / valueWidth;
        if (rowCount == 0) return Array.Empty<byte>();

        // Worst case: every value is unique -> rowCount * (valueWidth + 4)
        using var ms = new MemoryStream(rowCount * (valueWidth + 4) / 2);
        Span<byte> countBuf = stackalloc byte[4];

        int runStart = 0;
        while (runStart < rowCount)
        {
            var currentValue = values.Slice(runStart * valueWidth, valueWidth);
            int runLength = 1;
            int next = runStart + 1;

            while (next < rowCount &&
                   currentValue.SequenceEqual(values.Slice(next * valueWidth, valueWidth)))
            {
                runLength++;
                next++;
            }

            // Write value
            ms.Write(currentValue);
            // Write run length as 4-byte LE
            BinaryPrimitives.WriteInt32LittleEndian(countBuf, runLength);
            ms.Write(countBuf);

            runStart = next;
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decodes RLE-encoded data back to raw column values.
    /// </summary>
    /// <param name="encoded">RLE-encoded data.</param>
    /// <param name="valueWidth">Width of each value in bytes.</param>
    /// <param name="rowCount">Expected number of output rows.</param>
    /// <returns>Decoded raw column data.</returns>
    public static byte[] DecodeRunLength(ReadOnlySpan<byte> encoded, int valueWidth, int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueWidth);

        var result = new byte[rowCount * valueWidth];
        int outputOffset = 0;
        int inputOffset = 0;
        int entrySize = valueWidth + 4;

        while (inputOffset + entrySize <= encoded.Length && outputOffset < result.Length)
        {
            var value = encoded.Slice(inputOffset, valueWidth);
            int runLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(inputOffset + valueWidth, 4));

            for (int i = 0; i < runLength && outputOffset < result.Length; i++)
            {
                value.CopyTo(result.AsSpan(outputOffset, valueWidth));
                outputOffset += valueWidth;
            }

            inputOffset += entrySize;
        }

        return result;
    }

    // ── Dictionary Encoding ─────────────────────────────────────────────

    /// <summary>
    /// Builds a dictionary of unique values and replaces each value with its dictionary index.
    /// Index width is 1 byte if cardinality &lt;= 256, 2 bytes if &lt;= 65536, else 4 bytes.
    /// </summary>
    /// <param name="values">Raw column data.</param>
    /// <param name="valueWidth">Width of each value in bytes.</param>
    /// <returns>Tuple of (dictionary data, index data). Dictionary data is prefixed with 4-byte LE entry count.</returns>
    public static (byte[] DictionaryData, byte[] IndexData) EncodeDictionary(ReadOnlySpan<byte> values, int valueWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueWidth);
        if (values.Length % valueWidth != 0) throw new ArgumentException("Values length must be a multiple of valueWidth.", nameof(values));

        int rowCount = values.Length / valueWidth;
        if (rowCount == 0) return (Array.Empty<byte>(), Array.Empty<byte>());

        // Build dictionary: map value bytes -> index
        var dict = new Dictionary<ValueKey, int>(rowCount / 4);
        var dictEntries = new List<byte[]>();

        for (int i = 0; i < rowCount; i++)
        {
            var val = values.Slice(i * valueWidth, valueWidth);
            var key = new ValueKey(val);
            if (!dict.ContainsKey(key))
            {
                dict[key] = dictEntries.Count;
                dictEntries.Add(val.ToArray());
            }
        }

        int cardinality = dictEntries.Count;
        int indexWidth = cardinality <= 256 ? 1 : cardinality <= 65536 ? 2 : 4;

        // Dictionary data: [entryCount:4LE][indexWidth:1][entries...]
        var dictData = new byte[4 + 1 + cardinality * valueWidth];
        BinaryPrimitives.WriteInt32LittleEndian(dictData.AsSpan(0, 4), cardinality);
        dictData[4] = (byte)indexWidth;
        for (int i = 0; i < cardinality; i++)
        {
            dictEntries[i].CopyTo(dictData, 5 + i * valueWidth);
        }

        // Index data: one index per row
        var indexData = new byte[rowCount * indexWidth];
        for (int i = 0; i < rowCount; i++)
        {
            var val = values.Slice(i * valueWidth, valueWidth);
            int idx = dict[new ValueKey(val)];

            switch (indexWidth)
            {
                case 1:
                    indexData[i] = (byte)idx;
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(indexData.AsSpan(i * 2, 2), (ushort)idx);
                    break;
                case 4:
                    BinaryPrimitives.WriteInt32LittleEndian(indexData.AsSpan(i * 4, 4), idx);
                    break;
            }
        }

        return (dictData, indexData);
    }

    /// <summary>
    /// Decodes dictionary-encoded data back to raw column values.
    /// </summary>
    /// <param name="dictionaryData">Dictionary data (prefixed with entry count and index width).</param>
    /// <param name="indexData">Index data.</param>
    /// <param name="valueWidth">Width of each value in bytes.</param>
    /// <param name="rowCount">Expected number of output rows.</param>
    /// <returns>Decoded raw column data.</returns>
    public static byte[] DecodeDictionary(ReadOnlySpan<byte> dictionaryData, ReadOnlySpan<byte> indexData, int valueWidth, int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueWidth);

        int cardinality = BinaryPrimitives.ReadInt32LittleEndian(dictionaryData[..4]);
        int indexWidth = dictionaryData[4];
        var dictStart = dictionaryData.Slice(5);

        var result = new byte[rowCount * valueWidth];

        for (int i = 0; i < rowCount; i++)
        {
            int idx = indexWidth switch
            {
                1 => indexData[i],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(indexData.Slice(i * 2, 2)),
                4 => BinaryPrimitives.ReadInt32LittleEndian(indexData.Slice(i * 4, 4)),
                _ => throw new InvalidDataException($"Unsupported index width: {indexWidth}"),
            };

            dictStart.Slice(idx * valueWidth, valueWidth).CopyTo(result.AsSpan(i * valueWidth, valueWidth));
        }

        return result;
    }

    // ── Bit-Packing ─────────────────────────────────────────────────────

    /// <summary>
    /// Bit-packs boolean values (1 bit per value) from a byte-per-boolean input.
    /// </summary>
    /// <param name="values">One byte per boolean (0 = false, non-zero = true).</param>
    /// <returns>Bit-packed byte array.</returns>
    public static byte[] EncodeBitPacked(ReadOnlySpan<byte> values)
    {
        int byteCount = (values.Length + 7) / 8;
        var result = new byte[byteCount];

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != 0)
            {
                result[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return result;
    }

    /// <summary>
    /// Decodes bit-packed booleans back to one byte per boolean.
    /// </summary>
    /// <param name="packed">Bit-packed data.</param>
    /// <param name="rowCount">Number of boolean values.</param>
    /// <returns>One byte per boolean (0 or 1).</returns>
    public static byte[] DecodeBitPacked(ReadOnlySpan<byte> packed, int rowCount)
    {
        var result = new byte[rowCount];

        for (int i = 0; i < rowCount; i++)
        {
            result[i] = (byte)((packed[i / 8] >> (i % 8)) & 1);
        }

        return result;
    }

    // ── Encoding Selection ──────────────────────────────────────────────

    /// <summary>
    /// Selects the best encoding for the given column data using heuristics:
    /// - If boolean (valueWidth == 1 and all values 0 or 1): BitPacked
    /// - If distinct values &lt; 256: Dictionary
    /// - If &gt;50% of values form runs of 2+: RunLength
    /// - Otherwise: Plain
    /// </summary>
    /// <param name="values">Raw column data.</param>
    /// <param name="valueWidth">Width of each value in bytes.</param>
    /// <returns>The recommended encoding type.</returns>
    public static EncodingType SelectBestEncoding(ReadOnlySpan<byte> values, int valueWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueWidth);
        if (values.Length == 0) return EncodingType.Plain;
        if (values.Length % valueWidth != 0) return EncodingType.Plain;

        int rowCount = values.Length / valueWidth;

        // Check for boolean data (bit-packable)
        if (valueWidth == 1)
        {
            bool allBoolean = true;
            for (int i = 0; i < rowCount; i++)
            {
                if (values[i] > 1) { allBoolean = false; break; }
            }
            if (allBoolean) return EncodingType.BitPacked;
        }

        // Count distinct values (sample up to first 4096 rows for performance)
        int sampleCount = Math.Min(rowCount, 4096);
        var distinctSet = new HashSet<ValueKey>();
        for (int i = 0; i < sampleCount; i++)
        {
            distinctSet.Add(new ValueKey(values.Slice(i * valueWidth, valueWidth)));
        }

        if (distinctSet.Count < 256) return EncodingType.Dictionary;

        // Check run density
        int runsOfTwo = 0;
        for (int i = 1; i < sampleCount; i++)
        {
            if (values.Slice((i - 1) * valueWidth, valueWidth)
                .SequenceEqual(values.Slice(i * valueWidth, valueWidth)))
            {
                runsOfTwo++;
            }
        }

        if (sampleCount > 1 && runsOfTwo > sampleCount / 2) return EncodingType.RunLength;

        return EncodingType.Plain;
    }

    // ── Helper: Value key for dictionary-based operations ────────────────

    /// <summary>
    /// Immutable byte-array key with value equality for dictionary/set usage.
    /// </summary>
    private readonly struct ValueKey : IEquatable<ValueKey>
    {
        private readonly byte[] _data;
        private readonly int _hash;

        public ValueKey(ReadOnlySpan<byte> data)
        {
            _data = data.ToArray();
            var h = new HashCode();
            h.AddBytes(data);
            _hash = h.ToHashCode();
        }

        public bool Equals(ValueKey other)
        {
            if (_data.Length != other._data.Length) return false;
            return _data.AsSpan().SequenceEqual(other._data);
        }

        public override bool Equals(object? obj) => obj is ValueKey other && Equals(other);
        public override int GetHashCode() => _hash;
    }
}
