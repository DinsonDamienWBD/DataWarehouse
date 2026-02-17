// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Marks a plugin class with its service profile type for profile-based loading.
/// </summary>
/// <remarks>
/// <para>
/// When the DataWarehouse Launcher starts with a specific profile (server or client),
/// only plugins matching the active profile are loaded. Plugins without this attribute
/// default to <see cref="ServiceProfileType.Both"/> and are loaded in all profiles.
/// </para>
/// <para>
/// <strong>Inheritance:</strong> This attribute is inherited, so applying it to a base class
/// applies to all derived classes unless overridden.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Server-only plugin: loaded only with --profile server
/// [PluginProfile(ServiceProfileType.Server)]
/// public class ServerDispatcherPlugin : ServerDispatcherPluginBase { }
///
/// // Client-only plugin: loaded only with --profile client
/// [PluginProfile(ServiceProfileType.Client)]
/// public class ClientCourierPlugin : PlatformPluginBase { }
///
/// // Both profiles (default, no attribute needed):
/// public class IntentManifestSignerPlugin : FeaturePluginBase { }
///
/// // Explicitly both profiles:
/// [PluginProfile(ServiceProfileType.Both)]
/// public class SharedUtilityPlugin : FeaturePluginBase { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class PluginProfileAttribute : Attribute
{
    /// <summary>
    /// Gets the service profile type for this plugin.
    /// </summary>
    public ServiceProfileType Profile { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="PluginProfileAttribute"/> with the specified profile type.
    /// </summary>
    /// <param name="profile">The service profile type that determines when this plugin is loaded.</param>
    public PluginProfileAttribute(ServiceProfileType profile)
    {
        Profile = profile;
    }

    /// <summary>
    /// Gets the <see cref="ServiceProfileType"/> for the specified plugin type.
    /// Returns <see cref="ServiceProfileType.Both"/> if no <see cref="PluginProfileAttribute"/> is applied.
    /// </summary>
    /// <param name="pluginType">The plugin type to inspect.</param>
    /// <returns>The profile type for the plugin.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pluginType"/> is null.</exception>
    public static ServiceProfileType GetProfile(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);

        var attribute = (PluginProfileAttribute?)Attribute.GetCustomAttribute(pluginType, typeof(PluginProfileAttribute), inherit: true);
        return attribute?.Profile ?? ServiceProfileType.Both;
    }
}
