#!/usr/bin/env python3
"""
Fix BoundedDictionary property initializers using target-typed new().
Pattern: BoundedDictionary<K,V> PropName { get; ... } = new();
Or: BoundedDictionary<K,V> PropName { get; ... } = new(arg);
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

    # Handle both multi-line and single-line property/field inits with = new()
    # The issue: BoundedDictionary<K,V> name = new(); OR property initializer

    # First handle property initializers that span multiple lines:
    # public BoundedDictionary<K,V> Prop { get; } = new();
    # public BoundedDictionary<K,V> Prop { get; set; } = new();

    # We'll do a line-by-line scan and look for = new(); or = new(...); where
    # the same line or the previous lines declare a BoundedDictionary type

    # Strategy: find any = new(); or = new(...); that follows a BoundedDictionary type
    # on the same line or in adjacent context

    # Regex: on a single line: has BoundedDictionary<...> and ends/contains = new(...);
    # Already handled by previous script for "= new();" but not for property style

    # Property style: "} = new();"  where BoundedDictionary is on same or prev line
    # Let's handle multi-line case by scanning context

    lines = text.split('\n')
    changed = False
    new_lines = []

    # Track last seen BoundedDictionary type params for multi-line
    i = 0
    while i < len(lines):
        line = lines[i]

        # Check if this line contains a BoundedDictionary type declaration
        # but no new() initialization yet - could be a property
        if 'BoundedDictionary' in line:
            # Try to extract type params
            type_match = re.search(r'BoundedDictionary(<[^;{]+>)', line)
            if type_match:
                type_params = type_match.group(1)

                # Check if this same line has = new() or = new(...) pattern
                if '= new()' in line or re.search(r'= new\([^)]*\)', line):
                    # Already has new() on same line - check if it needs capacity
                    if '= new();' in line:
                        new_line = line.replace('= new();', f'= new BoundedDictionary{type_params}(1000);')
                        new_lines.append(new_line)
                        changed = True
                        i += 1
                        continue
                    elif re.search(r'= new\([^0-9)][^)]*\);', line):
                        # has non-integer arg
                        new_line = re.sub(r'= new\([^)]*\);', f'= new BoundedDictionary{type_params}(1000);', line)
                        new_lines.append(new_line)
                        changed = True
                        i += 1
                        continue

                # Check if next line has } = new() pattern (property initializer spanning lines)
                if i + 1 < len(lines):
                    next_line = lines[i + 1]
                    if re.search(r'^\s*\}\s*=\s*new\(\s*\)\s*;', next_line):
                        new_lines.append(line)
                        new_next = re.sub(r'=\s*new\(\s*\)', f'= new BoundedDictionary{type_params}(1000)', next_line)
                        new_lines.append(new_next)
                        changed = True
                        i += 2
                        continue
                    elif re.search(r'^\s*\}\s*=\s*new\([^)]*\)\s*;', next_line):
                        # next line has } = new(arg);
                        match = re.search(r'=\s*new\(([^)]*)\)', next_line)
                        if match:
                            args = match.group(1).strip()
                            if not re.match(r'^\d+', args):
                                new_lines.append(line)
                                new_next = re.sub(r'=\s*new\([^)]*\)', f'= new BoundedDictionary{type_params}(1000)', next_line)
                                new_lines.append(new_next)
                                changed = True
                                i += 2
                                continue

        new_lines.append(line)
        i += 1

    if changed:
        text = '\n'.join(new_lines)

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

    print(f"\nTotal fixed: {changed}")
    if errors:
        for rel, err in errors:
            print(f"ERROR: {rel}: {err}")

if __name__ == "__main__":
    main()
