#!/usr/bin/env python3
"""
Fix all remaining build errors using precise line/column targeting.
Handles:
  - CA1829: .Count() -> .Count (on BoundedDictionary/List/Array)
  - CS0428/CS0019/CS0030: .Count (method group) -> .Count()
  - CS1061: .Length -> .Count() on IEnumerable (from BoundedDictionary migration)
  - CS8619: Task.FromResult -> Task.FromResult<T?>
  - CS1729: new BoundedDictionary<K,V>() -> new BoundedDictionary<K,V>(1000)
  - CS1503/CS8917: method group .Count -> .Count()
"""

import re
import subprocess
import sys
import os

SOLUTION_ROOT = r"C:\Temp\DataWarehouse\DataWarehouse"

def run_build():
    result = subprocess.run(
        ["dotnet", "build", "--no-restore"],
        capture_output=True, text=True, cwd=SOLUTION_ROOT
    )
    return result.stdout + result.stderr

def parse_errors(build_output):
    """Parse build errors: returns list of (filepath, line, col, code, message)"""
    errors = []
    pattern = re.compile(
        r"(.+?)\((\d+),(\d+)\): error (CS\d+|CA\d+): (.+?) \["
    )
    for line in build_output.splitlines():
        m = pattern.search(line)
        if m:
            fpath = m.group(1).strip()
            lineno = int(m.group(2))
            col = int(m.group(3))
            code = m.group(4)
            msg = m.group(5)
            errors.append((fpath, lineno, col, code, msg))
    return errors

def fix_ca1829(filepath, lineno, col):
    """CA1829: Replace .Count() with .Count at given line/col"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()

        if lineno > len(lines):
            return False

        line = lines[lineno - 1]
        # col is 1-based
        idx = col - 1

        # Find .Count() at or near col
        # Search backward from col to find .Count()
        pos = line.find('.Count()', max(0, idx - 20))
        if pos == -1:
            # Try finding .Count() anywhere in line context
            pos = line.rfind('.Count()', 0, idx + 10)

        if pos == -1:
            return False

        lines[lineno - 1] = line[:pos] + '.Count' + line[pos + 8:]
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(lines)
        return True
    except Exception as e:
        print(f"  ERROR fixing CA1829 at {filepath}:{lineno}: {e}")
        return False

def fix_count_method_group(filepath, lineno, col):
    """CS0428/CS0019/CS0030: Add () to .Count method group"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()

        if lineno > len(lines):
            return False

        line = lines[lineno - 1]
        idx = col - 1

        # Find .Count at or near col that is NOT followed by (
        # Search from col - 20 to col + 20
        search_start = max(0, idx - 20)
        search_end = min(len(line), idx + 20)

        # Find .Count not followed by ( or property char
        best_pos = -1
        pos = search_start
        while True:
            found = line.find('.Count', pos, search_end + 6)
            if found == -1:
                break
            after = found + 6
            if after < len(line) and line[after] not in '(':
                best_pos = found
                break
            pos = found + 1

        if best_pos == -1:
            # Try broader search
            pos = 0
            while True:
                found = line.find('.Count', pos)
                if found == -1:
                    break
                after = found + 6
                if after >= len(line) or line[after] not in '(':
                    if abs(found - idx) < abs(best_pos - idx) if best_pos != -1 else True:
                        best_pos = found
                pos = found + 1

        if best_pos == -1:
            return False

        after = best_pos + 6
        lines[lineno - 1] = line[:after] + '()' + line[after:]
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(lines)
        return True
    except Exception as e:
        print(f"  ERROR fixing count method group at {filepath}:{lineno}: {e}")
        return False

def fix_length_to_count(filepath, lineno, col):
    """CS1061: .Length -> .Count() on IEnumerable (BoundedDictionary is IEnumerable)"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()

        if lineno > len(lines):
            return False

        line = lines[lineno - 1]
        idx = col - 1

        pos = line.find('.Length', max(0, idx - 20))
        if pos == -1:
            pos = line.rfind('.Length', 0, idx + 10)

        if pos == -1:
            return False

        lines[lineno - 1] = line[:pos] + '.Count()' + line[pos + 7:]
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(lines)
        return True
    except Exception as e:
        print(f"  ERROR fixing .Length at {filepath}:{lineno}: {e}")
        return False

def fix_cs8619_task_from_result(filepath, lineno, col, msg):
    """CS8619: Add generic type arg to Task.FromResult"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()

        if lineno > len(lines):
            return False

        line = lines[lineno - 1]

        # Extract the target type from error message
        # "Nullability of reference types in value of type 'Task<X>' doesn't match target type 'Task<X?>'"
        m = re.search(r"target type 'Task<(.+?)>'", msg)
        if not m:
            return False

        target_type = m.group(1)

        # Replace Task.FromResult( with Task.FromResult<TargetType>(
        new_line = re.sub(
            r'Task\.FromResult\(',
            f'Task.FromResult<{target_type}>(',
            line,
            count=1
        )

        if new_line == line:
            return False

        lines[lineno - 1] = new_line
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(lines)
        return True
    except Exception as e:
        print(f"  ERROR fixing CS8619 at {filepath}:{lineno}: {e}")
        return False

