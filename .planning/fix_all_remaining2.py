#!/usr/bin/env python3
"""
Fix ALL remaining build errors using build output to drive targeted fixes.
"""
import os
import re
import sys
import subprocess

def run_build():
    result = subprocess.run(
        ['dotnet', 'build', 'DataWarehouse.slnx'],
        capture_output=True, text=True, cwd='.',
        timeout=180
    )
    return result.stdout + result.stderr

ERROR_PATTERN = re.compile(
    r'([A-Z]:[^:]+\.cs)\((\d+),(\d+)\): error (CS\d+|CA\d+): (.+?)(?=\s*\[|\r|\n|$)'
)

def parse_errors(build_output):
    errors_by_file = {}
    for match in ERROR_PATTERN.finditer(build_output):
        filepath = match.group(1)
        lineno = int(match.group(2))
        col = int(match.group(3))
        ecode = match.group(4)
        msg = match.group(5)
        if filepath not in errors_by_file:
            errors_by_file[filepath] = []
        errors_by_file[filepath].append((lineno, col, ecode, msg))
    return errors_by_file


def fix_cs8619(line, col, msg):
    """Fix Task.FromResult(val) -> Task.FromResult<T?>(val)"""
    m = re.search(r"Task<(.+?)>'", msg)
    if not m:
        return line, False
    full_type = m.group(1).strip()
    nullable_type = full_type if full_type.endswith('?') else full_type + '?'
    new_line = re.sub(
        r'Task\.FromResult\(',
        f'Task.FromResult<{nullable_type}>(',
        line, count=1
    )
    return new_line, new_line != line


def fix_stringcomparer(line, col):
    """Fix new(StringComparer.X) -> new(1000) for BoundedDictionary"""
    new_line = re.sub(r'new\s*\(StringComparer\.\w+\)', 'new(1000)', line)
    return new_line, new_line != line


def fix_comparer_ctor(line, col):
    """Fix new BoundedDictionary<K,V>(SomeComparer.X) or new(SomeComparer) -> 1000"""
    new_line = re.sub(
        r'(new(?:\s+BoundedDictionary<[^>]+>)?)\s*\([A-Za-z]+Comparer[.\w]*\)',
        r'\1(1000)',
        line
    )
    return new_line, new_line != line


def fix_count_method_group(line, col):
    """Fix .Count method group used as int -> .Count()"""
    idx = col - 1
    count_re = re.compile(r'\.Count(?!\s*[\(\(])')
    matches = list(count_re.finditer(line))
    if not matches:
        return line, False
    best = min(matches, key=lambda m: abs(m.start() - (idx - 1)))
    pos = best.end()
    new_line = line[:pos] + '()' + line[pos:]
    return new_line, True


def fix_func_to_call(line, col):
    """Fix Func<T> assigned to T -> invoke it with ()"""
    idx = col - 1
    # Find method groups near col
    # Look for .Count without () and add ()
    count_re = re.compile(r'\.Count(?!\s*\()')
    matches = list(count_re.finditer(line))
    if matches:
        best = min(matches, key=lambda m: abs(m.start() - idx))
        pos = best.end()
        new_line = line[:pos] + '()' + line[pos:]
        return new_line, True
    return line, False


def fix_ca1829(line, col):
    """Fix .Count() -> .Count for CA1829"""
    idx = col - 1
    count_re = re.compile(r'\.Count\(\)')
    matches = list(count_re.finditer(line))
    if not matches:
        return line, False
    best = min(matches, key=lambda m: abs(m.start() - (idx - 1)))
    new_line = line[:best.start()] + '.Count' + line[best.end():]
    return new_line, True


def process_file(filepath, errors):
    try:
        with open(filepath, 'r', encoding='utf-8-sig', errors='replace') as f:
            lines = f.readlines()
    except Exception as e:
        print(f"ERROR reading {filepath}: {e}", file=sys.stderr)
        return False

    changed = False
    by_line = {}
    for lineno, col, ecode, msg in errors:
        if lineno not in by_line:
            by_line[lineno] = []
        by_line[lineno].append((col, ecode, msg))

    for lineno in sorted(by_line.keys()):
        if lineno > len(lines):
            continue
        line = lines[lineno - 1].rstrip('\n').rstrip('\r')

        for col, ecode, msg in sorted(by_line[lineno]):
            new_line = line
            line_changed = False

            if ecode == 'CS8619' and 'Task.FromResult' in line:
                new_line, line_changed = fix_cs8619(line, col, msg)
            elif ecode == 'CS1503' and 'StringComparer' in msg:
                new_line, line_changed = fix_stringcomparer(line, col)
            elif ecode == 'CS1503' and ('Comparer' in msg or 'Comparer' in line):
                new_line, line_changed = fix_comparer_ctor(line, col)
            elif ecode in ('CS0428', 'CS8917', 'CS0411') and 'Count' in (msg + line):
                new_line, line_changed = fix_count_method_group(line, col)
            elif ecode in ('CS0029', 'CS0019') and 'Func' in msg:
                new_line, line_changed = fix_func_to_call(line, col)
            elif ecode == 'CA1829':
                new_line, line_changed = fix_ca1829(line, col)
            elif ecode == 'CS1503' and 'method group' in msg:
                new_line, line_changed = fix_count_method_group(line, col)

            if line_changed:
                changed = True
                line = new_line

        lines[lineno - 1] = line + '\n'

    if changed:
        try:
            with open(filepath, 'w', encoding='utf-8') as f:
                f.writelines(lines)
            return True
        except Exception as e:
            print(f"ERROR writing {filepath}: {e}", file=sys.stderr)
    return False


if __name__ == "__main__":
    print("Running build...")
    build_output = run_build()
    errors = parse_errors(build_output)

    total_errors = sum(len(v) for v in errors.values())
    print(f"Found {total_errors} errors in {len(errors)} files")

    fixed = 0
    for filepath, file_errors in errors.items():
        if process_file(filepath, file_errors):
            rel = os.path.relpath(filepath, '.')
            print(f"FIXED: {rel} ({len(file_errors)} errors)")
            fixed += 1

    print(f"Fixed {fixed} files")
