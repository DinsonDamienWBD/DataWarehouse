#!/usr/bin/env python3
"""
Fix missing '>' in BoundedDictionary type parameters.
Pattern: new BoundedDictionary<outer, Inner<T>(cap) -> new BoundedDictionary<outer, Inner<T>>(cap)
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

def count_angles(s):
    """Count unmatched opening angle brackets in string."""
    depth = 0
    for c in s:
        if c == '<':
            depth += 1
        elif c == '>':
            depth -= 1
    return depth

def fix_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except Exception as e:
        return False, str(e)

    if 'BoundedDictionary' not in original:
        return False, None

    text = original
    lines = text.split('\n')
    new_lines = []
    changed = False

    for line in lines:
        if 'new BoundedDictionary<' in line:
            # Find all "new BoundedDictionary<...>(..." patterns
            # Check if angle brackets are balanced
            def fix_new_expr(m):
                full = m.group(0)
                type_part = m.group(1)  # everything after "new BoundedDictionary" until "("
                args = m.group(2)        # args inside ()

                # Count angle brackets in type_part
                opens = type_part.count('<')
                closes = type_part.count('>')
                if opens > closes:
                    # Missing closing brackets
                    missing = opens - closes
                    # Add them before the (
                    return f'new BoundedDictionary{type_part}{">" * missing}({args})'
                return full

            new_line = re.sub(
                r'new BoundedDictionary(<[^(]+)\(([^)]*)\)',
                fix_new_expr,
                line
            )
            if new_line != line:
                changed = True
                new_lines.append(new_line)
                continue

        # Also fix field declarations with BoundedDictionary type
        # Pattern: BoundedDictionary<...<T>> or missing >
        if 'BoundedDictionary<' in line:
            # Check type declarations (not new expressions)
            def fix_decl(m):
                full = m.group(0)
                prefix = m.group(1)  # "BoundedDictionary"
                type_part = m.group(2)  # <type params
                suffix = m.group(3)  # rest of line

                # Count angle brackets
                opens = type_part.count('<')
                closes = type_part.count('>')
                if opens > closes:
                    missing = opens - closes
                    return f'{prefix}{type_part}{">" * missing}{suffix}'
                return full

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

if __name__ == "__main__":
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
    print(f"Total: {changed}")
