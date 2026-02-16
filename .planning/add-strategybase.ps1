param()
Set-Location "C:\Temp\DataWarehouse\DataWarehouse"

# Map of file -> old class declaration -> new class declaration
$replacements = @(
    @{
        File = 'DataWarehouse.SDK\Contracts\Encryption\EncryptionStrategy.cs'
        Old = 'public abstract class EncryptionStrategyBase : IEncryptionStrategy'
        New = 'public abstract class EncryptionStrategyBase : StrategyBase, IEncryptionStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Compression\CompressionStrategy.cs'
        Old = 'public abstract class CompressionStrategyBase : ICompressionStrategy'
        New = 'public abstract class CompressionStrategyBase : StrategyBase, ICompressionStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Storage\StorageStrategy.cs'
        Old = 'public abstract class StorageStrategyBase : IStorageStrategy'
        New = 'public abstract class StorageStrategyBase : StrategyBase, IStorageStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Security\SecurityStrategy.cs'
        Old = 'public abstract class SecurityStrategyBase : ISecurityStrategy'
        New = 'public abstract class SecurityStrategyBase : StrategyBase, ISecurityStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Compliance\ComplianceStrategy.cs'
        Old = 'public abstract class ComplianceStrategyBase : IComplianceStrategy'
        New = 'public abstract class ComplianceStrategyBase : StrategyBase, IComplianceStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Streaming\StreamingStrategy.cs'
        Old = 'public abstract class StreamingStrategyBase : IStreamingStrategy'
        New = 'public abstract class StreamingStrategyBase : StrategyBase, IStreamingStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Replication\ReplicationStrategy.cs'
        Old = 'public abstract class ReplicationStrategyBase : IReplicationStrategy'
        New = 'public abstract class ReplicationStrategyBase : StrategyBase, IReplicationStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Transit\DataTransitStrategyBase.cs'
        Old = 'public abstract class DataTransitStrategyBase : IDataTransitStrategy'
        New = 'public abstract class DataTransitStrategyBase : StrategyBase, IDataTransitStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Interface\InterfaceStrategyBase.cs'
        Old = 'public abstract class InterfaceStrategyBase : IInterfaceStrategy, IDisposable'
        New = 'public abstract class InterfaceStrategyBase : StrategyBase, IInterfaceStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Media\MediaStrategyBase.cs'
        Old = 'public abstract class MediaStrategyBase : IMediaStrategy'
        New = 'public abstract class MediaStrategyBase : StrategyBase, IMediaStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Observability\ObservabilityStrategyBase.cs'
        Old = 'public abstract class ObservabilityStrategyBase : IObservabilityStrategy'
        New = 'public abstract class ObservabilityStrategyBase : StrategyBase, IObservabilityStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\Compute\PipelineComputeStrategy.cs'
        Old = 'public abstract class PipelineComputeStrategyBase : IPipelineComputeStrategy'
        New = 'public abstract class PipelineComputeStrategyBase : StrategyBase, IPipelineComputeStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\DataFormat\DataFormatStrategy.cs'
        Old = 'public abstract class DataFormatStrategyBase : IDataFormatStrategy'
        New = 'public abstract class DataFormatStrategyBase : StrategyBase, IDataFormatStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\DataLake\DataLakeStrategy.cs'
        Old = 'public abstract class DataLakeStrategyBase : IDataLakeStrategy'
        New = 'public abstract class DataLakeStrategyBase : StrategyBase, IDataLakeStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\DataMesh\DataMeshStrategy.cs'
        Old = 'public abstract class DataMeshStrategyBase : IDataMeshStrategy'
        New = 'public abstract class DataMeshStrategyBase : StrategyBase, IDataMeshStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\StorageProcessing\StorageProcessingStrategy.cs'
        Old = 'public abstract class StorageProcessingStrategyBase : IStorageProcessingStrategy'
        New = 'public abstract class StorageProcessingStrategyBase : StrategyBase, IStorageProcessingStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\RAID\RaidStrategy.cs'
        Old = 'public abstract class RaidStrategyBase : IRaidStrategy'
        New = 'public abstract class RaidStrategyBase : StrategyBase, IRaidStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Connectors\ConnectionStrategyBase.cs'
        Old = 'public abstract class ConnectionStrategyBase : IConnectionStrategy'
        New = 'public abstract class ConnectionStrategyBase : StrategyBase, IConnectionStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Security\IKeyStore.cs'
        Old = 'public abstract class KeyStoreStrategyBase : IKeyStoreStrategy, IDisposable'
        New = 'public abstract class KeyStoreStrategyBase : StrategyBase, IKeyStoreStrategy'
    },
    @{
        File = 'DataWarehouse.SDK\Contracts\StorageOrchestratorBase.cs'
        Old = 'public abstract class StorageStrategyBase : IStorageStrategy'
        New = 'public abstract class StorageStrategyBase : StrategyBase, IStorageStrategy'
    }
)

foreach ($r in $replacements) {
    $content = Get-Content $r.File -Raw -Encoding UTF8
    if ($content.Contains($r.Old)) {
        $content = $content.Replace($r.Old, $r.New)
        Set-Content $r.File -Value $content -Encoding UTF8 -NoNewline
        Write-Host "OK: $($r.File)"
    } else {
        Write-Host "SKIP: $($r.File) - pattern not found"
    }
}
