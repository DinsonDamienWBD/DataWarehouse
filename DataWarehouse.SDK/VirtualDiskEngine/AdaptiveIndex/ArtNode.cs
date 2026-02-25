using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Abstract base for Adaptive Radix Tree (ART) nodes.
/// Supports path compression and 4 node sizes (4, 16, 48, 256) for memory efficiency.
/// </summary>
/// <remarks>
/// ART provides O(k) lookup where k is key length, with adaptive node sizing
/// to minimize memory usage. Nodes grow (4->16->48->256) when full and
/// shrink (256->48->16->4) when children are removed below threshold.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 ART Node types")]
public abstract class ArtNode
{
    /// <summary>
    /// Compressed path prefix bytes shared by all children.
    /// </summary>
    public byte[] Prefix { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Number of valid bytes in <see cref="Prefix"/>.
    /// </summary>
    public int PrefixLength { get; set; }

    /// <summary>
    /// Gets the number of children in this node.
    /// </summary>
    public abstract int ChildCount { get; }

    /// <summary>
    /// Finds the child associated with the given key byte.
    /// </summary>
    /// <param name="keyByte">The key byte to look up.</param>
    /// <returns>The child node, or null if not found.</returns>
    public abstract ArtNode? FindChild(byte keyByte);

    /// <summary>
    /// Adds a child node at the given key byte. Returns the (possibly grown) node.
    /// </summary>
    /// <param name="keyByte">The key byte for the child.</param>
    /// <param name="child">The child node to add.</param>
    /// <returns>This node if space available, or a larger replacement node.</returns>
    public abstract ArtNode AddChild(byte keyByte, ArtNode child);

    /// <summary>
    /// Removes the child at the given key byte. Returns the (possibly shrunk) node.
    /// </summary>
    /// <param name="keyByte">The key byte to remove.</param>
    /// <returns>This node if still viable, or a smaller replacement node.</returns>
    public abstract ArtNode RemoveChild(byte keyByte);

    /// <summary>
    /// Iterates all children in key-byte order for range queries.
    /// </summary>
    /// <param name="action">Callback receiving (keyByte, childNode) pairs.</param>
    public abstract void ForEachChild(Action<byte, ArtNode> action);

    /// <summary>
    /// Checks if the prefix of this node matches the key at the given depth.
    /// </summary>
    /// <param name="key">The full key to match against.</param>
    /// <param name="depth">The current depth in the key.</param>
    /// <returns>Number of matching prefix bytes.</returns>
    public int CheckPrefix(byte[] key, int depth)
    {
        int maxCheck = Math.Min(PrefixLength, key.Length - depth);
        int matched = 0;
        for (int i = 0; i < maxCheck; i++)
        {
            if (Prefix[i] != key[depth + i])
                break;
            matched++;
        }
        return matched;
    }

    /// <summary>
    /// Leaf node storing a complete key-value pair.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 ART Leaf node")]
    public sealed class Leaf : ArtNode
    {
        /// <summary>The full key stored in this leaf.</summary>
        public byte[] Key { get; }

        /// <summary>The value associated with the key.</summary>
        public long Value { get; set; }

        /// <inheritdoc />
        public override int ChildCount => 0;

        /// <summary>
        /// Creates a new leaf node.
        /// </summary>
        public Leaf(byte[] key, long value)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Value = value;
        }

        /// <summary>
        /// Checks if this leaf matches the given key.
        /// </summary>
        public bool Matches(byte[] key)
        {
            if (Key.Length != key.Length) return false;
            return Key.AsSpan().SequenceEqual(key.AsSpan());
        }

        /// <inheritdoc />
        public override ArtNode? FindChild(byte keyByte) => null;
        /// <inheritdoc />
        public override ArtNode AddChild(byte keyByte, ArtNode child) =>
            throw new InvalidOperationException("Cannot add child to leaf node.");
        /// <inheritdoc />
        public override ArtNode RemoveChild(byte keyByte) =>
            throw new InvalidOperationException("Cannot remove child from leaf node.");
        /// <inheritdoc />
        public override void ForEachChild(Action<byte, ArtNode> action) { }
    }

    /// <summary>
    /// Node with up to 4 children. Uses linear scan for lookup.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 ART Node4")]
    public sealed class Node4 : ArtNode
    {
        private readonly byte[] _keys = new byte[4];
        private readonly ArtNode?[] _children = new ArtNode?[4];
        private int _count;

        /// <inheritdoc />
        public override int ChildCount => _count;

