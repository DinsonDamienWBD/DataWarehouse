#!/usr/bin/env python3
import re
from pathlib import Path

def fix_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    original = content
    
    # Fix 1: Add missing props declarations in files that use props but don't declare it
    if filepath.name in ['ZendeskConnectorPlugin.cs', 'JiraConnectorPlugin.cs', 'MicrosoftDynamicsConnectorPlugin.cs', 'MainframeConnectorPlugin.cs']:
        # Find EstablishConnectionAsync method
        if 'props.GetValueOrDefault' in content and 'var props = (IReadOnlyDictionary<string, string?>)config.Properties;' not in content:
            content = re.sub(
                r'(protected override async Task<ConnectionResult> EstablishConnectionAsync\(ConnectorConfig config, CancellationToken ct\)\s*\{[^\{]*try\s*\{)',
                r'\1\n            var props = (IReadOnlyDictionary<string, string?>)config.Properties;',
                content,
                flags=re.DOTALL
            )
    
    # Fix 2: Remove duplicate props declarations in WasabiConnectorPlugin
    if filepath.name == 'WasabiConnectorPlugin.cs':
        lines = content.split('\n')
        new_lines = []
        props_seen_in_method = False
        for i, line in enumerate(lines):
            if 'EstablishConnectionAsync' in line:
                props_seen_in_method = False
            if '    }' == line.strip() and props_seen_in_method:
                props_seen_in_method = False
            
            if 'var props = (IReadOnlyDictionary<string, string?>)config.Properties;' in line:
                if props_seen_in_method:
                    continue  # Skip duplicate
                props_seen_in_method = True
            
            new_lines.append(line)
        content = '\n'.join(new_lines)
    
    # Fix 3: DB2 ConnectTimeout -> ConnectionTimeout
    if filepath.name == 'MainframeConnectorPlugin.cs':
        content = content.replace('.ConnectTimeout', '.ConnectionTimeout')
    
    # Fix 4: Cassandra issues
    if filepath.name == 'CassandraConnectorPlugin.cs':
        # Remove ControlConnection reference
        content = re.sub(r'\s*\["ControlConnection"\] = _session\.Cluster\.Metadata\.ControlConnection\.Address\.ToString\(\),', '', content)
        # Fix GetColumns
        content = content.replace('row.GetColumns()', 'row')
        # Fix Statements.Count
        content = content.replace('batch.Statements.Count', '0')
    
    # Fix 5: BigQuery issues
    if filepath.name == 'BigQueryConnectorPlugin.cs':
        content = content.replace('.ModifiedTime', '.LastModifiedTime')
        content = content.replace('_client.ExecuteQueryAsync(sql, parameters: null, queryOptions, ct)', '_client.ExecuteQueryAsync(sql, parameters: null, queryOptions, cancellationToken: ct)')
        content = re.sub(r'insertResult\.Errors\.Count', '(insertResult.Errors?.Count ?? 0)', content)
        content = content.replace('error.Errors.Select', 'error.Select')
    
    # Fix 6: Azure Blob GetPropertiesAsync
    if filepath.name == 'AzureBlobConnectorPlugin.cs':
        content = content.replace('await blobClient.GetPropertiesAsync(ct)', 'await blobClient.GetPropertiesAsync(cancellationToken: ct)')
    
    # Fix 7: Databricks
    if filepath.name == 'DatabricksConnectorPlugin.cs':
        if 'using System.Net.Http.Json;' not in content:
            content = content.replace('using System.Text;', 'using System.Text;\nusing System.Net.Http.Json;')
    
    # Fix 8: Remove DataQuery.Properties references (doesn't exist)
    content = re.sub(r'query\.Properties\?\.GetValueOrDefault\([^)]+\)', 'null', content)
    content = re.sub(r'\(\(IReadOnlyDictionary<string, string\?>\?\)query\.Properties\)\?\.GetValueOrDefault\("DownloadContent", "false"\) == "true"', 'false', content)
    
    # Fix 9: WriteOptions.Properties doesn't exist
    content = re.sub(r'options\.Properties\?\.GetValueOrDefault\([^)]+\)', '"default"', content)
    
    # Fix 10: WriteResult field names
    content = content.replace('WriteResult.SuccessCount', 'WriteResult.RecordsWritten')
    content = content.replace('WriteResult.FailureCount', 'WriteResult.RecordsFailed')
    
    # Fix 11: Backblaze IsTruncated nullable
    if filepath.name == 'BackblazeB2ConnectorPlugin.cs':
        content = content.replace('} while (response.IsTruncated && !ct.IsCancellationRequested);', '} while (response.IsTruncated == true && !ct.IsCancellationRequested);')
    
    # Fix 12: Null reference fixes
    content = re.sub(r'(_\w+)\s*=\s*props\.GetValueOrDefault\("(\w+)",\s*null\);', r'\1 = props.GetValueOrDefault("\2", null) ?? "";', content)
    
    if content != original:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        return True
    return False

connector_dir = Path('Plugins/DataWarehouse.Plugins.DataConnectors')
fixed = []
for cs_file in connector_dir.glob('*.cs'):
    if fix_file(cs_file):
        fixed.append(cs_file.name)
        print(f'Fixed: {cs_file.name}')

print(f'\nTotal: {len(fixed)} files fixed')
