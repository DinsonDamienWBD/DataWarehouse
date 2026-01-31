# Fix config.Properties.GetValueOrDefault nullability issues

$files = Get-ChildItem -Path "Plugins\DataWarehouse.Plugins.DataConnectors\*.cs" -File

foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw
    $originalContent = $content

    # Pattern 1: var x = config.Properties.GetValueOrDefault
    # Add: var props = (IReadOnlyDictionary<string, string?>)config.Properties;
    # Replace: config.Properties.GetValueOrDefault -> props.GetValueOrDefault

    if ($content -match 'config\.Properties\.GetValueOrDefault') {
        # Check if props variable already exists
        if ($content -notmatch 'var props = \(IReadOnlyDictionary<string, string\?>\)config\.Properties;') {
            # Find the first occurrence of config.Properties.GetValueOrDefault
            if ($content -match '(\s+)(var \w+ = )?config\.Properties\.GetValueOrDefault') {
                $indentation = $matches[1]
                # Add the props variable before the first usage
                $content = $content -replace "(${indentation})(var \w+ = )?config\.Properties\.GetValueOrDefault",
                    "${indentation}var props = (IReadOnlyDictionary<string, string?>)config.Properties;`r`n${indentation}`$2props.GetValueOrDefault"
            }
        }

        # Replace all remaining config.Properties.GetValueOrDefault with props.GetValueOrDefault
        $content = $content -replace 'config\.Properties\.GetValueOrDefault', 'props.GetValueOrDefault'
    }

    # Pattern 2: query.Properties?.GetValueOrDefault
    $content = $content -replace 'query\.Properties\?\.GetValueOrDefault',
        '((IReadOnlyDictionary<string, string?>?)query.Properties)?.GetValueOrDefault'

    if ($content -ne $originalContent) {
        Set-Content -Path $file.FullName -Value $content -NoNewline
        Write-Host "Fixed: $($file.Name)"
    }
}

Write-Host "Done!"
