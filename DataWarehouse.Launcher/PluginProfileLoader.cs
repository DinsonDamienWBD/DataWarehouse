// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Launcher;

/// <summary>
/// Filters discovered plugins by the active service profile before loading.
/// </summary>
/// <remarks>
/// <para>
/// The PluginProfileLoader is invoked after plugin discovery but before plugin instantiation.
/// It filters the discovered plugin types based on the active <see cref="ServiceProfileType"/>:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Server:</strong> Loads plugins annotated with Server or Both</description></item>
/// <item><description><strong>Client:</strong> Loads plugins annotated with Client or Both</description></item>
/// <item><description><strong>Both:</strong> Loads all plugins (no filtering)</description></item>
/// <item><description><strong>Auto:</strong> Detects profile from available plugin types</description></item>
/// <item><description><strong>None:</strong> Loads only plugins annotated with None (diagnostic mode)</description></item>
/// </list>
/// </remarks>
public static class PluginProfileLoader
{
    /// <summary>
    /// Filters discovered plugin types by the active service profile.
    /// </summary>
    /// <param name="discoveredPlugins">All discovered plugin types from assembly scanning.</param>
    /// <param name="activeProfile">The active service profile from configuration.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Filtered list of plugin types matching the active profile.</returns>
    public static IReadOnlyList<Type> FilterPluginsByProfile(
        IReadOnlyList<Type> discoveredPlugins,
        ServiceProfileType activeProfile,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        // Resolve auto profile
        var resolvedProfile = activeProfile == ServiceProfileType.Auto
            ? DetectProfile(discoveredPlugins, logger)
            : activeProfile;

        logger.LogInformation("Active service profile: {Profile} (requested: {Requested})",
            resolvedProfile, activeProfile);

        // Both means no filtering
        if (resolvedProfile == ServiceProfileType.Both)
        {
            logger.LogInformation("Profile 'Both' active: loading all {Count} discovered plugins", discoveredPlugins.Count);
            return discoveredPlugins;
        }

        var included = new List<Type>();
        var excluded = new List<(Type Type, ServiceProfileType Profile)>();

        foreach (var pluginType in discoveredPlugins)
        {
            var pluginProfile = PluginProfileAttribute.GetProfile(pluginType);

            if (ShouldInclude(pluginProfile, resolvedProfile))
            {
                included.Add(pluginType);
            }
            else
            {
                excluded.Add((pluginType, pluginProfile));
            }
        }

        logger.LogInformation("Profile filtering: {Included} plugins included, {Excluded} plugins excluded",
            included.Count, excluded.Count);

        foreach (var (type, profile) in excluded)
        {
            logger.LogDebug("Excluded plugin {PluginType} (profile: {PluginProfile}, active: {ActiveProfile})",
                type.Name, profile, resolvedProfile);
        }

        return included.AsReadOnly();
    }

    /// <summary>
    /// Auto-detects the appropriate profile from available plugin types.
    /// </summary>
    /// <remarks>
    /// Detection logic:
    /// - Has ServerDispatcher-type plugin AND ClientCourier-type plugin: Both
    /// - Has ServerDispatcher-type plugin only: Server
    /// - Has ClientCourier-type plugin only: Client
    /// - Neither: Both (load everything, let plugins sort it out)
    /// </remarks>
    private static ServiceProfileType DetectProfile(IReadOnlyList<Type> discoveredPlugins, ILogger logger)
    {
        var hasServerPlugins = false;
        var hasClientPlugins = false;

        foreach (var pluginType in discoveredPlugins)
        {
            var profile = PluginProfileAttribute.GetProfile(pluginType);

            if (profile == ServiceProfileType.Server)
                hasServerPlugins = true;
            else if (profile == ServiceProfileType.Client)
                hasClientPlugins = true;
        }

        var detected = (hasServerPlugins, hasClientPlugins) switch
        {
            (true, true) => ServiceProfileType.Both,
            (true, false) => ServiceProfileType.Server,
            (false, true) => ServiceProfileType.Client,
            (false, false) => ServiceProfileType.Both
        };

        logger.LogInformation("Auto-detected service profile: {Profile} (hasServer: {HasServer}, hasClient: {HasClient})",
            detected, hasServerPlugins, hasClientPlugins);

        return detected;
    }

    /// <summary>
    /// Determines whether a plugin with the given profile should be included for the active profile.
    /// </summary>
    private static bool ShouldInclude(ServiceProfileType pluginProfile, ServiceProfileType activeProfile)
    {
        return activeProfile switch
        {
            ServiceProfileType.Server => pluginProfile is ServiceProfileType.Server or ServiceProfileType.Both,
            ServiceProfileType.Client => pluginProfile is ServiceProfileType.Client or ServiceProfileType.Both,
            ServiceProfileType.Both => pluginProfile is not ServiceProfileType.None,
            ServiceProfileType.None => pluginProfile == ServiceProfileType.None,
            _ => true
        };
    }
}
