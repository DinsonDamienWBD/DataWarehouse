using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Kernel.Infrastructure;

/// <summary>
/// Implementation of IKernelContext that provides plugins with access to kernel services.
/// This allows plugins to access storage, logging, and other plugins without direct kernel references.
/// </summary>
public sealed class KernelContext : IKernelContext
{
    private readonly DataWarehouseKernel _kernel;
    private readonly ILogger? _logger;

    public KernelContext(DataWarehouseKernel kernel, IKernelStorageService storage, ILogger? logger = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger;
    }

    public OperatingMode Mode => _kernel.OperatingMode;

    public string RootPath => Environment.CurrentDirectory;

    public IKernelStorageService Storage { get; }

    public void LogInfo(string message)
    {
        _logger?.LogInformation("{Message}", message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        if (ex != null)
        {
            _logger?.LogError(ex, "{Message}", message);
        }
        else
        {
            _logger?.LogError("{Message}", message);
        }
    }

    public void LogWarning(string message)
    {
        _logger?.LogWarning("{Message}", message);
    }

    public void LogDebug(string message)
    {
        _logger?.LogDebug("{Message}", message);
    }

    public T? GetPlugin<T>() where T : class, IPlugin
    {
        return _kernel.GetPlugin<T>();
    }

    public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin
    {
        return _kernel.GetPlugins<T>();
    }
}
