using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateSDKPorts;

/// <summary>Registry for SDK port strategies.</summary>
public sealed class SDKPortStrategyRegistry
{
    private readonly ConcurrentDictionary<string, SDKPortStrategyBase> _strategies = new(StringComparer.OrdinalIgnoreCase);

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
