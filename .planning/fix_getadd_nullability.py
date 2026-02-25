#!/usr/bin/env python3
"""
Fix remaining build errors:
1. GetOrAdd(key, value) where value needs to be Func<K,V> - wrap in lambda
2. Task.FromResult(val) nullability issues
3. CS0428 Count method group
4. CA1829 Count() on collections with Count property
5. CS1526 + CS1003 + CS0030 - various expressions
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

    text = original

    # Fix 1: GetOrAdd(key, concreteValue) where BoundedDictionary.GetOrAdd expects Func<K,V>
    # Pattern from errors: .GetOrAdd(key, value) where value is 0, int, long, etc.
    # Our BoundedDictionary.GetOrAdd signature: GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    # ConcurrentDictionary also has: GetOrAdd(TKey key, TValue value)
    # FIX: add GetOrAdd(TKey key, TValue value) overload to BoundedDictionary
    # OR: fix the call sites to use _ => value lambda

    # Fix 2: Task.FromResult(val) where return type is Task<T?>
    # Already fixed for SDK. Now fix plugins.
    # Pattern: _dict.TryGetValue(key, out var val); return Task.FromResult(val);
    # Fix: return Task.FromResult<T?>(val);
    # We need to infer T from the surrounding method return type.

    # For Task.FromResult patterns, use a heuristic:
    # Find method return type Task<T?> and then find Task.FromResult(val) in that method
    # Replace with Task.FromResult<T?>(val)

    def fix_task_from_result(t):
        # Find patterns: public virtual Task<T?> Method(...) { ... return Task.FromResult(val); }
        # and fix return to Task.FromResult<T?>(val)
        result = t

        # Pattern: method with nullable return type Task<T?>
        # Match method signature + body
        method_pattern = re.compile(
            r'(Task<(\w+)\?>)\s+\w+[^{]*\{[^{}]*?return\s+Task\.FromResult\((\w+)\)\s*;[^{}]*?\}',
            re.DOTALL
        )

        def fix_method(m):
            full_match = m.group(0)
            nullable_type = m.group(2)
            val_name = m.group(3)
            # Replace Task.FromResult(val) with Task.FromResult<T?>(val)
            old = f'Task.FromResult({val_name})'
            new = f'Task.FromResult<{nullable_type}?>({val_name})'
            if old in full_match:
                return full_match.replace(old, new)
            return full_match

        result = method_pattern.sub(fix_method, result)
        return result

    text = fix_task_from_result(text)

    # Fix 3: CA1829 - .Count() on List/IList/ICollection (has Count property)
    # These are non-field local vars or return values
    # Pattern: expr.Count() where expr is a list/array (toList/array etc.)
    # Fix: .ToList().Count() -> .ToList().Count
    # But be careful not to break IGrouping.Count()

    # Fix .ToList().Count() -> .ToList().Count
    text = re.sub(r'\.ToList\(\)\.Count\(\)', '.ToList().Count', text)
    text = re.sub(r'\.ToArray\(\)\.Count\(\)', '.ToArray().Length', text)

    # Fix 4: += someVar.Count where count is method group (CS0428)
    # These are += on long variables where dict.Count is method group
    # Pattern: _field += someVar.Count; -> _field += someVar.Count;
    # If Count is a method group, need ()
    # But BoundedDictionary.Count is property.
    # If someVar starts with _ (field), it's BoundedDictionary (property) -> no ()
    # If someVar is a parameter/local, it might be IGrouping (method) -> need ()

    # This is too ambiguous - skip

    # Fix 5: var x = (something).Count; assignment to int variable where Count is method group
    # From error: CS0030 - cannot convert method to double
    # Pattern: someIEnumerable.Count used where double/int expected
    text = re.sub(r'(=\s*)([a-zA-Z_]\w+)\.Count(\s*[;,)])', lambda m:
        m.group(1) + m.group(2) + '.Count()' + m.group(3)
        if not m.group(2).startswith('_') else m.group(0), text)

    # Fix 6: specific patterns from errors
    # "cannot convert type 'method' to 'double'" - x.Count used as double
    # These are local variables accessing .Count on IEnumerable
    # Pattern: (double)x.Count -> (double)x.Count()

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
