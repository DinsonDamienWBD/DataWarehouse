#!/usr/bin/env python3
"""
Migrate ConcurrentDictionary to BoundedDictionary across the DataWarehouse solution.
Run from the solution root directory.
"""
import os
import re
import sys

BASE = "."

# Dirs to skip
SKIP_DIRS = {"bin", "obj", "Tests", "Benchmarks", ".git", ".planning", ".omc",
             "DataWarehouse.Benchmarks"}

def get_capacity(field_name):
    name = field_name.lower()
    if any(k in name for k in ["metric", "counter", "stat", "monitor", "perf", "telemetry", "analytics"]):
        return 5000
    if any(k in name for k in ["cache", "index", "store", "data", "entries", "record", "block", "chunk"]):
        return 10000
    if any(k in name for k in ["session", "connection", "client", "peer", "node", "member", "subscription", "subscriber", "limiter"]):
        return 1000
    if any(k in name for k in ["strategy", "registry", "handler", "processor", "provider", "factory", "resolver", "plugin"]):
        return 200
    if any(k in name for k in ["config", "setting", "option", "param", "feature", "toggle", "policy", "rule", "tenant"]):
        return 500
    if any(k in name for k in ["job", "task", "queue", "pending", "active", "running", "scheduled"]):
        return 1000
    return 1000

def find_cs_files(base):
    result = []
    for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                result.append(os.path.join(root, f))
    return result

def migrate_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except Exception as e:
        return False, str(e)

    if 'ConcurrentDictionary' not in original:
        return False, None

    text = original

    # Replace type references: ConcurrentDictionary<...> -> BoundedDictionary<...>
    # Handle nested generics by doing multiple passes
    def replace_type(t):
        pattern = re.compile(r'ConcurrentDictionary(<[^<>]+(?:<[^<>]+>[^<>]*)?>)')
        return pattern.sub(r'BoundedDictionary\1', t)

    # Multiple passes for nested generics
    for _ in range(3):
        new_text = replace_type(text)
        if new_text == text:
            break
        text = new_text

    # Replace new BoundedDictionary<K,V>(...) - fix bad ctor args
    def fix_ctor(m):
        type_part = m.group(1) or ''
        args = (m.group(2) or '').strip()
        # If args is empty, add capacity
        if not args:
            return f'new BoundedDictionary{type_part}(1000)'
        # If args looks like a number already, keep it
        if re.match(r'^\d+$', args):
            return f'new BoundedDictionary{type_part}({args})'
        # Otherwise it's a comparer or other arg - replace with capacity
        return f'new BoundedDictionary{type_part}(1000)'

    ctor_pattern = re.compile(r'new BoundedDictionary(<[^>]+(?:<[^>]+>[^>]*)?>)?\s*\(([^)]*)\)')
    text = ctor_pattern.sub(fix_ctor, text)

    # Add using DataWarehouse.SDK.Utilities if BoundedDictionary is used and not already imported
    if 'BoundedDictionary' in text and 'using DataWarehouse.SDK.Utilities' not in text:
        using_block = re.search(r'^((?:using [^\n]+;\n)+)', text, re.MULTILINE)
        if using_block:
            insert_pos = using_block.end()
            text = text[:insert_pos] + 'using DataWarehouse.SDK.Utilities;\n' + text[insert_pos:]
        else:
            text = 'using DataWarehouse.SDK.Utilities;\n' + text

    # Remove using System.Collections.Concurrent if no longer needed
    if 'ConcurrentDictionary' not in text and 'ConcurrentBag' not in text and \
       'ConcurrentQueue' not in text and 'ConcurrentStack' not in text and \
       'BlockingCollection' not in text and 'IProducerConsumer' not in text:
        text = re.sub(r'using System\.Collections\.Concurrent;\n?', '', text)

    if text != original:
        try:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(text)
            return True, None
        except Exception as e:
            return False, str(e)
    return False, None

def main():
    files = find_cs_files(BASE)
    changed = 0
    errors = []

    for path in files:
        rel = os.path.relpath(path, BASE)
        ok, err = migrate_file(path)
        if ok:
            changed += 1
            print(f"MIGRATED: {rel}")
        elif err:
            errors.append((rel, err))
            print(f"ERROR: {rel}: {err}", file=sys.stderr)

    print(f"\nTotal files changed: {changed}")
    print(f"Total errors: {len(errors)}")
    if errors:
        for rel, err in errors:
            print(f"  ERROR: {rel}: {err}")

if __name__ == "__main__":
    main()
