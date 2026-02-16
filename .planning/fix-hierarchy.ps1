param()
Set-Location "C:\Temp\DataWarehouse\DataWarehouse"

function Fix-File {
    param([string]$Path, [hashtable[]]$Replacements)
    $content = Get-Content $Path -Raw -Encoding UTF8
    $changed = $false
    foreach ($r in $Replacements) {
        if ($content.Contains($r.Old)) {
            $content = $content.Replace($r.Old, $r.New)
            $changed = $true
        }
    }
    if ($changed) {
        Set-Content $Path -Value $content -Encoding UTF8 -NoNewline
        Write-Host "Fixed: $Path"
    } else {
        Write-Host "NOOP: $Path"
    }
}

# ===== Fix StrategyId hiding (add override to abstract declarations) =====
# Files with "public abstract string StrategyId { get; }" that need "public override abstract string StrategyId { get; }"
$strategyIdFiles = @(
    'DataWarehouse.SDK\Connectors\ConnectionStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Compliance\ComplianceStrategy.cs',
    'DataWarehouse.SDK\Contracts\Compute\PipelineComputeStrategy.cs',
    'DataWarehouse.SDK\Contracts\DataFormat\DataFormatStrategy.cs',
    'DataWarehouse.SDK\Contracts\Encryption\EncryptionStrategy.cs',
    'DataWarehouse.SDK\Contracts\Replication\ReplicationStrategy.cs',
    'DataWarehouse.SDK\Contracts\Security\SecurityStrategy.cs',
    'DataWarehouse.SDK\Contracts\Storage\StorageStrategy.cs',
    'DataWarehouse.SDK\Contracts\StorageProcessing\StorageProcessingStrategy.cs',
    'DataWarehouse.SDK\Contracts\Streaming\StreamingStrategy.cs',
    'DataWarehouse.SDK\Contracts\Transit\DataTransitStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\RAID\RaidStrategy.cs',
    'DataWarehouse.SDK\Contracts\StorageOrchestratorBase.cs'
)

foreach ($f in $strategyIdFiles) {
    $content = Get-Content $f -Raw -Encoding UTF8
    # Handle 8-space indent (inside namespace+class)
    $content = $content.Replace(
        "        public abstract string StrategyId { get; }",
        "        public override abstract string StrategyId { get; }")
    # Handle 4-space indent (file-scoped namespace)
    $content = $content.Replace(
        "    public abstract string StrategyId { get; }",
        "    public override abstract string StrategyId { get; }")
    Set-Content $f -Value $content -Encoding UTF8 -NoNewline
    Write-Host "StrategyId override: $f"
}

# ===== Fix Name hiding (files that use "Name" instead of "StrategyName") =====
$nameFiles = @(
    'DataWarehouse.SDK\Contracts\Media\MediaStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Storage\StorageStrategy.cs',
    'DataWarehouse.SDK\Contracts\StorageProcessing\StorageProcessingStrategy.cs',
    'DataWarehouse.SDK\Contracts\Streaming\StreamingStrategy.cs',
    'DataWarehouse.SDK\Contracts\Transit\DataTransitStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\StorageOrchestratorBase.cs'
)

foreach ($f in $nameFiles) {
    $content = Get-Content $f -Raw -Encoding UTF8
    $content = $content.Replace(
        "        public abstract string Name { get; }",
        "        public override abstract string Name { get; }")
    $content = $content.Replace(
        "    public abstract string Name { get; }",
        "    public override abstract string Name { get; }")
    Set-Content $f -Value $content -Encoding UTF8 -NoNewline
    Write-Host "Name override: $f"
}

# ===== Fix Observability: StrategyId/Name, IsInitialized, Dispose, InitializeAsyncCore, EnsureNotDisposed =====
$obs = Get-Content 'DataWarehouse.SDK\Contracts\Observability\ObservabilityStrategyBase.cs' -Raw -Encoding UTF8

# StrategyId and Name
$obs = $obs.Replace(
    "    public abstract string StrategyId { get; }",
    "    public override abstract string StrategyId { get; }")
$obs = $obs.Replace(
    "    public abstract string Name { get; }",
    "    public override abstract string Name { get; }")

# IsInitialized - remove local field and property, use base
$obs = $obs.Replace("    private bool _disposed;`n    private bool _initialized;",
    "    private bool _disposed;")
# Try different line endings
$obs = $obs -replace "    private bool _disposed;\r?\n    private bool _initialized;\r?\n", "    private bool _disposed;`r`n"
# IsInitialized - hide with new since base has it
$obs = $obs.Replace(
    "    protected bool IsInitialized => _initialized;",
    "    protected new bool IsInitialized => _initialized;")

# InitializeAsyncCore - add override
$obs = $obs.Replace(
    "    protected virtual Task InitializeAsyncCore(CancellationToken cancellationToken)",
    "    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)")

# Dispose(bool) - add override, add base call
$obs = $obs.Replace(
    "    protected virtual void Dispose(bool disposing)",
    "    protected override void Dispose(bool disposing)")
# Add base.Dispose(disposing) call before the end
$obs = $obs.Replace(
    "        _disposed = true;`n    }",
    "        base.Dispose(disposing);`n        _disposed = true;`n    }")
