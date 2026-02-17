// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Defines the service profile type for plugin loading in the unified DataWarehouse binary.
/// </summary>
/// <remarks>
/// <para>
/// The DataWarehouse.Launcher is a single cross-platform binary that runs as a daemon on both
/// server and client machines. The <see cref="ServiceProfileType"/> enum determines which plugins
/// are loaded at startup based on the active deployment profile.
/// </para>
/// <para>
/// <strong>Usage:</strong> Apply <see cref="PluginProfileAttribute"/> to plugin classes with the
/// appropriate profile type. Plugins without the attribute default to <see cref="Both"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [PluginProfile(ServiceProfileType.Server)]
/// public class ServerDispatcherPlugin : ServerDispatcherPluginBase { }
///
/// [PluginProfile(ServiceProfileType.Client)]
/// public class ClientCourierPlugin : PlatformPluginBase { }
/// </code>
/// </example>
public enum ServiceProfileType
{
    /// <summary>
    /// Plugin is loaded in both server and client profiles.
    /// This is the default when no <see cref="PluginProfileAttribute"/> is applied.
    /// </summary>
    Both = 0,

    /// <summary>
    /// Plugin is loaded only when running in server profile.
    /// Typical for: ServerDispatcher, control plane listeners, storage engines, intelligence, databases.
    /// </summary>
    Server = 1,

    /// <summary>
    /// Plugin is loaded only when running in client profile.
    /// Typical for: ClientCourier, client-side watchdog, policy engine.
    /// </summary>
    Client = 2,

    /// <summary>
    /// Plugin is never loaded automatically. Must be explicitly requested.
    /// Useful for diagnostic or testing plugins.
    /// </summary>
    None = 3,

    /// <summary>
    /// Auto-detect profile from available plugin types and configuration.
    /// Used as a startup option, not as a plugin annotation.
    /// </summary>
    Auto = 4
}
