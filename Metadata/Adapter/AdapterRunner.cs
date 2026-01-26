using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Integration;

/// <summary>
/// Reusable runner for executing kernel adapters.
/// Handles initialization, lifecycle management, and graceful shutdown.
///
/// Copy this entire Adapter folder to your project to use this infrastructure.
///
/// Usage:
/// <code>
/// // Register your adapter
/// AdapterFactory.Register("MyApp", () => new MyAppAdapter());
///
/// // Create and run
/// var runner = new AdapterRunner(loggerFactory);
/// await runner.RunAsync(options);
/// </code>
/// </summary>
public sealed class AdapterRunner : IAsyncDisposable
{
    private readonly ILogger<AdapterRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _cts = new();
    private IKernelAdapter? _adapter;
    private bool _disposed;

    public AdapterRunner(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AdapterRunner>();
    }

    /// <summary>
    /// Current adapter instance (null if not running).
    /// </summary>
    public IKernelAdapter? CurrentAdapter => _adapter;

    /// <summary>
    /// Runs the specified adapter type until cancellation.
    /// </summary>
    /// <param name="options">Configuration options for the adapter.</param>
    /// <param name="adapterType">Type of adapter to run (null for default).</param>
    /// <param name="cancellationToken">External cancellation token.</param>
    public async Task<int> RunAsync(
        AdapterOptions options,
        string? adapterType = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Link external cancellation with internal
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            // Create the adapter
            _adapter = AdapterFactory.Create(adapterType);
            _adapter.StateChanged += OnAdapterStateChanged;

            // Set logger factory in options
            options.LoggerFactory = _loggerFactory;

            _logger.LogInformation("Initializing adapter: {AdapterType}", adapterType ?? "default");

            // Initialize
            await _adapter.InitializeAsync(options, linkedCts.Token);
            _logger.LogInformation("Adapter initialized: {KernelId}", _adapter.KernelId);

            // Start
            await _adapter.StartAsync(linkedCts.Token);
            _logger.LogInformation("Adapter started: {KernelId}", _adapter.KernelId);

            // Wait until cancellation
            try
            {
                await Task.Delay(Timeout.Infinite, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }

            // Stop gracefully
            _logger.LogInformation("Stopping adapter: {KernelId}", _adapter.KernelId);
            await _adapter.StopAsync(CancellationToken.None);
            _logger.LogInformation("Adapter stopped: {KernelId}", _adapter.KernelId);

            return 0;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Adapter shutdown requested");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adapter failed unexpectedly");
            return 1;
        }
        finally
        {
            if (_adapter != null)
            {
                _adapter.StateChanged -= OnAdapterStateChanged;
                await _adapter.DisposeAsync();
                _adapter = null;
            }
        }
    }

    /// <summary>
    /// Requests graceful shutdown of the running adapter.
    /// </summary>
    public void RequestShutdown()
    {
        _logger.LogInformation("Shutdown requested");
        _cts.Cancel();
    }

    private void OnAdapterStateChanged(object? sender, KernelStateChangedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Adapter state changed: {PreviousState} -> {NewState}: {Message}",
                e.PreviousState, e.NewState, e.Message);
        }
        else
        {
            _logger.LogDebug("Adapter state changed: {PreviousState} -> {NewState}: {Message}",
                e.PreviousState, e.NewState, e.Message ?? "");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        if (_adapter != null)
        {
            _adapter.StateChanged -= OnAdapterStateChanged;
            await _adapter.DisposeAsync();
        }

        _cts.Dispose();
    }
}
