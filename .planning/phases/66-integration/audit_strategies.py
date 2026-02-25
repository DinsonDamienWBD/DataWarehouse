#!/usr/bin/env python3
"""Audit all strategy classes across the DataWarehouse codebase."""

import os
import re
import json
from collections import defaultdict

ROOT = r"C:\Temp\DataWarehouse\DataWarehouse"
PLUGINS_DIR = os.path.join(ROOT, "Plugins")
SDK_DIR = os.path.join(ROOT, "DataWarehouse.SDK")
TESTS_DIR = os.path.join(ROOT, "DataWarehouse.Tests")

# Pattern to find concrete strategy classes inheriting from a *StrategyBase
# Matches: class SomeName : SomeStrategyBase  or  class SomeName : SomeBase<T>
STRATEGY_CLASS_RE = re.compile(
    r'(?:public|internal|private)\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)\s*(?:<[^>]+>)?\s*:\s*(\w*(?:StrategyBase|Strategy)\w*)',
    re.MULTILINE
)

# More precise: inherits from *StrategyBase specifically
STRATEGY_BASE_RE = re.compile(
    r'(?:public|internal|private)\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)\s*(?:<[^>]+>)?\s*:\s*(\w*StrategyBase\w*)',
    re.MULTILINE
)

# Registration patterns
REG_PATTERNS = [
    re.compile(r'RegisterStrategy\s*[<(]', re.MULTILINE),
    re.compile(r'AddStrategy\s*[<(]', re.MULTILINE),
    re.compile(r'strategies\.Add\s*\(', re.MULTILINE),
    re.compile(r'yield\s+return\s+new\s+(\w+Strategy\w*)\s*[({]', re.MULTILINE),
    re.compile(r'new\s+(\w+Strategy\w*)\s*[({]', re.MULTILINE),
]

# Recognized domain bases
RECOGNIZED_BASES = {
    'StrategyBase', 'EncryptionStrategyBase', 'CompressionStrategyBase',
    'StorageStrategyBase', 'ReplicationStrategyBase', 'SecurityStrategyBase',
    'InterfaceStrategyBase', 'ConnectorStrategyBase', 'ComputeStrategyBase',
    'ObservabilityStrategyBase', 'MediaStrategyBase', 'StreamingStrategyBase',
    'FormatStrategyBase', 'TransitStrategyBase', 'DataManagementStrategyBase',
    'KeyManagementStrategyBase', 'ComplianceStrategyBase', 'DataProtectionStrategyBase',
    'RaidStrategyBase', 'DatabaseStorageStrategyBase', 'DataLakeStrategyBase',
    'DataMeshStrategyBase', 'ChaosVaccinationStrategyBase', 'CacheStrategyBase',
    'SearchStrategyBase', 'IndexStrategyBase', 'QueryStrategyBase',
    'IntelligenceStrategyBase', 'DataFlowStrategyBase', 'EdgeStrategyBase',
    'SyncStrategyBase', 'VersionControlStrategyBase', 'WorkflowStrategyBase',
    'AnalyticsStrategyBase', 'NetworkStrategyBase', 'TransportStrategyBase',
    'SchedulingStrategyBase', 'ResourceStrategyBase', 'AdaptiveTransportStrategyBase',
    'SchemaStrategyBase', 'MigrationStrategyBase', 'ValidationStrategyBase',
    'MonitoringStrategyBase', 'LoggingStrategyBase', 'AlertStrategyBase',
    'MetricsStrategyBase', 'TracingStrategyBase', 'ProfilerStrategyBase',
    'AuditStrategyBase', 'GovernanceStrategyBase', 'PolicyStrategyBase',
    'ClassificationStrategyBase', 'RetentionStrategyBase',
}

def get_plugin_name(filepath):
    """Extract plugin name from file path."""
    rel = os.path.relpath(filepath, ROOT)
    parts = rel.split(os.sep)
    if parts[0] == 'Plugins' and len(parts) > 1:
        return parts[1].replace('DataWarehouse.Plugins.', '')
    elif parts[0] == 'DataWarehouse.SDK':
        return 'SDK'
    elif parts[0] == 'DataWarehouse.Tests':
        return 'Tests'
    else:
        return parts[0]

def scan_cs_files(directory):
    """Yield all .cs file paths in directory."""
    for dirpath, _, filenames in os.walk(directory):
        for f in filenames:
            if f.endswith('.cs'):
                yield os.path.join(dirpath, f)

def find_all_strategy_bases():
    """Find all *StrategyBase abstract classes defined in the codebase."""
    bases = set()
    base_re = re.compile(
        r'(?:public|internal)\s+abstract\s+class\s+(\w*StrategyBase\w*)',
        re.MULTILINE
    )
    for d in [SDK_DIR, PLUGINS_DIR]:
        if os.path.exists(d):
            for f in scan_cs_files(d):
                try:
                    content = open(f, encoding='utf-8', errors='ignore').read()
                    for m in base_re.finditer(content):
                        bases.add(m.group(1))
                except:
                    pass
    return bases

