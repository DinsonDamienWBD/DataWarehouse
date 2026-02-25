#!/usr/bin/env python3
"""
Fix remaining ConcurrentDictionary instances:
1. Field declarations with outer ConcurrentDictionary (nested generics not caught by previous pass)
2. Broken new() expressions that have wrong type params
3. Non-plugin class usages
"""
import os
import re
import sys

SKIP_DIRS = {"bin", "obj", ".git", ".planning", ".omc", "DataWarehouse.Benchmarks"}
# Also skip tests since we're not migrating those
ALSO_SKIP = {"Tests", "Test"}

def find_cs_files(base="."):
    result = []
    for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not any(s in root for s in ALSO_SKIP)]
        for f in files:
            if f.endswith(".cs"):
                result.append(os.path.join(root, f))
    return result

def replace_concurrent_dict_types(text):
    """Replace all ConcurrentDictionary<K,V> type references with BoundedDictionary<K,V>."""

    # Handle nested generics by doing multiple passes
    prev = None
    while prev != text:
        prev = text
        # Replace innermost non-nested ConcurrentDictionary type params first
        text = re.sub(r'ConcurrentDictionary(<[^<>]+>)', r'BoundedDictionary\1', text)

    return text

def fix_broken_new_exprs(text):
    """Fix broken new() expressions where type params are wrong."""

    # Pattern: ConcurrentDictionary<outer, BoundedDictionary<inner>> _field = new BoundedDictionary<inner>>(cap);
    # The > at end of BoundedDictionary<inner> is extra because of the outer closing >
    # Fix: new BoundedDictionary<outer, BoundedDictionary<inner>>(cap);

    # Also fix: new BoundedDictionary<K,V>>(cap) -> new BoundedDictionary<K,V>(cap) (extra >)
    text = re.sub(r'new BoundedDictionary(<[^>]+>)>', r'new BoundedDictionary\1', text)

    # Fix broken declarations where outer type is still ConcurrentDictionary
    # These need the outer to be replaced too

    return text

def ensure_using(text):
    """Add using DataWarehouse.SDK.Utilities if BoundedDictionary used without it."""
    if 'BoundedDictionary' in text and 'using DataWarehouse.SDK.Utilities' not in text:
        using_block = re.search(r'^((?:using [^\n]+;\n)+)', text, re.MULTILINE)
        if using_block:
            insert_pos = using_block.end()
            text = text[:insert_pos] + 'using DataWarehouse.SDK.Utilities;\n' + text[insert_pos:]
        else:
            text = 'using DataWarehouse.SDK.Utilities;\n' + text
    return text

def fix_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except Exception as e:
        return False, str(e)

    if 'ConcurrentDictionary' not in original and 'BoundedDictionary' not in original:
        return False, None

    text = original

    # Fix 1: Replace remaining ConcurrentDictionary type references
    if 'ConcurrentDictionary' in text:
        text = replace_concurrent_dict_types(text)

    # Fix 2: Fix broken new BoundedDictionary expressions (extra > bracket)
    text = fix_broken_new_exprs(text)

    # Fix 3: Fix new BoundedDictionary<K,V>() missing capacity
    text = re.sub(
        r'new BoundedDictionary(<[^>]+(?:<[^>]+>[^>]*)?>)\(\)',
        lambda m: f'new BoundedDictionary{m.group(1)}(1000)',
        text
    )

    # Fix 4: Fix new() target-typed with BoundedDictionary
    lines = text.split('\n')
    new_lines = []
    for line in lines:
        if 'BoundedDictionary' in line and '= new();' in line:
            m = re.search(r'BoundedDictionary(<[^;]+?>)\s+\w+\s*=\s*new\(\);', line)
            if m:
                type_params = m.group(1)
                line = line.replace('= new();', f'= new BoundedDictionary{type_params}(1000);')
        new_lines.append(line)
    text = '\n'.join(new_lines)

    # Fix 5: Remove ConcurrentDictionary using if no longer used
    if 'ConcurrentDictionary' not in text and 'ConcurrentBag' not in text and \
       'ConcurrentQueue' not in text and 'ConcurrentStack' not in text and \
       'BlockingCollection' not in text:
        text = re.sub(r'using System\.Collections\.Concurrent;\n?', '', text)

    # Fix 6: Ensure using DataWarehouse.SDK.Utilities
    text = ensure_using(text)

    if text != original:
        try:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(text)
            return True, None
        except Exception as e:
            return False, str(e)
    return False, None

if __name__ == "__main__":
    files = find_cs_files()
    changed = 0
    errors = []

    for path in files:
        rel = os.path.relpath(path, ".")
        ok, err = fix_file(path)
        if ok:
            changed += 1
            print(f"FIXED: {rel}")
        elif err:
            errors.append((rel, err))

    print(f"\nTotal fixed: {changed}")
    if errors:
        for rel, err in errors:
            print(f"ERROR: {rel}: {err}")
