using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Thread-safe routing table that maps domain hash slots to downstream VDE instance IDs.
/// Each slot in the ring is assigned to a VDE ID; unassigned slots contain <see cref="Guid.Empty"/>.
/// Uses <see cref="ReaderWriterLockSlim"/> for concurrency: reads are the hot path (many concurrent resolves),
/// writes are rare administrative operations (shard rebalancing, additions).
/// </summary>
/// <remarks>
/// Binary serialization format:
/// [0..3]   Magic (FederationMagic, UInt32 LE)
/// [4..5]   FormatVersion (UInt16 LE)
/// [6..9]   SlotCount (Int32 LE)
/// [10..]   SlotCount * 16 bytes (each slot is a GUID)
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Federation Router (VFED-03)")]
public sealed class RoutingTable : IDisposable
{
    private const int HeaderSize = 10; // 4 magic + 2 version + 4 slotCount

    private readonly Guid[] _slots;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <summary>
    /// Creates a new routing table with the specified number of slots.
    /// </summary>
    /// <param name="slotCount">Number of hash slots in the routing ring.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when slotCount is less than 1.</exception>
    public RoutingTable(int slotCount = FederationConstants.DefaultDomainSlotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        _slots = new Guid[slotCount];
    }

    /// <summary>
    /// Total number of slots in the routing ring.
    /// </summary>
    public int SlotCount => _slots.Length;

    /// <summary>
    /// Number of slots assigned to a non-empty VDE ID.
    /// </summary>
    public int ActiveSlotCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                int count = 0;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i] != Guid.Empty)
                        count++;
                }
                return count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Assigns a VDE ID to a specific slot.
    /// </summary>
    /// <param name="slot">Slot index (0-based).</param>
    /// <param name="vdeId">VDE instance identifier to assign.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when slot is out of range.</exception>
    public void AssignSlot(int slot, Guid vdeId)
    {
        ValidateSlotIndex(slot);
        _lock.EnterWriteLock();
        try
        {
            _slots[slot] = vdeId;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Resolves the VDE ID assigned to the given slot.
    /// </summary>
    /// <param name="slot">Slot index (0-based).</param>
    /// <returns>The assigned VDE ID, or <see cref="Guid.Empty"/> if unassigned.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when slot is out of range.</exception>
    public Guid Resolve(int slot)
    {
        ValidateSlotIndex(slot);
        _lock.EnterReadLock();
        try
        {
            return _slots[slot];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Assigns a contiguous range of slots to a VDE ID (inclusive start, exclusive end).
    /// Used for bulk shard range assignment during rebalancing.
    /// </summary>
    /// <param name="startSlot">First slot index (inclusive).</param>
    /// <param name="endSlot">Last slot index (exclusive).</param>
    /// <param name="vdeId">VDE instance identifier to assign.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when slot indices are out of range.</exception>
    /// <exception cref="ArgumentException">Thrown when startSlot >= endSlot.</exception>
    public void AssignRange(int startSlot, int endSlot, Guid vdeId)
    {
        if (startSlot < 0 || startSlot >= _slots.Length)
            throw new ArgumentOutOfRangeException(nameof(startSlot));
        if (endSlot < 0 || endSlot > _slots.Length)
            throw new ArgumentOutOfRangeException(nameof(endSlot));
        if (startSlot >= endSlot)
            throw new ArgumentException("startSlot must be less than endSlot.", nameof(startSlot));

        _lock.EnterWriteLock();
        try
        {
            Array.Fill(_slots, vdeId, startSlot, endSlot - startSlot);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns all contiguous slot ranges and their assigned VDE IDs.
    /// Adjacent slots with the same VDE ID are merged into a single range.
    /// </summary>
    /// <returns>List of (StartSlot, EndSlot, VdeId) tuples where EndSlot is exclusive.</returns>
    public IReadOnlyList<(int StartSlot, int EndSlot, Guid VdeId)> GetAssignments()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<(int, int, Guid)>();
            if (_slots.Length == 0)
                return result;

            int rangeStart = 0;
            Guid currentVde = _slots[0];

            for (int i = 1; i < _slots.Length; i++)
            {
                if (_slots[i] != currentVde)
                {
                    if (currentVde != Guid.Empty)
                        result.Add((rangeStart, i, currentVde));
                    rangeStart = i;
                    currentVde = _slots[i];
                }
            }

            // Final range
            if (currentVde != Guid.Empty)
                result.Add((rangeStart, _slots.Length, currentVde));

            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Serializes the routing table to a stream using BinaryPrimitives.
    /// </summary>
    /// <param name="stream">Target stream.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public void SerializeTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _lock.EnterReadLock();
        try
        {
            // Write header
            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], FederationConstants.FederationMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], FederationConstants.FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header[6..10], _slots.Length);
            stream.Write(header);

            // Write slots
            Span<byte> guidBuffer = stackalloc byte[16];
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].TryWriteBytes(guidBuffer);
                stream.Write(guidBuffer);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deserializes a routing table from a stream using BinaryPrimitives.
    /// </summary>
    /// <param name="stream">Source stream.</param>
    /// <returns>The deserialized routing table.</returns>
    /// <exception cref="InvalidDataException">Thrown when magic bytes or version do not match.</exception>
    public static RoutingTable DeserializeFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Read header
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(stream, header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        if (magic != FederationConstants.FederationMagic)
            throw new InvalidDataException($"Invalid federation magic: expected 0x{FederationConstants.FederationMagic:X8}, got 0x{magic:X8}.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]);
        if (version != FederationConstants.FormatVersion)
            throw new InvalidDataException($"Unsupported federation format version: expected {FederationConstants.FormatVersion}, got {version}.");

        int slotCount = BinaryPrimitives.ReadInt32LittleEndian(header[6..10]);
        if (slotCount <= 0)
            throw new InvalidDataException($"Invalid slot count: {slotCount}.");

        var table = new RoutingTable(slotCount);

        // Read slots
        Span<byte> guidBuffer = stackalloc byte[16];
        for (int i = 0; i < slotCount; i++)
        {
            ReadExact(stream, guidBuffer);
            table._slots[i] = new Guid(guidBuffer);
        }

        return table;
    }

    /// <summary>
    /// Disposes the internal reader-writer lock.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }

    private void ValidateSlotIndex(int slot)
    {
        if ((uint)slot >= (uint)_slots.Length)
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot index {slot} is out of range [0, {_slots.Length}).");
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer[totalRead..]);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading routing table.");
            totalRead += read;
        }
    }
}
