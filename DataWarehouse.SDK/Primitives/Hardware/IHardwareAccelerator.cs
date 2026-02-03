namespace DataWarehouse.SDK.Primitives.Hardware;

/// <summary>
/// Provides hardware acceleration capabilities for compute-intensive operations.
/// </summary>
/// <remarks>
/// This interface abstracts hardware acceleration across different backends (SIMD, AVX, GPU CUDA, OpenCL).
/// Implementations can leverage specialized hardware to offload and accelerate operations such as
/// data transformations, compression, cryptographic operations, and bulk processing.
/// </remarks>
public interface IHardwareAccelerator
{
    /// <summary>
    /// Gets the hardware capabilities available on this accelerator.
    /// </summary>
    /// <value>
    /// A record containing information about supported acceleration types, memory limits,
    /// and other hardware-specific capabilities.
    /// </value>
    HardwareCapabilities Capabilities { get; }

    /// <summary>
    /// Checks whether hardware acceleration is currently available and operational.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if hardware acceleration is available; otherwise, false.</returns>
    /// <remarks>
    /// This method performs a runtime check to ensure the hardware and drivers are functioning.
    /// It may return false on systems where the hardware exists but is not accessible (e.g., disabled drivers).
    /// </remarks>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accelerates an operation using available hardware resources.
    /// </summary>
    /// <typeparam name="TInput">The type of input data.</typeparam>
    /// <typeparam name="TOutput">The type of output data.</typeparam>
    /// <param name="input">The input data to process.</param>
    /// <param name="operation">The operation to perform on the data.</param>
    /// <param name="hint">Optional hint about the operation for optimization.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The processed output data.</returns>
    /// <exception cref="NotSupportedException">Thrown when the operation cannot be accelerated.</exception>
    /// <exception cref="InvalidOperationException">Thrown when hardware is unavailable or fails.</exception>
    Task<TOutput> AccelerateAsync<TInput, TOutput>(
        TInput input,
        Func<TInput, TOutput> operation,
        AccelerationHint hint = AccelerationHint.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accelerates a bulk operation on a collection of items using hardware resources.
    /// </summary>
    /// <typeparam name="TInput">The type of input data items.</typeparam>
    /// <typeparam name="TOutput">The type of output data items.</typeparam>
    /// <param name="inputs">The collection of input items to process.</param>
    /// <param name="operation">The operation to perform on each item.</param>
    /// <param name="hint">Optional hint about the operation for optimization.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An enumerable of processed output items.</returns>
    /// <remarks>
    /// This method is optimized for batch processing and may leverage parallelism
    /// across multiple hardware units (e.g., SIMD lanes, GPU cores).
    /// </remarks>
    IAsyncEnumerable<TOutput> AccelerateBatchAsync<TInput, TOutput>(
        IEnumerable<TInput> inputs,
        Func<TInput, TOutput> operation,
        AccelerationHint hint = AccelerationHint.Auto,
        CancellationToken cancellationToken = default);
}
