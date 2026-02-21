#!/usr/bin/env python3
"""
audit_custom_registries.py - Custom Registry Audit Tool for DataWarehouse Plugins
Phase 65.4 Plan 01

Scans all Plugin directories for custom registry patterns and classifies each as:
  - DUPLICATE: Standard pattern matching StrategyRegistry<T> -- safe to migrate
  - UNIQUE: Has genuinely different functionality that requires custom handling
  - ALREADY_MIGRATED: Already uses SDK base class dispatch or SDK registry

Usage: python Metadata/audit_custom_registries.py [--plugins-dir PLUGINS_DIR]
Output: JSON report to stdout
"""

import os
import re
import sys
import json
import argparse
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional


# ---------------------------------------------------------------------------
# Hardcoded knowledge about known UNIQUE and ALREADY_MIGRATED registries
# (derived from architecture analysis; these take priority over heuristics)
# ---------------------------------------------------------------------------

KNOWN_UNIQUE = {
    # plugin_file_stem (lowercase) -> reason
    "replicationstrategyregistry": "Dual-index by ConsistencyModel enum + capability; keyed by strategy name not StrategyId",
    "computeruntimestrategyregistry": "Dual-index by ComputeRuntime enum + StrategyId",
    "storageprocessingstrategyregistryinternal": "Dual-index by category + StrategyId",
    "regenerationstrategyregistry": "Cross-index by format -> strategy list; format-based lookup not StrategyId",
    "embeddingproviderregistry": "Keyed by provider name not StrategyId; embedding-specific lookup patterns",
    "vectorstoreregistry": "Keyed by provider name not StrategyId; vector-specific capability indexing",
    "domainmodelregistry": "Domain model registry, not strategy registry; different lifecycle",
    "backendregistryimpl": "NOT a strategy registry at all -- backend routing with BackendDescriptor + IStorageStrategy tuple storage",
}

KNOWN_ALREADY_MIGRATED = {
    # plugin_directory_name (lowercase) -> reason
    "ultimateencryption": "Uses EncryptionPluginBase (HierarchyEncryptionPluginBase) dispatch; EncryptionStrategyRegistry is SDK type in Registration/ subfolder",
    "ultimatecompression": "Uses CompressionPluginBase dispatch; inherited RegisterStrategy/ResolveStrategy",
    "ultimateconnector": "Uses SDK ConnectionStrategyRegistry from DataWarehouse.SDK.Contracts",
}

# Plugin directory -> registry file patterns that are NOT standalone registries
# (helper classes or service registries, not strategy registries)
NON_STRATEGY_REGISTRY_PATTERNS = [
    r"RegionRegistryStrategy\.cs$",          # UltimateCompliance - a strategy, not a registry
    r"DeclarativeZoneRegistry\.cs$",          # UltimateCompliance - zone registry, not strategy
    r"SchemaRegistryStrategies\.cs$",         # UltimateDataCatalog - schema registry strategies
    r"MoonshotRegistryImpl\.cs$",             # UltimateDataGovernance - moonshot registry
    r"BackendGreenScoreRegistry\.cs$",        # UltimateSustainability - green score, not strategy
    r"AppRegistrationService\.cs$",           # AppPlatform - app registration service
]


# ---------------------------------------------------------------------------
# Patterns for detection
# ---------------------------------------------------------------------------

# Pattern A: Standalone registry file
PATTERN_A_BOUNDED_DICT = re.compile(
    r'BoundedDictionary<string\s*,\s*([\w<>,\s]+?)>\s+\w+\s*=\s*new\s+BoundedDictionary',
    re.DOTALL
)
PATTERN_A_REGISTER = re.compile(r'\bvoid\s+Register\s*\(', re.IGNORECASE)
PATTERN_A_GET = re.compile(r'\b(GetStrategy|GetAll(?:Strategies)?|Get\s*\()', re.IGNORECASE)
PATTERN_A_DISCOVER = re.compile(r'\b(DiscoverStrategies|DiscoverFromAssembly)\s*\(', re.IGNORECASE)
PATTERN_A_ASSEMBLY_SCAN = re.compile(r'GetTypes\s*\(\s*\)\s*\.?\s*Where\s*\(', re.DOTALL)

