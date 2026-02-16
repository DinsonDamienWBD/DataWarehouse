param()

function Remove-IntelligenceRegion {
    param([string]$FilePath)

    $lines = Get-Content $FilePath -Encoding UTF8
    $result = @()
    $inRegion = $false
    $regionDepth = 0
    $removedLines = 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '#region\s+Intelligence\s+Integration') {
            $inRegion = $true
            $regionDepth = 1
            $removedLines++
            # Also remove blank line before the region if exists
            if ($result.Count -gt 0 -and $result[-1].Trim() -eq '') {
                $result = $result[0..($result.Count-2)]
            }
            continue
        }

        if ($inRegion) {
            if ($line -match '#region\b') {
                $regionDepth++
            }
            if ($line -match '#endregion') {
                $regionDepth--
                if ($regionDepth -eq 0) {
                    $inRegion = $false
                    $removedLines++
                    continue
                }
            }
            $removedLines++
            continue
        }

        $result += $line
    }

    $result | Set-Content $FilePath -Encoding UTF8
    return $removedLines
}

Set-Location "C:\Temp\DataWarehouse\DataWarehouse"

$files = @(
    'DataWarehouse.SDK\Contracts\Encryption\EncryptionStrategy.cs',
    'DataWarehouse.SDK\Contracts\Compression\CompressionStrategy.cs',
    'DataWarehouse.SDK\Contracts\Storage\StorageStrategy.cs',
    'DataWarehouse.SDK\Contracts\Security\SecurityStrategy.cs',
    'DataWarehouse.SDK\Contracts\Compliance\ComplianceStrategy.cs',
    'DataWarehouse.SDK\Contracts\Streaming\StreamingStrategy.cs',
    'DataWarehouse.SDK\Contracts\Replication\ReplicationStrategy.cs',
    'DataWarehouse.SDK\Contracts\Transit\DataTransitStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Interface\InterfaceStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Media\MediaStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Observability\ObservabilityStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Compute\PipelineComputeStrategy.cs',
    'DataWarehouse.SDK\Contracts\DataFormat\DataFormatStrategy.cs',
    'DataWarehouse.SDK\Contracts\DataLake\DataLakeStrategy.cs',
    'DataWarehouse.SDK\Contracts\DataMesh\DataMeshStrategy.cs',
    'DataWarehouse.SDK\Contracts\StorageProcessing\StorageProcessingStrategy.cs',
    'DataWarehouse.SDK\Contracts\RAID\RaidStrategy.cs',
    'DataWarehouse.SDK\Connectors\ConnectionStrategyBase.cs',
    'DataWarehouse.SDK\Security\IKeyStore.cs'
)

$totalRemoved = 0
foreach ($f in $files) {
    $removed = Remove-IntelligenceRegion -FilePath $f
    Write-Host "$f : $removed lines removed"
    $totalRemoved += $removed
}
Write-Host "Total intelligence lines removed: $totalRemoved"
