#!/usr/bin/env python3
"""
Feature Verification Script for Domains 5-8
Systematically scans plugin code to score production readiness
"""

import os
import re
from pathlib import Path
from collections import defaultdict
from typing import Dict, List, Tuple

# Base directory
BASE_DIR = Path(r"C:\Temp\DataWarehouse\DataWarehouse")

# Plugin directories to scan
PLUGINS = {
    "Domain 5": {
        "UltimateReplication": BASE_DIR / "Plugins/DataWarehouse.Plugins.UltimateReplication",
        "UltimateConsensus": BASE_DIR / "Plugins/DataWarehouse.Plugins.UltimateConsensus",
        "UltimateResilience": BASE_DIR / "Plugins/DataWarehouse.Plugins.UltimateResilience",
    },
    "Domain 6": {
        "SDK.Hardware": BASE_DIR / "DataWarehouse.SDK/Hardware",
        "SDK.Deployment": BASE_DIR / "DataWarehouse.SDK/Deployment",
    },
    "Domain 7": {
        "UltimateEdgeComputing": BASE_DIR / "Plugins/DataWarehouse.Plugins.UltimateEdgeComputing",
        "UltimateIoTIntegration": BASE_DIR / "Plugins/DataWarehouse.Plugins.UltimateIoTIntegration",
        "SDK.Edge": BASE_DIR / "DataWarehouse.SDK/Edge",
    },
    "Domain 8": {
        "AedsCore": BASE_DIR / "Plugins/DataWarehouse.Plugins.AedsCore",
    }
}

def extract_classes(file_path: Path) -> List[Tuple[str, List[str]]]:
    """Extract class names and their method signatures from a C# file."""
    if not file_path.exists():
        return []

    try:
        content = file_path.read_text(encoding='utf-8')
    except:
        return []

    classes = []

    # Find all class declarations
    class_pattern = r'public\s+(sealed\s+)?class\s+(\w+Strategy|\w+Provider|\w+Engine|\w+Service|\w+Feature)'
    for match in re.finditer(class_pattern, content):
        class_name = match.group(2)

        # Extract methods for this class (simple heuristic)
        class_start = match.start()
        class_section = content[class_start:class_start+5000]  # Look ahead 5000 chars

        # Find method signatures
        method_pattern = r'(public|protected|private)\s+(override\s+)?(async\s+)?Task<?[\w<>,\s]*>?\s+(\w+)\s*\('
        methods = [m.group(4) for m in re.finditer(method_pattern, class_section)]

        classes.append((class_name, methods))

    return classes

def score_implementation(class_name: str, methods: List[str], content: str) -> int:
    """
    Score a strategy/class from 0-100 based on implementation completeness.

    100%: Fully implemented with real logic
    80-99%: Core logic exists, needs polish
    50-79%: Partial implementation
    20-49%: Scaffolding only
    1-19%: Interface/stub only
    0%: Does not exist
    """
    if not methods:
        return 1  # Class exists but no methods = stub

    # Check for NotImplementedException (indicates stub)
    if 'NotImplementedException' in content:
        return 15

    # Check for TODO/FIXME (indicates incomplete)
    if re.search(r'//\s*TODO|//\s*FIXME', content):
        return 60

    # Check method count (more methods = more complete)
    method_count = len(methods)
    if method_count >= 10:
        # Many methods, check for real logic
        if re.search(r'(await|Task\.)', content):
            return 95  # Has async logic
        return 85
    elif method_count >= 5:
        return 70
    elif method_count >= 2:
        return 50
    else:
        return 25

def scan_plugin(plugin_dir: Path) -> Dict[str, int]:
    """Scan a plugin directory and return feature scores."""
    features = {}

    if not plugin_dir.exists():
        return features

    # Find all .cs files
    for cs_file in plugin_dir.rglob("*.cs"):
        if "obj" in str(cs_file) or "bin" in str(cs_file):
            continue

        try:
            content = cs_file.read_text(encoding='utf-8')
        except:
            continue

        # Extract classes
        classes = extract_classes(cs_file)

        for class_name, methods in classes:
            # Get class content for scoring
            class_pattern = rf'class\s+{re.escape(class_name)}.*?\n(.*?)(?=\n\s*class\s+|\n\s*namespace\s+|\Z)'
            match = re.search(class_pattern, content, re.DOTALL)
            class_content = match.group(1) if match else content

            score = score_implementation(class_name, methods, class_content)
            features[class_name] = score

    return features

