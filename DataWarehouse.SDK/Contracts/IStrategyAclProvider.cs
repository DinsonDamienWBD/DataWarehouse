using DataWarehouse.SDK.Security;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Provides access control checks for strategy resolution.
/// Plugins can inject this to enforce per-strategy ACL based on CommandIdentity.
/// Default: all strategies accessible (no restrictions).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Strategy-level ACL via CommandIdentity")]
public interface IStrategyAclProvider
{
    /// <summary>
    /// Checks whether the given identity is allowed to use the specified strategy.
    /// Implementations MUST evaluate identity.EffectivePrincipalId, NEVER identity.ActorId.
    /// </summary>
    /// <param name="strategyId">The strategy being accessed.</param>
    /// <param name="identity">The command identity requesting access.</param>
    /// <returns>True if access is allowed, false if denied.</returns>
    bool IsStrategyAllowed(string strategyId, CommandIdentity identity);
}