def find_strategy_classes():
    """Find all concrete strategy classes and their base classes."""
    strategies = []  # (class_name, base_class, plugin, filepath, line)

    for d in [PLUGINS_DIR, SDK_DIR]:
        if not os.path.exists(d):
            continue
        for filepath in scan_cs_files(d):
            try:
                content = open(filepath, encoding='utf-8', errors='ignore').read()
            except:
                continue

            for m in STRATEGY_BASE_RE.finditer(content):
                cls_name = m.group(1)
                base_name = m.group(2)
                # Skip abstract classes (they are bases, not concrete)
                # Check if the class declaration includes 'abstract'
                start = max(0, m.start() - 50)
                prefix = content[start:m.start()]
                if 'abstract' in prefix:
                    continue
                # Skip test classes
                plugin = get_plugin_name(filepath)
                if plugin == 'Tests':
                    continue
                line_num = content[:m.start()].count('\n') + 1
                strategies.append({
                    'class': cls_name,
                    'base': base_name,
                    'plugin': plugin,
                    'file': os.path.relpath(filepath, ROOT),
                    'line': line_num,
                })

    return strategies

def find_registrations():
    """Find all strategy registration calls in plugin code."""
    registrations = defaultdict(set)  # plugin -> set of registered class names

    # Also look for strategies instantiated in GetStrategies/RegisterStrategies methods
    method_re = re.compile(
        r'(?:GetStrategies|RegisterStrategies|RegisterStrategy|InitializeAsync|Register)\s*\(',
        re.MULTILINE
    )

    new_strategy_re = re.compile(r'new\s+(\w+)\s*[({]')

    for filepath in scan_cs_files(PLUGINS_DIR):
        try:
            content = open(filepath, encoding='utf-8', errors='ignore').read()
        except:
            continue

        plugin = get_plugin_name(filepath)

        # Find all 'new StrategyName()' patterns
        for m in new_strategy_re.finditer(content):
            name = m.group(1)
            if 'Strategy' in name:
                registrations[plugin].add(name)

        # Find RegisterStrategy<T> patterns
        reg_generic = re.compile(r'RegisterStrategy<(\w+)>')
        for m in reg_generic.finditer(content):
            registrations[plugin].add(m.group(1))

        # Find AddStrategy<T> patterns
        add_generic = re.compile(r'AddStrategy<(\w+)>')
        for m in add_generic.finditer(content):
            registrations[plugin].add(m.group(1))

    return registrations

def main():
    print("=== Strategy Audit ===")

    # Find all strategy base classes
    all_bases = find_all_strategy_bases()
    print(f"\nFound {len(all_bases)} strategy base classes:")
    for b in sorted(all_bases):
        print(f"  - {b}")

    # Find all concrete strategy classes
    strategies = find_strategy_classes()
    print(f"\nFound {len(strategies)} concrete strategy classes")

    # Find registrations
    registrations = find_registrations()

    # Cross-reference
    by_plugin = defaultdict(list)
    for s in strategies:
        by_plugin[s['plugin']].append(s)

    # Build report data
    report = {
        'total_strategies': len(strategies),
        'total_plugins': len(by_plugin),
        'bases': sorted(all_bases),
        'plugins': {},
        'orphaned': [],
        'base_distribution': defaultdict(int),
    }

    total_registered = 0
    total_orphaned = 0

    for plugin in sorted(by_plugin.keys()):
        plugin_strategies = by_plugin[plugin]
        plugin_regs = registrations.get(plugin, set())

        registered = []
        orphaned = []

        for s in plugin_strategies:
            cls = s['class']
            # Check if registered: class name appears in registrations for this plugin
            is_registered = cls in plugin_regs
            if is_registered:
                registered.append(s)
            else:
                # Check cross-plugin registration
                found_elsewhere = False
                for p, regs in registrations.items():
                    if cls in regs:
                        found_elsewhere = True
                        break
                if found_elsewhere:
                    registered.append(s)
                else:
                    orphaned.append(s)

        report['plugins'][plugin] = {
            'total': len(plugin_strategies),
            'registered': len(registered),
            'orphaned': len(orphaned),
            'strategies': plugin_strategies,
            'orphaned_list': orphaned,
        }

        total_registered += len(registered)
        total_orphaned += len(orphaned)

        for s in plugin_strategies:
            report['base_distribution'][s['base']] += 1

    report['total_registered'] = total_registered
    report['total_orphaned'] = total_orphaned

    # Output as JSON for further processing
    # Convert defaultdict to dict for JSON
    report['base_distribution'] = dict(report['base_distribution'])

    print(f"\nTotal: {len(strategies)} strategies, {total_registered} registered, {total_orphaned} potentially orphaned")
    print(f"\nBase distribution:")
    for base, count in sorted(report['base_distribution'].items(), key=lambda x: -x[1]):
        print(f"  {base}: {count}")

    print(f"\nOrphaned strategies ({total_orphaned}):")
    for plugin, data in sorted(report['plugins'].items()):
        for s in data['orphaned_list']:
            print(f"  [{plugin}] {s['class']} : {s['base']} ({s['file']}:{s['line']})")

    # Write JSON for report generation
    with open(os.path.join(ROOT, '.planning', 'phases', '66-integration', 'strategy_audit.json'), 'w') as f:
        json.dump(report, f, indent=2, default=str)

    print(f"\nJSON written to strategy_audit.json")

if __name__ == '__main__':
    main()
