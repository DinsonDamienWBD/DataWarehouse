namespace DataWarehouse.CLI.Integration;

/// <summary>
/// Factory for creating kernel adapters.
/// Register custom adapter types to extend launcher functionality.
///
/// Usage:
/// <code>
/// // Register a custom adapter
/// AdapterFactory.Register("MyKernel", () => new MyKernelAdapter());
///
/// // Create an adapter instance
/// var adapter = AdapterFactory.Create("MyKernel");
/// </code>
/// </summary>
public static class AdapterFactory
{
    private static readonly Dictionary<string, Func<IKernelAdapter>> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private static string _defaultAdapterType = "DataWarehouse";

    /// <summary>
    /// Registers an adapter type with the factory.
    /// </summary>
    /// <param name="adapterType">Unique name for this adapter type.</param>
    /// <param name="factory">Factory function to create adapter instances.</param>
    public static void Register(string adapterType, Func<IKernelAdapter> factory)
    {
        ArgumentNullException.ThrowIfNull(adapterType);
        ArgumentNullException.ThrowIfNull(factory);
        _adapters[adapterType] = factory;
    }

    /// <summary>
    /// Registers an adapter type using generics.
    /// </summary>
    /// <typeparam name="T">Adapter type to register.</typeparam>
    /// <param name="adapterType">Unique name for this adapter type.</param>
    public static void Register<T>(string adapterType) where T : IKernelAdapter, new()
    {
        Register(adapterType, () => new T());
    }

    /// <summary>
    /// Sets the default adapter type.
    /// </summary>
    public static void SetDefault(string adapterType)
    {
        if (!_adapters.ContainsKey(adapterType))
        {
            throw new ArgumentException($"Adapter type '{adapterType}' is not registered.", nameof(adapterType));
        }
        _defaultAdapterType = adapterType;
    }

    /// <summary>
    /// Creates an adapter instance of the specified type.
    /// </summary>
    /// <param name="adapterType">The adapter type to create. If null, uses the default type.</param>
    /// <returns>A new adapter instance.</returns>
    public static IKernelAdapter Create(string? adapterType = null)
    {
        var type = adapterType ?? _defaultAdapterType;

        if (!_adapters.TryGetValue(type, out var factory))
        {
            throw new InvalidOperationException(
                $"Adapter type '{type}' is not registered. " +
                $"Available types: {string.Join(", ", _adapters.Keys)}");
        }

        return factory();
    }

    /// <summary>
    /// Gets all registered adapter types.
    /// </summary>
    public static IReadOnlyCollection<string> GetRegisteredTypes() => _adapters.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Checks if an adapter type is registered.
    /// </summary>
    public static bool IsRegistered(string adapterType) => _adapters.ContainsKey(adapterType);

    /// <summary>
    /// Clears all registered adapters (useful for testing).
    /// </summary>
    public static void Clear()
    {
        _adapters.Clear();
        _defaultAdapterType = "DataWarehouse";
    }
}
