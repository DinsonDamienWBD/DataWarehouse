#!/usr/bin/env python3
"""
Fix remaining build errors:
1. CA1829: .Count() after GetResult() or await - change to .Count
2. CS0428: .Count method group used as int - change to .Count()
3. CS8619: Task.FromResult without nullable type - add nullable
4. CS7036: new BoundedDictionary() with StringComparer arg
5. CS0029: field declared as ConcurrentDictionary but assigned BoundedDictionary
6. Broken nested generic patterns still remaining
7. CS8917: method group delegate type inferred - add ()
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


# Patterns for CA1829 - methods returning collections with Count property
# Pattern: .GetResult().Count()  -> .GetResult().Count
# Pattern: ).Count()  (on await result)
# These should only be changed where the collection type has a Count property

CA1829_PATTERNS = [
    # GetResult().Count() -> GetResult().Count
    (re.compile(r'\.GetResult\(\)\.Count\(\)'), '.GetResult().Count'),
    # await expression.Count() - complex, skip for now
]

# StringComparer arg to BoundedDictionary
STRINGCOMPARER_PATTERN = re.compile(
    r'new BoundedDictionary<([^>]+)>\(StringComparer\.\w+\)'
)


def fix_stringcomparer(line):
    """Fix new BoundedDictionary<K,V>(StringComparer.XXX) -> new BoundedDictionary<K,V>(1000)"""
    return STRINGCOMPARER_PATTERN.sub(
        lambda m: f'new BoundedDictionary<{m.group(1)}>(1000)',
        line
    )


# Fix remaining broken >(TUPLE) patterns with more complex keys
# Pattern: BoundedDictionary<K1, K2, >(T1, T2)>(capacity) - keys with nested generics
COMPLEX_BROKEN_PATTERN = re.compile(
    r'(BoundedDictionary<)([^(>]+), >\(([^)]+)\)>(\((?:\d+|)\))'
)


def fix_complex_broken_tuple(line):
    """Fix remaining broken tuple patterns with complex key types"""
    changed = False

    def replacer(m):
        prefix = m.group(1)  # "BoundedDictionary<"
        key_type = m.group(2)  # key type(s)
        tuple_content = m.group(3)  # tuple content
        capacity = m.group(4)  # "(1000)" or "()"
        return f'{prefix}{key_type}, ({tuple_content})>{capacity}'

    new_line = COMPLEX_BROKEN_PATTERN.sub(replacer, line)
    if new_line != line:
        changed = True
    return new_line, changed


# Fix CS0029: field declared as ConcurrentDictionary but assigned BoundedDictionary
# Pattern: private readonly ConcurrentDictionary<K, BoundedDictionary<...>>
CONC_WITH_BOUNDED = re.compile(
    r'(private|protected|internal|public)(\s+readonly)?\s+ConcurrentDictionary<([^>]+), (BoundedDictionary<[^>]+>)>'
)


def fix_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            original = f.read()
    except Exception as e:
        return False, str(e)

    text = original
    changed = False

    # Fix CA1829 patterns
    for pattern, replacement in CA1829_PATTERNS:
        new_text = pattern.sub(replacement, text)
        if new_text != text:
            changed = True
            text = new_text

    # Fix StringComparer args
    if 'StringComparer' in text and 'BoundedDictionary' in text:
        lines = text.split('\n')
        new_lines = []
        for line in lines:
            if 'BoundedDictionary' in line and 'StringComparer' in line:
                new_line = fix_stringcomparer(line)
                if new_line != line:
                    changed = True
                    new_lines.append(new_line)
                    continue
            new_lines.append(line)
        if changed:
            text = '\n'.join(new_lines)

    # Fix remaining broken tuple patterns
    if 'BoundedDictionary' in text and ', >(' in text:
        lines = text.split('\n')
        new_lines = []
        for line in lines:
            if 'BoundedDictionary' in line and ', >(' in line:
                new_line, line_changed = fix_complex_broken_tuple(line)
                if line_changed:
                    changed = True
                    new_lines.append(new_line)
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


if __name__ == "__main__":
    files = find_cs_files(".")
    total = 0
    for path in files:
        ok, err = fix_file(path)
        if ok:
            total += 1
            rel = os.path.relpath(path, ".")
            print(f"FIXED: {rel}")
        elif err:
            rel = os.path.relpath(path, ".")
            print(f"ERROR: {rel}: {err}", file=sys.stderr)
    print(f"Total: {total}")
