using System.Buffers.Binary;
using System.Numerics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// A 64-bit module manifest where each bit indicates whether the corresponding
/// <see cref="ModuleId"/> is active. Bits 0-38 map to the 39 defined modules;
/// bits 39-63 are reserved. Immutable value type with functional mutation methods.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE v2.1 module manifest upgraded to uint64 (VOPT-87)")]
public readonly struct ModuleManifestField : IEquatable<ModuleManifestField>
{
    /// <summary>The raw 64-bit manifest value.</summary>
    public ulong Value { get; }

    /// <summary>Creates a manifest from a raw 64-bit value.</summary>
    public ModuleManifestField(ulong value) => Value = value;

    /// <summary>Checks whether the specified module is active in this manifest.</summary>
    public bool IsModuleActive(ModuleId module) => (Value & (1UL << (int)module)) != 0;

    /// <summary>Returns a new manifest with the specified module activated.</summary>
    public ModuleManifestField WithModule(ModuleId module) => new(Value | (1UL << (int)module));

    /// <summary>Returns a new manifest with the specified module deactivated.</summary>
    public ModuleManifestField WithoutModule(ModuleId module) => new(Value & ~(1UL << (int)module));

    /// <summary>Number of active modules (population count of set bits).</summary>
    public int ActiveModuleCount => BitOperations.PopCount(Value);

    /// <summary>Returns the <see cref="ModuleId"/> values for all active modules.</summary>
    public IReadOnlyList<ModuleId> GetActiveModuleIds()
    {
        var result = new List<ModuleId>(BitOperations.PopCount(Value));
        ulong bits = Value;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            result.Add((ModuleId)bit);
            bits &= bits - 1;
        }
        return result;
    }

    /// <summary>Creates a manifest from a list of module identifiers.</summary>
    public static ModuleManifestField FromModules(params ModuleId[] modules)
    {
        ulong value = 0;
        foreach (var module in modules)
        {
            value |= 1UL << (int)module;
        }
        return new ModuleManifestField(value);
    }

    /// <summary>A manifest with all 39 defined modules active (bits 0-38 set).</summary>
    public static ModuleManifestField AllModules { get; } = new(0x0000_007F_FFFF_FFFFuL);

    /// <summary>An empty manifest with no modules active.</summary>
    public static ModuleManifestField None { get; } = new(0UL);

    /// <summary>Implicit conversion from <see cref="ModuleManifestField"/> to ulong.</summary>
    public static implicit operator ulong(ModuleManifestField manifest) => manifest.Value;

    /// <summary>Implicit conversion from ulong to <see cref="ModuleManifestField"/>.</summary>
    public static implicit operator ModuleManifestField(ulong value) => new(value);

    /// <summary>Serializes this manifest to 8 bytes in little-endian format.</summary>
    public static void Serialize(in ModuleManifestField manifest, Span<byte> buffer)
    {
        if (buffer.Length < 8)
            throw new ArgumentException("Buffer must be at least 8 bytes.", nameof(buffer));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, manifest.Value);
    }

    /// <summary>Deserializes a manifest from 8 bytes in little-endian format.</summary>
    public static ModuleManifestField Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            throw new ArgumentException("Buffer must be at least 8 bytes.", nameof(buffer));
        return new ModuleManifestField(BinaryPrimitives.ReadUInt64LittleEndian(buffer));
    }

    /// <inheritdoc />
    public bool Equals(ModuleManifestField other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ModuleManifestField other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ModuleManifestField left, ModuleManifestField right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ModuleManifestField left, ModuleManifestField right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"ModuleManifest(0x{Value:X16}, {ActiveModuleCount} active)";
}
