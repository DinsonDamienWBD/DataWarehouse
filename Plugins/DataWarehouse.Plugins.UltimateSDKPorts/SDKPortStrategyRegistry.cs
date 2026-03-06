using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSDKPorts;

/// <summary>
/// Registry for SDK port strategies.
/// </summary>
/// <remarks>
/// <b>Migration note (65.4-07):</b> <see cref="UltimateSdkPortsPlugin"/> inherits
/// <see cref="DataWarehouse.SDK.Contracts.Hierarchy.PlatformPluginBase"/> which exposes
/// <c>RegisterPlatformStrategy</c> and <c>DispatchPlatformStrategyAsync</c> for
/// strategy lifecycle and dispatch. Because <see cref="SdkPortStrategyBase"/> does not
/// implement <see cref="DataWarehouse.SDK.Contracts.IStrategy"/>, this custom registry
/// is retained as a typed lookup layer while base-class dispatch is used where applicable.
/// New plugins should prefer the base-class strategy registry over this class.
/// </remarks>
public sealed class SdkPortStrategyRegistry
{
    private readonly BoundedDictionary<string, SdkPortStrategyBase> _strategies = new BoundedDictionary<string, SdkPortStrategyBase>(1000);

    public int Count => _strategies.Count;
    // Cat 13 (finding 3803): expose as IEnumerable to avoid .ToList().AsReadOnly() GC pressure on every access.
    // Callers that need materialized collection should call .ToList() themselves when needed.
    public IEnumerable<string> RegisteredStrategies => _strategies.Keys;

    public void Register(SdkPortStrategyBase strategy) => _strategies[strategy.StrategyId] = strategy;
    public SdkPortStrategyBase? Get(string name) => _strategies.TryGetValue(name, out var s) ? s : null;
    public IEnumerable<SdkPortStrategyBase> GetAll() => _strategies.Values;

    public IEnumerable<SdkPortStrategyBase> GetByCategory(SdkPortCategory category) =>
        _strategies.Values.Where(s => s.Characteristics.Category == category);

    public IEnumerable<SdkPortStrategyBase> GetByLanguage(LanguageTarget language) =>
        _strategies.Values.Where(s => s.Characteristics.Capabilities.SupportedLanguages.Contains(language));

    public SdkPortStrategyBase? SelectBest(LanguageTarget language, TransportType? preferredTransport = null) =>
        _strategies.Values
            .Where(s => s.Characteristics.Capabilities.SupportedLanguages.Contains(language))
            .Where(s => preferredTransport == null ||
                       s.Characteristics.Capabilities.SupportedTransports.Contains(preferredTransport.Value))
            .OrderByDescending(s => s.Characteristics.Capabilities.MaxConcurrentCalls)
            .FirstOrDefault();
}
