import re
import os
from pathlib import Path

def fix_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original = content
    modified = False

    # Fix 1: Remove SignatureVersion = "4" from AmazonS3Config
    if 'SignatureVersion = "4"' in content:
        content = re.sub(r',\s*SignatureVersion\s*=\s*"4"', '', content)
        modified = True

    # Fix 2: Replace BigQueryClient.Scope with direct scope array
    if 'BigQueryClient.Scope' in content:
        content = content.replace('BigQueryClient.Scope', 'new[] { "https://www.googleapis.com/auth/bigquery" }')
        modified = True

    # Fix 3: Rename conflicting 'dataset' variable to 'datasetRef'
    if 'var dataset = await _client.GetDatasetAsync' in content:
        # Find the specific block and rename
        content = re.sub(
            r'(var\s+)dataset(\s*=\s*await\s+_client\.GetDatasetAsync.*?\r?\n.*?dataset\.)',
            r'\1datasetRef\2',
            content,
            flags=re.DOTALL
        )
        content = content.replace('dataset.Resource', 'datasetRef.Resource')
        modified = True

    # Fix 4: Add props variable and replace config.Properties.GetValueOrDefault
    # Find EstablishConnectionAsync method
    method_pattern = r'(protected\s+override\s+async\s+Task<ConnectionResult>\s+EstablishConnectionAsync\([^{]*\{)'
    match = re.search(method_pattern, content)

    if match and 'config.Properties.GetValueOrDefault' in content:
        # Check if we already have props variable
        if 'var props = (IReadOnlyDictionary<string, string?>)config.Properties;' not in content:
            # Find first occurrence of config.Properties in the method
            method_start = match.end()
            # Look for the first try block or first config.Properties usage
            try_match = re.search(r'(\s+)(try\s*\{|\w+\s+\w+\s*=\s*config\.Properties)', content[method_start:])
            if try_match:
                insert_pos = method_start + try_match.start() + len(try_match.group(1))
                indent = try_match.group(1)

                # If it's a try block, insert after the opening brace
                if 'try' in try_match.group():
                    try_end = content.find('{', insert_pos) + 1
                    # Find the indentation of the next line
                    next_line_match = re.search(r'\n(\s+)', content[try_end:])
                    if next_line_match:
                        indent = next_line_match.group(1)
                        insert_pos = try_end + next_line_match.start() + 1

                # Insert the props declaration
                content = content[:insert_pos] + f'{indent}var props = (IReadOnlyDictionary<string, string?>)config.Properties;\n' + content[insert_pos:]
                modified = True

        # Replace all config.Properties.GetValueOrDefault with props.GetValueOrDefault in this method
        content = re.sub(r'config\.Properties\.GetValueOrDefault', 'props.GetValueOrDefault', content)
        modified = True

    # Fix 5: query.Properties?.GetValueOrDefault needs cast
    if 'query.Properties?.GetValueOrDefault' in content:
        content = re.sub(
            r'query\.Properties\?\.GetValueOrDefault',
            r'((IReadOnlyDictionary<string, string?>?)query.Properties)?.GetValueOrDefault',
            content
        )
        modified = True

    if modified:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        return True
    return False

# Process all .cs files in the connector plugin directory
connector_dir = Path('Plugins/DataWarehouse.Plugins.DataConnectors')
fixed_count = 0

for cs_file in connector_dir.glob('*.cs'):
    if fix_file(cs_file):
        print(f'Fixed: {cs_file.name}')
        fixed_count += 1

print(f'\nTotal files fixed: {fixed_count}')
