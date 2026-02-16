using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// IComparer implementation for byte arrays using lexicographic comparison.
    /// Uses Span.SequenceCompareTo for efficient memory comparison.
    /// </summary>
    public sealed class ByteArrayComparer : IComparer<byte[]>
    {
        /// <summary>
        /// Singleton instance for reuse.
        /// </summary>
        public static readonly ByteArrayComparer Instance = new ByteArrayComparer();

        private ByteArrayComparer()
        {
        }

        /// <summary>
        /// Compares two byte arrays lexicographically.
        /// </summary>
        /// <param name="x">First byte array.</param>
        /// <param name="y">Second byte array.</param>
        /// <returns>Negative if x &lt; y, zero if x == y, positive if x &gt; y.</returns>
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return x.AsSpan().SequenceCompareTo(y.AsSpan());
        }
    }
}
