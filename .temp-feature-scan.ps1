# Feature Verification Scanner
# Scans plugins to verify feature implementation status

param(
    [string]$PluginPath = "C:/Temp/DataWarehouse/DataWarehouse/Plugins",
    [string]$OutputPath = "C:/Temp/DataWarehouse/DataWarehouse/.planning/phases/42-feature-verification-matrix"
)

$results = @{}

# Get all strategy files from each plugin
Get-ChildItem -Path $PluginPath -Directory | ForEach-Object {
    $pluginName = $_.Name
    $strategyFiles = Get-ChildItem -Path $_.FullName -Filter "*Strategy.cs" -Recurse -ErrorAction SilentlyContinue

    $results[$pluginName] = @{
        Path = $_.FullName
        StrategyCount = $strategyFiles.Count
        Strategies = @()
    }

    foreach ($file in $strategyFiles) {
        $content = Get-Content -Path $file.FullName -Raw

        # Basic scoring heuristic
        $score = 0
        if ($content -match "class \w+Strategy") { $score += 20 }
        if ($content -notmatch "NotImplementedException") { $score += 20 }
        if ($content -match "///\s*<summary>") { $score += 10 }
        if ($content -match "override.*CompressCore|override.*EncryptCore|override.*Execute") { $score += 30 }
        if ($content -match "using|namespace") { $score += 10 }
        if ($content -notmatch "TODO|HACK|FIXME") { $score += 10 }

        $results[$pluginName].Strategies += @{
            Name = $file.BaseName
            Path = $file.FullName
            Score = [Math]::Min($score, 100)
            Lines = ($content -split "`n").Count
        }
    }
}

# Output JSON for further processing
$results | ConvertTo-Json -Depth 10 | Out-File -FilePath "$OutputPath/plugin-scan-results.json" -Encoding UTF8

Write-Host "Scanned $($results.Count) plugins"
Write-Host "Results written to: $OutputPath/plugin-scan-results.json"
