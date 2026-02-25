#!/usr/bin/env python3
"""
Fix cases where CA1829 fix script incorrectly changed .Count() to .Count
after LINQ method chains that return IEnumerable (not ICollection).

Pattern to restore:
  .Where(...).Count -> .Where(...).Count()
  .Select(...).Count -> .Select(...).Count()
  .Distinct(...).Count -> .Distinct(...).Count()
  .SelectMany(...).Count -> .SelectMany(...).Count()
  .GroupBy(...).Count -> .GroupBy(...).Count()
  .OrderBy(...).Count -> .OrderBy(...).Count() (rare but possible)
  .Skip(...).Count -> .Skip(...).Count()
  .Take(...).Count -> .Take(...).Count()
  .Concat(...).Count -> .Concat(...).Count()
  .Union(...).Count -> .Union(...).Count()
  .Intersect(...).Count -> .Intersect(...).Count()
  .Except(...).Count -> .Except(...).Count()

These LINQ methods return IEnumerable<T>, not IList/ICollection/IReadOnlyCollection.
So .Count property doesn't exist on them - needs .Count().

Also restore .Count() from single-line chained expressions:
  ).Count; -> ).Count() when the ) is closing a LINQ method

CS8917 pattern: delegate type cannot be inferred - occurs when .Count is used
as a method group reference instead of invoked.
"""
import os
import re
import sys

SKIP_DIRS = {"bin", "obj", ".git", ".planning", ".omc", "DataWarehouse.Benchmarks"}

LINQ_METHODS_RETURNING_ENUMERABLE = {
    'Where', 'Select', 'SelectMany', 'Distinct', 'GroupBy',
    'OrderBy', 'OrderByDescending', 'ThenBy', 'ThenByDescending',
    'Skip', 'Take', 'SkipWhile', 'TakeWhile', 'Concat', 'Union',
    'Intersect', 'Except', 'Reverse', 'OfType', 'Cast', 'AsEnumerable',
    'DefaultIfEmpty', 'Zip', 'Flatten', 'Append', 'Prepend',
    'GetValues',  # custom
}

def find_cs_files(base="."):
    result = []
    for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                result.append(os.path.join(root, f))
    return result


# Match patterns like:
#   .Where(...)\n.Count;
#   .Where(...).Count;
#   ).Count; (at end of chained LINQ)
#   .Distinct(StringComparer.OrdinalIgnoreCase)\n.Count;
#   ).Count\n  or ).Count;

# Single-line: ).Count; or ).Count\n where ) closes a LINQ-chain method
# Multi-line: we need to detect patterns where .Count is on a new line after )

def fix_file(path):
    try:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            text = f.read()
    except Exception as e:
        return False, str(e)

    if '.Count;' not in text and '.Count\n' not in text and '.Count,' not in text:
        return False, None

    changed = False
    lines = text.split('\n')
    new_lines = []

    for i, line in enumerate(lines):
        stripped = line.rstrip()

        # Check if this line ends with .Count; or .Count, or is just .Count (with whitespace)
        # and the previous non-empty line ends with ) from a LINQ method
        if stripped.endswith('.Count;') or stripped.endswith('.Count,') or stripped.rstrip() == stripped.rstrip()[:-len('.Count')] + '.Count':
            # Check the previous line ends with ) suggesting LINQ chain
            # Also check that this line or previous line has a LINQ method
            prev_context = ''
            for j in range(max(0, i-3), i):
                prev_context += lines[j]

            # Check if any LINQ method appears in recent context
            is_linq_chain = any(
                re.search(r'\.' + method + r'\s*\(', prev_context)
                for method in LINQ_METHODS_RETURNING_ENUMERABLE
            )

            if is_linq_chain:
                # Also check that this line ends with .Count; and not .SomeProperty.Count;
                # The .Count should be on something that's an IEnumerable result
                if stripped.endswith('.Count;'):
                    new_line = stripped[:-len('.Count;')] + '.Count();'
                    new_lines.append(new_line)
                    changed = True
                    continue
                elif stripped.endswith('.Count,'):
                    new_line = stripped[:-len('.Count,')] + '.Count(),'
                    new_lines.append(new_line)
                    changed = True
                    continue

        new_lines.append(line)

    if changed:
        try:
            with open(path, 'w', encoding='utf-8') as f:
                f.write('\n'.join(new_lines))
            return True, None
        except Exception as e:
            return False, str(e)

    return False, None


if __name__ == "__main__":
    # This approach is fragile. Better: run build again, get CS8917/CS0428 errors,
    # fix those specific lines.
    import subprocess

    print("Running build to find remaining Count-related errors...")
    result = subprocess.run(
        ['dotnet', 'build', 'DataWarehouse.slnx'],
        capture_output=True, text=True, cwd='.'
    )

    # CS8917 and CS0428 are the two error types for method group Count
    # CS0030: Cannot convert type 'method' to 'double/float/int'
    # CS0411: type arguments cannot be inferred
    error_pattern = re.compile(
        r'([A-Z]:[^:]+\.cs)\((\d+),(\d+)\): error (CS8917|CS0428|CS0030|CS0411|CS0019|CS0828):'
    )

    errors_by_file = {}
    for line in result.stdout.split('\n') + result.stderr.split('\n'):
        m = error_pattern.search(line)
        if m:
            filepath = m.group(1)
            lineno = int(m.group(2))
            col = int(m.group(3))
            ecode = m.group(4)
            if filepath not in errors_by_file:
                errors_by_file[filepath] = []
            errors_by_file[filepath].append((lineno, col, ecode))

    print(f"Found {sum(len(v) for v in errors_by_file.values())} method-group-count errors in {len(errors_by_file)} files")

    fixed = 0
    for filepath, errs in errors_by_file.items():
        try:
            with open(filepath, 'r', encoding='utf-8-sig', errors='replace') as f:
                lines = f.readlines()
        except Exception as e:
            print(f"ERROR reading {filepath}: {e}", file=sys.stderr)
            continue

        file_changed = False
        for lineno, col, ecode in errs:
            if lineno > len(lines):
                continue
            line = lines[lineno - 1].rstrip('\n').rstrip('\r')

            # For CS8917/CS0428/CS0030/CS0019/CS0828 at column col:
            # The issue is .Count used as method group (not invoked)
            # Fix: find .Count near the column and add ()

            idx = col - 1  # 0-based column

            # Find .Count without () near this column
            # Pattern: .Count that's NOT followed by (
            count_pattern = re.compile(r'\.Count(?!\s*\()')
            matches = list(count_pattern.finditer(line))

            if matches:
                # Find closest to col
                best = min(matches, key=lambda m: abs(m.start() - idx))
                # Insert () after Count
                pos = best.end()
                new_line = line[:pos] + '()' + line[pos:]
                lines[lineno - 1] = new_line + '\n'
                file_changed = True

        if file_changed:
            try:
                with open(filepath, 'w', encoding='utf-8') as f:
                    f.writelines(lines)
                rel = os.path.relpath(filepath, '.')
                print(f"FIXED: {rel}")
                fixed += 1
            except Exception as e:
                print(f"ERROR writing {filepath}: {e}", file=sys.stderr)

    print(f"Fixed {fixed} files")
