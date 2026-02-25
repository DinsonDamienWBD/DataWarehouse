#!/usr/bin/env python3
"""
Fix LINQ chain .Count - restore () where Count is used after LINQ operator or on IEnumerable.
Pattern: .Where(...).Count -> .Where(...).Count()
Pattern: .Select(...).Count -> .Select(...).Count()
Pattern: .Distinct().Count -> .Distinct().Count()
Pattern: methodCall().Count -> methodCall().Count()  (where method returns IEnumerable)
Pattern: variable.Count where variable is a loop iteration var or parameter (not _field)
"""
import os
import re
import sys

SKIP_DIRS = {"bin", "obj", ".git", ".planning", ".omc", "DataWarehouse.Benchmarks"}
LINQ_METHODS = ['Where', 'Select', 'Distinct', 'OrderBy', 'OrderByDescending',
                'GroupBy', 'Take', 'Skip', 'Reverse', 'Concat', 'Union',
                'Intersect', 'Except', 'SelectMany', 'OfType', 'Cast',
                'GetActiveTopics', 'GetAllStrategies', 'Values', 'Keys']

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

    if '.Count' not in original:
        return False, None

    text = original

    # Fix: .MethodWithArgs(...).Count -> .MethodWithArgs(...).Count()
    # The issue: after a method call that returns IEnumerable, .Count needs ()
    # Pattern: )\s*.Count followed by non-( character
    # But NOT when preceded by _field (BoundedDictionary)

    # Fix 1: After closing paren of method call, .Count -> .Count()
    # ").Count" -> ").Count()"
    # But not ")?.Count"
    text = re.sub(r'\)\.Count\b(?!\s*\()', r').Count()', text)

    # Fix 2: Method().Count -> Method().Count()
    # Already covered by fix 1

    # Fix 3: variable.Count where variable is a parameter or loop var (no underscore prefix)
    # Pattern: Interlocked.Add(ref _field, param.Count) where param doesn't start with _
    # -> Interlocked.Add(ref _field, param.Count())
    text = re.sub(
        r'(Interlocked\.\w+\(ref [^,]+,\s*)([a-z][a-zA-Z0-9]*\.Count)(\s*\))',
        lambda m: m.group(1) + m.group(2) + '()' + m.group(3),
        text
    )

    # Fix 4: += someVar.Count; where someVar is not _ (IEnumerable param)
    text = re.sub(
        r'(\+=\s*)([a-z][a-zA-Z0-9]*\.Count)(\s*;)',
        lambda m: m.group(1) + m.group(2) + '()' + m.group(3),
        text
    )

    # Fix 5: = (long)someVar.Count -> = (long)someVar.Count()
    text = re.sub(
        r'(\((?:long|int|double|float)\))([a-z][a-zA-Z0-9]*\.Count)(\s*[;,)])',
        lambda m: m.group(1) + m.group(2) + '()' + m.group(3),
        text
    )

    # But restore: )).Count() -> )).Count if the receiver is BoundedDictionary (has property)
    # We can detect BoundedDictionary by checking if it's _field
    # Actually, after a closing ) it's always a method call return -> needs ()
    # So the pattern ").Count()" is correct

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
