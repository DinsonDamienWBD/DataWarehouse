#!/usr/bin/env python3
"""Fix critical API compatibility errors."""
import re
from pathlib import Path

def fix_critical_errors(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    original = content

    # Fix Cassandra ControlConnection - remove entire line
    if filepath.name == 'CassandraConnectorPlugin.cs':
        # Remove the ControlConnection line completely
        content = re.sub(
            r'\s*\["ControlConnection"\][^\n]*\n',
            '',
            content
        )
        # Fix foreach over row - need to cast column properly
        content = content.replace(
            'foreach (var column in row)',
            'foreach (var kvp in (System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object>>)row)'
        )
        # Simplify to just get column names from result.Columns
        content = re.sub(
            r'foreach \(var kvp in \(System\.Collections\.Generic\.IEnumerable<System\.Collections\.Generic\.KeyValuePair<string, object>>\)row\)\s*\{\s*var colName = kvp\.Key;',
            'foreach (var column in result.Columns) { var colName = column.Name;',
            content
        )
        content = re.sub(
            r'var value = row\[kvp\.Key\];',
            'var value = row[colName];',
            content
        )

    # Fix BigQuery insertResult.Errors issue - it's a property, not a method
    if filepath.name == 'BigQueryConnectorPlugin.cs':
        # Fix the nullable Errors access
        content = re.sub(
            r'if \(\(insertResult\.Errors\?\.Count \?\? 0\) > 0\)',
            'if (insertResult.Errors != null && insertResult.Errors.Count > 0)',
            content
        )
        # Fix the error.Select - error is already the error object, not a container
        content = content.replace(
            'string.Join(", ", error.Select(e => e.Message))',
            'string.Join(", ", error.Errors.Select(e => e.Message))'
        )

    # Fix Zendesk WriteResult field names from result object
    if filepath.name == 'ZendeskConnectorPlugin.cs':
        # The result variable name in the problematic lines
        content = content.replace('result.SuccessCount', 'result.RecordsWritten')
        content = content.replace('result.FailureCount', 'result.RecordsFailed')

    # Fix Databricks IAsyncEnumerable issue
    if filepath.name == 'DatabricksConnectorPlugin.cs':
        # Can't await IAsyncEnumerable directly, need to enumerate
        content = content.replace(
            'var records = await dataRecords;',
            '// Cannot await IAsyncEnumerable directly'
        )
        # Instead, process inline
        content = re.sub(
            r'var records = await dataRecords;\s*if \(records != null\)',
            'if (dataRecords != null)',
            content
        )

    # Fix null reference warnings with proper null coalescing
    content = re.sub(
        r'(_\w+)\s*=\s*props\.GetValueOrDefault\("(\w+)",\s*null\)\s*\?\?\s*"";',
        r'\1 = props.GetValueOrDefault("\2", "") ?? "";',
        content
    )

    # Fix HubSpot, Jira null assignments
    if filepath.name in ['HubSpotConnectorPlugin.cs', 'JiraConnectorPlugin.cs']:
        content = re.sub(
            r'(Access(?:Token|_?RefreshToken))\s*=\s*([^;]+);',
            r'\1 = \2 ?? "";',
            content,
            flags=re.MULTILINE
        )

    if content != original:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        return True
    return False

# Process files
connector_dir = Path('Plugins/DataWarehouse.Plugins.DataConnectors')
fixed_files = []

for cs_file in connector_dir.glob('*.cs'):
    if fix_critical_errors(cs_file):
        fixed_files.append(cs_file.name)
        print(f'Fixed: {cs_file.name}')

print(f'\nTotal: {len(fixed_files)} files fixed')
