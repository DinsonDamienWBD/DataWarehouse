using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSDKPorts;

/// <summary>
/// Registry for SDK port strategies.
/// </summary>
/// <remarks>
/// <b>Migration note (65.4-07):</b> <see cref="UltimateSDKPortsPlugin"/> inherits
/// <see cref="DataWarehouse.SDK.Contracts.Hierarchy.PlatformPluginBase"/> which exposes
/// <c>RegisterPlatformStrategy</c> and <c>DispatchPlatformStrategyAsync</c> for
/// strategy lifecycle and dispatch. Because <see cref="SDKPortStrategyBase"/> does not
/// implement <see cref="DataWarehouse.SDK.Contracts.IStrategy"/>, this custom registry
/// is retained as a typed lookup layer while base-class dispatch is used where applicable.
/// New plugins should prefer the base-class strategy registry over this class.
/// </remarks>
[System.Obsolete("Prefer base-class strategy dispatch via PlatformPluginBase. This registry is retained as a typed lookup thin wrapper.")]
public sealed class SDKPortStrategyRegistry
{
    private readonly BoundedDictionary<string, SDKPortStrategyBase> _strategies = new BoundedDictionary<string, SDKPortStrategyBase>(1000);

    public int Count => _strategies.Count;
    public IReadOnlyCollection<string> RegisteredStrategies => _strategies.Keys.ToList().AsReadOnly();

    public void Register(SDKPortStrategyBase strategy) => _strategies[strategy.StrategyId] = strategy;
    public SDKPortStrategyBase? Get(string name) => _strategies.TryGetValue(name, out var s) ? s : null;
    public IEnumerable<SDKPortStrategyBase> GetAll() => _strategies.Values;

    public IEnumerable<SDKPortStrategyBase> GetByCategory(SDKPortCategory category) =>
        _strategies.Values.Where(s => s.Characteristics.Category == category);

    public IEnumerable<SDKPortStrategyBase> GetByLanguage(LanguageTarget language) =>
        _strategies.Values.Where(s => s.Characteristics.Capabilities.SupportedLanguages.Contains(language));

    public SDKPortStrategyBase? SelectBest(LanguageTarget language, TransportType? preferredTransport = null) =>
        _strategies.Values
            .Where(s => s.Characteristics.Capabilities.SupportedLanguages.Contains(language))
            .Where(s => preferredTransport == null ||
                       s.Characteristics.Capabilities.SupportedTransports.Contains(preferredTransport.Value))
            .OrderByDescending(s => s.Characteristics.Capabilities.MaxConcurrentCalls)
            .FirstOrDefault();
}
