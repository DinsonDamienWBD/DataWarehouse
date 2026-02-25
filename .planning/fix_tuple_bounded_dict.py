#!/usr/bin/env python3
"""
Fix broken BoundedDictionary<KEY, >(TUPLE)>(CAPACITY) patterns.
These were caused by fix_missing_angle.py incorrectly treating '(' in tuple types as the
constructor argument opening bracket.

Broken pattern:  new BoundedDictionary<K, >(T1 N1, T2 N2)>(capacity)
Fixed pattern:   new BoundedDictionary<K, (T1 N1, T2 N2)>(capacity)

Also fixes field declarations like:
  new BoundedDictionary<K, >(T1 N1, T2 N2, T3 N3)>(capacity)
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


def fix_broken_tuple_pattern(line):
    """
    Fix: new BoundedDictionary<KEY_TYPE, >(T1 N1, T2 N2, ...)>(capacity)
    To:  new BoundedDictionary<KEY_TYPE, (T1 N1, T2 N2, ...)>(capacity)

    The pattern is: "BoundedDictionary<STUFF, >(" where STUFF doesn't contain '>'
    followed by tuple content and ">(" at the end.
    """
    # Pattern: BoundedDictionary<KEY, >(TUPLE_CONTENT)>(CAPACITY)
    # KEY = anything without '(' or ')'
    # TUPLE_CONTENT = anything (can contain commas, spaces, type names)
    # CAPACITY = number or empty

    # We need to match: ", >" followed by "(" then tuple content ")" then ">("
    # The ", >" is the broken part - it should be ", ("

    # Match the specific broken pattern where we have >, >( which indicates
    # the tuple was split from the closing >

    changed = False

    # Pattern: <KEYTYPE, >(TUPLE)>(CAPACITY)
    # The broken part is: ", >" before the tuple's "("
    # We want to match "new BoundedDictionary<KEYTYPE, >" where KEYTYPE doesn't contain ">"

    # More specific: match ", >(" where ">" was incorrectly inserted
    # The tuple type content is between the "(" and the matching ")"

    result = line

    # Find all occurrences of the broken pattern
    # Pattern: BoundedDictionary<([^<>()]+), >\(([^)]+)\)>\(([^)]*)\)
    # This matches:
    #   <SIMPLE_KEY, >(TUPLE_WITH_NO_NESTED_PARENS)>(CAPACITY)

    def fix_match(m):
        key_type = m.group(1)  # The key type (e.g., "string", "long")
        tuple_content = m.group(2)  # The tuple content (e.g., "byte[] Data, long Version")
        capacity = m.group(3)  # The capacity arg (e.g., "1000")
        return f'BoundedDictionary<{key_type}, ({tuple_content})>({capacity})'

    # Simple case: no nested generics in value type, no nested parens in tuple
    pattern = re.compile(r'BoundedDictionary<([^<>(),]+), >\(([^)]+)\)>\(([^)]*)\)')
    new_result = pattern.sub(fix_match, result)
    if new_result != result:
        changed = True
        result = new_result

    return result, changed


def fix_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except Exception as e:
        return False, str(e)

    if 'BoundedDictionary' not in original:
        return False, None

    lines = original.split('\n')
    new_lines = []
    changed_count = 0

    for line in lines:
        if 'BoundedDictionary' in line and ', >(' in line:
            new_line, changed = fix_broken_tuple_pattern(line)
            if changed:
                changed_count += 1
                new_lines.append(new_line)
                continue
        new_lines.append(line)

    if changed_count > 0:
        text = '\n'.join(new_lines)
        try:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(text)
            return True, f"{changed_count} lines fixed"
        except Exception as e:
            return False, str(e)

    return False, None


if __name__ == "__main__":
    files = find_cs_files(".")
    total_changed = 0
    for path in files:
        ok, msg = fix_file(path)
        if ok:
            total_changed += 1
            rel = os.path.relpath(path, ".")
            print(f"FIXED: {rel} ({msg})")
        elif msg:
            rel = os.path.relpath(path, ".")
            print(f"ERROR: {rel}: {msg}", file=sys.stderr)
    print(f"Total files changed: {total_changed}")
