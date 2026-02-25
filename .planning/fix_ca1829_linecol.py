#!/usr/bin/env python3
"""
Fix CA1829 errors: .Count() -> .Count on lines/columns specified by build output.
Reads the build output from stdin or from a file, parses the CA1829 errors,
then replaces .Count() with .Count at the specified positions.

Also handles:
- .Count() at end of line -> .Count
- .Count() followed by .toString/comparisons/etc -> .Count

Strategy: for each CA1829 line, on that exact line number, replace the first
.Count() that appears at or near the specified column with .Count.
"""
import os
import re
import sys
import subprocess

SKIP_DIRS = {"bin", "obj", ".git", ".planning", ".omc", "DataWarehouse.Benchmarks"}

# Build error parsing
ERROR_PATTERN = re.compile(
    r'([A-Z]:[^:]+\.cs)\((\d+),(\d+)\): error CA1829:'
)


def get_ca1829_errors():
    """Run dotnet build and parse CA1829 errors."""
    result = subprocess.run(
        ['dotnet', 'build', 'DataWarehouse.slnx', '--no-incremental'],
        capture_output=True, text=True, cwd='.'
    )
    errors = []
    for line in result.stdout.split('\n') + result.stderr.split('\n'):
        m = ERROR_PATTERN.search(line)
        if m:
            filepath = m.group(1)
            lineno = int(m.group(2))
            col = int(m.group(3))
            errors.append((filepath, lineno, col))
    return errors


def fix_count_on_line(line, col):
    """
    Replace .Count() with .Count at approximately the given column.
    The column points to the 'C' of Count().
    col is 1-based.
    """
    # Find .Count() patterns in the line
    # The col tells us roughly where the .Count() starts (the dot is before col)
    idx = col - 1  # Convert to 0-based

    # Search backward from col to find the dot
    # The col might point to 'C' in Count, so dot is at col-2
    dot_positions = []
    pos = 0
    while True:
        match = re.search(r'\.Count\(\)', line[pos:])
        if not match:
            break
        abs_pos = pos + match.start()
        dot_positions.append(abs_pos)
        pos = abs_pos + 1

    if not dot_positions:
        return line, False

    # Find the closest .Count() to the reported column
    # The column typically points to the 'C' of Count, which is at dot+1
    target = idx - 1  # dot position (1 before C)
    best = min(dot_positions, key=lambda p: abs(p - target))

    # Replace this specific .Count() with .Count
    before = line[:best]
    after = line[best:]
    new_after = after.replace('.Count()', '.Count', 1)
    new_line = before + new_after

    return new_line, new_line != line


def process_errors(errors):
    """Group errors by file and fix each file."""
    by_file = {}
    for filepath, lineno, col in errors:
        if filepath not in by_file:
            by_file[filepath] = []
        by_file[filepath].append((lineno, col))

    fixed_count = 0
    for filepath, line_cols in by_file.items():
        try:
            with open(filepath, 'r', encoding='utf-8-sig', errors='replace') as f:
                lines = f.readlines()
        except Exception as e:
            print(f"ERROR reading {filepath}: {e}", file=sys.stderr)
            continue

        file_changed = False
        # Group multiple errors on same line
        by_line = {}
        for lineno, col in line_cols:
            if lineno not in by_line:
                by_line[lineno] = []
            by_line[lineno].append(col)

        for lineno, cols in by_line.items():
            if lineno > len(lines):
                continue
            line = lines[lineno - 1]
            # Apply fixes for each column (might be multiple on same line)
            for col in sorted(cols):
                new_line, changed = fix_count_on_line(line.rstrip('\n').rstrip('\r'), col)
                if changed:
                    lines[lineno - 1] = new_line + '\n'
                    line = lines[lineno - 1].rstrip('\n')
                    file_changed = True

        if file_changed:
            try:
                with open(filepath, 'w', encoding='utf-8') as f:
                    f.writelines(lines)
                rel = os.path.relpath(filepath, '.')
                print(f"FIXED: {rel} ({len(line_cols)} CA1829 errors)")
                fixed_count += 1
            except Exception as e:
                print(f"ERROR writing {filepath}: {e}", file=sys.stderr)

    return fixed_count


if __name__ == "__main__":
    print("Running build to get CA1829 errors...")
    errors = get_ca1829_errors()
    print(f"Found {len(errors)} CA1829 errors")

    if not errors:
        print("No CA1829 errors found.")
        sys.exit(0)

    fixed = process_errors(errors)
    print(f"Fixed {fixed} files")
