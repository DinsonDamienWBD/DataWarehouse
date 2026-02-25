#!/usr/bin/env python3
import re, sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path

V4_DOMAINS = {
    1:"Data Pipeline", 2:"Storage", 3:"Security", 4:"Media", 5:"Distributed",
    6:"Hardware", 7:"Edge/IoT", 8:"AEDS", 9:"Air-Gap", 10:"Filesystem",
    11:"Compute", 12:"Transport", 13:"Intelligence", 14:"Observability",
    15:"Governance", 16:"Cloud", 17:"CLI/GUI"
}

ASP = {
    "UltimateStorage": ["Hot-path caching", "Cost optimization", "Geo-failover", "Capacity planning", "Tiering recommendations", "Dedup savings", "Cross-cloud cost compare", "Perf benchmarking", "Auto migration", "Inventory dashboard"],
    "UltimateEncryption": ["Key ceremony", "Crypto agility", "Quantum-safe migration", "Rotation impact", "Perf profiling", "HW accelerator dash", "Compliance report", "Key audit trail", "Emergency escrow", "Algorithm recommend"],
    "UltimateRAID": ["Rebuild progress", "Self-healing", "Level recommendation", "Degraded analysis", "Parity verify", "Hot spare mgmt", "Migration no downtime", "Erasure calc", "Shard viz", "Scrub scheduler"],
    "UltimateIntelligence": ["Model A/B test", "Inference cost track", "Model versioning", "Multi-provider fallback", "Embedding cache", "Graph viz", "Provider benchmark", "Fine-tuning pipeline", "Anomaly confidence", "Federated learning mon"],
    "AEDS": ["Client heartbeat dash", "Bandwidth throttle", "Update rollback", "Health scoring", "Geo distribution map", "Delta updates", "Client auth", "Manifest sig verify"],
    "UltimateCompliance": ["Compliance dash", "Auto reports", "Data residency verify", "PII detection", "Retention enforce", "Violation alerts", "Regulatory tracking", "Multi-framework map", "Sovereignty controls", "Evidence collect"],
    "UltimateReplication": ["Lag monitoring", "Conflict dash", "Topology viz", "Geo heatmap", "Bandwidth throttle", "Failover simulation", "Split-brain detect", "Replication verify", "Cross-cloud cost", "Cluster health"],
    "UltimateAccessControl": ["Policy templates", "Permission viz", "Least privilege analysis", "Access review workflow", "Temporal grants", "Zero-trust enforcement", "Pattern anomaly", "Delegated admin", "MFA customize", "Audit trail"]
}

def load(p):
    fb = defaultdict(list)
    cd = None
    for line in open(p, encoding='utf-8'):
        line = line.strip()
        if line.startswith('===') and 'features)' in line:
            m = re.search(r'=== (.+?) \(', line)
            if m: cd = m.group(1)
        elif line.startswith('- ') and cd:
            fb[cd].append(line[2:].strip())
    return fb

def map_d(f, d):
    fl = f.lower()
    if any(k in fl for k in ['encrypt','key','crypto','security']): return 3
    if any(k in fl for k in ['ai ','ml ','model','semantic']): return 13
    if any(k in fl for k in ['video','audio','image','codec']): return 4
    if any(k in fl for k in ['storage','s3','blob']): return 2
    if any(k in fl for k in ['raft','consensus','replication']): return 5
    if any(k in fl for k in ['governance','compliance','gdpr']): return 15
    return 1

def main():
    base = Path("C:/Temp/DataWarehouse/DataWarehouse/Metadata")
    features = load(base / "FeatureCatalog.txt")
    
    df = defaultdict(list)
    for dn, fl in features.items():
        for f in fl:
            v4d = map_d(f, dn)
            df[v4d].append((f, dn))
    
    verified = sum(len(v) for v in df.values())
    aspirational = sum(len(v) for v in ASP.values())
    total = verified + aspirational
    
    with open(base / "FeatureVerificationMatrix.md", 'w', encoding='utf-8') as out:
        out.write(f"# DataWarehouse v4.0 Feature Verification Matrix\n\n")
        out.write(f"Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n\n")
        out.write(f"## Statistics\n\n- Verified: {verified}\n- Aspirational: {aspirational}\n- **TOTAL: {total}**\n\n---\n\n")
        
        for dn in sorted(df.keys()):
            out.write(f"## Domain {dn}: {V4_DOMAINS.get(dn, f'D{dn}')}\n\n")
            out.write("### Verified Features\n\n")
            for f, src in sorted(df[dn])[:15]:
                out.write(f"- [ ] {f} (Source: {src})\n")
            if len(df[dn]) > 15:
                out.write(f"- ... and {len(df[dn])-15} more\n")
            out.write("\n### Aspirational\n\n")
            for plugin, feats in sorted(ASP.items()):
                for feat in feats[:2]:
                    out.write(f"- [ ] {feat} — {plugin}\n")
            out.write("\n---\n\n")
    
    print(f"✅ Generated: {total} items ({verified} verified + {aspirational} aspirational)")
    return 0

if __name__ == "__main__":
    sys.exit(main())
