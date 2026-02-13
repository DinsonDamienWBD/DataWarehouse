---
phase: 22-build-safety-supply-chain
plan: 04
---
# Phase 22 Plan 04 Summary

## What Changed
- Migrated DataWarehouse.CLI Program.cs from deprecated System.CommandLine NamingConventionBinder to 2.0.3 stable SetAction API
- Replaced all 8 CommandHandler.Create call sites with SetAction pattern
- Fixed Option<T> and Argument<T> constructors for 2.0.3 (name-only, Description via property)
- Added MakeArg<T>/MakeOpt<T> helper methods for consistent symbol creation
- Migrated .Add() calls to .Subcommands.Add()/.Options.Add()/.Arguments.Add()
- Used ParseResult.GetResult() for non-generic argument/option value extraction
- Fixed StartsWith to use char overload (CA1866/S6610)
- Verified --help output for root command and all subcommand hierarchies

## Deviations from Plan
### Auto-fixed Issues

**1. [Rule 1 - Bug] Option<T> constructor signature change**
- **Found during:** Task 1
- **Issue:** Plan described Option<T>(name, description) constructor, but 2.0.3 uses Option<T>(name, params string[] aliases). Description is a property, not a constructor parameter.
- **Fix:** Changed all Option constructors to use name-only with Description property initializer
- **Files modified:** DataWarehouse.CLI/Program.cs
- **Commit:** 786189d

**2. [Rule 1 - Bug] ParseResult.GetValue<T> requires generic Argument<T>/Option<T>**
- **Found during:** Task 1
- **Issue:** CreateSubCommand stored symbols as non-generic Argument/Option base types, but GetValue<T> requires the generic type
- **Fix:** Changed to ParseResult.GetResult(arg/opt) which accepts non-generic base types
- **Files modified:** DataWarehouse.CLI/Program.cs
- **Commit:** 786189d

## Files Modified
- DataWarehouse.CLI/Program.cs (193 insertions, 155 deletions)

## Build Status
CLI project builds with 0 errors. --help output verified for root and subcommands.

## Metrics
- CommandHandler.Create sites migrated: 8
- Option constructor fixes: 8
- Helper methods added: 3 (MakeArg, MakeOpt x2)
- Commit: 786189d
