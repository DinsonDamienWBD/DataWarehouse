#!/usr/bin/env python3
"""
Fix LINQ Count method group errors.
Pattern: , g => g.Count) or g => g.Count,
Should be: , g => g.Count()) or g => g.Count(),
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

    text = original

    # Fix lambda returning Count method group (single char variable names common in LINQ)
    # Pattern: => var.Count) or => var.Count, or => var.Count;
    # But NOT => var.Count() (already correct)
    # General pattern: any single-char or simple variable name in lambda returning .Count
    text = re.sub(
        r'(=>\s*\w+\.Count)(?!\s*[\(\(])',  # => x.Count not followed by (
        r'\1()',
        text
    )

    # Also fix longer variable names in lambdas: group.Count, item.Count, etc.
    # Pattern: g => g.Count not followed by ()
    # Already covered above

    # Fix: .ToDictionary(g => g.Key, g => g.Count) type patterns
    # These should already be covered

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

    for path in files:
        rel = os.path.relpath(path, ".")
        ok, err = fix_file(path)
        if ok:
            changed += 1
            print(f"FIXED: {rel}")
        elif err:
            print(f"ERROR: {rel}: {err}", file=sys.stderr)

    print(f"\nTotal fixed: {changed}")

if __name__ == "__main__":
    main()
