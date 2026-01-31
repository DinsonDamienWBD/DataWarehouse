# Script to fix additional connector plugin errors

$pluginPath = "C:\Temp\DataWarehouse\DataWarehouse\Plugins\DataWarehouse.Plugins.DataConnectors"
$files = Get-ChildItem -Path $pluginPath -Filter "*ConnectorPlugin.cs" -File

foreach ($file in $files) {
    Write-Host "Fixing $($file.Name)..."

    $content = Get-Content $file.FullName -Raw
    $modified = $false

    # Remove query.Parameters usage (DataQuery has no Parameters property)
    if ($content -match '\bquery\.Parameters\b') {
        # Just comment out or remove parameter handling for now
        $content = $content -replace 'if \(query\.Parameters != null\)\s*\{[^}]+\}', '// Parameters not supported in DataQuery'
        $content = $content -replace '\bquery\.Parameters\b', 'null /* no Parameters in DataQuery */'
        $modified = $true
        Write-Host "  - Removed query.Parameters usage"
    }

    # Remove remaining query.Source usage (should be query.TableOrCollection)
    if ($content -match '\bquery\.Source\b' -and $content -notmatch 'query\.TableOrCollection') {
        $content = $content -replace '(\s+)query\.Source\b', '$1(query.TableOrCollection ?? "data")'
        $modified = $true
        Write-Host "  - Fixed remaining query.Source"
    }

    # Remove options.Properties usage (WriteOptions has no Properties)
    if ($content -match '\boptions\.Properties\b') {
        # Replace with null/empty for key fields detection
        $content = $content -replace '\(options\.Properties\?\.GetValueOrDefault\("keyFields"\) as IEnumerable<string>\)', '(null as IEnumerable<string>)'
        $modified = $true
        Write-Host "  - Removed options.Properties usage"
    }

    # Fix Math.Min byte cast issue
    if ($content -match 'Math\.Min\(query\.Limit') {
        $content = $content -replace 'Math\.Min\(query\.Limit\b', 'Math.Min((int)(query.Limit ?? 100)'
        $modified = $true
        Write-Host "  - Fixed Math.Min cast"
    }

    # Fix WriteResult with wrong signature patterns that slipped through
    if ($content -match 'new WriteResult\(true,') {
        $content = $content -replace 'new WriteResult\(true,\s*(\w+),\s*"([^"]+)"\)', 'new WriteResult($1, 0, null)'
        $content = $content -replace 'new WriteResult\(true,\s*(\w+),\s*null\)', 'new WriteResult($1, 0, null)'
        $modified = $true
        Write-Host "  - Fixed WriteResult true pattern"
    }

    if ($content -match 'new WriteResult\(false,') {
        $content = $content -replace 'new WriteResult\(false,\s*(\w+),\s*"([^"]+)"\)', 'new WriteResult(0, $1, new[] { "$2" })'
        $content = $content -replace 'new WriteResult\(false,\s*(\w+),\s*null\)', 'new WriteResult(0, $1, null)'
        $modified = $true
        Write-Host "  - Fixed WriteResult false pattern"
    }

    if ($modified) {
        Set-Content -Path $file.FullName -Value $content -NoNewline
        Write-Host "  Modified!"
    } else {
        Write-Host "  No changes needed"
    }
}

Write-Host "`nDone!"
