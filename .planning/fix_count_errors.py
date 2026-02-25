#!/usr/bin/env python3
"""
Fix two types of Count() issues:
1. BoundedDictionary.Count() -> BoundedDictionary.Count (has property)
2. List/Array .Count() -> .Count (CA1829 - use property not extension)

Also fix CS0411 - type arguments cannot be inferred (Count used as method group in LINQ)
"""
import os
import re
import sys

SKIP_DIRS = {"bin", "obj", ".git", ".planning", ".omc", "DataWarehouse.Benchmarks"}

def find_cs_files(base="."):
    result = []
    for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                result.append(os.path.join(root, f))
    return result

# Fields/variables that are BoundedDictionary (known from context)
# We can't easily detect these statically, but the CA1829 errors tell us the files
# The safest approach: revert ALL .Count() calls that are immediately on a field
# (preceded by _fieldname.Count() or variable.Count())
# to .Count, since we need to figure out if they're on IGrouping or BoundedDictionary

# Strategy: the LINQ .Count() is needed when:
# 1. The receiver is IGrouping (from GroupBy)
# 2. The receiver is IEnumerable that doesn't have a Count property

# The PROPERTY .Count is needed when:
# 1. The receiver is BoundedDictionary (has Count property)
# 2. The receiver is List, Array, Dictionary, etc. (has Count property)

# The problem: my script blindly changed all .Count method groups to .Count()
# including ones on BoundedDictionary and List which have Count properties

# Fix: for the files listed in CA1829 errors, convert the problematic .Count() back to .Count
# These are on collections that already have Count property

# Read error list
CA1829_FILES = [
    "DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/ExtentTree.cs",
    "DataWarehouse.SDK/Validation/InputValidation.cs",
    "DataWarehouse.SDK/Tags/TagValueTypes.cs",
    "DataWarehouse.SDK/Tags/TagTypes.cs",
    "DataWarehouse.SDK/Tags/TagAcl.cs",
    "DataWarehouse.SDK/Storage/Migration/ReadForwardingTable.cs",
    "DataWarehouse.SDK/Security/Siem/SiemTransportBridge.cs",
    "DataWarehouse.SDK/Security/IncidentResponse/IncidentResponseEngine.cs",
    "DataWarehouse.SDK/Replication/DottedVersionVector.cs",
    "DataWarehouse.SDK/Primitives/Probabilistic/TopKHeavyHitters.cs",
    "DataWarehouse.SDK/Primitives/Probabilistic/TDigest.cs",
    "DataWarehouse.SDK/Primitives/Performance/PerformanceUtilities.cs",
    "DataWarehouse.SDK/Infrastructure/StorageConnectionRegistry.cs",
    "DataWarehouse.SDK/Infrastructure/Distributed/Consensus/MultiRaftManager.cs",
    "DataWarehouse.SDK/Edge/Flash/BadBlockManager.cs",
    "DataWarehouse.SDK/Contracts/TamperProof/TamperProofResults.cs",
    "DataWarehouse.SDK/Contracts/TamperProof/IWormStorageProvider.cs",
    "DataWarehouse.SDK/Contracts/Query/ColumnarBatch.cs",
    "DataWarehouse.SDK/Contracts/Gaming/GamingTypes.cs",
    "DataWarehouse.SDK/Contracts/Dashboards/DashboardCapabilities.cs",
    "DataWarehouse.SDK/Contracts/Consciousness/ConsciousnessStrategyBase.cs",
    "DataWarehouse.SDK/Connectors/ConnectionStrategyRegistry.cs",
    "DataWarehouse.SDK/Configuration/ConfigurationHierarchy.cs",
    "DataWarehouse.SDK/AI/GraphStructures.cs",
]

# For these files, we need to carefully handle Count() vs Count
# The CA1829 says "use Count property" which means those collections already have .Count
# So we need to revert from .Count() to .Count on non-IGrouping receivers

def fix_ca1829_file(path):
    """Fix CA1829: convert .Count() to .Count where the receiver has a Count property."""
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except:
        return False

    text = original

    # Pattern: fieldname.Count() where fieldname is a concrete collection (not IGrouping)
    # We can identify IGrouping by looking at GroupBy context
    # For simplicity: convert _xxx.Count() to _xxx.Count unless in a lambda context
    # specifically after GroupBy

    # Strategy: revert ALL .Count() that are not in a lambda body immediately after GroupBy
    # This is tricky. Use a simpler heuristic:
    # .Count() on a field (starts with _) -> .Count (fields are usually concrete types)

    # Actually, the CA1829 errors are about specific instances.
    # Let's just fix by converting _field.Count() to _field.Count
    # and local variable.Count() (without lambda context) to variable.Count
    # But keep g.Count() and other single-char lambda vars as Count()

    # Simple: convert .Count() to .Count when preceded by _ (field names)
    text = re.sub(r'(_\w+)\.Count\(\)', r'\1.Count', text)

    # Also fix: VarName.Count() where VarName is a long identifier (not short lambda var)
    # Short lambda vars (single char or common: g, x, s, e, etc.) keep Count()
    # Long names (>2 chars) that aren't lambda vars -> .Count
    def fix_count(m):
        varname = m.group(1)
        # Keep Count() for short LINQ lambda vars
        if len(varname) <= 2:
            return m.group(0)  # keep as-is
        # Check if it's in a lambda context (preceded by =>)
        return m.group(0)  # keep as-is for safety, we'll only fix fields

    # Actually just fix the field pattern above - that's the most common CA1829 pattern

    if text != original:
        with open(path, 'w', encoding='utf-8') as f:
            f.write(text)
        return True
    return False

def main():
    changed = 0
    for relpath in CA1829_FILES:
        if os.path.exists(relpath):
            if fix_ca1829_file(relpath):
                changed += 1
                print(f"FIXED: {relpath}")
        else:
            print(f"NOT FOUND: {relpath}")

    # Also fix all files where _field.Count() was wrongly applied
    print("\nFixing all field.Count() instances...")
    for path in find_cs_files():
        try:
            with open(path, 'r', encoding='utf-8', errors='replace') as f:
                content = f.read()
        except:
            continue
        orig = content
        content = re.sub(r'(_\w+)\.Count\(\)', r'\1.Count', content)
        if content != orig:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(content)
            changed += 1
            print(f"FIXED-FIELD: {path}")

    print(f"\nTotal: {changed}")

if __name__ == "__main__":
    main()