# Pattern B: Inline registry in plugin class (strategies dict + Register/Get methods in plugin file)
PATTERN_B_STRATEGIES_FIELD = re.compile(
    r'(?:BoundedDictionary|Dictionary)<string\s*,\s*([\w<>,\s]+?)>\s+_strategies\s*[=;]',
    re.DOTALL | re.IGNORECASE
)

# Pattern C: Inline registry class (nested class or sibling class with "Registr" in name)
PATTERN_C_REGISTRY_CLASS = re.compile(
    r'\bclass\s+\w*[Rr]egistr(?:y|i)\w*\s*[:{<]',
    re.DOTALL
)

# Detection of already-migrated indicators
ALREADY_MIGRATED_INDICATORS = [
    re.compile(r'\bEncryptionPluginBase\b'),
    re.compile(r'\bCompressionPluginBase\b'),
    re.compile(r'\bRegisterStrategy\s*\('),
    re.compile(r'\bResolveStrategy\s*<'),
    re.compile(r'\bDispatchEncryptionStrategyAsync\b'),
    re.compile(r'\bDispatchCompressionStrategyAsync\b'),
    re.compile(r'\bConnectionStrategyRegistry\b'),
]

# Dual-index indicators (UNIQUE markers)
DUAL_INDEX_INDICATORS = [
    re.compile(r'BoundedDictionary<\w+Enum\b'),   # enum-keyed secondary index
    re.compile(r'BoundedDictionary<[A-Z]\w+,\s*List<'),  # any-type-keyed list index (secondary)
    re.compile(r'_by\w+\s*=\s*new\s+BoundedDictionary'),  # named secondary index field
    re.compile(r'_byConsistencyModel|_byRuntime|_byCategory|_byCapability|_byFormat|_formatToStrategies'),
]

# Name-keyed (not StrategyId) indicators (UNIQUE)
NAME_KEYED_INDICATORS = [
    re.compile(r'Characteristics\.StrategyName'),        # keyed by name property
    re.compile(r'strategy\.Name\b'),                      # keyed by Name
    re.compile(r'RegisteredStrategyNames\b'),             # exposes names, not IDs
]

# Cross-index (format -> strategies) patterns (UNIQUE)
CROSS_INDEX_INDICATORS = [
    re.compile(r'SupportedFormats'),
    re.compile(r'_formatToStrategies'),
]

# Standard replaceable methods
STANDARD_REPLACEABLE_METHODS = [
    "Register", "GetStrategy", "GetAllStrategies", "DiscoverStrategies",
    "DiscoverFromAssembly", "Unregister", "Get", "GetAll", "GetDefault",
    "SetDefault", "SetDefaultStrategy", "ContainsStrategy", "Count",
]


# ---------------------------------------------------------------------------
# Helper functions
# ---------------------------------------------------------------------------

def extract_plugin_name(plugin_dir: str) -> str:
    """Extract short plugin name from directory path."""
    base = os.path.basename(plugin_dir)
    # Strip 'DataWarehouse.Plugins.' prefix
    if base.startswith("DataWarehouse.Plugins."):
        return base[len("DataWarehouse.Plugins."):]
    return base


def is_non_strategy_file(filepath: str) -> bool:
    """Check if a file matches known non-strategy registry patterns."""
    fname = os.path.basename(filepath)
    for pattern in NON_STRATEGY_REGISTRY_PATTERNS:
        if re.search(pattern, fname):
            return True
    return False


