using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Registry that maps data keys to CRDT types for conflict-free merge operations.
    /// Supports exact key matches, prefix-based registrations, and a default type (LWWRegister).
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT type registry")]
    public sealed class CrdtRegistry
    {
        private readonly BoundedDictionary<string, Type> _exactRegistrations = new BoundedDictionary<string, Type>(1000);
        private readonly BoundedDictionary<string, Type> _prefixRegistrations = new BoundedDictionary<string, Type>(1000);

        /// <summary>
        /// The default CRDT type used when no pattern matches (LWWRegister -- most general).
        /// </summary>
        public Type DefaultType => typeof(SdkLWWRegister);

        /// <summary>
        /// Registers an exact key to a specific CRDT type.
        /// </summary>
        /// <param name="key">The exact data key.</param>
        /// <param name="crdtType">The CRDT type (must implement ICrdtType).</param>
        public void Register(string key, Type crdtType)
        {
            ValidateCrdtType(crdtType);
            _exactRegistrations[key] = crdtType;
        }

        /// <summary>
        /// Registers all keys starting with the given prefix to a specific CRDT type.
        /// </summary>
        /// <param name="prefix">The key prefix to match.</param>
        /// <param name="crdtType">The CRDT type (must implement ICrdtType).</param>
        public void RegisterPrefix(string prefix, Type crdtType)
        {
            ValidateCrdtType(crdtType);
            _prefixRegistrations[prefix] = crdtType;
        }

        /// <summary>
        /// Gets the CRDT type for the given key.
        /// LOW-450: exact match checked first (highest specificity), then longest-prefix match, then default.
        /// </summary>
        public Type GetCrdtType(string key)
        {
            // Check exact registration first (highest specificity) — LOW-450
            if (_exactRegistrations.TryGetValue(key, out var exactType))
            {
                return exactType;
            }

            // Check prefix registrations (longest matching prefix wins)
            Type? longestPrefixMatch = null;
            int longestPrefixLength = 0;

            foreach (var (prefix, type) in _prefixRegistrations)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > longestPrefixLength)
                {
                    longestPrefixMatch = type;
                    longestPrefixLength = prefix.Length;
                }
            }

            if (longestPrefixMatch != null)
            {
                return longestPrefixMatch;
            }

            // Default to LWWRegister
            return DefaultType;
        }

        /// <summary>
        /// Creates a new instance of the CRDT type for the given key.
        /// </summary>
        internal ICrdtType CreateInstance(string key)
        {
            var type = GetCrdtType(key);
            return (ICrdtType)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Failed to create instance of CRDT type {type.Name}"));
        }

        /// <summary>
        /// Deserializes data using the correct CRDT type for the given key.
        /// </summary>
        internal ICrdtType Deserialize(string key, byte[] data)
        {
            var type = GetCrdtType(key);

            if (type == typeof(SdkGCounter))
                return SdkGCounter.Deserialize(data);

            if (type == typeof(SdkPNCounter))
                return SdkPNCounter.Deserialize(data);

            if (type == typeof(SdkLWWRegister))
                return SdkLWWRegister.Deserialize(data);

            if (type == typeof(SdkORSet))
                return SdkORSet.Deserialize(data);

            throw new NotSupportedException($"Deserialization not supported for CRDT type {type.Name}");
        }

        private static void ValidateCrdtType(Type crdtType)
        {
            if (!typeof(ICrdtType).IsAssignableFrom(crdtType))
            {
                throw new ArgumentException(
                    $"Type {crdtType.Name} does not implement ICrdtType.",
                    nameof(crdtType));
            }
        }
    }
}
