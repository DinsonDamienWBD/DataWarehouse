using System;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Marks a public SDK type with version compatibility metadata.
    /// Applied to all public types to track when they were introduced,
    /// when they were deprecated, and what replaces them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This attribute enables tooling and consumers to:
    /// <list type="bullet">
    /// <item><description>Determine which SDK version introduced a type</description></item>
    /// <item><description>Identify deprecated types and their replacements</description></item>
    /// <item><description>Plan migration paths between SDK versions</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Version strings follow semantic versioning (e.g., "2.0.0", "1.5.0").
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [SdkCompatibility("2.0.0")]
    /// public class StrategyBase { }
    ///
    /// [SdkCompatibility("1.0.0", DeprecatedVersion = "2.0.0", ReplacementType = "NewType")]
    /// public class OldType { }
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct |
        AttributeTargets.Enum | AttributeTargets.Delegate,
        Inherited = false,
        AllowMultiple = false)]
    [SdkCompatibility("2.0.0", Notes = "API versioning attribute for all public SDK types")]
    public sealed class SdkCompatibilityAttribute : Attribute
    {
        /// <summary>
        /// Gets the SDK version in which this type was introduced.
        /// </summary>
        public string IntroducedVersion { get; }

        /// <summary>
        /// Gets or sets the SDK version in which this type was deprecated.
        /// Null if the type is not deprecated.
        /// </summary>
        public string? DeprecatedVersion { get; init; }

        /// <summary>
        /// Gets or sets the fully qualified name of the replacement type.
        /// Only applicable when <see cref="DeprecatedVersion"/> is set.
        /// </summary>
        public string? ReplacementType { get; init; }

        /// <summary>
        /// Gets or sets additional notes about compatibility, migration, or usage.
        /// </summary>
        public string? Notes { get; init; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdkCompatibilityAttribute"/> class.
        /// </summary>
        /// <param name="introducedVersion">The SDK version that introduced this type (e.g., "2.0.0").</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="introducedVersion"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="introducedVersion"/> is empty or whitespace.</exception>
        public SdkCompatibilityAttribute(string introducedVersion)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(introducedVersion);
            IntroducedVersion = introducedVersion;
        }
    }
}
