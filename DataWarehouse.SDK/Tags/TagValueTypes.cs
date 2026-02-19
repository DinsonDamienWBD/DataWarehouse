using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Discriminator for the <see cref="TagValue"/> hierarchy.
    /// Each member corresponds to a sealed concrete <see cref="TagValue"/> subtype.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public enum TagValueKind
    {
        String,
        Color,
        Object,
        Pointer,
        Link,
        Paragraph,
        Number,
        List,
        Tree,
        Bool
    }

    /// <summary>
    /// Abstract base for all tag values. Forms a sealed discriminated union
    /// with concrete subtypes for each <see cref="TagValueKind"/>.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public abstract record TagValue
    {
        /// <summary>Gets the discriminator kind for this value.</summary>
        public abstract TagValueKind Kind { get; }

        // --- Static factory methods ---

        public static StringTagValue String(string value) => new(value);
        public static ColorTagValue Color(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);
        public static ObjectTagValue Object(IReadOnlyDictionary<string, TagValue> properties) => new(properties);
        public static PointerTagValue Pointer(string targetObjectId, string? targetTagKey = null) => new(targetObjectId, targetTagKey);
        public static LinkTagValue Link(Uri url, string? label = null) => new(url, label);
        public static ParagraphTagValue Paragraph(string text, string format = "plain") => new(text, format);
        public static NumberTagValue Number(decimal value, string? unit = null) => new(value, unit);
        public static ListTagValue List(IReadOnlyList<TagValue> items) => new(items);
        public static TreeTagValue Tree(string label, IReadOnlyList<TreeTagValue> children) => new(label, children);
        public static BoolTagValue Bool(bool value) => new(value);
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record StringTagValue(string Value) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.String;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record ColorTagValue(byte R, byte G, byte B, byte A = 255) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Color;

        /// <summary>Returns the color as a hex string (e.g., "#FF00AAFF").</summary>
        public string ToHex() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record ObjectTagValue(IReadOnlyDictionary<string, TagValue> Properties) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Object;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record PointerTagValue(string TargetObjectId, string? TargetTagKey = null) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Pointer;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record LinkTagValue(Uri Url, string? Label = null) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Link;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record ParagraphTagValue(string Text, string Format = "plain") : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Paragraph;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record NumberTagValue(decimal Value, string? Unit = null) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Number;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record ListTagValue(IReadOnlyList<TagValue> Items) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.List;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record TreeTagValue(string Label, IReadOnlyList<TreeTagValue> Children) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Tree;
    }

    [SdkCompatibility("5.0.0", Notes = "Phase 55: Universal tag value types")]
    public sealed record BoolTagValue(bool Value) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Bool;
    }
}
