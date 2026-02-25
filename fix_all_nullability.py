import re
import os
from pathlib import Path

def fix_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        lines = f.readlines()

    modified = False
    new_lines = []
    in_establish_connection = False
    props_added = False

    for i, line in enumerate(lines):
        # Detect if we're entering EstablishConnectionAsync method
        if 'EstablishConnectionAsync' in line and 'Task<ConnectionResult>' in line:
            in_establish_connection = True
            props_added = False

        # Detect if we're leaving the method (closing brace at column 4 or less)
        if in_establish_connection and re.match(r'^\s{0,4}\}', line):
            in_establish_connection = False

        # If we're in the method and see config.Properties.GetValueOrDefault
        if in_establish_connection and 'config.Properties.GetValueOrDefault' in line and not props_added:
            # Add props declaration before this line
            indent = len(line) - len(line.lstrip())
            new_lines.append(' ' * indent + 'var props = (IReadOnlyDictionary<string, string?>)config.Properties;\n')
            props_added = True
            modified = True

        # Replace config.Properties.GetValueOrDefault with props.GetValueOrDefault
        if in_establish_connection and 'config.Properties.GetValueOrDefault' in line:
            line = line.replace('config.Properties.GetValueOrDefault', 'props.GetValueOrDefault')
            modified = True

        # Fix query.Properties?.GetValueOrDefault
        if 'query.Properties?.GetValueOrDefault' in line:
            line = line.replace(
                'query.Properties?.GetValueOrDefault',
                '((IReadOnlyDictionary<string, string?>?)query.Properties)?.GetValueOrDefault'
            )
            modified = True

        # Fix SignatureVersion = "4"
        if 'SignatureVersion = "4"' in line:
            # Remove the entire line if it only contains SignatureVersion
            if line.strip() == 'SignatureVersion = "4"' or line.strip() == 'SignatureVersion = "4",':
                continue  # Skip this line
            else:
                line = re.sub(r',?\s*SignatureVersion\s*=\s*"4",?', '', line)
                modified = True

        # Fix BigQueryClient.Scope
        if 'BigQueryClient.Scope' in line:
            line = line.replace('BigQueryClient.Scope', 'new[] { "https://www.googleapis.com/auth/bigquery" }')
            modified = True

        # Fix dataset variable conflict in BigQuery
        if filepath.name == 'BigQueryConnectorPlugin.cs':
            if 'var dataset = await _client.GetDatasetAsync' in line:
                line = line.replace('var dataset =', 'var datasetRef =')
                modified = True
            if 'dataset.Resource' in line and 'datasetRef' not in line:
                line = line.replace('dataset.Resource', 'datasetRef.Resource')
                modified = True

        new_lines.append(line)

    if modified:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(new_lines)
        return True
    return False

# Process all .cs files in the connector plugin directory
connector_dir = Path('Plugins/DataWarehouse.Plugins.DataConnectors')
fixed_count = 0
fixed_files = []

for cs_file in connector_dir.glob('*.cs'):
    if fix_file(cs_file):
        fixed_files.append(cs_file.name)
        fixed_count += 1

print('Fixed files:')
for name in sorted(fixed_files):
    print(f'  - {name}')
print(f'\nTotal files fixed: {fixed_count}')
