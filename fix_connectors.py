import os
import re
import glob

plugin_path = r"C:\Temp\DataWarehouse\DataWarehouse\Plugins\DataWarehouse.Plugins.DataConnectors"
files = glob.glob(os.path.join(plugin_path, "*ConnectorPlugin.cs"))

for file_path in files:
    filename = os.path.basename(file_path)
    print(f"Processing {filename}...")

    with open(file_path, 'r', encoding='utf-8') as f:
        content = f.read()

    original = content

    # 1. Fix ConnectionResult missing ServerInfo parameter
    content = re.sub(
        r'return new ConnectionResult\(false, ([^)]+)\);',
        r'return new ConnectionResult(false, \1, null);',
        content
    )

    # 2. Fix ConnectionState ambiguity
    content = re.sub(
        r'(?<!System\.Data\.)(?<!\.)(ConnectionState\.Open\b)',
        r'System.Data.ConnectionState.Open',
        content
    )
    content = re.sub(
        r'(?<!System\.Data\.)(?<!\.)(ConnectionState\.Closed\b)',
        r'System.Data.ConnectionState.Closed',
        content
    )

    # 3. Fix WriteMode ambiguity
    content = re.sub(
        r'(\s+case\s+)WriteMode\.',
        r'\1SDK.Connectors.WriteMode.',
        content
    )
    content = re.sub(
        r'(\s+==\s+)WriteMode\.',
        r'\1SDK.Connectors.WriteMode.',
        content
    )
    content = re.sub(
        r'(\s+!=\s+)WriteMode\.',
        r'\1SDK.Connectors.WriteMode.',
        content
    )

    # 4. Fix Cassandra ConsistencyLevel ambiguity (Cassandra file only)
    if 'Cassandra' in filename:
        content = re.sub(
            r'(?<!Cassandra\.)(?<!\.)(ConsistencyLevel\.)',
            r'Cassandra.ConsistencyLevel.',
            content
        )

    # 5. Fix DataSchema - fields.ToArray()
    content = re.sub(
        r'(new DataSchema\([^,]+,\s*)fields,',
        r'\1fields.ToArray(),',
        content
    )

    # 6. Fix DataSchema - primaryKeys.ToArray()
    content = re.sub(
        r'(new DataSchema\([^,]+,\s*[^,]+,\s*)primaryKeys([,\)])',
        r'\1primaryKeys.ToArray()\2',
        content
    )

    # 7. Fix WriteResult signature - success case
    content = re.sub(
        r'new WriteResult\(true, (\w+), "[^"]+"\)',
        r'new WriteResult(\1, 0, null)',
        content
    )

    # 8. Fix WriteResult signature - failure case
    content = re.sub(
        r'new WriteResult\(false, (\d+), "([^"]+)"\)',
        r'new WriteResult(0, \1, new[] { "\2" })',
        content
    )

    # 9. Fix query.Source to query.TableOrCollection
    content = re.sub(
        r'\bquery\.Source\b',
        r'(query.TableOrCollection ?? "data")',
        content
    )

    if content != original:
        with open(file_path, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"  Modified!")
    else:
        print(f"  No changes")

print("\nAll files processed!")
