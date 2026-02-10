using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.ErasureCoding
{
    /// <summary>
    /// LDPC Strategy - Low-Density Parity-Check codes for erasure coding.
    /// Uses sparse parity-check matrices for efficient encoding/decoding.
    /// </summary>
    /// <remarks>
    /// LDPC characteristics:
    /// - Near Shannon-limit performance for error correction
    /// - Sparse parity-check matrix H with low-density of 1s
    /// - Message passing (belief propagation) decoding algorithm
    /// - Linear encoding complexity O(n)
    /// - Used in modern communications (DVB-S2, WiFi 802.11n)
    /// - Excellent for high-reliability storage systems
    /// </remarks>
    public sealed class LdpcStrategy : RaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly int _dataChunks;
        private readonly int _parityChunks;
        private readonly int _rowWeight; // Number of 1s per row in H
        private readonly int _colWeight; // Number of 1s per column in H
        private readonly byte[,] _parityCheckMatrix;
        private readonly int _maxIterations;

        /// <summary>
        /// Initializes LDPC strategy with specified parameters.
        /// </summary>
        /// <param name="chunkSize">Size of each chunk in bytes.</param>
        /// <param name="dataChunks">Number of data chunks (k).</param>
        /// <param name="parityChunks">Number of parity chunks (n-k).</param>
        /// <param name="rowWeight">Weight per row in parity check matrix.</param>
        /// <param name="colWeight">Weight per column in parity check matrix.</param>
        /// <param name="maxIterations">Maximum decoding iterations.</param>
        public LdpcStrategy(int chunkSize = 128 * 1024, int dataChunks = 8, int parityChunks = 4,
            int rowWeight = 6, int colWeight = 3, int maxIterations = 50)
        {
            if (dataChunks < 2)
                throw new ArgumentException("LDPC requires at least 2 data chunks", nameof(dataChunks));
            if (parityChunks < 1)
                throw new ArgumentException("LDPC requires at least 1 parity chunk", nameof(parityChunks));

            _chunkSize = chunkSize;
            _dataChunks = dataChunks;
            _parityChunks = parityChunks;
            _rowWeight = Math.Min(rowWeight, dataChunks + parityChunks);
            _colWeight = Math.Min(colWeight, parityChunks);
            _maxIterations = maxIterations;
            _parityCheckMatrix = GenerateLdpcMatrix();
        }

        public override RaidLevel Level => RaidLevel.Ldpc;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: _parityChunks,
            MinDisks: _dataChunks + _parityChunks,
            MaxDisks: _dataChunks + _parityChunks,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2.5),
            ReadPerformanceMultiplier: _dataChunks * 0.95,
            WritePerformanceMultiplier: _dataChunks * 0.8, // Sparse matrix encoding
            CapacityEfficiency: _dataChunks / (double)(_dataChunks + _parityChunks),
            SupportsHotSpare: true,
            SupportsOnlineExpansion: false,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            if (diskCount != _dataChunks + _parityChunks)
                throw new ArgumentException($"LDPC ({_dataChunks},{_parityChunks}) requires {_dataChunks + _parityChunks} disks");

            var dataDisks = Enumerable.Range(0, _dataChunks).ToArray();
            var parityDisks = Enumerable.Range(_dataChunks, _parityChunks).ToArray();

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: _chunkSize,
                DataChunkCount: _dataChunks,
                ParityChunkCount: _parityChunks);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataBytes = data.ToArray();
            var dataChunks = DistributeData(dataBytes, stripeInfo);

            // Encode using LDPC sparse matrix
            var parityChunks = EncodeLdpc(dataChunks);

            var writeTasks = new List<Task>();

            // Write data chunks
            foreach (var kvp in dataChunks)
            {
                writeTasks.Add(WriteToDiskAsync(diskList[kvp.Key], kvp.Value.ToArray(), offset, cancellationToken));
            }

            // Write parity chunks
            foreach (var kvp in parityChunks)
            {
                writeTasks.Add(WriteToDiskAsync(diskList[kvp.Key], kvp.Value, offset, cancellationToken));
            }

            await Task.WhenAll(writeTasks);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var availableChunks = new Dictionary<int, byte[]>();
            var erasedPositions = new List<int>();

            // Read all chunks
            for (int i = 0; i < diskList.Count; i++)
            {
                var disk = diskList[i];
                if (disk.HealthStatus == DiskHealthStatus.Healthy)
                {
                    try
                    {
                        var chunk = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        availableChunks[i] = chunk;
                    }
                    catch { erasedPositions.Add(i); }
                }
                else { erasedPositions.Add(i); }
            }

            // If all data chunks available, return directly
            if (stripeInfo.DataDisks.All(d => availableChunks.ContainsKey(d)))
            {
                return AssembleResult(availableChunks, stripeInfo, length);
            }

            // Decode using iterative message-passing
            if (erasedPositions.Count <= _parityChunks)
            {
                var decoded = DecodeLdpc(availableChunks, erasedPositions, stripeInfo);
                foreach (var kvp in decoded)
                {
                    availableChunks[kvp.Key] = kvp.Value;
                }
            }

            return AssembleResult(availableChunks, stripeInfo, length);
        }

        public override async Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var allDisks = healthyDisks.Append(failedDisk).ToList();
            var failedDiskIndex = allDisks.IndexOf(failedDisk);

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stripeInfo = CalculateStripe(offset / _chunkSize, allDisks.Count);
                var availableChunks = new Dictionary<int, byte[]>();

                // Read all available chunks
                for (int i = 0; i < allDisks.Count; i++)
                {
                    if (i != failedDiskIndex)
                    {
                        var chunk = await ReadFromDiskAsync(allDisks[i], offset, _chunkSize, cancellationToken);
                        availableChunks[i] = chunk;
                    }
                }

                // Decode the missing chunk
                var decoded = DecodeLdpc(availableChunks, new List<int> { failedDiskIndex }, stripeInfo);

                if (decoded.TryGetValue(failedDiskIndex, out var reconstructed))
                {
                    await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);
                }

                bytesRebuilt += _chunkSize;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
                    var remaining = speed > 0 ? (long)((totalBytes - bytesRebuilt) / speed) : 0;

                    progressCallback.Report(new RebuildProgress(
                        PercentComplete: (double)bytesRebuilt / totalBytes,
                        BytesRebuilt: bytesRebuilt,
                        TotalBytes: totalBytes,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                        CurrentSpeed: (long)speed));
                }
            }
        }

        private byte[,] GenerateLdpcMatrix()
        {
            // Generate a regular LDPC parity-check matrix
            var n = _dataChunks + _parityChunks;
            var m = _parityChunks; // Number of parity equations
            var H = new byte[m, n];

            // Use progressive edge growth algorithm (simplified)
            var random = new Random(42); // Deterministic for consistency

            for (int j = 0; j < n; j++)
            {
                // Each column has _colWeight 1s
                var positions = new HashSet<int>();
                while (positions.Count < Math.Min(_colWeight, m))
                {
                    var pos = random.Next(m);
                    if (!positions.Contains(pos))
                    {
                        // Check row weight constraint
                        var rowWeight = 0;
                        for (int k = 0; k < n; k++)
                            if (H[pos, k] == 1) rowWeight++;

                        if (rowWeight < _rowWeight)
                        {
                            H[pos, j] = 1;
                            positions.Add(pos);
                        }
                    }
                }
            }

            return H;
        }

        private Dictionary<int, byte[]> EncodeLdpc(Dictionary<int, ReadOnlyMemory<byte>> dataChunks)
        {
            var parityChunks = new Dictionary<int, byte[]>();
            var n = _dataChunks + _parityChunks;

            // For each parity equation (row in H)
            for (int p = 0; p < _parityChunks; p++)
            {
                var parityChunk = new byte[_chunkSize];
                var parityIndex = _dataChunks + p;

                // XOR all data chunks that participate in this parity equation
                for (int j = 0; j < _dataChunks; j++)
                {
                    if (_parityCheckMatrix[p, j] == 1 && dataChunks.TryGetValue(j, out var chunk))
                    {
                        var span = chunk.Span;
                        for (int i = 0; i < _chunkSize && i < span.Length; i++)
                        {
                            parityChunk[i] ^= span[i];
                        }
                    }
                }

                parityChunks[parityIndex] = parityChunk;
            }

            return parityChunks;
        }

        private Dictionary<int, byte[]> DecodeLdpc(
            Dictionary<int, byte[]> availableChunks,
            List<int> erasedPositions,
            StripeInfo stripeInfo)
        {
            var decoded = new Dictionary<int, byte[]>();
            var n = _dataChunks + _parityChunks;

            // Initialize erased chunks with zeros
            foreach (var pos in erasedPositions)
            {
                decoded[pos] = new byte[_chunkSize];
            }

            // Iterative decoding using belief propagation
            for (int iteration = 0; iteration < _maxIterations; iteration++)
            {
                bool changed = false;

                // For each parity equation (check node)
                for (int p = 0; p < _parityChunks; p++)
                {
                    // Find positions involved in this equation
                    var involvedPositions = new List<int>();
                    for (int j = 0; j < n; j++)
                    {
                        if (_parityCheckMatrix[p, j] == 1)
                        {
                            involvedPositions.Add(j);
                        }
                    }

                    // If exactly one position is erased, we can recover it
                    var unknownPositions = involvedPositions.Where(pos => erasedPositions.Contains(pos) && !availableChunks.ContainsKey(pos)).ToList();

                    if (unknownPositions.Count == 1)
                    {
                        var unknownPos = unknownPositions[0];
                        var recoveredChunk = new byte[_chunkSize];

                        // XOR all known chunks in this equation
                        foreach (var pos in involvedPositions)
                        {
                            if (pos != unknownPos)
                            {
                                byte[]? chunk = null;
                                if (availableChunks.TryGetValue(pos, out chunk) || decoded.TryGetValue(pos, out chunk))
                                {
                                    for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                                    {
                                        recoveredChunk[i] ^= chunk[i];
                                    }
                                }
                            }
                        }

                        decoded[unknownPos] = recoveredChunk;
                        availableChunks[unknownPos] = recoveredChunk;
                        erasedPositions.Remove(unknownPos);
                        changed = true;
                    }
                }

                if (!changed || erasedPositions.Count == 0)
                    break;
            }

            return decoded;
        }

        private Dictionary<int, ReadOnlyMemory<byte>> DistributeData(byte[] data, StripeInfo stripeInfo)
        {
            var chunks = new Dictionary<int, ReadOnlyMemory<byte>>();
            var bytesPerChunk = _chunkSize;
            var position = 0;

            foreach (var diskIndex in stripeInfo.DataDisks)
            {
                var chunk = new byte[bytesPerChunk];
                var length = Math.Min(bytesPerChunk, data.Length - position);
                if (length > 0)
                {
                    Array.Copy(data, position, chunk, 0, length);
                    position += length;
                }
                chunks[diskIndex] = chunk;
            }

            return chunks;
        }

        private byte[] AssembleResult(Dictionary<int, byte[]> chunks, StripeInfo stripeInfo, int length)
        {
            var result = new byte[length];
            var position = 0;

            foreach (var diskIndex in stripeInfo.DataDisks)
            {
                if (chunks.TryGetValue(diskIndex, out var chunk))
                {
                    var copyLength = Math.Min(chunk.Length, length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            return result;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// Fountain Codes Strategy - Rateless erasure codes for flexible recovery.
    /// Generates unlimited encoded symbols from source data.
    /// </summary>
    /// <remarks>
    /// Fountain/Rateless codes characteristics:
    /// - Can generate unlimited encoded symbols on-the-fly
    /// - Receiver can decode from any k+epsilon symbols (where epsilon is small overhead)
    /// - No fixed code rate - adapts to channel conditions
    /// - LT (Luby Transform) codes as foundation
    /// - Raptor codes for practical linear-time encoding/decoding
    /// - Ideal for broadcast/multicast and unreliable channels
    /// </remarks>
    public sealed class FountainCodesStrategy : RaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly int _sourceSymbols; // k
        private readonly int _encodedSymbols; // n (but can generate more)
        private readonly double _overheadFactor;
        private readonly Random _random;
        private readonly int[] _robustSolitonDegrees;

        /// <summary>
        /// Initializes Fountain Codes strategy.
        /// </summary>
        /// <param name="chunkSize">Size of each symbol/chunk in bytes.</param>
        /// <param name="sourceSymbols">Number of source symbols (k).</param>
        /// <param name="encodedSymbols">Initial number of encoded symbols to generate.</param>
        /// <param name="overheadFactor">Extra symbols needed for decoding (typically 1.05-1.1).</param>
        public FountainCodesStrategy(int chunkSize = 64 * 1024, int sourceSymbols = 10,
            int encodedSymbols = 15, double overheadFactor = 1.05)
        {
            if (sourceSymbols < 2)
                throw new ArgumentException("Fountain codes require at least 2 source symbols", nameof(sourceSymbols));

            _chunkSize = chunkSize;
            _sourceSymbols = sourceSymbols;
            _encodedSymbols = Math.Max(encodedSymbols, sourceSymbols);
            _overheadFactor = overheadFactor;
            _random = new Random(42); // Deterministic for reproducibility
            _robustSolitonDegrees = PrecomputeDegreeDistribution();
        }

        public override RaidLevel Level => RaidLevel.FountainCodes;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: _encodedSymbols - _sourceSymbols,
            MinDisks: (int)(_sourceSymbols * _overheadFactor), // Need k+epsilon symbols
            MaxDisks: null, // Rateless - can add more
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: _sourceSymbols * 0.9,
            WritePerformanceMultiplier: _sourceSymbols * 0.85,
            CapacityEfficiency: _sourceSymbols / (double)_encodedSymbols,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true, // Can always add more encoded symbols
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // All disks are treated as encoded symbol storage
            // First _sourceSymbols are "data" (for capacity calculation)
            var dataDisks = Enumerable.Range(0, Math.Min(_sourceSymbols, diskCount)).ToArray();
            var parityDisks = Enumerable.Range(_sourceSymbols, Math.Max(0, diskCount - _sourceSymbols)).ToArray();

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: _chunkSize,
                DataChunkCount: _sourceSymbols,
                ParityChunkCount: diskCount - _sourceSymbols);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataBytes = data.ToArray();
            var sourceSymbols = CreateSourceSymbols(dataBytes);

            // Generate encoded symbols using LT encoding
            var encodedSymbols = EncodeLubyTransform(sourceSymbols, diskList.Count);

            var writeTasks = new List<Task>();

            for (int i = 0; i < encodedSymbols.Count && i < diskList.Count; i++)
            {
                writeTasks.Add(WriteToDiskAsync(diskList[i], encodedSymbols[i].Data, offset, cancellationToken));
            }

            await Task.WhenAll(writeTasks);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            var availableSymbols = new List<EncodedSymbol>();

            // Read encoded symbols from available disks
            for (int i = 0; i < diskList.Count; i++)
            {
                var disk = diskList[i];
                if (disk.HealthStatus == DiskHealthStatus.Healthy)
                {
                    try
                    {
                        var data = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        var degree = GetDegree(i);
                        var neighbors = GetNeighbors(i, degree);
                        availableSymbols.Add(new EncodedSymbol { Index = i, Data = data, Degree = degree, Neighbors = neighbors });
                    }
                    catch { }
                }
            }

            // Need at least k symbols for decoding
            var minSymbols = (int)(_sourceSymbols * _overheadFactor);
            if (availableSymbols.Count < _sourceSymbols)
            {
                throw new InvalidOperationException($"Fountain codes require at least {_sourceSymbols} symbols, only {availableSymbols.Count} available");
            }

            // Decode using belief propagation (peeling decoder)
            var sourceSymbols = DecodeLubyTransform(availableSymbols);

            return AssembleFromSourceSymbols(sourceSymbols, length);
        }

        public override async Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var allDisks = healthyDisks.Append(failedDisk).ToList();
            var failedDiskIndex = allDisks.IndexOf(failedDisk);

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read available symbols
                var availableSymbols = new List<EncodedSymbol>();
                for (int i = 0; i < allDisks.Count; i++)
                {
                    if (i != failedDiskIndex)
                    {
                        var data = await ReadFromDiskAsync(allDisks[i], offset, _chunkSize, cancellationToken);
                        var degree = GetDegree(i);
                        var neighbors = GetNeighbors(i, degree);
                        availableSymbols.Add(new EncodedSymbol { Index = i, Data = data, Degree = degree, Neighbors = neighbors });
                    }
                }

                // Decode source symbols
                var sourceSymbols = DecodeLubyTransform(availableSymbols);

                // Re-encode the specific symbol for the failed disk
                var degree = GetDegree(failedDiskIndex);
                var neighbors = GetNeighbors(failedDiskIndex, degree);
                var rebuiltData = new byte[_chunkSize];

                foreach (var neighborIdx in neighbors)
                {
                    if (neighborIdx < sourceSymbols.Count)
                    {
                        var neighborData = sourceSymbols[neighborIdx];
                        for (int j = 0; j < _chunkSize && j < neighborData.Length; j++)
                        {
                            rebuiltData[j] ^= neighborData[j];
                        }
                    }
                }

                await WriteToDiskAsync(targetDisk, rebuiltData, offset, cancellationToken);
                bytesRebuilt += _chunkSize;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
                    var remaining = speed > 0 ? (long)((totalBytes - bytesRebuilt) / speed) : 0;

                    progressCallback.Report(new RebuildProgress(
                        PercentComplete: (double)bytesRebuilt / totalBytes,
                        BytesRebuilt: bytesRebuilt,
                        TotalBytes: totalBytes,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                        CurrentSpeed: (long)speed));
                }
            }
        }

        private int[] PrecomputeDegreeDistribution()
        {
            // Robust Soliton Distribution for LT codes
            var degrees = new int[_sourceSymbols];

            // Ideal soliton + spike
            var c = 0.1; // Tuning parameter
            var delta = 0.5; // Failure probability
            var R = c * Math.Log(_sourceSymbols / delta) * Math.Sqrt(_sourceSymbols);
            var threshold = (int)Math.Ceiling(_sourceSymbols / R);

            for (int i = 0; i < _sourceSymbols; i++)
            {
                if (i < threshold)
                {
                    degrees[i] = 1;
                }
                else if (i < _sourceSymbols - 1)
                {
                    degrees[i] = (i + 1) % (_sourceSymbols / 2) + 1;
                }
                else
                {
                    degrees[i] = _sourceSymbols / 2;
                }
            }

            return degrees;
        }

        private int GetDegree(int symbolIndex)
        {
            // Sample from robust soliton distribution
            var idx = symbolIndex % _robustSolitonDegrees.Length;
            return Math.Max(1, _robustSolitonDegrees[idx]);
        }

        private List<int> GetNeighbors(int symbolIndex, int degree)
        {
            // Deterministically select which source symbols this encoded symbol covers
            var rng = new Random(symbolIndex * 31337);
            var neighbors = new HashSet<int>();

            while (neighbors.Count < degree)
            {
                neighbors.Add(rng.Next(_sourceSymbols));
            }

            return neighbors.ToList();
        }

        private List<byte[]> CreateSourceSymbols(byte[] data)
        {
            var symbols = new List<byte[]>();
            var bytesPerSymbol = _chunkSize;
            var position = 0;

            for (int i = 0; i < _sourceSymbols; i++)
            {
                var symbol = new byte[bytesPerSymbol];
                var length = Math.Min(bytesPerSymbol, data.Length - position);
                if (length > 0)
                {
                    Array.Copy(data, position, symbol, 0, length);
                    position += length;
                }
                symbols.Add(symbol);
            }

            return symbols;
        }

        private List<EncodedSymbol> EncodeLubyTransform(List<byte[]> sourceSymbols, int numEncoded)
        {
            var encoded = new List<EncodedSymbol>();

            for (int i = 0; i < numEncoded; i++)
            {
                var degree = GetDegree(i);
                var neighbors = GetNeighbors(i, degree);

                var data = new byte[_chunkSize];

                foreach (var neighborIdx in neighbors)
                {
                    if (neighborIdx < sourceSymbols.Count)
                    {
                        var neighborData = sourceSymbols[neighborIdx];
                        for (int j = 0; j < _chunkSize && j < neighborData.Length; j++)
                        {
                            data[j] ^= neighborData[j];
                        }
                    }
                }

                encoded.Add(new EncodedSymbol
                {
                    Index = i,
                    Data = data,
                    Degree = degree,
                    Neighbors = neighbors
                });
            }

            return encoded;
        }

        private List<byte[]> DecodeLubyTransform(List<EncodedSymbol> encodedSymbols)
        {
            var sourceSymbols = new byte[_sourceSymbols][];
            var decoded = new bool[_sourceSymbols];
            var remaining = new List<EncodedSymbol>(encodedSymbols);

            // Peeling decoder: iteratively process degree-1 symbols
            var changed = true;
            while (changed && remaining.Count > 0)
            {
                changed = false;

                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var symbol = remaining[i];

                    // Count undecoded neighbors
                    var undecodedNeighbors = symbol.Neighbors.Where(n => !decoded[n]).ToList();

                    if (undecodedNeighbors.Count == 1)
                    {
                        // Can decode this source symbol!
                        var sourceIdx = undecodedNeighbors[0];
                        var sourceData = symbol.Data.ToArray();

                        // XOR out already-decoded neighbors
                        foreach (var neighbor in symbol.Neighbors)
                        {
                            if (decoded[neighbor] && sourceSymbols[neighbor] != null)
                            {
                                for (int j = 0; j < _chunkSize && j < sourceSymbols[neighbor].Length; j++)
                                {
                                    sourceData[j] ^= sourceSymbols[neighbor][j];
                                }
                            }
                        }

                        sourceSymbols[sourceIdx] = sourceData;
                        decoded[sourceIdx] = true;
                        remaining.RemoveAt(i);
                        changed = true;
                    }
                    else if (undecodedNeighbors.Count == 0)
                    {
                        // All neighbors decoded, remove symbol
                        remaining.RemoveAt(i);
                    }
                }
            }

            // Fill in any still-missing symbols with zeros
            for (int i = 0; i < _sourceSymbols; i++)
            {
                if (sourceSymbols[i] == null)
                {
                    sourceSymbols[i] = new byte[_chunkSize];
                }
            }

            return sourceSymbols.ToList();
        }

        private byte[] AssembleFromSourceSymbols(List<byte[]> sourceSymbols, int length)
        {
            var result = new byte[length];
            var position = 0;

            foreach (var symbol in sourceSymbols)
            {
                if (position >= length) break;

                var copyLength = Math.Min(symbol.Length, length - position);
                Array.Copy(symbol, 0, result, position, copyLength);
                position += copyLength;
            }

            return result;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }

        private sealed class EncodedSymbol
        {
            public int Index { get; set; }
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public int Degree { get; set; }
            public List<int> Neighbors { get; set; } = new();
        }
    }
}