def extract_strategy_interface(content: str) -> str:
    """Extract strategy interface name from BoundedDictionary generic parameter."""
    # Try Pattern A: BoundedDictionary<string, ISomeStrategy>
    match = PATTERN_A_BOUNDED_DICT.search(content)
    if match:
        interface_type = match.group(1).strip()
        # Clean up nested generics and whitespace
        interface_type = re.sub(r'\s+', ' ', interface_type)
        # Take first type if it's a tuple like (BackendDescriptor, IStorageStrategy)
        if ',' in interface_type and '(' in interface_type:
            return interface_type
        return interface_type

    # Try Pattern B: Dictionary<string, _strategies>
    match = PATTERN_B_STRATEGIES_FIELD.search(content)
    if match:
        return match.group(1).strip()

    return "unknown"


def extract_methods_present(content: str) -> tuple[list, list]:
    """
    Returns (replaceable_methods_found, unique_methods_found) based on content analysis.
    """
    replaceable = []
    unique_methods = []

    for method in STANDARD_REPLACEABLE_METHODS:
        if re.search(rf'\b{re.escape(method)}\s*[\(<]', content):
            replaceable.append(method)

    # Methods unique to UNIQUE registries
    unique_candidates = [
        ("GetByConsistencyModel", "GetByConsistencyModel"),
        ("GetByRuntime|GetStrategiesByRuntime", "GetByRuntime/GetStrategiesByRuntime"),
        ("GetByCategory|GetStrategiesByCategory", "GetByCategory/GetStrategiesByCategory"),
        ("GetByCapability", "GetByCapability"),
        ("SelectBestStrategy", "SelectBestStrategy"),
        ("GetSummary", "GetSummary"),
        ("GetStrategiesForFormat|GetStrategiesByFormat", "GetStrategiesForFormat"),
        ("IndexByCapabilities", "IndexByCapabilities"),
    ]
    for pattern, label in unique_candidates:
        if re.search(rf'\b(?:{pattern})\b', content):
            unique_methods.append(label)

    return replaceable, unique_methods


def classify_registry(
    filepath: str,
    content: str,
    plugin_name: str,
    file_stem_lower: str,
    plugin_dir_lower: str,
) -> dict:
    """Classify a registry file and return classification metadata."""

    # 1. Check known ALREADY_MIGRATED (by plugin dir)
    if plugin_dir_lower in KNOWN_ALREADY_MIGRATED:
        return {
            "classification": "ALREADY_MIGRATED",
            "reason": KNOWN_ALREADY_MIGRATED[plugin_dir_lower],
            "unique_methods": [],
        }

    # 2. Check file-level already-migrated indicators
    migrated_hits = [ind.pattern for ind in ALREADY_MIGRATED_INDICATORS if ind.search(content)]
    if migrated_hits:
        return {
            "classification": "ALREADY_MIGRATED",
            "reason": f"Uses SDK base class dispatch patterns: {', '.join(migrated_hits[:3])}",
            "unique_methods": [],
        }

    # 3. Check known UNIQUE (by file stem)
    if file_stem_lower in KNOWN_UNIQUE:
        return {
            "classification": "UNIQUE",
            "reason": KNOWN_UNIQUE[file_stem_lower],
            "unique_methods": [],  # filled by caller
        }

    # 4. Heuristic UNIQUE detection
    dual_index_hits = [ind.pattern for ind in DUAL_INDEX_INDICATORS if ind.search(content)]
    name_keyed_hits = [ind.pattern for ind in NAME_KEYED_INDICATORS if ind.search(content)]
    cross_index_hits = [ind.pattern for ind in CROSS_INDEX_INDICATORS if ind.search(content)]

    if dual_index_hits:
        return {
            "classification": "UNIQUE",
            "reason": f"Dual-index BoundedDictionary detected: {', '.join(dual_index_hits[:2])}",
            "unique_methods": [],
        }
    if name_keyed_hits:
        return {
            "classification": "UNIQUE",
            "reason": f"Keyed by name not StrategyId: {', '.join(name_keyed_hits[:2])}",
            "unique_methods": [],
        }
    if cross_index_hits:
        return {
            "classification": "UNIQUE",
            "reason": f"Cross-index by format/category: {', '.join(cross_index_hits[:2])}",
            "unique_methods": [],
        }

    # 5. Default: DUPLICATE (standard BoundedDictionary + Register/Get/Discover pattern)
    return {
        "classification": "DUPLICATE",
        "reason": "Standard BoundedDictionary + Register/Get/Discover pattern; matches StrategyRegistry<T>",
        "unique_methods": [],
    }


