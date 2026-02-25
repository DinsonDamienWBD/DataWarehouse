#!/usr/bin/env python3
import re
from pathlib import Path

def fix_indentation(filepath):
    """Fix indentation issues where lines start with space + keyword without proper indent."""
    with open(filepath, 'r', encoding='utf-8') as f:
        lines = f.readlines()

    original = ''.join(lines)
    new_lines = []
    modified = False

    for i, line in enumerate(lines):
        # Check if line starts with a single space followed by a keyword (var, _field, etc.)
        # This indicates a line that should have been indented properly
        match = re.match(r'^ (var |_\w+|public |private |protected |internal )', line)
        if match:
            # Look at previous line to determine correct indentation
            if i > 0:
                prev_line = lines[i-1]
                # Get indentation of previous line
                prev_indent = len(prev_line) - len(prev_line.lstrip())
                # Use same indentation
                line = ' ' * prev_indent + line.lstrip()
                modified = True

        new_lines.append(line)

    if modified:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(new_lines)
        return True
    return False

# Process all .cs files in the connector plugin directory
connector_dir = Path('Plugins/DataWarehouse.Plugins.DataConnectors')
fixed = []

for cs_file in connector_dir.glob('*.cs'):
    if fix_indentation(cs_file):
        fixed.append(cs_file.name)
        print(f'Fixed indentation: {cs_file.name}')

print(f'\nTotal files fixed: {len(fixed)}')
