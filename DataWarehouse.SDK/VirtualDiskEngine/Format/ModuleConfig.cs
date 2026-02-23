using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Nibble-encoded module configuration: 16 bytes encoding 32 module slots at 16
/// configuration levels each (0-15). Modules 0-15 are packed into
/// <see cref="ConfigPrimary"/> and modules 16-31 into <see cref="ConfigExtended"/>,
/// with each module occupying 4 bits (one nibble).
/// </summary>
/// <remarks>
/// Level 0 means the module's configuration is at its default/disabled state.
/// Levels 1-15 represent progressively higher configuration intensities whose
/// meaning is module-specific (e.g., replication factor, compression level).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 nibble-encoded module config (VDE2-04)")]
public readonly struct ModuleConfigField : IEquatable<ModuleConfigField>
{
    /// <summary>Configuration nibbles for modules 0-15 (4 bits each, packed into 64 bits).</summary>
    public ulong ConfigPrimary { get; }

    /// <summary>Configuration nibbles for modules 16-31 (4 bits each, packed into 64 bits).</summary>
    public ulong ConfigExtended { get; }

    /// <summary>Creates a config field from raw primary and extended values.</summary>
    public ModuleConfigField(ulong configPrimary, ulong configExtended)
    {
        ConfigPrimary = configPrimary;
        ConfigExtended = configExtended;
    }

    /// <summary>
    /// Extracts the 4-bit configuration level (0-15) for the specified module.
    /// </summary>
    /// <param name="module">The module identifier.</param>
    /// <returns>Configuration level from 0 to 15.</returns>
    public byte GetLevel(ModuleId module)
    {
        int index = (int)module;
        if (index < 16)
        {
            return (byte)((ConfigPrimary >> (index * 4)) & 0xFu);
        }
        return (byte)((ConfigExtended >> ((index - 16) * 4)) & 0xFu);
    }

    /// <summary>
    /// Returns a new config with the specified module's level set.
    /// </summary>
    /// <param name="module">The module identifier.</param>
    /// <param name="level">Configuration level (0-15).</param>
    /// <returns>A new <see cref="ModuleConfigField"/> with the updated nibble.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> exceeds 15.</exception>
    public ModuleConfigField WithLevel(ModuleId module, byte level)
    {
        if (level > 15)
            throw new ArgumentOutOfRangeException(nameof(level), level, "Module configuration level must be 0-15.");

        int index = (int)module;
        if (index < 16)
        {
            int shift = index * 4;
            ulong cleared = ConfigPrimary & ~(0xFUL << shift);
            ulong updated = cleared | ((ulong)level << shift);
            return new ModuleConfigField(updated, ConfigExtended);
        }
        else
        {
            int shift = (index - 16) * 4;
            ulong cleared = ConfigExtended & ~(0xFUL << shift);
            ulong updated = cleared | ((ulong)level << shift);
            return new ModuleConfigField(ConfigPrimary, updated);
        }
    }

    /// <summary>Returns true if the module's configuration level is 0 (disabled/default).</summary>
    public bool IsDisabled(ModuleId module) => GetLevel(module) == 0;

    /// <summary>
    /// Returns a dictionary of all modules with non-zero configuration levels.
    /// </summary>
    public Dictionary<ModuleId, byte> GetAllLevels()
    {
        var result = new Dictionary<ModuleId, byte>();
        for (int i = 0; i < FormatConstants.MaxModules; i++)
        {
            var id = (ModuleId)i;
            byte level = GetLevel(id);
            if (level != 0)
            {
                result[id] = level;
            }
        }
        return result;
    }

    /// <summary>A config with all 32 module slots set to maximum level (0xF).</summary>
    public static ModuleConfigField AllMaximum { get; } = new(
        0xFFFF_FFFF_FFFF_FFFFuL,
        0xFFFF_FFFF_FFFF_FFFFuL);

    /// <summary>Builds a config from a dictionary of module levels.</summary>
    public static ModuleConfigField FromLevels(IReadOnlyDictionary<ModuleId, byte> levels)
    {
        var config = new ModuleConfigField(0, 0);
        foreach (var (module, level) in levels)
        {
            config = config.WithLevel(module, level);
        }
        return config;
    }

    /// <summary>Serializes this config to 16 bytes (8 primary LE + 8 extended LE).</summary>
    public static void Serialize(in ModuleConfigField config, Span<byte> buffer)
    {
        if (buffer.Length < 16)
            throw new ArgumentException("Buffer must be at least 16 bytes.", nameof(buffer));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, config.ConfigPrimary);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], config.ConfigExtended);
    }

    /// <summary>Deserializes a config from 16 bytes (8 primary LE + 8 extended LE).</summary>
    public static ModuleConfigField Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 16)
            throw new ArgumentException("Buffer must be at least 16 bytes.", nameof(buffer));
        ulong primary = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        ulong extended = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);
        return new ModuleConfigField(primary, extended);
    }

    /// <inheritdoc />
    public bool Equals(ModuleConfigField other) =>
        ConfigPrimary == other.ConfigPrimary && ConfigExtended == other.ConfigExtended;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ModuleConfigField other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ConfigPrimary, ConfigExtended);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ModuleConfigField left, ModuleConfigField right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ModuleConfigField left, ModuleConfigField right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"ModuleConfig(Primary=0x{ConfigPrimary:X16}, Extended=0x{ConfigExtended:X16})";
}