def detect_pattern(filepath: str, content: str) -> Optional[str]:
    """
    Detect which pattern (A/B/C) applies.
    Returns 'A', 'B', 'C', or None if no registry pattern detected.
    """
    fname = os.path.basename(filepath).lower()
    has_bounded_dict = bool(PATTERN_A_BOUNDED_DICT.search(content))
    has_strategies_field = bool(PATTERN_B_STRATEGIES_FIELD.search(content))
    has_register = bool(PATTERN_A_REGISTER.search(content))
    has_get = bool(PATTERN_A_GET.search(content))
    has_discover = bool(PATTERN_A_DISCOVER.search(content))
    has_assembly_scan = bool(PATTERN_A_ASSEMBLY_SCAN.search(content))
    has_registry_class = bool(PATTERN_C_REGISTRY_CLASS.search(content))

    # Pattern A: Standalone registry (file IS a registry, not a plugin)
    # Heuristic: filename contains "registry" or "Registry"
    is_registry_file = "registry" in fname.lower()

    if is_registry_file and (has_bounded_dict or has_strategies_field):
        if has_register or has_get or has_discover:
            return "A"

    # Pattern B: Inline registry in plugin class
    # Plugin file (not registry file) that has _strategies field + Register/Get methods
    if not is_registry_file and has_strategies_field and (has_register or has_get):
        # Check if there's a nested registry class
        if has_registry_class:
            return "C"
        return "B"

    # Pattern C: Inline registry class (nested class with "Registry" in name)
    # Could be inside a registry-named file too if it's a compound file
    if has_registry_class and (has_bounded_dict or has_strategies_field):
        if has_register or has_get:
            return "C"

    # Fallback: if it's a registry file with any BoundedDict
    if is_registry_file and has_bounded_dict:
        return "A"

    return None


def scan_file(filepath: str, plugin_name: str, plugin_dir: str) -> Optional[dict]:
    """
    Scan a single .cs file for registry patterns.
    Returns registry metadata dict or None if no registry found.
    """
    if is_non_strategy_file(filepath):
        return None

    try:
        with open(filepath, encoding="utf-8", errors="replace") as f:
            content = f.read()
    except OSError:
        return None

    pattern = detect_pattern(filepath, content)
    if pattern is None:
        return None

    file_stem = Path(filepath).stem
    file_stem_lower = file_stem.lower()
    plugin_dir_lower = plugin_name.lower()

    strategy_interface = extract_strategy_interface(content)
    replaceable_methods, unique_methods = extract_methods_present(content)

    classification_info = classify_registry(
        filepath, content, plugin_name, file_stem_lower, plugin_dir_lower
    )

    # Fill in unique_methods from method extraction
    if classification_info["classification"] == "UNIQUE" and not classification_info["unique_methods"]:
        classification_info["unique_methods"] = unique_methods

    # Compute relative path for report (relative to plugins root)
    try:
        rel_path = os.path.relpath(filepath, plugin_dir).replace("\\", "/")
    except ValueError:
        rel_path = filepath.replace("\\", "/")

    return {
        "plugin": plugin_name,
        "file": os.path.basename(filepath),
        "file_path": rel_path,
        "pattern": pattern,
        "strategy_interface": strategy_interface,
        "classification": classification_info["classification"],
        "reason": classification_info["reason"],
        "replaceable_methods": replaceable_methods,
        "unique_methods": classification_info["unique_methods"],
    }


