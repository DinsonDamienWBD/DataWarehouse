using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Enumerates all supported tag value kinds in the universal tag system.
/// Each member corresponds to exactly one sealed concrete <see cref="TagValue"/> subtype.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value kind enumeration")]
public enum TagValueKind
{
    /// <summary>Plain text string value (<see cref="StringTagValue"/>).</summary>
    String = 0,

    /// <summary>RGBA color value (<see cref="ColorTagValue"/>).</summary>
    Color = 1,

    /// <summary>Nested key-value object value (<see cref="ObjectTagValue"/>).</summary>
    Object = 2,

    /// <summary>Cross-object pointer reference (<see cref="PointerTagValue"/>).</summary>
    Pointer = 3,

    /// <summary>External URL link (<see cref="LinkTagValue"/>).</summary>
    Link = 4,

    /// <summary>Long-form text with format hint (<see cref="ParagraphTagValue"/>).</summary>
    Paragraph = 5,

    /// <summary>Numeric value with optional unit (<see cref="NumberTagValue"/>).</summary>
    Number = 6,

    /// <summary>Ordered list of tag values (<see cref="ListTagValue"/>).</summary>
    List = 7,

    /// <summary>Recursive tree structure (<see cref="TreeTagValue"/>).</summary>
    Tree = 8,

    /// <summary>Boolean flag value (<see cref="BoolTagValue"/>).</summary>
    Bool = 9
}

/// <summary>
/// Abstract base for all tag values. Forms a sealed discriminated union hierarchy
/// ensuring every tag value is strongly typed -- never an untyped <c>object</c> bag.
/// Use the static factory methods (e.g., <see cref="String"/>, <see cref="Number"/>)
/// for convenient construction.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value discriminated union")]
public abstract record TagValue
{
    /// <summary>
    /// Gets the kind discriminator for this tag value.
    /// </summary>
    public abstract TagValueKind Kind { get; }

    // ── Static factory methods ───────────────────────────────────────

    /// <summary>Creates a <see cref="StringTagValue"/> with the given text.</summary>
    /// <param name="value">The string content.</param>
    public static StringTagValue String(string value) => new(value);

    /// <summary>Creates a <see cref="ColorTagValue"/> with RGBA channels.</summary>
    /// <param name="r">Red channel (0-255).</param>
    /// <param name="g">Green channel (0-255).</param>
    /// <param name="b">Blue channel (0-255).</param>
    /// <param name="a">Alpha channel (0-255). Defaults to 255 (fully opaque).</param>
    public static ColorTagValue Color(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);

    /// <summary>Creates an <see cref="ObjectTagValue"/> with nested key-value properties.</summary>
    /// <param name="properties">The property dictionary.</param>
    public static ObjectTagValue Object(IReadOnlyDictionary<string, TagValue> properties) => new(properties);

    /// <summary>Creates a <see cref="PointerTagValue"/> referencing another object.</summary>
    /// <param name="targetObjectId">The ID of the target object.</param>
    /// <param name="targetTagKey">Optional specific tag key on the target.</param>
    public static PointerTagValue Pointer(string targetObjectId, string? targetTagKey = null) => new(targetObjectId, targetTagKey);

    /// <summary>Creates a <see cref="LinkTagValue"/> with an external URL.</summary>
    /// <param name="url">The link target URL.</param>
    /// <param name="label">Optional human-readable label.</param>
    public static LinkTagValue Link(Uri url, string? label = null) => new(url, label);

    /// <summary>Creates a <see cref="ParagraphTagValue"/> with long-form text.</summary>
    /// <param name="text">The text content.</param>
    /// <param name="format">Format hint: "plain", "markdown", or "html". Defaults to "plain".</param>
    public static ParagraphTagValue Paragraph(string text, string format = "plain") => new(text, format);

    /// <summary>Creates a <see cref="NumberTagValue"/> with an optional unit.</summary>
    /// <param name="value">The numeric value.</param>
    /// <param name="unit">Optional unit of measurement (e.g., "kg", "m/s").</param>
    public static NumberTagValue Number(decimal value, string? unit = null) => new(value, unit);

    /// <summary>Creates a <see cref="ListTagValue"/> from an ordered list of values.</summary>
    /// <param name="items">The ordered list of tag values.</param>
    public static ListTagValue List(IReadOnlyList<TagValue> items) => new(items);

    /// <summary>Creates a <see cref="TreeTagValue"/> node with a label and children.</summary>
    /// <param name="label">The node label.</param>
    /// <param name="children">Child tree nodes.</param>
    public static TreeTagValue Tree(string label, IReadOnlyList<TreeTagValue> children) => new(label, children);

    /// <summary>Creates a <see cref="BoolTagValue"/> with the given boolean.</summary>
    /// <param name="value">The boolean value.</param>
    public static BoolTagValue Bool(bool value) => new(value);
}

