#!/usr/bin/env python3
"""
Extract all features scored 50-79% from verification reports.
Group by domain and plugin.
Estimate effort (S/M/L/XL).
"""

import re
import json
from pathlib import Path

# Verification report files from Plans 42-01 through 42-04
VERIFICATION_REPORTS = [
    "domain-01-pipeline-verification.md",
    "domain-02-storage-verification.md",
    "domain-03-security-verification.md",
    "domain-04-media-verification.md",
]

def extract_50_79_features(report_path):
    """Extract features with 50-79% scores from a verification report."""
    content = report_path.read_text(encoding='utf-8')
    
    # Pattern to match feature entries with scores
    # Looking for patterns like: "- Feature Name (60%)" or "- **Feature** — 65%"
    pattern = r'[-*]\s+(.+?)\s*[:(—]\s*(\d+)%'
    
    features_50_79 = []
    
    for match in re.finditer(pattern, content):
        feature_name = match.group(1).strip()
        score = int(match.group(2))
        
        if 50 <= score <= 79:
            features_50_79.append({
                'name': feature_name,
                'score': score,
                'domain': report_path.stem
            })
    
    return features_50_79

def main():
    base_dir = Path(__file__).parent
    
    all_features = []
    
    for report_file in VERIFICATION_REPORTS:
        report_path = base_dir / report_file
        if report_path.exists():
            features = extract_50_79_features(report_path)
            all_features.extend(features)
            print(f"Extracted {len(features)} features from {report_file}")
        else:
            print(f"WARNING: {report_file} not found")
    
    # Save results
    output_path = base_dir / "significant-items-extracted.json"
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(all_features, f, indent=2)
    
    print(f"\nTotal significant items (50-79%): {len(all_features)}")
    print(f"Saved to: {output_path}")

if __name__ == '__main__':
    main()