$obs = $obs -replace "        _disposed = true;\r?\n    \}", "        base.Dispose(disposing);`r`n        _disposed = true;`r`n    }"

# Remove standalone Dispose() and GC.SuppressFinalize - inherited from StrategyBase
$obs = $obs -replace "\r?\n    /// <inheritdoc/>\r?\n    public void Dispose\(\)\r?\n    \{\r?\n        Dispose\(disposing: true\);\r?\n        GC\.SuppressFinalize\(this\);\r?\n    \}", ""

# EnsureNotDisposed - remove it (use inherited from StrategyBase)
$obs = $obs -replace "\r?\n    private void EnsureNotDisposed\(\)\r?\n    \{\r?\n        if \(_disposed\)\r?\n            throw new ObjectDisposedException\(GetType\(\)\.Name\);\r?\n    \}", ""

Set-Content 'DataWarehouse.SDK\Contracts\Observability\ObservabilityStrategyBase.cs' -Value $obs -Encoding UTF8 -NoNewline
Write-Host "Fixed: ObservabilityStrategyBase.cs"

# ===== Fix InterfaceStrategyBase: Dispose pattern =====
$iface = Get-Content 'DataWarehouse.SDK\Contracts\Interface\InterfaceStrategyBase.cs' -Raw -Encoding UTF8

# Dispose(bool) - add override
$iface = $iface.Replace(
    "    protected virtual void Dispose(bool disposing)",
    "    protected override void Dispose(bool disposing)")

# Add base.Dispose(disposing) if not present
$iface = $iface -replace "(        // Subclasses can override to dispose additional resources\r?\n    \})", "        base.Dispose(disposing);`r`n    }"

# Remove standalone Dispose() - inherited from StrategyBase
$iface = $iface -replace "\r?\n    /// <summary>\r?\n    /// Releases all resources used by the strategy\.\r?\n    /// </summary>\r?\n    public void Dispose\(\)\r?\n    \{\r?\n        Dispose\(disposing: true\);\r?\n        GC\.SuppressFinalize\(this\);\r?\n    \}", ""

# EnsureNotDisposed - remove (inherited from StrategyBase)
$iface = $iface -replace "\r?\n    private void EnsureNotDisposed\(\)\r?\n    \{\r?\n        if \(_disposed\)\r?\n            throw new ObjectDisposedException\(GetType\(\)\.Name\);\r?\n    \}", ""

Set-Content 'DataWarehouse.SDK\Contracts\Interface\InterfaceStrategyBase.cs' -Value $iface -Encoding UTF8 -NoNewline
Write-Host "Fixed: InterfaceStrategyBase.cs"

# ===== Fix DataLake/DataMesh: StrategyId, Name, IsInitialized, InitializeAsync, DisposeAsync =====
foreach ($f in @('DataWarehouse.SDK\Contracts\DataLake\DataLakeStrategy.cs', 'DataWarehouse.SDK\Contracts\DataMesh\DataMeshStrategy.cs')) {
    $content = Get-Content $f -Raw -Encoding UTF8

    # StrategyId
    $content = $content.Replace(
        "    public abstract string StrategyId { get; }",
        "    public override abstract string StrategyId { get; }")

    # Name
    $content = $content.Replace(
        "    public abstract string Name { get; }",
        "    public override abstract string Name { get; }")

    # IsInitialized
    $content = $content.Replace(
        "    protected bool IsInitialized",
        "    protected new bool IsInitialized")

    # InitializeAsync - hide with new
    $content = $content.Replace(
        "    public virtual async Task InitializeAsync(CancellationToken",
        "    public new virtual async Task InitializeAsync(CancellationToken")
    $content = $content.Replace(
        "    public async Task InitializeAsync(CancellationToken",
        "    public new async Task InitializeAsync(CancellationToken")

    # DisposeAsync - hide with new
    $content = $content.Replace(
        "    public virtual async ValueTask DisposeAsync()",
        "    public new virtual async ValueTask DisposeAsync()")
    $content = $content.Replace(
        "    public async ValueTask DisposeAsync()",
        "    public new async ValueTask DisposeAsync()")

    Set-Content $f -Value $content -Encoding UTF8 -NoNewline
    Write-Host "Fixed: $f"
}

# ===== Remove unused 'using DataWarehouse.SDK.AI;' from files that no longer use AI types =====
$aiCheckFiles = @(
    'DataWarehouse.SDK\Contracts\Observability\ObservabilityStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Interface\InterfaceStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Media\MediaStrategyBase.cs',
    'DataWarehouse.SDK\Contracts\Transit\DataTransitStrategyBase.cs'
)

foreach ($f in $aiCheckFiles) {
    $content = Get-Content $f -Raw -Encoding UTF8
    # Check if any AI types are still used (KnowledgeObject, RegisteredCapability, CapabilityCategory)
    if ($content -notmatch 'KnowledgeObject|RegisteredCapability|CapabilityCategory' -and $content -match 'using DataWarehouse\.SDK\.AI;') {
        $content = $content -replace "using DataWarehouse\.SDK\.AI;\r?\n", ""
        Set-Content $f -Value $content -Encoding UTF8 -NoNewline
        Write-Host "Removed AI using: $f"
    }
}

Write-Host "All fixes applied."
