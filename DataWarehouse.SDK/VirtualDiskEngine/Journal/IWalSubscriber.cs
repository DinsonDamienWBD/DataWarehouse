using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Zero-copy WAL subscription interface.
///
/// Exposes a filesystem-as-Kafka streaming model: subscribers read WAL mutations
/// after their current cursor position via <see cref="PollAsync"/>, then advance
/// that cursor via <see cref="AdvanceAsync"/> once entries have been durably processed.
///
/// Cursor state is persisted in the <see cref="WalSubscriberCursorTable"/> WALS region
/// (Module bit 21, <c>BlockTypeTags.WALS</c> = 0x57414C53).  Per-subscriber ACL
/// enforcement is applied via the <see cref="WalSubscriberFlags.AclRestricted"/> flag
/// and the policy vault before any entries are returned.
///
/// Thread safety: implementations MUST be safe for concurrent calls from multiple
/// threads.  However, callers SHOULD serialize <see cref="AdvanceAsync"/> against
/// <see cref="PollAsync"/> to avoid cursor skips.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: WALS zero-copy WAL subscription interface (VOPT-35)")]
public interface IWalSubscriber
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Unique subscriber identifier; matches the
    /// <see cref="WalSubscriberCursor.SubscriberId"/> stored in the WALS region.
    /// </summary>
    ulong SubscriberId { get; }

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when the subscriber is active (not paused and not
    /// unregistered).  Equivalent to checking that <see cref="WalSubscriberFlags.Active"/>
    /// is set and <see cref="WalSubscriberFlags.Paused"/> is clear.
    /// </summary>
    bool IsActive { get; }

    // ── Consumption ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads up to <paramref name="maxEntries"/> <see cref="JournalEntry"/> objects
    /// whose sequence numbers are strictly greater than the subscriber's current
    /// <see cref="WalSubscriberCursor.LastSequence"/>.
    ///
    /// Entries are returned in ascending sequence-number order.  If fewer than
    /// <paramref name="maxEntries"/> entries are available at the time of the call,
    /// all available entries are returned without blocking.
    ///
    /// ACL enforcement is applied before returning entries.  Entries that the
    /// subscriber is not authorised to observe are silently skipped.
    ///
    /// If <see cref="WalSubscriberFlags.AutoAdvance"/> is set, the cursor is advanced
    /// to the last returned entry's epoch and sequence number before this method returns.
    /// </summary>
    /// <param name="maxEntries">Maximum number of entries to return (must be &gt; 0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of journal entries; empty when no new entries are available.</returns>
    Task<IReadOnlyList<JournalEntry>> PollAsync(int maxEntries, CancellationToken ct = default);

    /// <summary>
    /// Atomically commits the subscriber's cursor position to
    /// (<paramref name="epoch"/>, <paramref name="sequence"/>), persisting the update
    /// to the WALS region.
    ///
    /// Callers SHOULD only advance to positions already returned by
    /// <see cref="PollAsync"/> to avoid skipping uncommitted entries.
    ///
    /// Subscribers whose <see cref="WalSubscriberFlags.ReadOnly"/> flag is set will
    /// receive an <see cref="System.InvalidOperationException"/> if they attempt to
    /// call this method.
    /// </summary>
    /// <param name="epoch">MVCC epoch of the last consumed entry.</param>
    /// <param name="sequence">WAL sequence number of the last consumed entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AdvanceAsync(ulong epoch, ulong sequence, CancellationToken ct = default);

    // ── Cursor inspection ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of the subscriber's current cursor state from the WALS
    /// region without modifying any state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="WalSubscriberCursor"/> for this subscriber.</returns>
    Task<WalSubscriberCursor> GetCursorAsync(CancellationToken ct = default);

    // ── Flow control ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the <see cref="WalSubscriberFlags.Paused"/> flag on the subscriber's cursor,
    /// causing <see cref="IsActive"/> to return <see langword="false"/>.
    ///
    /// WAL entries continue to accumulate during a pause; calling
    /// <see cref="ResumeAsync"/> followed by <see cref="PollAsync"/> will return all
    /// entries generated while paused (subject to WAL retention limits).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task PauseAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears the <see cref="WalSubscriberFlags.Paused"/> flag on the subscriber's cursor,
    /// re-enabling delivery and causing <see cref="IsActive"/> to return
    /// <see langword="true"/> (provided the <see cref="WalSubscriberFlags.Active"/> flag
    /// remains set).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task ResumeAsync(CancellationToken ct = default);
}
