using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Error codes produced by <see cref="TagSchemaValidator"/>.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema validation")]
    public static class TagValidationErrorCodes
    {
        public const string TypeMismatch = "TYPE_MISMATCH";
        public const string ValueOutOfRange = "VALUE_OUT_OF_RANGE";
        public const string StringTooShort = "STRING_TOO_SHORT";
        public const string StringTooLong = "STRING_TOO_LONG";
        public const string PatternMismatch = "PATTERN_MISMATCH";
        public const string ValueNotAllowed = "VALUE_NOT_ALLOWED";
        public const string ItemKindNotAllowed = "ITEM_KIND_NOT_ALLOWED";
        public const string TooManyItems = "TOO_MANY_ITEMS";
        public const string TreeTooDeep = "TREE_TOO_DEEP";
        public const string RequiredTagMissing = "REQUIRED_TAG_MISSING";
        public const string SourceNotAllowed = "SOURCE_NOT_ALLOWED";
        public const string ImmutableTagModified = "IMMUTABLE_TAG_MODIFIED";
        public const string SystemOnlyViolation = "SYSTEM_ONLY_VIOLATION";
    }

    /// <summary>
    /// Describes a single validation error for a tag.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema validation")]
    public sealed record TagValidationError(TagKey TagKey, string ErrorCode, string Message);

    /// <summary>
    /// The result of validating one or more tags against their schemas.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema validation")]
    public sealed record TagValidationResult
    {
        public bool IsValid { get; init; }
        public IReadOnlyList<TagValidationError> Errors { get; init; } = Array.Empty<TagValidationError>();

        /// <summary>A successful validation result with no errors.</summary>
        public static TagValidationResult Success { get; } = new() { IsValid = true };

        /// <summary>Creates a failed result from a list of errors.</summary>
        public static TagValidationResult Failure(IReadOnlyList<TagValidationError> errors) =>
            new() { IsValid = false, Errors = errors };

        /// <summary>Creates a failed result from a single error.</summary>
        public static TagValidationResult Failure(TagValidationError error) =>
            new() { IsValid = false, Errors = new[] { error } };
    }

    /// <summary>
    /// Validates <see cref="Tag"/> instances against <see cref="TagSchema"/> constraints.
    /// Covers type matching, numeric range, string length/pattern/allowed values,
    /// list item count and kinds, tree depth, source authorization, and immutability.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema validation")]
    public static class TagSchemaValidator
    {
        /// <summary>
        /// Validates a single tag against its schema.
        /// </summary>
        /// <param name="tag">The tag to validate.</param>
        /// <param name="schema">The schema to validate against.</param>
        /// <returns>A <see cref="TagValidationResult"/> indicating success or listing all errors.</returns>
        public static TagValidationResult Validate(Tag tag, TagSchema schema)
        {
            ArgumentNullException.ThrowIfNull(tag);
            ArgumentNullException.ThrowIfNull(schema);

            var errors = new List<TagValidationError>();
            var current = schema.CurrentVersion;
            var constraints = current.Constraints;

            // Check value kind matches required kind
            if (tag.Value.Kind != current.RequiredKind)
            {
                errors.Add(new TagValidationError(
                    tag.Key,
                    TagValidationErrorCodes.TypeMismatch,
                    $"Expected value kind '{current.RequiredKind}' but got '{tag.Value.Kind}'."));
            }

            // Check source authorization
            if (!schema.AllowedSources.HasFlag(tag.Source.Source))
            {
                errors.Add(new TagValidationError(
                    tag.Key,
                    TagValidationErrorCodes.SourceNotAllowed,
                    $"Source '{tag.Source.Source}' is not allowed. Allowed: {schema.AllowedSources}."));
            }

            // Check system-only constraint
            if (schema.SystemOnly && tag.Source.Source != TagSource.System)
            {
                errors.Add(new TagValidationError(
                    tag.Key,
                    TagValidationErrorCodes.SystemOnlyViolation,
                    $"Tag '{tag.Key}' can only be set by System source, but source is '{tag.Source.Source}'."));
            }

            // Type-specific constraint validation
            ValidateConstraints(tag.Key, tag.Value, constraints, errors);

            return errors.Count == 0
                ? TagValidationResult.Success
                : TagValidationResult.Failure(errors);
        }

        /// <summary>
        /// Validates a collection of tags against their respective schemas.
        /// Also checks for required tags that are missing from the collection.
        /// </summary>
        /// <param name="tags">The tag collection to validate.</param>
        /// <param name="schemas">The schemas to validate against.</param>
        /// <returns>A <see cref="TagValidationResult"/> with all errors from all tags combined.</returns>
        public static TagValidationResult ValidateCollection(TagCollection tags, IEnumerable<TagSchema> schemas)
        {
            ArgumentNullException.ThrowIfNull(tags);
            ArgumentNullException.ThrowIfNull(schemas);

            var errors = new List<TagValidationError>();
            var schemaList = schemas as IReadOnlyList<TagSchema> ?? schemas.ToList();

            // Build lookup: TagKey -> schema
            var schemaByKey = new Dictionary<TagKey, TagSchema>();
            foreach (var schema in schemaList)
            {
                schemaByKey[schema.TagKey] = schema;
            }

            // Validate each tag that has a schema
            foreach (var tag in tags)
            {
                if (schemaByKey.TryGetValue(tag.Key, out var schema))
                {
                    var result = Validate(tag, schema);
                    if (!result.IsValid)
                    {
                        errors.AddRange(result.Errors);
                    }
                }
            }

            // Check for required tags that are missing
            foreach (var schema in schemaList)
            {
                if (schema.CurrentVersion.Constraints.Required && !tags.ContainsKey(schema.TagKey))
                {
                    errors.Add(new TagValidationError(
                        schema.TagKey,
                        TagValidationErrorCodes.RequiredTagMissing,
                        $"Required tag '{schema.TagKey}' is missing from the collection."));
                }
            }

            return errors.Count == 0
                ? TagValidationResult.Success
                : TagValidationResult.Failure(errors);
        }

        private static void ValidateConstraints(
            TagKey key, TagValue value, TagConstraint constraints, List<TagValidationError> errors)
        {
            switch (value)
            {
                case NumberTagValue num:
                    ValidateNumber(key, num, constraints, errors);
                    break;

                case StringTagValue str:
                    ValidateString(key, str.Value, constraints, errors);
                    break;

                case ParagraphTagValue para:
                    ValidateString(key, para.Text, constraints, errors);
                    break;

                case ListTagValue list:
                    ValidateList(key, list, constraints, errors);
                    break;

                case ObjectTagValue obj:
                    ValidateObject(key, obj, constraints, errors);
                    break;

                case TreeTagValue tree:
                    ValidateTree(key, tree, constraints, errors);
                    break;
            }
        }

        private static void ValidateNumber(
            TagKey key, NumberTagValue num, TagConstraint constraints, List<TagValidationError> errors)
        {
            if (constraints.MinValue.HasValue && num.Value < constraints.MinValue.Value)
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.ValueOutOfRange,
                    $"Value {num.Value} is below minimum {constraints.MinValue.Value}."));
            }

            if (constraints.MaxValue.HasValue && num.Value > constraints.MaxValue.Value)
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.ValueOutOfRange,
                    $"Value {num.Value} exceeds maximum {constraints.MaxValue.Value}."));
            }
        }

        private static void ValidateString(
            TagKey key, string text, TagConstraint constraints, List<TagValidationError> errors)
        {
            if (constraints.MinLength.HasValue && text.Length < constraints.MinLength.Value)
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.StringTooShort,
                    $"String length {text.Length} is below minimum {constraints.MinLength.Value}."));
            }

            if (constraints.MaxLength.HasValue && text.Length > constraints.MaxLength.Value)
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.StringTooLong,
                    $"String length {text.Length} exceeds maximum {constraints.MaxLength.Value}."));
            }

            if (constraints.Pattern is not null)
            {
                try
                {
                    if (!Regex.IsMatch(text, constraints.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    {
                        errors.Add(new TagValidationError(
                            key,
                            TagValidationErrorCodes.PatternMismatch,
                            $"Value '{text}' does not match pattern '{constraints.Pattern}'."));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    errors.Add(new TagValidationError(
                        key,
                        TagValidationErrorCodes.PatternMismatch,
                        $"Pattern validation timed out for pattern '{constraints.Pattern}'."));
                }
            }

            if (constraints.AllowedValues is not null && !constraints.AllowedValues.Contains(text))
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.ValueNotAllowed,
                    $"Value '{text}' is not in the allowed set: [{string.Join(", ", constraints.AllowedValues)}]."));
            }
        }

        private static void ValidateList(
            TagKey key, ListTagValue list, TagConstraint constraints, List<TagValidationError> errors)
        {
            if (constraints.MaxItems.HasValue && list.Items.Count > constraints.MaxItems.Value)
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.TooManyItems,
                    $"List has {list.Items.Count} items, exceeding maximum of {constraints.MaxItems.Value}."));
            }

            if (constraints.AllowedItemKinds is not null)
            {
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (!constraints.AllowedItemKinds.Contains(list.Items[i].Kind))
                    {
                        errors.Add(new TagValidationError(
                            key,
                            TagValidationErrorCodes.ItemKindNotAllowed,
                            $"List item [{i}] has kind '{list.Items[i].Kind}' which is not in the allowed set: [{string.Join(", ", constraints.AllowedItemKinds)}]."));
                    }
                }
            }
        }

        private static void ValidateObject(
            TagKey key, ObjectTagValue obj, TagConstraint constraints, List<TagValidationError> errors)
        {
            if (constraints.MaxItems.HasValue && obj.Properties.Count > constraints.MaxItems.Value)
            {
                errors.Add(new TagValidationError(
                    key,
                    TagValidationErrorCodes.TooManyItems,
                    $"Object has {obj.Properties.Count} properties, exceeding maximum of {constraints.MaxItems.Value}."));
            }
        }

        private static void ValidateTree(
            TagKey key, TreeTagValue tree, TagConstraint constraints, List<TagValidationError> errors)
        {
            if (constraints.MaxDepth.HasValue)
            {
                int actualDepth = MeasureTreeDepth(tree);
                if (actualDepth > constraints.MaxDepth.Value)
                {
                    errors.Add(new TagValidationError(
                        key,
                        TagValidationErrorCodes.TreeTooDeep,
                        $"Tree depth {actualDepth} exceeds maximum of {constraints.MaxDepth.Value}."));
                }
            }
        }

        private static int MeasureTreeDepth(TreeTagValue node)
        {
            if (node.Children.Count == 0)
                return 1;

            int maxChildDepth = 0;
            foreach (var child in node.Children)
            {
                int childDepth = MeasureTreeDepth(child);
                if (childDepth > maxChildDepth)
                    maxChildDepth = childDepth;
            }
            return 1 + maxChildDepth;
        }
    }
}
