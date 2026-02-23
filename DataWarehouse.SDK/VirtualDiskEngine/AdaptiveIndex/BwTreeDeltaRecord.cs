using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Enumerates the types of delta records that can be prepended to Bw-Tree page chains.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public enum DeltaRecordType
{
    /// <summary>Key-value insertion delta.</summary>
    Insert,

    /// <summary>Key deletion delta.</summary>
    Delete,

    /// <summary>Key-value update delta.</summary>
    Update,

    /// <summary>Page split delta (creates new sibling).</summary>
    Split,

    /// <summary>Page merge delta (absorbs sibling).</summary>
    Merge,

    /// <summary>Node removal delta (after merge completes).</summary>
    RemoveNode,

    /// <summary>Consolidated base page replacing a delta chain.</summary>
    Consolidation
}

/// <summary>
/// Abstract base class for Bw-Tree delta records that form append-only modification chains.
/// </summary>
/// <remarks>
/// Delta records are prepended to page chains via CAS on the mapping table.
/// Each record is immutable after creation - fields are readonly.
/// The chain terminates at either a <see cref="ConsolidationRecord{TKey, TValue}"/>
/// (consolidated base page) or null (empty page).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public abstract class BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Gets the type of this delta record.
    /// </summary>
    public abstract DeltaRecordType Type { get; }

    /// <summary>
    /// Gets the next record in the delta chain (toward the base page).
    /// Null indicates the end of the chain.
    /// </summary>
    public BwTreeDeltaRecord<TKey, TValue>? Next { get; }

    /// <summary>
    /// Initializes a new delta record with the specified chain continuation.
    /// </summary>
    /// <param name="next">The next record in the delta chain, or null if this is the terminal record.</param>
    protected BwTreeDeltaRecord(BwTreeDeltaRecord<TKey, TValue>? next)
    {
        Next = next;
    }
}

/// <summary>
/// Delta record representing a key-value insertion into a Bw-Tree page.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class InsertDeltaRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.Insert;

    /// <summary>
    /// Gets the key being inserted.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// Gets the value being inserted.
    /// </summary>
    public TValue Value { get; }

    /// <summary>
    /// Creates an insert delta record.
    /// </summary>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to insert.</param>
    /// <param name="next">The next record in the delta chain.</param>
    public InsertDeltaRecord(TKey key, TValue value, BwTreeDeltaRecord<TKey, TValue>? next)
        : base(next)
    {
        Key = key;
        Value = value;
    }
}

/// <summary>
/// Delta record representing a key deletion from a Bw-Tree page.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class DeleteDeltaRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.Delete;

    /// <summary>
    /// Gets the key being deleted.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// Creates a delete delta record.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="next">The next record in the delta chain.</param>
    public DeleteDeltaRecord(TKey key, BwTreeDeltaRecord<TKey, TValue>? next)
        : base(next)
    {
        Key = key;
    }
}

/// <summary>
/// Delta record representing a key-value update on a Bw-Tree page.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class UpdateDeltaRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.Update;

    /// <summary>
    /// Gets the key being updated.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// Gets the new value for the key.
    /// </summary>
    public TValue NewValue { get; }

    /// <summary>
    /// Creates an update delta record.
    /// </summary>
    /// <param name="key">The key to update.</param>
    /// <param name="newValue">The new value.</param>
    /// <param name="next">The next record in the delta chain.</param>
    public UpdateDeltaRecord(TKey key, TValue newValue, BwTreeDeltaRecord<TKey, TValue>? next)
        : base(next)
    {
        Key = key;
        NewValue = newValue;
    }
}

