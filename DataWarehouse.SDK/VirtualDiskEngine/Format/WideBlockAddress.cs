using System.Buffers.Binary;
using System.Numerics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// On-disk address width for block pointers. Enum values are byte counts.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: 128-bit block addressing (CE-07)")]
public enum AddressWidth : byte
{
    /// <summary>32-bit addressing: 4 bytes per pointer, up to 16 TB at 4KB blocks.</summary>
    Width32 = 4,

    /// <summary>48-bit addressing: 6 bytes per pointer, up to 1 EB at 4KB blocks.</summary>
    Width48 = 6,

    /// <summary>64-bit addressing: 8 bytes per pointer, up to 64 ZB at 4KB blocks.</summary>
    Width64 = 8,

    /// <summary>128-bit addressing: 16 bytes per pointer, effectively unlimited capacity.</summary>
    Width128 = 16
}

/// <summary>
/// Variable-width block address type supporting 32/48/64/128-bit on-disk representation.
/// Internally stores all values as <see cref="UInt128"/> for uniform arithmetic.
/// </summary>
/// <remarks>
/// <para>Capacity table (4KB block size):</para>
/// <list type="bullet">
///   <item>32-bit: 16 TB (2^32 x 4KB)</item>
///   <item>48-bit: 1 EB (2^48 x 4KB)</item>
///   <item>64-bit: 64 ZB (2^64 x 4KB)</item>
///   <item>128-bit: 1.36e27 YB (2^128 x 4KB, effectively unlimited)</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Variable-width block address (CE-07)")]
public readonly struct WideBlockAddress : IEquatable<WideBlockAddress>, IComparable<WideBlockAddress>
{
    /// <summary>The raw 128-bit block address value.</summary>
    public UInt128 Value { get; }

    /// <summary>The on-disk serialization width for this address.</summary>
    public AddressWidth Width { get; }

    // ── Constructors ────────────────────────────────────────────────────

    /// <summary>
    /// Creates an address from a 64-bit value for 32/48/64-bit widths.
    /// </summary>
    /// <param name="value">The block address value.</param>
    /// <param name="width">The on-disk width.</param>
    public WideBlockAddress(long value, AddressWidth width)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Block address must be non-negative.");

        Value = (UInt128)(ulong)value;
        Width = width;

        if (!FitsIn(width))
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} exceeds maximum for {width}.");
    }

    /// <summary>
    /// Creates an address from a 128-bit value for any width.
    /// </summary>
    /// <param name="value">The 128-bit block address value.</param>
    /// <param name="width">The on-disk width.</param>
    public WideBlockAddress(UInt128 value, AddressWidth width)
    {
        Value = value;
        Width = width;

        if (!FitsInWidth(value, width))
            throw new ArgumentOutOfRangeException(nameof(value), $"Value exceeds maximum for {width}.");
    }

    /// <summary>
    /// Deserializes an address from on-disk bytes using the specified width.
    /// </summary>
    /// <param name="source">Source bytes, must contain at least <paramref name="width"/> bytes.</param>
    /// <param name="width">The on-disk width to read.</param>
    public WideBlockAddress(ReadOnlySpan<byte> source, AddressWidth width)
    {
        int byteCount = (int)width;
        if (source.Length < byteCount)
            throw new ArgumentException($"Source must contain at least {byteCount} bytes.", nameof(source));

        Value = ReadValue(source, width);
        Width = width;
    }

    // ── Factory methods ─────────────────────────────────────────────────

    /// <summary>
    /// Reads an address from on-disk bytes using the specified width.
    /// </summary>
    public static WideBlockAddress ReadFrom(ReadOnlySpan<byte> source, AddressWidth width)
        => new(source, width);

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Writes exactly <see cref="Width"/> bytes to the destination in little-endian format.
    /// </summary>
    /// <param name="destination">Destination buffer, must be at least <see cref="Width"/> bytes.</param>
    public void WriteTo(Span<byte> destination)
    {
        int byteCount = (int)Width;
        if (destination.Length < byteCount)
            throw new ArgumentException($"Destination must be at least {byteCount} bytes.", nameof(destination));

        WriteValue(destination, Value, Width);
    }

    // ── Conversion ──────────────────────────────────────────────────────

    /// <summary>
    /// Converts the address to a 64-bit integer. Throws <see cref="OverflowException"/>
    /// if the value exceeds <see cref="long.MaxValue"/>.
    /// </summary>
    public long ToInt64()
    {
        if (Value > (UInt128)long.MaxValue)
            throw new OverflowException($"Wide block address value exceeds Int64.MaxValue.");

        return (long)(ulong)Value;
    }

    /// <summary>
    /// Checks whether the current value can be represented in the specified narrower width.
    /// </summary>
    public bool FitsIn(AddressWidth width) => FitsInWidth(Value, width);

    // ── Static helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the maximum address representable at the given width.
    /// </summary>
    public static WideBlockAddress MaxValue(AddressWidth width)
    {
        var max = MaxValueRaw(width);
        return new WideBlockAddress(max, width);
    }

    /// <summary>
    /// Returns the maximum number of blocks addressable at the given width.
    /// For widths under 128, this is 2^N where N = width * 8.
    /// For 128-bit, returns <see cref="long.MaxValue"/> (actual max exceeds long).
    /// </summary>
    public static long MaxBlockCount(AddressWidth width)
    {
        return width switch
        {
            AddressWidth.Width32 => 1L << 32,                // 4,294,967,296
            AddressWidth.Width48 => 1L << 48,                // 281,474,976,710,656
            AddressWidth.Width64 => long.MaxValue,           // 2^63-1 (actual 2^64 exceeds long)
            AddressWidth.Width128 => long.MaxValue,          // actual 2^128 exceeds long
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        };
    }

    /// <summary>
    /// Returns a human-readable capacity string for the given width and block size.
    /// </summary>
    /// <param name="width">The address width.</param>
    /// <param name="blockSize">The block size in bytes (default 4096).</param>
    public static string MaxCapacityHuman(AddressWidth width, int blockSize = 4096)
    {
        return width switch
        {
            AddressWidth.Width32 => FormatCapacity((UInt128)(1L << 32) * (UInt128)blockSize),
            AddressWidth.Width48 => FormatCapacity((UInt128)(1L << 48) * (UInt128)blockSize),
            AddressWidth.Width64 => FormatCapacity(UInt128.MaxValue / 2),   // approximate for display
            AddressWidth.Width128 => "340 undecillion bytes",
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        };
    }

    /// <summary>
    /// Returns the number of bytes per pointer for the given address width.
    /// </summary>
    public static int BytesPerPointer(AddressWidth width) => (int)width;

    // ── Arithmetic operators ────────────────────────────────────────────

    /// <summary>Adds a signed offset to an address.</summary>
    public static WideBlockAddress operator +(WideBlockAddress address, long offset)
    {
        UInt128 result;
        if (offset >= 0)
            result = address.Value + (UInt128)(ulong)offset;
        else
            result = address.Value - (UInt128)(ulong)(-offset);

        return new WideBlockAddress(result, address.Width);
    }

    /// <summary>Subtracts two addresses to get a signed delta.</summary>
    public static long operator -(WideBlockAddress left, WideBlockAddress right)
    {
        if (left.Value >= right.Value)
        {
            var delta = left.Value - right.Value;
            if (delta > (UInt128)long.MaxValue)
                throw new OverflowException("Address delta exceeds Int64.MaxValue.");
            return (long)(ulong)delta;
        }
        else
        {
            var delta = right.Value - left.Value;
            if (delta > (UInt128)long.MaxValue)
                throw new OverflowException("Address delta exceeds Int64.MaxValue.");
            return -(long)(ulong)delta;
        }
    }

    /// <summary>Increment operator.</summary>
    public static WideBlockAddress operator ++(WideBlockAddress address)
        => new(address.Value + UInt128.One, address.Width);

    /// <summary>Decrement operator.</summary>
    public static WideBlockAddress operator --(WideBlockAddress address)
        => new(address.Value - UInt128.One, address.Width);

    // ── Comparison operators ────────────────────────────────────────────

    /// <summary>Less than.</summary>
    public static bool operator <(WideBlockAddress left, WideBlockAddress right) => left.Value < right.Value;

    /// <summary>Greater than.</summary>
    public static bool operator >(WideBlockAddress left, WideBlockAddress right) => left.Value > right.Value;

    /// <summary>Less than or equal.</summary>
    public static bool operator <=(WideBlockAddress left, WideBlockAddress right) => left.Value <= right.Value;

    /// <summary>Greater than or equal.</summary>
    public static bool operator >=(WideBlockAddress left, WideBlockAddress right) => left.Value >= right.Value;

    /// <summary>Equality.</summary>
    public static bool operator ==(WideBlockAddress left, WideBlockAddress right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(WideBlockAddress left, WideBlockAddress right) => !left.Equals(right);

    // ── IEquatable / IComparable ────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(WideBlockAddress other) => Value == other.Value && Width == other.Width;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WideBlockAddress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, Width);

    /// <inheritdoc />
    public int CompareTo(WideBlockAddress other) => Value.CompareTo(other.Value);

    // ── ToString ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns hex representation with width prefix, e.g., "32:0x00001000" or "128:0x000000000000000000001000".
    /// </summary>
    public override string ToString()
    {
        int bitWidth = (int)Width * 8;
        int hexDigits = (int)Width * 2;
        var hex = Value.ToString($"X{hexDigits}");
        return $"{bitWidth}:0x{hex}";
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private static UInt128 ReadValue(ReadOnlySpan<byte> source, AddressWidth width)
    {
        switch (width)
        {
            case AddressWidth.Width32:
                return BinaryPrimitives.ReadUInt32LittleEndian(source);

            case AddressWidth.Width48:
            {
                // Read 6 bytes little-endian into a ulong
                ulong val = source[0]
                    | ((ulong)source[1] << 8)
                    | ((ulong)source[2] << 16)
                    | ((ulong)source[3] << 24)
                    | ((ulong)source[4] << 32)
                    | ((ulong)source[5] << 40);
                return val;
            }

            case AddressWidth.Width64:
                return BinaryPrimitives.ReadUInt64LittleEndian(source);

            case AddressWidth.Width128:
            {
                var lower = BinaryPrimitives.ReadUInt64LittleEndian(source);
                var upper = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8));
                return new UInt128(upper, lower);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(width));
        }
    }

    private static void WriteValue(Span<byte> destination, UInt128 value, AddressWidth width)
    {
        switch (width)
        {
            case AddressWidth.Width32:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value);
                break;

            case AddressWidth.Width48:
            {
                ulong val = (ulong)value;
                destination[0] = (byte)val;
                destination[1] = (byte)(val >> 8);
                destination[2] = (byte)(val >> 16);
                destination[3] = (byte)(val >> 24);
                destination[4] = (byte)(val >> 32);
                destination[5] = (byte)(val >> 40);
                break;
            }

            case AddressWidth.Width64:
                BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)value);
                break;

            case AddressWidth.Width128:
            {
                ulong lower = (ulong)value;
                ulong upper = (ulong)(value >> 64);
                BinaryPrimitives.WriteUInt64LittleEndian(destination, lower);
                BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8), upper);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(width));
        }
    }

    private static bool FitsInWidth(UInt128 value, AddressWidth width)
    {
        return width switch
        {
            AddressWidth.Width32 => value <= uint.MaxValue,
            AddressWidth.Width48 => value <= 0xFFFF_FFFF_FFFF,
            AddressWidth.Width64 => value <= ulong.MaxValue,
            AddressWidth.Width128 => true,
            _ => false
        };
    }

    private static UInt128 MaxValueRaw(AddressWidth width)
    {
        return width switch
        {
            AddressWidth.Width32 => uint.MaxValue,
            AddressWidth.Width48 => 0xFFFF_FFFF_FFFF,
            AddressWidth.Width64 => ulong.MaxValue,
            AddressWidth.Width128 => UInt128.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        };
    }

    private static string FormatCapacity(UInt128 bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        int unitIndex = 0;
        var remaining = bytes;

        while (remaining >= 1024 && unitIndex < units.Length - 1)
        {
            remaining /= 1024;
            unitIndex++;
        }

        return $"{remaining} {units[unitIndex]}";
    }
}