/// <summary>
/// A plain text string tag value.
/// </summary>
/// <param name="Value">The string content.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record StringTagValue(string Value) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.String;

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// An RGBA color tag value. Each channel is a byte (0-255).
/// </summary>
/// <param name="R">Red channel (0-255).</param>
/// <param name="G">Green channel (0-255).</param>
/// <param name="B">Blue channel (0-255).</param>
/// <param name="A">Alpha channel (0-255). Defaults to 255 (fully opaque).</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record ColorTagValue(byte R, byte G, byte B, byte A = 255) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Color;

    /// <summary>
    /// Converts this color to a hexadecimal string representation (e.g., "#FF00AAFF").
    /// </summary>
    /// <returns>A hex string in #RRGGBBAA format.</returns>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <inheritdoc />
    public override string ToString() => ToHex();
}

/// <summary>
/// A nested key-value object tag value, allowing structured data within a tag.
/// Property values can be any <see cref="TagValue"/> subtype, enabling deep nesting.
/// </summary>
/// <param name="Properties">The key-value properties. Keys are property names; values are any <see cref="TagValue"/>.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record ObjectTagValue(IReadOnlyDictionary<string, TagValue> Properties) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Object;

    /// <inheritdoc />
    public override string ToString() => $"Object({Properties.Count} properties)";

    /// <summary>Compares properties by structural equality (key-value pairs must match).</summary>
    public bool Equals(ObjectTagValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Properties.Count != other.Properties.Count) return false;
        foreach (var kvp in Properties)
        {
            if (!other.Properties.TryGetValue(kvp.Key, out var otherVal) || !Equals(kvp.Value, otherVal))
                return false;
        }
        return true;
    }

    /// <summary>Computes hash from properties ordered by key.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var kvp in Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(kvp.Key, StringComparer.Ordinal);
            hash.Add(kvp.Value);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// A cross-object pointer reference tag value. Points to another object,
/// optionally targeting a specific tag key on that object.
/// </summary>
/// <param name="TargetObjectId">The ID of the target object being referenced.</param>
/// <param name="TargetTagKey">Optional specific tag key on the target object. Null means the pointer references the object itself.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record PointerTagValue(string TargetObjectId, string? TargetTagKey = null) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Pointer;

    /// <inheritdoc />
    public override string ToString() =>
        TargetTagKey is not null ? $"Pointer({TargetObjectId}:{TargetTagKey})" : $"Pointer({TargetObjectId})";
}

/// <summary>
/// An external URL link tag value with an optional human-readable label.
/// </summary>
/// <param name="Url">The link target URL.</param>
/// <param name="Label">Optional human-readable label for the link.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record LinkTagValue(Uri Url, string? Label = null) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Link;

    /// <inheritdoc />
    public override string ToString() => Label is not null ? $"[{Label}]({Url})" : Url.ToString();
}

/// <summary>
/// A long-form text tag value with a format hint (plain, markdown, or html).
/// </summary>
/// <param name="Text">The text content.</param>
/// <param name="Format">The format hint: "plain", "markdown", or "html". Defaults to "plain".</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record ParagraphTagValue(string Text, string Format = "plain") : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Paragraph;

    /// <inheritdoc />
    public override string ToString() => Text.Length > 80 ? $"{Text[..77]}..." : Text;
}

/// <summary>
/// A numeric tag value with optional unit of measurement.
/// </summary>
/// <param name="Value">The numeric value.</param>
/// <param name="Unit">Optional unit of measurement (e.g., "kg", "m/s", "USD").</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record NumberTagValue(decimal Value, string? Unit = null) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Number;

    /// <inheritdoc />
    public override string ToString() => Unit is not null ? $"{Value} {Unit}" : Value.ToString();
}

/// <summary>
/// An ordered list of tag values, supporting heterogeneous elements.
/// Each item can be any <see cref="TagValue"/> subtype.
/// </summary>
/// <param name="Items">The ordered list of tag values.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record ListTagValue(IReadOnlyList<TagValue> Items) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.List;

    /// <inheritdoc />
    public override string ToString() => $"List({Items.Count} items)";

    /// <summary>Compares items by structural sequence equality.</summary>
    public bool Equals(ListTagValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Items.SequenceEqual(other.Items);
    }

    /// <summary>Computes hash from ordered items.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
            hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A recursive tree-structured tag value. Each node has a label and zero or more children.
/// Useful for hierarchical categorization, taxonomy trees, or nested organizational structures.
/// </summary>
/// <param name="Label">The label for this tree node.</param>
/// <param name="Children">The child nodes. Empty for leaf nodes.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record TreeTagValue(string Label, IReadOnlyList<TreeTagValue> Children) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Tree;

    /// <inheritdoc />
    public override string ToString() =>
        Children.Count > 0 ? $"Tree({Label}, {Children.Count} children)" : $"Tree({Label})";

    /// <summary>Compares tree nodes by structural equality (label and children sequence).</summary>
    public bool Equals(TreeTagValue? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Label == other.Label && Children.SequenceEqual(other.Children);
    }

    /// <summary>Computes hash from label and children.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Label);
        foreach (var child in Children)
            hash.Add(child);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A boolean flag tag value.
/// </summary>
/// <param name="Value">The boolean value.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
public sealed record BoolTagValue(bool Value) : TagValue
{
    /// <inheritdoc />
    public override TagValueKind Kind => TagValueKind.Bool;

    /// <inheritdoc />
    public override string ToString() => Value ? "true" : "false";
}
