using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write Stage 7: journals metadata to the WAL and writes data blocks to the block
/// device. This stage always runs (no module gate) and is the final stage in the
/// write pipeline. It ensures crash-consistent writes by journaling before writing.
/// </summary>
/// <remarks>
/// <para>
/// The stage first creates a WAL transaction and appends the inode update entry.
/// Then for each extent in <see cref="VdePipelineContext.Extents"/>, it writes the
/// corresponding data blocks to the device. After all blocks are written, the
/// WAL transaction is flushed for durability.
/// </para>
/// <para>
/// TRLR entries (DataBlockTypeTag + GenerationNumber + XxHash64) are written as
/// trailers after each block for on-disk integrity verification during reads.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE write pipeline WAL block writer stage (VOPT-90)")]
public sealed class WalBlockWriterStage : IVdeWriteStage
{
    private readonly IBlockDevice _device;
    private readonly IWriteAheadLog _wal;

    /// <summary>Property key set to <c>true</c> after all blocks are written successfully.</summary>
    public const string WriteCompleteKey = "WriteComplete";

    /// <summary>
    /// Initializes a new <see cref="WalBlockWriterStage"/> with the given device and WAL.
    /// </summary>
    /// <param name="device">Block device to write data blocks to.</param>
    /// <param name="wal">Write-ahead log for crash-consistent journaling.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="wal"/> is null.</exception>
    public WalBlockWriterStage(IBlockDevice device, IWriteAheadLog wal)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
    }

    /// <inheritdoc />
    public string StageName => "WalBlockWriter";

    /// <inheritdoc />
    /// <remarks>Always null: WAL journaling and block writing are unconditional for every write.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public async Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        int blockSize = context.BlockSize > 0 ? context.BlockSize : _device.BlockSize;
        var dataBuffer = context.DataBuffer;

        // Begin WAL transaction for crash consistency
        await using var transaction = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Journal inode metadata update
        var inodeEntry = new JournalEntry
        {
            Type = JournalEntryType.InodeUpdate,
            TargetBlockNumber = context.Inode?.InodeNumber ?? context.InodeNumber,
            TransactionId = transaction.TransactionId,
        };
        await _wal.AppendEntryAsync(inodeEntry, ct).ConfigureAwait(false);

        // Read generation number from integrity stage (if available)
        context.TryGetProperty<long>(IntegrityCalculatorStage.TrlrGenerationKey, out var generation);

        // Write data blocks for each extent
        for (int i = 0; i < context.Extents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var extent = context.Extents[i];
            int dataOffset = (int)extent.LogicalOffset;

            for (int b = 0; b < extent.BlockCount; b++)
            {
                long blockNumber = extent.StartBlock + b;
                int bufferOffset = dataOffset + (b * blockSize);

                // Prepare block data (pad to block size if needed)
                ReadOnlyMemory<byte> blockData;
                if (bufferOffset + blockSize <= dataBuffer.Length)
                {
                    blockData = dataBuffer.Slice(bufferOffset, blockSize);
                }
                else if (bufferOffset < dataBuffer.Length)
                {
                    // Last partial block: pad with zeros
                    var padded = new byte[blockSize];
                    dataBuffer[bufferOffset..].Span.CopyTo(padded);
                    blockData = padded;
                }
                else
                {
                    // Beyond data: write zero block
                    blockData = new byte[blockSize];
                }

                // Journal the block write
                var blockEntry = new JournalEntry
                {
                    Type = JournalEntryType.BlockWrite,
                    TargetBlockNumber = blockNumber,
                    TransactionId = transaction.TransactionId,
                    AfterImage = blockData.ToArray(),
                };
                await _wal.AppendEntryAsync(blockEntry, ct).ConfigureAwait(false);

                // Write data block to device
                await _device.WriteBlockAsync(blockNumber, blockData, ct).ConfigureAwait(false);
            }
        }

        // Flush WAL for durability
        await _wal.FlushAsync(ct).ConfigureAwait(false);

        // Commit the transaction
        var commitEntry = new JournalEntry
        {
            Type = JournalEntryType.CommitTransaction,
            TargetBlockNumber = -1,
            TransactionId = transaction.TransactionId,
        };
        await _wal.AppendEntryAsync(commitEntry, ct).ConfigureAwait(false);
        await _wal.FlushAsync(ct).ConfigureAwait(false);

        // Signal write completion
        context.SetProperty(WriteCompleteKey, true);
    }
}
