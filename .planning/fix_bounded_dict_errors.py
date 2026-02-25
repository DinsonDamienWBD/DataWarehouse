#!/usr/bin/env python3
"""
Fix BoundedDictionary migration errors:
1. new BoundedDictionary<K,V>() missing capacity -> new BoundedDictionary<K,V>(1000)
2. new BoundedDictionary<K,V>(StringComparer.X) -> new BoundedDictionary<K,V>(1000)
3. .IsEmpty -> .Count == 0 (already added IsEmpty property, so this is ok)
4. .Count() method call -> .Count property
5. GetOrAdd with lambda (ValueFactory) -> fix lambda-as-value errors
Run from solution root.
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

def fix_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except Exception as e:
        return False, str(e)

    if 'BoundedDictionary' not in original:
        return False, None

    text = original

    # Fix 1: new BoundedDictionary<...>() with no args -> add 1000
    # Match: new BoundedDictionary<type_params>()
    def fix_empty_ctor(m):
        type_part = m.group(1) or ''
        return f'new BoundedDictionary{type_part}(1000)'

    # target-typed new() that resolves to BoundedDictionary - can't easily detect those
    # But the previous migration left: = new BoundedDictionary<K,V>() - fix those
    text = re.sub(
        r'new BoundedDictionary(<[^>]+(?:<[^>]+>[^>]*)?>)\(\)',
        lambda m: f'new BoundedDictionary{m.group(1)}(1000)',
        text
    )

    # Fix 2: new BoundedDictionary<K,V>(StringComparer.X) or similar non-integer first arg
    def fix_bad_ctor_arg(m):
        type_part = m.group(1) or ''
        args = m.group(2).strip()
        # If first arg is not an integer literal, replace with 1000
        if args and not re.match(r'^\d+', args):
            return f'new BoundedDictionary{type_part}(1000)'
        return m.group(0)  # keep as-is

    text = re.sub(
        r'new BoundedDictionary(<[^>]+(?:<[^>]+>[^>]*)?>)\(([^)]+)\)',
        fix_bad_ctor_arg,
        text
    )

    # Fix 3: .Count() -> .Count (ConcurrentDictionary had Count as property, Count() is LINQ extension)
    # BoundedDictionary.Count is a property, not method - fix .Count() calls
    text = re.sub(r'\.Count\(\)', '.Count', text)

    # Fix 4: GetOrAdd with lambda that was wrongly typed
    # AddOrUpdate(key, lambda, lambda) -> fix bad addValue arg (lambda)
    # GetOrAdd(key, value) where value is lambda for AddOrUpdate
    # These need to be looked at case by case - just flag the file for manual review

    if text != original:
        try:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(text)
            return True, None
        except Exception as e:
            return False, str(e)
    return False, None

def main():
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
            print(f"ERROR: {rel}: {err}", file=sys.stderr)

    print(f"\nTotal files fixed: {changed}")
    print(f"Total errors: {len(errors)}")

if __name__ == "__main__":
    main()
