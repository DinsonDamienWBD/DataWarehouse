---
name: profile
description: Captures performance (dotTrace) or memory (dotMemory) snapshots.
argument-hint: "[trace|memory] [project-path]"
---
# Profiling Skill
- **If trace**: Run `dottrace start --save-to=".reports/perf.dtp" dotnet -- run -c Release --project [path]`.
- **If memory**: 
  1. Locate the `dotMemory.exe` within the local project package folder.
  2. Run `dotnet exec [path-to-package-dll] start --save-to=".reports/mem.dmw" dotnet -- run -c Release --project [path]`.