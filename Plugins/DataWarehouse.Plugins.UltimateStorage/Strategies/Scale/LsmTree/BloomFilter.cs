using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.IO.Hashing;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Probabilistic data structure for fast membership testing.
    /// Uses double hashing with XxHash64 and XxHash32 for bit positions.
    /// </summary>
    public sealed class BloomFilter
    {
        private readonly BitArray _bits;
        private readonly int _hashCount;
        private readonly int _bitCount;

        /// <summary>
        /// Initializes a new Bloom filter with the specified parameters.
        /// </summary>
        /// <param name="expectedItems">Expected number of items to be inserted.</param>
        /// <param name="falsePositiveRate">Target false positive rate (default 0.01 = 1%).</param>
        public BloomFilter(int expectedItems, double falsePositiveRate = 0.01)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedItems);

            if (falsePositiveRate <= 0 || falsePositiveRate >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));
            }

            // Calculate optimal bit count: m = -n * ln(p) / (ln(2)^2)
            _bitCount = (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));

            // Calculate optimal hash count: k = (m / n) * ln(2)
            _hashCount = Math.Max(1, (int)Math.Round(_bitCount * Math.Log(2) / expectedItems));

            _bits = new BitArray(_bitCount);
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private BloomFilter(BitArray bits, int hashCount, int bitCount)
        {
            _bits = bits;
            _hashCount = hashCount;
            _bitCount = bitCount;
        }

        /// <summary>
        /// Adds a key to the Bloom filter.
        /// </summary>
        /// <param name="key">Key to add.</param>
        public void Add(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var (hash1, hash2) = ComputeHashes(key);

            for (int i = 0; i < _hashCount; i++)
            {
                var bitIndex = (int)((hash1 + (ulong)i * hash2) % (ulong)_bitCount);
                _bits[bitIndex] = true;
            }
        }

        /// <summary>
        /// Tests whether a key may be in the set.
        /// </summary>
        /// <param name="key">Key to test.</param>
        /// <returns>False if definitely not present, true if possibly present.</returns>
        public bool MayContain(byte[] key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var (hash1, hash2) = ComputeHashes(key);

            for (int i = 0; i < _hashCount; i++)
            {
                var bitIndex = (int)((hash1 + (ulong)i * hash2) % (ulong)_bitCount);
                if (!_bits[bitIndex])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Computes two independent hash values using XxHash64 and XxHash32.
        /// </summary>
        private static (ulong hash1, ulong hash2) ComputeHashes(byte[] key)
        {
            var hash64Bytes = XxHash64.Hash(key);
            var hash64 = BinaryPrimitives.ReadUInt64LittleEndian(hash64Bytes);

            var hash32Bytes = XxHash32.Hash(key);
            var hash32 = BinaryPrimitives.ReadUInt32LittleEndian(hash32Bytes);

            return (hash64, hash32);
        }

        /// <summary>
        /// Serializes the Bloom filter to a stream.
        /// Format: [bitCount:4][hashCount:4][bits:bytes]
        /// </summary>
        public async Task SerializeAsync(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            // Write header
            var header = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), _bitCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), _hashCount);
            await stream.WriteAsync(header);

            // Write bit array
            var byteCount = (_bitCount + 7) / 8;
            var bytes = new byte[byteCount];
            _bits.CopyTo(bytes, 0);
            await stream.WriteAsync(bytes);
        }

        /// <summary>
        /// Deserializes a Bloom filter from a stream.
        /// </summary>
        public static async Task<BloomFilter> DeserializeAsync(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            // Read header
            var header = new byte[8];
            if (await stream.ReadAsync(header) != 8)
            {
                throw new InvalidDataException("Invalid Bloom filter header");
            }

            var bitCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            var hashCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

            if (bitCount <= 0 || hashCount <= 0)
            {
                throw new InvalidDataException("Invalid Bloom filter parameters");
            }

            // Read bit array
            var byteCount = (bitCount + 7) / 8;
            var bytes = new byte[byteCount];
            if (await stream.ReadAsync(bytes) != byteCount)
            {
                throw new InvalidDataException("Invalid Bloom filter data");
            }

            var bits = new BitArray(bytes)
            {
                Length = bitCount
            };

            return new BloomFilter(bits, hashCount, bitCount);
        }

        /// <summary>
        /// Gets the size of the Bloom filter in bytes.
        /// </summary>
        public int SizeInBytes => 8 + (_bitCount + 7) / 8;
    }
}
