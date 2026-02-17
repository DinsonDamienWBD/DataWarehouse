using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Security;

/// <summary>
/// Holds cryptographic key material in unmanaged (native) memory with secure wipe guarantees.
/// Uses <see cref="NativeMemory.AllocZeroed"/> for allocation outside the GC heap,
/// preventing accidental copying, compaction moves, or generation promotion that could
/// leave key material in multiple memory locations.
/// </summary>
/// <remarks>
/// <para><b>Security properties:</b></para>
/// <list type="bullet">
///   <item>Key bytes are allocated in unmanaged memory (not subject to GC compaction/copying).</item>
///   <item>On Dispose, memory is zeroed via <see cref="NativeMemory.Clear"/> before being freed.</item>
///   <item>Access is via <see cref="KeySpan"/> which returns a <see cref="Span{T}"/> (no heap copy).</item>
///   <item>The handle tracks disposal state and throws <see cref="ObjectDisposedException"/> after Dispose.</item>
/// </list>
/// <para><b>Usage pattern:</b></para>
/// <code>
/// using var handle = NativeKeyHandle.FromBytes(keyBytes);
/// // Zero the original managed array immediately
/// CryptographicOperations.ZeroMemory(keyBytes);
/// // Use handle.KeySpan for crypto operations
/// aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, handle.KeySpan);
/// // handle is securely wiped on Dispose
/// </code>
/// <para><b>WARNING:</b> Do not call <see cref="Span{T}.ToArray"/> on <see cref="KeySpan"/> —
/// that defeats the purpose by copying key material back to the managed heap.</para>
/// </remarks>
public sealed class NativeKeyHandle : IDisposable
{
    private unsafe byte* _pointer;
    private readonly int _length;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="NativeKeyHandle"/> with the specified size.
    /// Memory is zero-initialized via <see cref="NativeMemory.AllocZeroed"/>.
    /// </summary>
    /// <param name="length">The key size in bytes. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is less than or equal to zero.</exception>
    private unsafe NativeKeyHandle(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _length = length;
        _pointer = (byte*)NativeMemory.AllocZeroed((nuint)length);
    }

    /// <summary>
    /// Gets the length of the key in bytes.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Gets a <see cref="Span{T}"/> over the key material for zero-copy access.
    /// The span is valid only while this handle is not disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the handle has been disposed.</exception>
    /// <remarks>
    /// <b>WARNING:</b> Do not call <c>.ToArray()</c> on the returned span — that copies
    /// key material to the managed heap, defeating the security purpose of this type.
    /// </remarks>
    public unsafe Span<byte> KeySpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<byte>(_pointer, _length);
        }
    }

    /// <summary>
    /// Creates a <see cref="NativeKeyHandle"/> from a managed byte array.
    /// The key bytes are copied into unmanaged memory. The caller should zero
    /// the source array immediately after this call using
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>.
    /// </summary>
    /// <param name="key">The key bytes to copy into native memory.</param>
    /// <returns>A new <see cref="NativeKeyHandle"/> containing the key material.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public static NativeKeyHandle FromBytes(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        return FromBytes((ReadOnlySpan<byte>)key);
    }

    /// <summary>
    /// Creates a <see cref="NativeKeyHandle"/> from a <see cref="ReadOnlySpan{T}"/>.
    /// The key bytes are copied into unmanaged memory. If the source is a managed array,
    /// the caller should zero it immediately after this call.
    /// </summary>
    /// <param name="key">The key bytes to copy into native memory.</param>
    /// <returns>A new <see cref="NativeKeyHandle"/> containing the key material.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public static unsafe NativeKeyHandle FromBytes(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        var handle = new NativeKeyHandle(key.Length);
        key.CopyTo(new Span<byte>(handle._pointer, handle._length));
        return handle;
    }

    /// <summary>
    /// Securely wipes the key material from memory and frees the unmanaged allocation.
    /// After this call, <see cref="KeySpan"/> will throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public unsafe void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_pointer != null)
        {
            // Zero the memory before freeing to prevent key material leakage
            NativeMemory.Clear(_pointer, (nuint)_length);
            NativeMemory.Free(_pointer);
            _pointer = null;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer ensures key material is wiped even if Dispose is not called.
    /// This is a safety net — callers should always use the <c>using</c> pattern.
    /// </summary>
    ~NativeKeyHandle()
    {
        Dispose();
    }
}
