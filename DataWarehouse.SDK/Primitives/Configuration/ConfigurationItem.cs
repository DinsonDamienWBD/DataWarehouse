using System.Xml.Serialization;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Wrapper for configuration items with override control.
/// Each setting in DataWarehouseConfiguration is wrapped in this type to provide
/// per-item override control and policy locking.
/// </summary>
/// <typeparam name="T">The type of the configuration value.</typeparam>
public class ConfigurationItem<T>
{
    /// <summary>Gets or sets the configuration value.</summary>
    public T Value { get; set; }

    /// <summary>Gets or sets whether users can override this value at runtime.</summary>
    public bool AllowUserToOverride { get; set; } = true;

    /// <summary>Gets or sets the policy that locked this value (if AllowUserToOverride is false).</summary>
    [XmlElement(IsNullable = true)]
    public string? LockedByPolicy { get; set; }

    /// <summary>Gets or sets the description of this configuration item.</summary>
    [XmlElement(IsNullable = true)]
    public string? Description { get; set; }

    /// <summary>
    /// Parameterless constructor for XML serialization.
    /// </summary>
    public ConfigurationItem()
    {
        Value = default!;
    }

    /// <summary>
    /// Creates a new configuration item with the specified value and override settings.
    /// </summary>
    public ConfigurationItem(T value, bool allowOverride = true, string? lockedBy = null, string? description = null)
    {
        Value = value;
        AllowUserToOverride = allowOverride;
        LockedByPolicy = lockedBy;
        Description = description;
    }

    /// <summary>Implicit conversion to T for easier usage.</summary>
    public static implicit operator T(ConfigurationItem<T> item) => item.Value;
}
