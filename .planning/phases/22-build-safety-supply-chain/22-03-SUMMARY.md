---
phase: 22-build-safety-supply-chain
plan: 03
---
# Phase 22 Plan 03 Summary

## What Changed
- Pinned 46+ floating package versions to exact resolved versions across 9 plugin projects
- Fixed NU1510 warnings: removed System.Data.Common from UltimateDatabaseProtocol, System.Net.Http.Json from UltimateKeyManagement and UniversalDashboards, System.Net.WebSockets.Client from UniversalDashboards
- Fixed NU1603 warnings: pinned MySqlConnector to 2.5.0 and Neo4j.Driver to 6.0.0 in UltimateDatabaseProtocol
- Enabled RestorePackagesWithLockFile=true in Directory.Build.props
- Generated 69 packages.lock.json files (one per project)
- Installed CycloneDX 6.0.0 as local tool via dotnet-tools.json
- Generated SDK SBOM at artifacts/sbom/sdk-sbom.json
- Verified zero known vulnerabilities (direct and transitive)
- SDK has 4 direct PackageReferences (within SUPPLY-04 limit of 6)

## Files Modified
- Directory.Build.props (added RestorePackagesWithLockFile)
- 9 plugin .csproj files (pinned floating versions)
- 3 plugin .csproj files (removed unnecessary packages for NU1510)
- 1 plugin .csproj file (pinned versions for NU1603)

## Files Created
- dotnet-tools.json (CycloneDX 6.0.0 local tool manifest)
- 69 packages.lock.json files
- artifacts/sbom/sdk-sbom.json (CycloneDX SBOM)

## Build Status
Solution restores successfully with all pinned versions. Locked-mode restore verified for SDK.

## Metrics
- Floating versions pinned: 46+
- NU1510 warnings fixed: 4 (unnecessary packages removed)
- NU1603 warnings fixed: 2 (versions pinned)
- Lock files generated: 69
- Vulnerabilities found: 0
- SDK direct PackageReferences: 4 (within 6 limit)
- Commit: 61ff477

## Notes
- Full solution SBOM generation fails due to CycloneDX inability to resolve transitive version range conflicts (AWSSDK.SecurityToken requires AWSSDK.Core <4.0 but 4.0.x resolved). SDK SBOM generated successfully as primary artifact.
- NU1608 warnings (dependency constraint violations from upstream packages) will be addressed in Plan 22-02 via NoWarn suppression.
