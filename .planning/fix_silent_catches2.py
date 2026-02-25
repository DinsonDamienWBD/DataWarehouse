import re, os, glob

PLUGIN = 'Plugins/DataWarehouse.Plugins.UltimateIntelligence'
files = glob.glob(f'{PLUGIN}/**/*.cs', recursive=True)
modified_files = []

for fpath in files:
    with open(fpath, 'r', encoding='utf-8-sig') as f:
        content = f.read()
    original = content
    needs_using = 'using System.Diagnostics;' not in content

    def get_context(text, pos):
        before = text[:pos]
        cls = re.findall(r'(?:class|struct|record)\s+(\w+)', before)
        meth = re.findall(r'(?:async\s+)?(?:\w+(?:<[^>]+>)?(?:\[\])?\s+)+(\w+)\s*\(', before)
        return (cls[-1] if cls else 'Unknown'), (meth[-1] if meth else 'Unknown')

    # Match bare catch with code body (NOT already caught by pattern 1)
    # This captures: catch\n  {\n    <code line(s)>\n  }
    # where there is no (Exception ...) after catch
    pattern = re.compile(
        r'(\s+)catch\n(\s+)\{\n((?:\s+[^\n]+\n)+?)(\s+)\}',
        re.MULTILINE
    )

    def replace_with_body(m):
        indent1 = m.group(1)
        indent2 = m.group(2)
        body = m.group(3)
        indent3 = m.group(4)
        
        # Skip if already has Debug or throw
        if 'Debug.' in body or 'throw;' in body or 'throw ' in body:
            return m.group(0)
        
        pos = m.start()
        cls, meth = get_context(original, pos)
        
        # Calculate body indent
        body_lines = body.rstrip('\n').split('\n')
        if body_lines:
            first_line = body_lines[0]
            body_indent = len(first_line) - len(first_line.lstrip())
            body_indent_str = ' ' * body_indent
        else:
            body_indent_str = indent2 + '    '
        
        debug_line = f'{body_indent_str}Debug.WriteLine($"[{cls}.{meth}] {{ex.GetType().Name}}: {{ex.Message}}");\n'
        
        return (f'{indent1}catch (Exception ex)\n'
                f'{indent2}{{\n'
                f'{debug_line}'
                f'{body}'
                f'{indent3}}}')

    content = pattern.sub(replace_with_body, content)

    if content != original:
        if needs_using:
            lines = content.split('\n')
            last_using = -1
            has_debug = False
            for i, line in enumerate(lines):
                if line.strip() == 'using System.Diagnostics;':
                    has_debug = True
                if line.strip().startswith('using ') and line.strip().endswith(';'):
                    last_using = i
            if not has_debug and last_using >= 0:
                lines.insert(last_using + 1, 'using System.Diagnostics;')
                content = '\n'.join(lines)
        with open(fpath, 'w', encoding='utf-8-sig') as f:
            f.write(content)
        modified_files.append(os.path.relpath(fpath, PLUGIN))
        print(f'Fixed: {os.path.relpath(fpath, PLUGIN)}')

print(f'\nTotal: {len(modified_files)} files')
