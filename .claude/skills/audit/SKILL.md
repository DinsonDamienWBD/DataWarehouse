---
name: audit
description: Runs Stryker mutation testing, Coyote concurrency, and dotCover code coverage on a project using unified 2026 CLI tools.
argument-hint: "[project-path]"
---
# Audit Skill
When invoked:
1. **Build**: Run `dotnet build [project-path] -c Release`.
2. **Coyote**: Run `coyote test [dll-path] --iterations 1000`.
3. **Coverage**: Run `dotcover cover --target-executable="dotnet.exe" --target-arguments="test [project-path]" --output=".reports/coverage.html" --report-type="HTML"`.
4. **Memory**: Run `dotnet exec --depsfile [project-path]/bin/Release/net8.0/[project-name].deps.json --runtimeconfig [project-path]/bin/Release/net8.0/[project-name].runtimeconfig.json ~/.nuget/packages/jetbrains.dotmemory.console.windows-x64/[version]/tools/dotMemory.exe start --save-to=".reports/audit-mem.dmw" -- dotnet test [project-path]`.