# Run-Audits.ps1

Write-Host "🚀 Preparing Audit Environment..."
New-Item -ItemType Directory -Force -Path ".\.reports"

Write-Host "🏗️ Building the Solution (Release Mode)..."
dotnet build .\DataWarehouse.Hardening.Tests\DataWarehouse.Hardening.Tests.csproj -c Release

Write-Host "🧬 Running Stryker.NET Mutation Tests..."
# Stryker needs to be run from inside the test project directory to map references
Push-Location .\DataWarehouse.Hardening.Tests
dotnet stryker -r json -o "..\.reports\stryker-report.json"
Pop-Location

Write-Host "🕸️ Running Microsoft Coyote Concurrency Tests..."
# Coyote tests the compiled DLL to manipulate the task scheduler
# Note: Update 'net8.0' below if you are targeting a different .NET version
coyote test .\DataWarehouse.Hardening.Tests\bin\Release\net8.0\DataWarehouse.Hardening.Tests.dll > ".\.reports\coyote-deadlocks.txt"

Write-Host "✅ Audit complete. Logs generated in the .reports folder."
