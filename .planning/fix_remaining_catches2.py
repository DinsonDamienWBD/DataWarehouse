import re, os, glob

PLUGIN = 'Plugins/DataWarehouse.Plugins.UltimateIntelligence'
files = glob.glob(f'{PLUGIN}/**/*.cs', recursive=True)
modified = 0

for fpath in files:
    with open(fpath, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
    
    changed = False
    needs_using = not any('using System.Diagnostics;' in l for l in lines)
    
    i = 0
    while i < len(lines):
        line = lines[i]
        if re.match(r'^(\s+)catch\s*\n$', line):
            indent_match = re.match(r'^(\s+)', line)
            indent_str = indent_match.group(1)
            
            # Check next line is opening brace
            if i + 1 < len(lines) and lines[i + 1].strip().startswith('{'):
                # Find the body and closing brace
                body_start = i + 2
                brace_depth = 1
                body_end = body_start
                has_debug = False
                has_throw = False
                
                for j in range(body_start, min(len(lines), body_start + 30)):
                    for ch in lines[j]:
                        if ch == '{': brace_depth += 1
                        elif ch == '}': brace_depth -= 1
                    if 'Debug.' in lines[j]: has_debug = True
                    if 'throw;' in lines[j]: has_throw = True
                    if brace_depth == 0:
                        body_end = j
                        break
                
                if not has_debug and not has_throw:
                    before = ''.join(lines[:i])
                    cls_matches = re.findall(r'(?:class|struct|record)\s+(\w+)', before)
                    meth_matches = re.findall(r'(?:async\s+)?(?:\w+(?:<[^>]+>)?(?:\[\])?\s+)+(\w+)\s*\(', before)
                    cls = cls_matches[-1] if cls_matches else 'Unknown'
                    meth = meth_matches[-1] if meth_matches else 'Unknown'
                    
                    # Get body indent (one level deeper than catch)
                    body_indent = indent_str + '    '
                    
                    # Replace catch line with catch (Exception ex)
                    lines[i] = f'{indent_str}catch (Exception ex)\n'
                    
                    # Insert Debug.WriteLine after opening brace
                    debug_line = f'{body_indent}Debug.WriteLine($"[{cls}.{meth}] {{ex.GetType().Name}}: {{ex.Message}}");\n'
                    lines.insert(body_start, debug_line)
                    
                    changed = True
        i += 1
    
    if changed:
        if needs_using:
            last_using = -1
            has_debug_using = False
            for idx in range(len(lines)):
                stripped = lines[idx].strip()
                if stripped == 'using System.Diagnostics;':
                    has_debug_using = True
                if stripped.startswith('using ') and stripped.endswith(';'):
                    last_using = idx
            if not has_debug_using and last_using >= 0:
                lines.insert(last_using + 1, 'using System.Diagnostics;\n')
        
        with open(fpath, 'w', encoding='utf-8-sig') as f:
            f.writelines(lines)
        print(f'Fixed: {os.path.relpath(fpath, PLUGIN)}')
        modified += 1

print(f'\nTotal: {modified} files')
