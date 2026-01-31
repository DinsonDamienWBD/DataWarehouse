# Comprehensive connector fix script - safe replacements only

$pluginPath = "C:\Temp\DataWarehouse\DataWarehouse\Plugins\DataWarehouse.Plugins.DataConnectors"
$files = Get-ChildItem -Path $pluginPath -Filter "*ConnectorPlugin.cs" -File

foreach ($file in $files) {
    Write-Host "Processing $($file.Name)..."

    $lines = Get-Content $file.FullName
    $modified = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $originalLine = $line

        # Fix ConnectionResult - add missing third parameter (ServerInfo)
        if ($line -match 'return new ConnectionResult\(false,\s*([^)]+)\);' -and $line -notmatch ', null\);') {
            $line = $line -replace '(return new ConnectionResult\(false,\s*[^)]+)\);', '$1, null);'
            Write-Host "  Line $($i+1): Fixed ConnectionResult"
        }

        # Fix ConnectionState.Open to System.Data.ConnectionState.Open
        if ($line -match '(?<!System\.Data\.)ConnectionState\.Open') {
            $line = $line -replace '(?<!System\.Data\.)ConnectionState\.Open', 'System.Data.ConnectionState.Open'
            Write-Host "  Line $($i+1): Fixed ConnectionState.Open"
        }

        if ($line -match '(?<!System\.Data\.)ConnectionState\.Closed') {
            $line = $line -replace '(?<!System\.Data\.)ConnectionState\.Closed', 'System.Data.ConnectionState.Closed'
            Write-Host "  Line $($i+1): Fixed ConnectionState.Closed"
        }

        # Fix WriteMode to SDK.Connectors.WriteMode
        if ($line -match '\bcase\s+WriteMode\.' -and $line -notmatch 'SDK\.Connectors\.WriteMode') {
            $line = $line -replace '\bcase\s+WriteMode\.', 'case SDK.Connectors.WriteMode.'
            Write-Host "  Line $($i+1): Fixed WriteMode in case"
        }

        if ($line -match '\b==\s+WriteMode\.' -and $line -notmatch 'SDK\.Connectors\.WriteMode') {
            $line = $line -replace '\b==\s+WriteMode\.', '== SDK.Connectors.WriteMode.'
            Write-Host "  Line $($i+1): Fixed WriteMode in comparison"
        }

        # Fix ConsistencyLevel to Cassandra.ConsistencyLevel (Cassandra only)
        if ($file.Name -eq "CassandraConnectorPlugin.cs" -and $line -match '(?<!Cassandra\.)ConsistencyLevel\.') {
            $line = $line -replace '(?<!Cassandra\.)ConsistencyLevel\.', 'Cassandra.ConsistencyLevel.'
            Write-Host "  Line $($i+1): Fixed ConsistencyLevel"
        }

        # Fix DataSchema - fields to fields.ToArray()
        if ($line -match 'new DataSchema\(' -and $lines[$i+1] -match '^\s+fields,' -and $lines[$i+1] -notmatch 'fields\.ToArray') {
            $lines[$i+1] = $lines[$i+1] -replace '\bfields,', 'fields.ToArray(),'
            Write-Host "  Line $($i+2): Fixed DataSchema fields"
            $modified = $true
        }

        # Fix DataSchema - primaryKeys to primaryKeys.ToArray()
        if ($line -match 'new DataSchema\(' -and $lines[$i+2] -match '^\s+primaryKeys[,\)]' -and $lines[$i+2] -notmatch 'primaryKeys\.ToArray') {
            $lines[$i+2] = $lines[$i+2] -replace '\bprimaryKeys([,\)])', 'primaryKeys.ToArray()$1'
            Write-Host "  Line $($i+3): Fixed DataSchema primaryKeys"
            $modified = $true
        }

        # Fix WriteResult - (bool, int, string) to (long, long, string[]?)
        if ($line -match 'return new WriteResult\(true,\s*\w+,\s*"[^"]+"\)') {
            $line = $line -replace 'return new WriteResult\(true,\s*(\w+),\s*"[^"]+"\)', 'return new WriteResult($1, 0, null)'
            Write-Host "  Line $($i+1): Fixed WriteResult success"
        }

        if ($line -match 'return new WriteResult\(false,\s*\d+,\s*"([^"]+)"\)') {
            $line = $line -replace 'return new WriteResult\(false,\s*\d+,\s*"([^"]+)"\)', 'return new WriteResult(0, 0, new[] { "$1" })'
            Write-Host "  Line $($i+1): Fixed WriteResult failure"
        }

        # Fix query.Source to query.TableOrCollection ?? "data"
        if ($line -match 'query\.Source' -and $line -notmatch 'TableOrCollection') {
            $line = $line -replace '\bquery\.Source\b', '(query.TableOrCollection ?? "data")'
            Write-Host "  Line $($i+1): Fixed query.Source"
        }

        if ($line -ne $originalLine) {
            $lines[$i] = $line
            $modified = $true
        }
    }

    if ($modified) {
        Set-Content -Path $file.FullName -Value $lines
        Write-Host "  File modified!"
    } else {
        Write-Host "  No changes"
    }
}

Write-Host "`nAll files processed!"