/// <summary>
/// Delta record representing a page split in the Bw-Tree.
/// </summary>
/// <remarks>
/// Part of the two-phase split protocol: (1) split delta on child page,
/// (2) index delta on parent page. The separator key divides entries between
/// the original page and the new sibling.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class SplitDeltaRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.Split;

    /// <summary>
    /// Gets the separator key that divides entries between the original and new sibling pages.
    /// Keys >= separator go to the new sibling.
    /// </summary>
    public TKey SeparatorKey { get; }

    /// <summary>
    /// Gets the logical page ID of the new sibling page.
    /// </summary>
    public long NewSiblingPageId { get; }

    /// <summary>
    /// Creates a split delta record.
    /// </summary>
    /// <param name="separatorKey">The key that separates entries between pages.</param>
    /// <param name="newSiblingPageId">Logical page ID of the new sibling.</param>
    /// <param name="next">The next record in the delta chain.</param>
    public SplitDeltaRecord(TKey separatorKey, long newSiblingPageId, BwTreeDeltaRecord<TKey, TValue>? next)
        : base(next)
    {
        SeparatorKey = separatorKey;
        NewSiblingPageId = newSiblingPageId;
    }
}

/// <summary>
/// Delta record representing a page merge in the Bw-Tree.
/// </summary>
/// <remarks>
/// Reverse of split: the merged page's entries are absorbed into this page.
/// The merged page is subsequently marked with a <see cref="RemoveNodeDeltaRecord{TKey, TValue}"/>.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class MergeDeltaRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.Merge;

    /// <summary>
    /// Gets the logical page ID of the page being merged into this one.
    /// </summary>
    public long MergedPageId { get; }

    /// <summary>
    /// Gets the delta chain of the merged page (snapshot at merge time).
    /// </summary>
    public BwTreeDeltaRecord<TKey, TValue>? MergedChain { get; }

    /// <summary>
    /// Creates a merge delta record.
    /// </summary>
    /// <param name="mergedPageId">Logical page ID of the page being merged.</param>
    /// <param name="mergedChain">The delta chain of the merged page.</param>
    /// <param name="next">The next record in the delta chain.</param>
    public MergeDeltaRecord(long mergedPageId, BwTreeDeltaRecord<TKey, TValue>? mergedChain, BwTreeDeltaRecord<TKey, TValue>? next)
        : base(next)
    {
        MergedPageId = mergedPageId;
        MergedChain = mergedChain;
    }
}

/// <summary>
/// Delta record marking a node for removal after a merge operation completes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class RemoveNodeDeltaRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.RemoveNode;

    /// <summary>
    /// Creates a remove-node delta record.
    /// </summary>
    /// <param name="next">The next record in the delta chain.</param>
    public RemoveNodeDeltaRecord(BwTreeDeltaRecord<TKey, TValue>? next)
        : base(next)
    {
    }
}

/// <summary>
/// Terminal delta record holding a consolidated base page with sorted entries and child pointers.
/// </summary>
/// <remarks>
/// Created during consolidation when a delta chain exceeds the configured threshold.
/// This record replaces the entire delta chain and serves as the new base page.
/// Contains the materialized view of all preceding deltas applied to the previous base.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree delta records")]
public sealed class ConsolidationRecord<TKey, TValue> : BwTreeDeltaRecord<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <inheritdoc />
    public override DeltaRecordType Type => DeltaRecordType.Consolidation;

    /// <summary>
    /// Gets the sorted key-value entries in this consolidated page.
    /// </summary>
    public (TKey Key, TValue Value)[] Entries { get; }

    /// <summary>
    /// Gets the child page IDs for internal nodes. Empty for leaf nodes.
    /// </summary>
    public long[] Children { get; }

    /// <summary>
    /// Gets whether this consolidated page is a leaf node.
    /// </summary>
    public bool IsLeaf { get; }

    /// <summary>
    /// Gets the logical page ID of the right sibling, or -1 if none.
    /// </summary>
    public long RightSiblingPageId { get; }

    /// <summary>
    /// Creates a consolidation record with sorted entries.
    /// </summary>
    /// <param name="entries">Sorted key-value entries.</param>
    /// <param name="children">Child page IDs (empty for leaf nodes).</param>
    /// <param name="isLeaf">Whether this is a leaf page.</param>
    /// <param name="rightSiblingPageId">Right sibling page ID, or -1.</param>
    public ConsolidationRecord(
        (TKey Key, TValue Value)[] entries,
        long[] children,
        bool isLeaf,
        long rightSiblingPageId = -1)
        : base(null) // Terminal record - no next
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        Children = children ?? throw new ArgumentNullException(nameof(children));
        IsLeaf = isLeaf;
        RightSiblingPageId = rightSiblingPageId;
    }
}