def generate_report(domain_name: str, domain_plugins: Dict[str, Path], output_file: Path):
    """Generate a verification report for a domain."""
    all_features = {}

    # Scan all plugins in the domain
    for plugin_name, plugin_dir in domain_plugins.items():
        plugin_features = scan_plugin(plugin_dir)
        for feature, score in plugin_features.items():
            all_features[f"{feature} ({plugin_name})"] = score

    # Calculate statistics
    total = len(all_features)
    if total == 0:
        return

    score_100 = sum(1 for s in all_features.values() if s == 100)
    score_80_99 = sum(1 for s in all_features.values() if 80 <= s <= 99)
    score_50_79 = sum(1 for s in all_features.values() if 50 <= s <= 79)
    score_20_49 = sum(1 for s in all_features.values() if 20 <= s <= 49)
    score_1_19 = sum(1 for s in all_features.values() if 1 <= s <= 19)
    score_0 = sum(1 for s in all_features.values() if s == 0)

    avg_score = sum(all_features.values()) // total if total > 0 else 0

    # Write report
    with open(output_file, 'w', encoding='utf-8') as f:
        f.write(f"# {domain_name} Verification Report\n\n")
        f.write(f"## Summary\n\n")
        f.write(f"- Total Features: {total}\n")
        f.write(f"- Average Score: {avg_score}%\n")
        f.write(f"- Scanned Plugins: {', '.join(domain_plugins.keys())}\n\n")

        f.write(f"## Score Distribution\n\n")
        f.write(f"| Range | Count | % |\n")
        f.write(f"|-------|-------|---|\n")
        f.write(f"| 100% | {score_100} | {score_100*100//total if total else 0}% |\n")
        f.write(f"| 80-99% | {score_80_99} | {score_80_99*100//total if total else 0}% |\n")
        f.write(f"| 50-79% | {score_50_79} | {score_50_79*100//total if total else 0}% |\n")
        f.write(f"| 20-49% | {score_20_49} | {score_20_49*100//total if total else 0}% |\n")
        f.write(f"| 1-19% | {score_1_19} | {score_1_19*100//total if total else 0}% |\n")
        f.write(f"| 0% | {score_0} | {score_0*100//total if total else 0}% |\n\n")

        f.write(f"## Feature Scores\n\n")

        # Group by plugin
        by_plugin = defaultdict(list)
        for feature, score in sorted(all_features.items(), key=lambda x: x[1], reverse=True):
            # Extract plugin name from feature name
            match = re.search(r'\(([^)]+)\)$', feature)
            plugin = match.group(1) if match else "Unknown"
            by_plugin[plugin].append((feature, score))

        for plugin, features in sorted(by_plugin.items()):
            f.write(f"### Plugin: {plugin}\n\n")
            for feature, score in features:
                clean_name = re.sub(r'\s*\([^)]+\)$', '', feature)
                status = get_status_text(score)
                f.write(f"- [~] {score}% {clean_name}\n")
                f.write(f"  - **Status**: {status}\n")
            f.write("\n")

def get_status_text(score: int) -> str:
    """Get status description for a score."""
    if score == 100:
        return "Fully implemented, production-ready"
    elif score >= 80:
        return "Core logic done, needs polish"
    elif score >= 50:
        return "Partial implementation"
    elif score >= 20:
        return "Scaffolding only"
    elif score >= 1:
        return "Interface/stub only"
    else:
        return "Missing"

def main():
    """Main execution."""
    output_dir = BASE_DIR / ".planning/phases/42-feature-verification-matrix"
    output_dir.mkdir(parents=True, exist_ok=True)

    # Generate reports for each domain
    for domain_name, domain_plugins in PLUGINS.items():
        domain_num = domain_name.split()[1]
        output_file = output_dir / f"domain-0{domain_num}-auto-verification.md"

        print(f"Scanning {domain_name}...")
        generate_report(domain_name, domain_plugins, output_file)
        print(f"  Written: {output_file}")

    print("\nVerification complete!")

if __name__ == "__main__":
    main()
