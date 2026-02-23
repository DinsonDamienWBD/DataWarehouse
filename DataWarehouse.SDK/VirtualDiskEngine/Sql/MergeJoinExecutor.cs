using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Join type for merge join operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 merge join types")]
public enum JoinType
{
    /// <summary>Only emit rows where both sides have a matching key.</summary>
    Inner,

    /// <summary>Emit all left rows; right side is null when no match.</summary>
    LeftOuter,

    /// <summary>Emit all right rows; left side is null when no match.</summary>
    RightOuter,

    /// <summary>Emit all rows from both sides; null where no match on either side.</summary>
    FullOuter,
}

/// <summary>
/// Statistics for a merge join execution.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 merge join statistics")]
public readonly struct MergeJoinStats
{
    /// <summary>Number of rows scanned from the left input.</summary>
    public long RowsScannedLeft { get; init; }

    /// <summary>Number of rows scanned from the right input.</summary>
    public long RowsScannedRight { get; init; }

    /// <summary>Number of joined rows emitted.</summary>
    public long RowsEmitted { get; init; }

    /// <summary>Total elapsed time for the join execution.</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Performs merge join on two pre-sorted input streams from Be-tree / adaptive index range scans.
/// Both inputs must be sorted by the join key in the same order for correct results.
/// </summary>
/// <remarks>
/// <para>
/// The merge join algorithm advances both streams in lock-step, emitting joined pairs when keys match.
/// For many-to-many joins (duplicate keys), one side is buffered to produce the cross product.
/// </para>
/// <para>
/// This is optimal for sorted Be-tree range scans where both inputs are already sorted by join key,
/// achieving O(N + M) time complexity where N and M are the input sizes.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 merge join executor")]
public sealed class MergeJoinExecutor
{
    private readonly IAsyncEnumerable<(byte[] Key, byte[] Row)> _leftInput;
    private readonly IAsyncEnumerable<(byte[] Key, byte[] Row)> _rightInput;
    private readonly Func<byte[], byte[], int> _keyComparer;

    private long _rowsScannedLeft;
    private long _rowsScannedRight;
    private long _rowsEmitted;
    private TimeSpan _elapsed;

    /// <summary>
    /// The join type to perform. Default: <see cref="JoinType.Inner"/>.
    /// </summary>
    public JoinType JoinType { get; set; } = JoinType.Inner;

    /// <summary>
    /// Initializes a new merge join executor.
    /// </summary>
    /// <param name="leftInput">Left pre-sorted input stream of (Key, Row) pairs.</param>
    /// <param name="rightInput">Right pre-sorted input stream of (Key, Row) pairs.</param>
    /// <param name="keyComparer">Comparison function for join keys. Returns negative if left &lt; right, 0 if equal, positive if left &gt; right.</param>
    public MergeJoinExecutor(
        IAsyncEnumerable<(byte[] Key, byte[] Row)> leftInput,
        IAsyncEnumerable<(byte[] Key, byte[] Row)> rightInput,
        Func<byte[], byte[], int> keyComparer)
    {
        _leftInput = leftInput ?? throw new ArgumentNullException(nameof(leftInput));
        _rightInput = rightInput ?? throw new ArgumentNullException(nameof(rightInput));
        _keyComparer = keyComparer ?? throw new ArgumentNullException(nameof(keyComparer));
    }

    /// <summary>
    /// Executes the merge join, producing a stream of joined row pairs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of (LeftRow, RightRow) pairs. Null values indicate outer join non-matches.</returns>
    public async IAsyncEnumerable<(byte[]? LeftRow, byte[]? RightRow)> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        long leftCount = 0;
        long rightCount = 0;
        long emittedCount = 0;

        await using var leftEnum = _leftInput.GetAsyncEnumerator(ct);
        await using var rightEnum = _rightInput.GetAsyncEnumerator(ct);

        bool hasLeft = await leftEnum.MoveNextAsync().ConfigureAwait(false);
        bool hasRight = await rightEnum.MoveNextAsync().ConfigureAwait(false);

        if (hasLeft) leftCount++;
        if (hasRight) rightCount++;

        while (hasLeft && hasRight)
        {
            ct.ThrowIfCancellationRequested();

            int cmp = _keyComparer(leftEnum.Current.Key, rightEnum.Current.Key);

            if (cmp < 0)
            {
                // Left key < right key: advance left
                if (JoinType == JoinType.LeftOuter || JoinType == JoinType.FullOuter)
                {
                    emittedCount++;
                    yield return (leftEnum.Current.Row, null);
                }

                hasLeft = await leftEnum.MoveNextAsync().ConfigureAwait(false);
                if (hasLeft) leftCount++;
            }
            else if (cmp > 0)
            {
                // Left key > right key: advance right
                if (JoinType == JoinType.RightOuter || JoinType == JoinType.FullOuter)
                {
                    emittedCount++;
                    yield return (null, rightEnum.Current.Row);
                }

                hasRight = await rightEnum.MoveNextAsync().ConfigureAwait(false);
                if (hasRight) rightCount++;
            }
            else
            {
                // Keys match: handle many-to-many by buffering right duplicates
                var rightBuffer = new List<byte[]> { rightEnum.Current.Row };
                byte[] matchKey = rightEnum.Current.Key;

                hasRight = await rightEnum.MoveNextAsync().ConfigureAwait(false);
                if (hasRight) rightCount++;

                while (hasRight && _keyComparer(rightEnum.Current.Key, matchKey) == 0)
                {
                    rightBuffer.Add(rightEnum.Current.Row);
                    hasRight = await rightEnum.MoveNextAsync().ConfigureAwait(false);
                    if (hasRight) rightCount++;
                }

                // Emit cross product of matching left rows with buffered right rows
                do
                {
                    foreach (byte[] rightRow in rightBuffer)
                    {
                        emittedCount++;
                        yield return (leftEnum.Current.Row, rightRow);
                    }

                    hasLeft = await leftEnum.MoveNextAsync().ConfigureAwait(false);
                    if (hasLeft) leftCount++;
                }
                while (hasLeft && _keyComparer(leftEnum.Current.Key, matchKey) == 0);
            }
        }

        // Handle remaining rows for outer joins
        if (JoinType == JoinType.LeftOuter || JoinType == JoinType.FullOuter)
        {
            while (hasLeft)
            {
                emittedCount++;
                yield return (leftEnum.Current.Row, null);
                hasLeft = await leftEnum.MoveNextAsync().ConfigureAwait(false);
                if (hasLeft) leftCount++;
            }
        }

        if (JoinType == JoinType.RightOuter || JoinType == JoinType.FullOuter)
        {
            while (hasRight)
            {
                emittedCount++;
                yield return (null, rightEnum.Current.Row);
                hasRight = await rightEnum.MoveNextAsync().ConfigureAwait(false);
                if (hasRight) rightCount++;
            }
        }

        sw.Stop();
        _rowsScannedLeft = leftCount;
        _rowsScannedRight = rightCount;
        _rowsEmitted = emittedCount;
        _elapsed = sw.Elapsed;
    }

    /// <summary>
    /// Returns statistics from the last completed execution.
    /// </summary>
    public MergeJoinStats GetStats() => new()
    {
        RowsScannedLeft = Interlocked.Read(ref _rowsScannedLeft),
        RowsScannedRight = Interlocked.Read(ref _rowsScannedRight),
        RowsEmitted = Interlocked.Read(ref _rowsEmitted),
        Elapsed = _elapsed,
    };
}
