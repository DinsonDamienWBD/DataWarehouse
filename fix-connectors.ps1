# Script to fix common connector plugin errors

$pluginPath = "C:\Temp\DataWarehouse\DataWarehouse\Plugins\DataWarehouse.Plugins.DataConnectors"
$files = Get-ChildItem -Path $pluginPath -Filter "*ConnectorPlugin.cs" -File

foreach ($file in $files) {
    Write-Host "Fixing $($file.Name)..."

    $content = Get-Content $file.FullName -Raw
    $modified = $false

    # Fix ConnectionResult missing ServerInfo parameter
    if ($content -match 'new ConnectionResult\(false,\s*([^,)]+)\)(?!\s*,)') {
        $content = $content -replace 'new ConnectionResult\(false,\s*([^,)]+)\)(?!\s*,)', 'new ConnectionResult(false, $1, null)'
        $modified = $true
        Write-Host "  - Fixed ConnectionResult"
    }

    # Fix ConnectionState ambiguity
    if ($content -match 'ConnectionState\.Open' -and $content -notmatch 'System\.Data\.ConnectionState\.Open') {
        $content = $content -replace '(?<!System\.Data\.)ConnectionState\.Open', 'System.Data.ConnectionState.Open'
        $content = $content -replace '(?<!System\.Data\.)ConnectionState\.Closed', 'System.Data.ConnectionState.Closed'
        $modified = $true
        Write-Host "  - Fixed ConnectionState"
    }

    # Fix WriteMode ambiguity
    if ($content -match '\bWriteMode\.' -and $content -notmatch 'SDK\.Connectors\.WriteMode\.') {
        $content = $content -replace '(\s+case\s+)WriteMode\.', '$1SDK.Connectors.WriteMode.'
        $content = $content -replace '(\s+==\s+)WriteMode\.', '$1SDK.Connectors.WriteMode.'
        $content = $content -replace '(\s+!=\s+)WriteMode\.', '$1SDK.Connectors.WriteMode.'
        $modified = $true
        Write-Host "  - Fixed WriteMode"
    }

    # Fix DataSchema List to Array
    if ($content -match 'new DataSchema\([^)]+,\s*fields,') {
        $content = $content -replace '(new DataSchema\([^,]+,\s*)fields,', '$1fields.ToArray(),'
        $content = $content -replace '(new DataSchema\([^,]+,\s*[^,]+,\s*)primaryKeys,', '$1primaryKeys.ToArray(),'
        $content = $content -replace '(new DataSchema\([^,]+,\s*[^,]+,\s*)primaryKeys\)', '$1primaryKeys.ToArray())'
        $modified = $true
        Write-Host "  - Fixed DataSchema arrays"
    }

    # Fix WriteResult signature (bool, int, string) to (long, long, string[]?)
    if ($content -match 'new WriteResult\(true,\s*\d+,\s*"[^"]+"\)') {
        $content = $content -replace 'new WriteResult\(true,\s*(\w+),\s*"([^"]+)"\)', 'new WriteResult($1, 0, null)'
        $modified = $true
        Write-Host "  - Fixed WriteResult (success)"
    }

    if ($content -match 'new WriteResult\(false,\s*\d+,\s*"[^"]+"\)') {
        $content = $content -replace 'new WriteResult\(false,\s*\d+,\s*"([^"]+)"\)', 'new WriteResult(0, 0, new[] { "$1" })'
        $modified = $true
        Write-Host "  - Fixed WriteResult (failure)"
    }

    # Fix query.Source to query.TableOrCollection
    if ($content -match '\bquery\.Source\b' -and $content -notmatch 'query\.TableOrCollection') {
        $content = $content -replace '(\s+)query\.Source\b', '$1(query.TableOrCollection ?? query.Source ?? "data")'
        $modified = $true
        Write-Host "  - Fixed query.Source"
    }

    # Fix options.KeyFields
    if ($content -match '\boptions\.KeyFields\b') {
        $content = $content -replace '\boptions\.KeyFields\b', '(options.Properties?.GetValueOrDefault("keyFields") as IEnumerable<string>)'
        $modified = $true
        Write-Host "  - Fixed options.KeyFields"
    }

    if ($modified) {
        Set-Content -Path $file.FullName -Value $content -NoNewline
        Write-Host "  Modified!"
    } else {
        Write-Host "  No changes needed"
    }
}

Write-Host "`nDone!"
