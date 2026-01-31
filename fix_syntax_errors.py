#!/usr/bin/env python3
import re
from pathlib import Path

def fix_mangled_file(filepath):
    """Fix files mangled by the PowerShell script."""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original = content

    # Pattern 1: _field = var props = (IReadOnlyDictionary<string, string?>)config.Properties;\n props.GetValueOrDefault(...)
    # Should be: _field = props.GetValueOrDefault(...)
    pattern1 = r'(\w+)\s*=\s*var props = \(IReadOnlyDictionary<string, string\?>\)config\.Properties;\s*\n\s*props\.GetValueOrDefault\('
    content = re.sub(pattern1, r'\1 = props.GetValueOrDefault(', content)

    # Pattern 2: var x = var props = (IReadOnlyDictionary<string, string?>)config.Properties;\n props.GetValueOrDefault(...)
    # Should be: var x = props.GetValueOrDefault(...)
    pattern2 = r'var\s+(\w+)\s*=\s*var props = \(IReadOnlyDictionary<string, string\?>\)config\.Properties;\s*\n\s*props\.GetValueOrDefault\('
    content = re.sub(pattern2, r'var \1 = props.GetValueOrDefault(', content)

    # Pattern 3: Remove duplicate "var props =" declarations (keep only the first one per method)
    # This is more complex - we need to find blocks and remove duplicates
    lines = content.split('\n')
    new_lines = []
    in_method = False
    props_seen = False

    for i, line in enumerate(lines):
        # Detect entering a method
        if 'EstablishConnectionAsync' in line or 'protected override async Task' in line:
            in_method = True
            props_seen = False

        # Detect leaving a method
        if in_method and re.match(r'^\s{0,4}\}', line):
            in_method = False
            props_seen = False

        # Check if this line declares props
        if in_method and 'var props = (IReadOnlyDictionary<string, string?>)config.Properties;' in line:
            if props_seen:
                # Skip duplicate declaration
                continue
            else:
                props_seen = True

        new_lines.append(line)

    content = '\n'.join(new_lines)

    if content != original:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        return True
    return False

# List of files that were broken by PowerShell script
broken_files = [
    'HubSpotConnectorPlugin.cs',
    'JiraConnectorPlugin.cs',
    'MainframeConnectorPlugin.cs',
    'MicrosoftDynamicsConnectorPlugin.cs',
    'SalesforceConnectorPlugin.cs',
    'ZendeskConnectorPlugin.cs',
    'RedisConnectorPlugin.cs',
]

connector_dir = Path('Plugins/DataWarehouse.Plugins.DataConnectors')
fixed = []

for filename in broken_files:
    filepath = connector_dir / filename
    if filepath.exists():
        if fix_mangled_file(filepath):
            fixed.append(filename)
            print(f'Fixed: {filename}')

print(f'\nTotal files fixed: {len(fixed)}')