        /// <inheritdoc />
        public override ArtNode? FindChild(byte keyByte)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == keyByte)
                    return _children[i];
            }
            return null;
        }

        /// <inheritdoc />
        public override ArtNode AddChild(byte keyByte, ArtNode child)
        {
            if (_count < 4)
            {
                // Insert in sorted order
                int pos = _count;
                for (int i = 0; i < _count; i++)
                {
                    if (_keys[i] > keyByte)
                    {
                        pos = i;
                        break;
                    }
                }
                // Shift right
                for (int i = _count; i > pos; i--)
                {
                    _keys[i] = _keys[i - 1];
                    _children[i] = _children[i - 1];
                }
                _keys[pos] = keyByte;
                _children[pos] = child;
                _count++;
                return this;
            }

            // Grow to Node16
            var node16 = new Node16
            {
                Prefix = Prefix,
                PrefixLength = PrefixLength
            };
            for (int i = 0; i < _count; i++)
            {
                node16.AddChild(_keys[i], _children[i]!);
            }
            return node16.AddChild(keyByte, child);
        }

        /// <inheritdoc />
        public override ArtNode RemoveChild(byte keyByte)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == keyByte)
                {
                    // Shift left
                    for (int j = i; j < _count - 1; j++)
                    {
                        _keys[j] = _keys[j + 1];
                        _children[j] = _children[j + 1];
                    }
                    _children[_count - 1] = null;
                    _count--;
                    return this;
                }
            }
            return this;
        }

        /// <inheritdoc />
        public override void ForEachChild(Action<byte, ArtNode> action)
        {
            for (int i = 0; i < _count; i++)
            {
                action(_keys[i], _children[i]!);
            }
        }
    }

    /// <summary>
    /// Node with up to 16 children. Uses SIMD scan via SSE2 when available, else linear scan.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 ART Node16 with SIMD")]
    public sealed class Node16 : ArtNode
    {
        internal readonly byte[] _keys = new byte[16];
        internal readonly ArtNode?[] _children = new ArtNode?[16];
        internal int _count;

        /// <inheritdoc />
        public override int ChildCount => _count;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override ArtNode? FindChild(byte keyByte)
        {
            if (Sse2.IsSupported && _count >= 4)
            {
                return FindChildSimd(keyByte);
            }
            return FindChildScalar(keyByte);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ArtNode? FindChildSimd(byte keyByte)
        {
            unsafe
            {
                fixed (byte* keysPtr = _keys)
                {
                    var keyVec = Vector128.Create(keyByte);
                    var keysVec = Sse2.LoadVector128(keysPtr);
                    var cmp = Sse2.CompareEqual(keysVec, keyVec);
                    int mask = Sse2.MoveMask(cmp);
                    // Only check bits within valid range
                    mask &= (1 << _count) - 1;
                    if (mask != 0)
                    {
                        int idx = System.Numerics.BitOperations.TrailingZeroCount(mask);
                        return _children[idx];
                    }
                }
            }
            return null;
        }

        private ArtNode? FindChildScalar(byte keyByte)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == keyByte)
                    return _children[i];
            }
            return null;
        }

        /// <inheritdoc />
        public override ArtNode AddChild(byte keyByte, ArtNode child)
        {
            if (_count < 16)
            {
                // Insert in sorted order
                int pos = _count;
                for (int i = 0; i < _count; i++)
                {
                    if (_keys[i] > keyByte)
                    {
                        pos = i;
                        break;
                    }
                }
                for (int i = _count; i > pos; i--)
                {
                    _keys[i] = _keys[i - 1];
                    _children[i] = _children[i - 1];
                }
                _keys[pos] = keyByte;
                _children[pos] = child;
                _count++;
                return this;
            }

            // Grow to Node48
            var node48 = new Node48
            {
                Prefix = Prefix,
                PrefixLength = PrefixLength
            };
            for (int i = 0; i < _count; i++)
            {
                node48.AddChild(_keys[i], _children[i]!);
            }
            return node48.AddChild(keyByte, child);
        }

        /// <inheritdoc />
        public override ArtNode RemoveChild(byte keyByte)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == keyByte)
                {
                    for (int j = i; j < _count - 1; j++)
                    {
                        _keys[j] = _keys[j + 1];
                        _children[j] = _children[j + 1];
                    }
                    _children[_count - 1] = null;
                    _count--;

                    // Shrink to Node4 if below threshold
                    if (_count <= 3)
                    {
                        var node4 = new Node4
                        {
                            Prefix = Prefix,
                            PrefixLength = PrefixLength
                        };
                        for (int j = 0; j < _count; j++)
                        {
                            node4.AddChild(_keys[j], _children[j]!);
                        }
                        return node4;
                    }
                    return this;
                }
            }
            return this;
        }

        /// <inheritdoc />
        public override void ForEachChild(Action<byte, ArtNode> action)
        {
            for (int i = 0; i < _count; i++)
            {
                action(_keys[i], _children[i]!);
            }
        }
    }

    /// <summary>
    /// Node with up to 48 children. Uses 256-byte child-index array + 48 children pointers for O(1) lookup.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 ART Node48")]
    public sealed class Node48 : ArtNode
    {
        /// <summary>
        /// Index array: childIndex[keyByte] -> index into _children (0xFF = empty).
        /// </summary>
        internal readonly byte[] _childIndex = new byte[256];
        internal readonly ArtNode?[] _children = new ArtNode?[48];
        internal int _count;

        /// <summary>
        /// Initializes a new Node48 with empty child index.
        /// </summary>
        public Node48()
        {
            Array.Fill(_childIndex, (byte)0xFF);
        }

        /// <inheritdoc />
        public override int ChildCount => _count;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override ArtNode? FindChild(byte keyByte)
        {
            byte idx = _childIndex[keyByte];
            return idx != 0xFF ? _children[idx] : null;
        }

        /// <inheritdoc />
        public override ArtNode AddChild(byte keyByte, ArtNode child)
        {
            if (_count < 48)
            {
                // Find first free slot
                int slot = -1;
                for (int i = 0; i < 48; i++)
                {
                    if (_children[i] == null)
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot >= 0)
                {
                    _childIndex[keyByte] = (byte)slot;
                    _children[slot] = child;
                    _count++;
                    return this;
                }
            }

            // Grow to Node256
            var node256 = new Node256
            {
                Prefix = Prefix,
                PrefixLength = PrefixLength
            };
            for (int i = 0; i < 256; i++)
            {
                if (_childIndex[i] != 0xFF)
                {
                    node256.AddChild((byte)i, _children[_childIndex[i]]!);
                }
            }
            return node256.AddChild(keyByte, child);
        }

        /// <inheritdoc />
        public override ArtNode RemoveChild(byte keyByte)
        {
            byte idx = _childIndex[keyByte];
            if (idx != 0xFF)
            {
                _children[idx] = null;
                _childIndex[keyByte] = 0xFF;
                _count--;

                // Shrink to Node16 if below threshold
                if (_count <= 12)
                {
                    var node16 = new Node16
                    {
                        Prefix = Prefix,
                        PrefixLength = PrefixLength
                    };
                    for (int i = 0; i < 256; i++)
                    {
                        if (_childIndex[i] != 0xFF)
                        {
                            node16.AddChild((byte)i, _children[_childIndex[i]]!);
                        }
                    }
                    return node16;
                }
            }
            return this;
        }

        /// <inheritdoc />
        public override void ForEachChild(Action<byte, ArtNode> action)
        {
            for (int i = 0; i < 256; i++)
            {
                if (_childIndex[i] != 0xFF)
                {
                    action((byte)i, _children[_childIndex[i]]!);
                }
            }
        }
    }

    /// <summary>
    /// Node with up to 256 children. Direct O(1) lookup by key byte.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 ART Node256")]
    public sealed class Node256 : ArtNode
    {
        internal readonly ArtNode?[] _children = new ArtNode?[256];
        internal int _count;

        /// <inheritdoc />
        public override int ChildCount => _count;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override ArtNode? FindChild(byte keyByte)
        {
            return _children[keyByte];
        }

        /// <inheritdoc />
        public override ArtNode AddChild(byte keyByte, ArtNode child)
        {
            if (_children[keyByte] == null)
                _count++;
            _children[keyByte] = child;
            return this;
        }

        /// <inheritdoc />
        public override ArtNode RemoveChild(byte keyByte)
        {
            if (_children[keyByte] != null)
            {
                _children[keyByte] = null;
                _count--;

                // Shrink to Node48 if below threshold
                if (_count <= 36)
                {
                    var node48 = new Node48
                    {
                        Prefix = Prefix,
                        PrefixLength = PrefixLength
                    };
                    for (int i = 0; i < 256; i++)
                    {
                        if (_children[i] != null)
                        {
                            node48.AddChild((byte)i, _children[i]!);
                        }
                    }
                    return node48;
                }
            }
            return this;
        }

        /// <inheritdoc />
        public override void ForEachChild(Action<byte, ArtNode> action)
        {
            for (int i = 0; i < 256; i++)
            {
                if (_children[i] != null)
                {
                    action((byte)i, _children[i]!);
                }
            }
        }
    }
}
