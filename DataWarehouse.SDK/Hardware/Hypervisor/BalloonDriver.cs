using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware.Hypervisor;

/// <summary>
/// Implementation of <see cref="IBalloonDriver"/> for cooperating with hypervisor memory balloon drivers.
/// </summary>
/// <remarks>
/// <para>
/// BalloonDriver cooperates with hypervisor memory management by releasing cache buffers when
/// the hypervisor requests memory reclamation. This improves memory efficiency in VM environments
/// by allowing the hypervisor to reallocate memory to VMs that need it more.
/// </para>
/// <para>
/// <strong>Detection:</strong> The balloon driver uses <see cref="IHypervisorDetector"/> to
/// determine if ballooning is available. Balloon driver cooperation is only available in
/// hypervisor environments that support it (VMware, Hyper-V, KVM, Xen).
/// </para>
/// <para>
/// <strong>Memory Pressure Notification:</strong> Phase 35 provides the API contract and
/// availability detection. Actual hypervisor-specific pressure notification (via vmballoon,
/// hv_balloon, virtio-balloon) is marked as future work and currently uses simplified placeholders.
/// </para>
/// <para>
/// <strong>Memory Release Reporting:</strong> Phase 35 provides the API contract. Actual
/// hypervisor-specific memory release notification is marked as future work and currently
/// uses simplified placeholders.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: Balloon driver implementation (HW-06)")]
public sealed class BalloonDriver : IBalloonDriver, IDisposable
{
    private readonly IHypervisorDetector _hypervisorDetector;
    private readonly HypervisorInfo _hypervisorInfo;
    private bool _isAvailable = false;
    private Action<long>? _pressureHandler;
    private readonly object _lock = new();
    private bool _disposed = false;
    private CancellationTokenSource? _gcMonitorCts; // GC pressure monitoring task (finding P2-379)

    /// <summary>
    /// Initializes a new instance of the <see cref="BalloonDriver"/> class.
    /// </summary>
    /// <param name="hypervisorDetector">Hypervisor detector for environment detection.</param>
    /// <remarks>
    /// The constructor detects the hypervisor environment and determines balloon driver availability.
    /// Balloon driver is only available in VM environments that support memory ballooning.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="hypervisorDetector"/> is null.
    /// </exception>
    public BalloonDriver(IHypervisorDetector hypervisorDetector)
    {
        ArgumentNullException.ThrowIfNull(hypervisorDetector);

        _hypervisorDetector = hypervisorDetector;
        _hypervisorInfo = hypervisorDetector.Detect();

        // Balloon driver available only in VM environments that support it
        _isAvailable = _hypervisorInfo.BalloonDriverAvailable;
    }

    /// <inheritdoc/>
    public bool IsAvailable => _isAvailable;

    /// <inheritdoc/>
    public void RegisterPressureHandler(Action<long> onMemoryPressure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onMemoryPressure);

        lock (_lock)
        {
            _pressureHandler = onMemoryPressure;

            if (!_isAvailable)
            {
                // Not running in a hypervisor or balloon driver not available
                return;
            }

            // Cross-platform GC pressure monitoring as a baseline signal (finding P2-379).
            // Full hypervisor-specific implementations (vmballoon, hv_balloon, virtio-balloon)
            // require platform SDK integration and are tracked for future phases.
            _gcMonitorCts?.Cancel();
            _gcMonitorCts?.Dispose();
            _gcMonitorCts = new CancellationTokenSource();
            var ct = _gcMonitorCts.Token;
            var capturedHandler = _pressureHandler;

            _ = Task.Run(async () =>
            {
                try
                {
                    GC.RegisterForFullGCNotification(10, 10);
                    while (!ct.IsCancellationRequested)
                    {
                        var status = GC.WaitForFullGCApproach(millisecondsTimeout: 1000);
                        if (status == GCNotificationStatus.Succeeded && !ct.IsCancellationRequested)
                        {
                            var pressureBytes = GC.GetTotalMemory(false);
                            capturedHandler?.Invoke(pressureBytes);
                            GC.WaitForFullGCComplete(millisecondsTimeout: 5000);
                        }
                        else if (status == GCNotificationStatus.Canceled || ct.IsCancellationRequested)
                        {
                            break;
                        }
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BalloonDriver] GC pressure monitor failed: {ex.Message}");
                }
                finally
                {
                    try { GC.CancelFullGCNotification(); } catch { }
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public void ReportMemoryReleased(long bytesReleased)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isAvailable)
        {
            // Not running in a hypervisor or balloon driver not available - nothing to report
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesReleased);

        lock (_lock)
        {
            // Trigger GC to ensure managed memory is actually freed, then OS memory manager
            // returns pages to the hypervisor
            System.Diagnostics.Debug.WriteLine(
                $"[BalloonDriver] Reporting {bytesReleased} bytes released to hypervisor ({_hypervisorInfo.Type})");
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            _pressureHandler = null;

            // Stop GC pressure monitoring task (finding P2-379)
            _gcMonitorCts?.Cancel();
            _gcMonitorCts?.Dispose();
            _gcMonitorCts = null;

            _disposed = true;
        }
    }
}