def fix_cs1729_no_arg_constructor(filepath, lineno, col):
    """CS1729: BoundedDictionary<K,V>() -> BoundedDictionary<K,V>(1000)"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()

        if lineno > len(lines):
            return False

        line = lines[lineno - 1]
        idx = col - 1

        # Find BoundedDictionary<...>() pattern near col - no arg constructor
        # Replace with BoundedDictionary<...>(1000)
        new_line = re.sub(
            r'(new\s+BoundedDictionary<[^>]+(?:>[^>]*>)?)\(\)',
            r'\1(1000)',
            line
        )
        # Also handle target-typed new(): new()
        if new_line == line:
            new_line = re.sub(r'\bnew\(\)', 'new(1000)', line)

        if new_line == line:
            return False

        lines[lineno - 1] = new_line
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(lines)
        return True
    except Exception as e:
        print(f"  ERROR fixing CS1729 at {filepath}:{lineno}: {e}")
        return False

def fix_cs8602_null_deref(filepath, lineno, col):
    """CS8602: Add null-conditional operator or null-forgiving operator"""
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()

        if lineno > len(lines):
            return False

        line = lines[lineno - 1]
        idx = col - 1

        # Simple heuristic: if there's a .method call, add ! before it
        # or change .X to ?.X
        # Find what's at idx
        if idx < len(line):
            char = line[idx]
            if char == '.':
                # Change . to ?.
                lines[lineno - 1] = line[:idx] + '?' + line[idx:]
                with open(filepath, 'w', encoding='utf-8') as f:
                    f.writelines(lines)
                return True
        return False
    except Exception as e:
        print(f"  ERROR fixing CS8602 at {filepath}:{lineno}: {e}")
        return False

def process_errors(errors):
    fixed = 0
    skipped = 0

    # Group by file to avoid redundant reads
    from collections import defaultdict
    by_file = defaultdict(list)
    for err in errors:
        by_file[err[0]].append(err)

    for filepath, file_errors in by_file.items():
        if not os.path.exists(filepath):
            print(f"File not found: {filepath}")
            continue

        print(f"\nProcessing: {os.path.basename(filepath)} ({len(file_errors)} errors)")

        # Process errors in reverse line order to avoid offset issues
        file_errors_sorted = sorted(file_errors, key=lambda e: e[1], reverse=True)

        for (fp, lineno, col, code, msg) in file_errors_sorted:
            print(f"  [{code}] line {lineno}, col {col}: {msg[:80]}")

            result = False
            if code == 'CA1829':
                result = fix_ca1829(fp, lineno, col)
            elif code in ('CS0428', 'CS0019', 'CS0030', 'CS8917'):
                if 'Count' in msg:
                    result = fix_count_method_group(fp, lineno, col)
                elif 'method group' in msg.lower():
                    result = fix_count_method_group(fp, lineno, col)
            elif code == 'CS1503':
                if 'method group' in msg.lower() or 'Count' in msg:
                    result = fix_count_method_group(fp, lineno, col)
            elif code == 'CS1061' and 'Length' in msg:
                result = fix_length_to_count(fp, lineno, col)
            elif code == 'CS8619':
                result = fix_cs8619_task_from_result(fp, lineno, col, msg)
            elif code == 'CS1729':
                result = fix_cs1729_no_arg_constructor(fp, lineno, col)
            elif code == 'CS8602':
                result = fix_cs8602_null_deref(fp, lineno, col)
            elif code == 'CS1662':
                # lambda delegate type mismatch - likely Count method group in lambda
                result = fix_count_method_group(fp, lineno, col)

            if result:
                print(f"    -> FIXED")
                fixed += 1
            else:
                print(f"    -> SKIPPED (no automatic fix)")
                skipped += 1

    return fixed, skipped

def main():
    print("Running build to collect errors...")
    build_output = run_build()

    errors = parse_errors(build_output)
    unique_errors = list({(e[0], e[1], e[3]): e for e in errors}.values())

    print(f"\nFound {len(unique_errors)} unique errors")

    if not unique_errors:
        print("No errors found!")
        return

    fixed, skipped = process_errors(unique_errors)

    print(f"\n=== SUMMARY ===")
    print(f"Fixed: {fixed}")
    print(f"Skipped: {skipped}")
    print(f"Total: {len(unique_errors)}")

if __name__ == '__main__':
    main()
