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

    # Match bare catch with only comment lines or empty body
    pattern = re.compile(
        r'(\s+)catch\n(\s+)\{(\s*\n(?:\s*//[^\n]*\n)*)(\s+)\}',
        re.MULTILINE
    )

    def replace_empty(m):
        indent1 = m.group(1)
        indent2 = m.group(2)
        body = m.group(3)
        indent3 = m.group(4)
        pos = m.start()
        cls, meth = get_context(original, pos)
        comment_match = re.search(r'//\s*(.+)', body)
        comment = f' // {comment_match.group(1).strip()}' if comment_match else ''
        return (f'{indent1}catch (Exception ex){comment}\n'
                f'{indent2}{{\n'
                f'{indent2}    Debug.WriteLine($"[{cls}.{meth}] {{ex.GetType().Name}}: {{ex.Message}}");\n'
                f'{indent3}}}')

    content = pattern.sub(replace_empty, content)

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
