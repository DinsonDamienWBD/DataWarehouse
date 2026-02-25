#!/usr/bin/env python3
"""
Fix target-typed new() for BoundedDictionary fields.
Pattern: BoundedDictionary<K,V> _field = new();
Fix to: BoundedDictionary<K,V> _field = new BoundedDictionary<K,V>(1000);
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

    # Pattern: BoundedDictionary<TypeParams> _fieldName = new();
    # We need to replace "= new();" with "= new BoundedDictionary<TypeParams>(1000);"
    # but only on lines that have BoundedDictionary type declared

    # Match field declarations ending with = new();
    # BoundedDictionary<K,V> name = new();
    # Also handle: private readonly BoundedDictionary<K,V> name = new();

    # Build a pattern that captures type params of BoundedDictionary on the same line
    # then replaces the = new(); part

    lines = text.split('\n')
    changed = False
    new_lines = []

    for line in lines:
        # Find lines with BoundedDictionary that end with = new();
        # Match: BoundedDictionary<type_params> fieldname = new();
        # The type_params can be complex with nested generics - use a simplified match
        # We'll match the type params portion by tracking the type on the same line

        if 'BoundedDictionary' in line and '= new();' in line:
            # Extract the type parameters from the BoundedDictionary<...> on this line
            m = re.search(r'BoundedDictionary(<[^;]+?>)\s+\w+\s*=\s*new\(\);', line)
            if m:
                type_params = m.group(1)
                # Replace = new(); with = new BoundedDictionary<type_params>(1000);
                new_line = line.replace('= new();', f'= new BoundedDictionary{type_params}(1000);')
                new_lines.append(new_line)
                changed = True
                continue

        # Also handle: = new(StringComparer.X); or = new(someOtherArg);
        if 'BoundedDictionary' in line and re.search(r'= new\([^)]+\);', line):
            m = re.search(r'BoundedDictionary(<[^;]+?>)\s+\w+\s*=\s*new\(([^)]*)\);', line)
            if m:
                type_params = m.group(1)
                args = m.group(2).strip()
                # If first arg is not an integer literal, replace with capacity
                if args and not re.match(r'^\d+', args):
                    new_line = re.sub(r'= new\([^)]+\);', f'= new BoundedDictionary{type_params}(1000);', line)
                    new_lines.append(new_line)
                    changed = True
                    continue

        new_lines.append(line)

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
            print(f"ERROR: {rel}: {err}", file=sys.stderr)

    print(f"\nTotal files fixed: {changed}")
    if errors:
        for rel, err in errors:
            print(f"  ERROR: {rel}: {err}")

if __name__ == "__main__":
    main()