def scan_plugin_directory(plugin_dir: str) -> list[dict]:
    """Scan a single plugin directory and return all registries found."""
    plugin_name = extract_plugin_name(plugin_dir)
    results = []

    for root, dirs, files in os.walk(plugin_dir):
        # Skip build output directories
        dirs[:] = [d for d in dirs if d not in ("bin", "obj", ".git")]

        for filename in files:
            if not filename.endswith(".cs"):
                continue
            filepath = os.path.join(root, filename)
            registry = scan_file(filepath, plugin_name, plugin_dir)
            if registry is not None:
                results.append(registry)

    return results


def scan_all_plugins(plugins_root: str) -> dict:
    """Scan all Plugin directories and produce the audit report."""
    if not os.path.isdir(plugins_root):
        return {
            "error": f"Plugins directory not found: {plugins_root}",
            "scan_date": datetime.now(timezone.utc).isoformat(),
        }

    all_registries = []
    plugins_scanned = []

    for entry in sorted(os.listdir(plugins_root)):
        plugin_dir = os.path.join(plugins_root, entry)
        if not os.path.isdir(plugin_dir):
            continue
        if not entry.startswith("DataWarehouse.Plugins."):
            continue

        registries = scan_plugin_directory(plugin_dir)
        plugins_scanned.append(entry)
        all_registries.extend(registries)

    # Deduplicate: same plugin + file combination (in case of nested patterns)
    seen = set()
    unique_registries = []
    for reg in all_registries:
        key = (reg["plugin"], reg["file"])
        if key not in seen:
            seen.add(key)
            unique_registries.append(reg)

    # Compute classification counts
    classifications = {"DUPLICATE": 0, "UNIQUE": 0, "ALREADY_MIGRATED": 0}
    pattern_counts = {"A": 0, "B": 0, "C": 0}
    for reg in unique_registries:
        cls = reg.get("classification", "UNKNOWN")
        if cls in classifications:
            classifications[cls] += 1
        pat = reg.get("pattern", "")
        if pat in pattern_counts:
            pattern_counts[pat] += 1

    return {
        "scan_date": datetime.now(timezone.utc).isoformat(),
        "plugins_root": plugins_root,
        "total_plugins_scanned": len(plugins_scanned),
        "registries_found": len(unique_registries),
        "classifications": classifications,
        "pattern_counts": pattern_counts,
        "registries": unique_registries,
    }


def main():
    parser = argparse.ArgumentParser(
        description="Audit custom strategy registries in DataWarehouse Plugin directories."
    )
    parser.add_argument(
        "--plugins-dir",
        default=None,
        help="Path to Plugins directory. Defaults to auto-detect from script location.",
    )
    parser.add_argument(
        "--indent",
        type=int,
        default=2,
        help="JSON indentation level (default: 2). Use 0 for compact.",
    )
    args = parser.parse_args()

    # Auto-detect plugins directory relative to script location
    if args.plugins_dir:
        plugins_root = args.plugins_dir
    else:
        # Script is in Metadata/, plugins are in Plugins/ sibling
        script_dir = os.path.dirname(os.path.abspath(__file__))
        project_root = os.path.dirname(script_dir)
        plugins_root = os.path.join(project_root, "Plugins")

    report = scan_all_plugins(plugins_root)
    indent = args.indent if args.indent > 0 else None
    print(json.dumps(report, indent=indent, default=str))

    # Print summary to stderr for human visibility
    if "error" not in report:
        sys.stderr.write(
            f"\n[SUMMARY] Scanned {report['total_plugins_scanned']} plugins, "
            f"found {report['registries_found']} registries\n"
            f"  DUPLICATE:        {report['classifications']['DUPLICATE']}\n"
            f"  UNIQUE:           {report['classifications']['UNIQUE']}\n"
            f"  ALREADY_MIGRATED: {report['classifications']['ALREADY_MIGRATED']}\n"
            f"  Pattern A (standalone): {report['pattern_counts']['A']}\n"
            f"  Pattern B (inline):     {report['pattern_counts']['B']}\n"
            f"  Pattern C (inline cls): {report['pattern_counts']['C']}\n"
        )


if __name__ == "__main__":
    main()
