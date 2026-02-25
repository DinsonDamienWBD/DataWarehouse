import re, glob

PLUGIN = 'Plugins/DataWarehouse.Plugins.UltimateIntelligence'
files = glob.glob(f'{PLUGIN}/**/*.cs', recursive=True)
modified = 0

for fpath in files:
    with open(fpath, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
    
    original_lines = lines[:]
    needs_using = not any('using System.Diagnostics;' in l for l in lines)
    
    i = 0
    while i < len(lines):
        stripped = lines[i].rstrip()
        if re.match(r'^\s+catch\s*$', stripped):
            # Found bare catch, get indent
            indent = len(stripped) - len(stripped.lstrip())
            indent_str = stripped[:indent]
            
            # Look for the opening brace on next line
            if i + 1 < len(lines) and '{' in lines[i + 1]:
                # Check if the body already has Debug.Write
                body_start = i + 2
                body_end = body_start
                brace_depth = 1
                has_debug = False
                has_throw = False
                
                for j in range(body_start, min(len(lines), body_start + 20)):
                    for ch in lines[j]:
                        if ch == '{': brace_depth += 1
                        elif ch == '}': brace_depth -= 1
                    if 'Debug.' in lines[j]: has_debug = True
                    if 'throw;' in lines[j] or 'throw ' in lines[j]: has_throw = True
                    if brace_depth == 0:
                        body_end = j
                        break
                
                if not has_debug and not has_throw:
                    # Get class and method context
                    before = ''.join(lines[:i])
                    cls_matches = re.findall(r'(?:class|struct|record)\s+(\w+)', before)
                    meth_matches = re.findall(r'(?:async\s+)?(?:\w+(?:<[^>]+>)?(?:\[\])?\s+)+(\w+)\s*\(', before)
                    cls = cls_matches[-1] if cls_matches else 'Unknown'
                    meth = meth_matches[-1] if meth_matches else 'Unknown'
                    
                    # Replace the catch line
                    body_indent = indent_str + '    '
                    lines[i] = f'{indent_str}catch (Exception ex)\n'
                    
                    # Insert Debug.WriteLine after the opening brace
                    debug_line = f'{body_indent}    Debug.WriteLine($"[{cls}.{meth}] {{ex.GetType().Name}}: {{ex.Message}}");\n'
                    lines.insert(body_start, debug_line)
        i += 1
    
    if lines != original_lines:
        if needs_using:
            # Add using at top
            for idx in range(len(lines)):
                if lines[idx].strip().startswith('using ') and lines[idx].strip().endswith(';\n'):
                    pass
                elif lines[idx].strip().startswith('using ') and lines[idx].strip().endswith(';'):
                    pass
                else:
                    continue
            # Find last using
            last_using = -1
            has_debug_using = False
            for idx in range(len(lines)):
                if 'using System.Diagnostics;' in lines[idx]:
                    has_debug_using = True
                if lines[idx].strip().startswith('using ') and lines[idx].strip().rstrip('\n').endswith(';'):
                    last_using = idx
            if not has_debug_using and last_using >= 0:
                lines.insert(last_using + 1, 'using System.Diagnostics;\n')
        
        with open(fpath, 'w', encoding='utf-8-sig') as f:
            f.writelines(lines)
        import os
        print(f'Fixed: {os.path.relpath(fpath, PLUGIN)}')
        modified += 1

print(f'\nTotal: {modified} files')
