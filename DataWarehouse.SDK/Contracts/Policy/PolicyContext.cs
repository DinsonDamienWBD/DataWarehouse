namespace DataWarehouse.SDK.Contracts.Policy;

/// <summary>
/// Provides policy engine access to plugins. Injected into PluginBase during initialization.
/// Plugins use this to resolve effective policies for their features, check AI autonomy levels,
/// and read the active OperationalProfile.
/// </summary>
public sealed class PolicyContext
{
    /// <summary>
    /// The policy engine instance for resolving effective policies.
    /// </summary>
    public IPolicyEngine? Engine { get; }

    /// <summary>
    /// The metadata residency resolver for determining where metadata lives.
    /// </summary>
    public IMetadataResidencyResolver? ResidencyResolver { get; }

    /// <summary>
    /// Whether the policy engine is available (false during startup or in minimal deployments).
    /// </summary>
    public bool IsAvailable => Engine != null;

    /// <summary>
    /// Constructor. Null engine is valid — plugins gracefully degrade when policy engine is absent.
    /// </summary>
    /// <param name="engine">The policy engine instance, or null if unavailable.</param>
    /// <param name="residencyResolver">The metadata residency resolver, or null if unavailable.</param>
    public PolicyContext(IPolicyEngine? engine, IMetadataResidencyResolver? residencyResolver)
    {
        Engine = engine;
        ResidencyResolver = residencyResolver;
    }

    /// <summary>
    /// Empty context for when no policy engine is configured.
    /// All plugins default to this, ensuring zero-impact on existing code.
    /// </summary>
    public static PolicyContext Empty { get; } = new PolicyContext(null, null);
}
