namespace DataWarehouse.Plugins.SharedRaidUtilities
{
    /// <summary>
    /// High-level Reed-Solomon parity calculation and reconstruction helper for RAID systems.
    /// Provides simplified interfaces for common RAID parity operations using Galois Field arithmetic.
    /// </summary>
    public static class ReedSolomonHelper
    {
        private static readonly GaloisField _galoisField = new GaloisField();

        /// <summary>
        /// Calculates Z1 (P) parity - XOR of all data blocks.
        /// Used for RAID-5, RAID-Z1, and as the first parity in RAID-6/RAID-Z2/RAID-Z3.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks to calculate parity for.</param>
        /// <returns>Z1 parity block.</returns>
        public static byte[] CalculateZ1Parity(params byte[][] dataBlocks)
        {
            return _galoisField.CalculatePParity(dataBlocks);
        }

        /// <summary>
        /// Calculates Z2 (Q) parity - Reed-Solomon parity with generator 2.
        /// Used for RAID-6 and RAID-Z2 as the second parity level.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks to calculate parity for.</param>
        /// <returns>Z2 parity block.</returns>
        public static byte[] CalculateZ2Parity(params byte[][] dataBlocks)
        {
            return _galoisField.CalculateQParity(dataBlocks);
        }

        /// <summary>
        /// Calculates Z3 (R) parity - Reed-Solomon parity with generator 4.
        /// Used for RAID-Z3 as the third parity level.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks to calculate parity for.</param>
        /// <returns>Z3 parity block.</returns>
        public static byte[] CalculateZ3Parity(params byte[][] dataBlocks)
        {
            return _galoisField.CalculateRParity(dataBlocks);
        }

        /// <summary>
        /// Generates all parity blocks for a given number of parity levels (1-3).
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks.</param>
        /// <param name="parityCount">Number of parity blocks to generate (1-3).</param>
        /// <returns>Array of parity blocks.</returns>
        /// <exception cref="ArgumentOutOfRangeException">When parityCount is not between 1 and 3.</exception>
        public static byte[][] GenerateParityBlocks(byte[][] dataBlocks, int parityCount)
        {
            return _galoisField.GenerateParityShards(dataBlocks, parityCount);
        }

        /// <summary>
        /// Reconstructs a single failed block using Z1 (XOR) parity.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (null for the failed block).</param>
        /// <param name="z1Parity">Z1 parity block.</param>
        /// <param name="failedIndex">Index of the failed block.</param>
        /// <returns>Reconstructed data block.</returns>
        public static byte[] ReconstructSingleFailure(byte[][] dataBlocks, byte[] z1Parity, int failedIndex)
        {
            return _galoisField.ReconstructFromP(dataBlocks, z1Parity, failedIndex);
        }

        /// <summary>
        /// Reconstructs two failed blocks using Z1 and Z2 parity.
        /// Modifies the dataBlocks array in place with the reconstructed data.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (will be modified with reconstructed data).</param>
        /// <param name="z1Parity">Z1 parity block.</param>
        /// <param name="z2Parity">Z2 parity block.</param>
        /// <param name="failed1Index">Index of first failed block.</param>
        /// <param name="failed2Index">Index of second failed block.</param>
        public static void ReconstructDoubleFailure(byte[][] dataBlocks, byte[] z1Parity, byte[] z2Parity,
            int failed1Index, int failed2Index)
        {
            _galoisField.ReconstructFromPQ(dataBlocks, z1Parity, z2Parity, failed1Index, failed2Index);
        }

        /// <summary>
        /// Reconstructs three failed blocks using Z1, Z2, and Z3 parity.
        /// Modifies the dataBlocks array in place with the reconstructed data.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (will be modified with reconstructed data).</param>
        /// <param name="z1Parity">Z1 parity block.</param>
        /// <param name="z2Parity">Z2 parity block.</param>
        /// <param name="z3Parity">Z3 parity block.</param>
        /// <param name="failed1Index">Index of first failed block.</param>
        /// <param name="failed2Index">Index of second failed block.</param>
        /// <param name="failed3Index">Index of third failed block.</param>
        public static void ReconstructTripleFailure(byte[][] dataBlocks, byte[] z1Parity, byte[] z2Parity, byte[] z3Parity,
            int failed1Index, int failed2Index, int failed3Index)
        {
            _galoisField.ReconstructFromPQR(dataBlocks, z1Parity, z2Parity, z3Parity, failed1Index, failed2Index, failed3Index);
        }

        /// <summary>
        /// Reconstructs failed blocks based on the number of failures and available parity.
        /// Automatically determines which reconstruction method to use.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (null for failed blocks, will be modified).</param>
        /// <param name="failedIndices">Indices of failed blocks.</param>
        /// <param name="parityBlocks">Array of parity blocks (1-3 blocks).</param>
        /// <exception cref="ArgumentException">When there are too many failures for available parity.</exception>
        public static void ReconstructFailures(byte[][] dataBlocks, int[] failedIndices, byte[][] parityBlocks)
        {
            if (failedIndices.Length == 0)
                return;

            if (failedIndices.Length > parityBlocks.Length)
            {
                throw new ArgumentException(
                    $"Cannot reconstruct {failedIndices.Length} failures with only {parityBlocks.Length} parity blocks.");
            }

            switch (failedIndices.Length)
            {
                case 1:
                    var reconstructed = ReconstructSingleFailure(dataBlocks, parityBlocks[0], failedIndices[0]);
                    dataBlocks[failedIndices[0]] = reconstructed;
                    break;

                case 2:
                    if (parityBlocks.Length < 2)
                        throw new ArgumentException("Two parity blocks required for double failure reconstruction.");
                    ReconstructDoubleFailure(dataBlocks, parityBlocks[0], parityBlocks[1], failedIndices[0], failedIndices[1]);
                    break;

                case 3:
                    if (parityBlocks.Length < 3)
                        throw new ArgumentException("Three parity blocks required for triple failure reconstruction.");
                    ReconstructTripleFailure(dataBlocks, parityBlocks[0], parityBlocks[1], parityBlocks[2],
                        failedIndices[0], failedIndices[1], failedIndices[2]);
                    break;

                default:
                    throw new ArgumentException($"Cannot reconstruct more than 3 failures. Received {failedIndices.Length} failures.");
            }
        }

        /// <summary>
        /// Verifies that the data blocks match the expected parity values.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks.</param>
        /// <param name="parityBlocks">Array of parity blocks (1-3).</param>
        /// <returns>True if all parity values match.</returns>
        public static bool VerifyParity(byte[][] dataBlocks, byte[][] parityBlocks)
        {
            if (parityBlocks.Length == 0 || parityBlocks.Length > 3)
                throw new ArgumentException("Must provide 1-3 parity blocks.");

            var z1 = parityBlocks[0];
            var z2 = parityBlocks.Length > 1 ? parityBlocks[1] : null;
            var z3 = parityBlocks.Length > 2 ? parityBlocks[2] : null;

            return _galoisField.VerifyParity(dataBlocks, z1, z2, z3);
        }

        /// <summary>
        /// Gets a shared instance of the Galois Field for advanced operations.
        /// Use this when you need direct access to Galois Field arithmetic.
        /// </summary>
        /// <returns>Shared GaloisField instance.</returns>
        public static GaloisField GetGaloisField()
        {
            return _galoisField;
        }
    }
}
