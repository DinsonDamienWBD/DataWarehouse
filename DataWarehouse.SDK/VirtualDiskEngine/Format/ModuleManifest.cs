using System.Buffers.Binary;
using System.Numerics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// A 32-bit module manifest where each bit indicates whether the corresponding
/// <see cref="ModuleId"/> is active. Bits 0-18 map to the 19 defined modules;
/// bits 19-31 are reserved. Immutable value type with functional mutation methods.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 module manifest (VDE2-04)")]
public readonly struct ModuleManifestField : IEquatable<ModuleManifestField>
{
    /// <summary>The raw 32-bit manifest value.</summary>
    public uint Value { get; }

    /// <summary>Creates a manifest from a raw 32-bit value.</summary>
    public ModuleManifestField(uint value) => Value = value;

    /// <summary>Checks whether the specified module is active in this manifest.</summary>
    public bool IsModuleActive(ModuleId module) => (Value & (1u << (int)module)) != 0;

    /// <summary>Returns a new manifest with the specified module activated.</summary>
    public ModuleManifestField WithModule(ModuleId module) => new(Value | (1u << (int)module));

    /// <summary>Returns a new manifest with the specified module deactivated.</summary>
    public ModuleManifestField WithoutModule(ModuleId module) => new(Value & ~(1u << (int)module));

    /// <summary>Number of active modules (population count of set bits).</summary>
    public int ActiveModuleCount => BitOperations.PopCount(Value);

    /// <summary>Returns the <see cref="ModuleId"/> values for all active modules.</summary>
    public IReadOnlyList<ModuleId> GetActiveModuleIds()
    {
        var result = new List<ModuleId>(BitOperations.PopCount(Value));
        uint bits = Value;
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
        uint value = 0;
        foreach (var module in modules)
        {
            value |= 1u << (int)module;
        }
        return new ModuleManifestField(value);
    }

    /// <summary>A manifest with all 19 defined modules active (bits 0-18 set).</summary>
    public static ModuleManifestField AllModules { get; } = new(0x0007_FFFFu);

    /// <summary>An empty manifest with no modules active.</summary>
    public static ModuleManifestField None { get; } = new(0x0000_0000u);

    /// <summary>Implicit conversion from <see cref="ModuleManifestField"/> to uint.</summary>
    public static implicit operator uint(ModuleManifestField manifest) => manifest.Value;

    /// <summary>Implicit conversion from uint to <see cref="ModuleManifestField"/>.</summary>
    public static implicit operator ModuleManifestField(uint value) => new(value);

    /// <summary>Serializes this manifest to 4 bytes in little-endian format.</summary>
    public static void Serialize(in ModuleManifestField manifest, Span<byte> buffer)
    {
        if (buffer.Length < 4)
            throw new ArgumentException("Buffer must be at least 4 bytes.", nameof(buffer));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, manifest.Value);
    }

    /// <summary>Deserializes a manifest from 4 bytes in little-endian format.</summary>
    public static ModuleManifestField Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4)
            throw new ArgumentException("Buffer must be at least 4 bytes.", nameof(buffer));
        return new ModuleManifestField(BinaryPrimitives.ReadUInt32LittleEndian(buffer));
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
    public override string ToString() => $"ModuleManifest(0x{Value:X8}, {ActiveModuleCount} active)";
}
