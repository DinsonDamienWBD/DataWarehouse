# DataWarehouse.Plugins.SharedRaidUtilities

Shared RAID utilities to eliminate code duplication across RAID plugins.

## Overview

This project consolidates ~1,450 lines of duplicated code from 10+ RAID plugins into a single, well-tested, shared library.

## Components

### GaloisField.cs (~640 lines)
- Complete GF(2^8) Galois Field implementation
- Uses irreducible polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D)
- Precomputed lookup tables for O(1) operations
- Reed-Solomon parity calculations (P, Q, R)
- Single, dual, and triple failure reconstruction algorithms

### ReedSolomonHelper.cs (~150 lines)
- High-level helper methods for common RAID operations
- Simplified interfaces for Z1/Z2/Z3 parity calculation
- Automatic failure reconstruction based on failure count
- Parity verification utilities

### RaidConstants.cs (~200 lines)
- Standard RAID configuration constants
- Minimum device requirements for all RAID levels
- Capacity factor calculations
- Failure tolerance information
- Hot spare support detection

## Usage Example

```csharp
using DataWarehouse.Plugins.SharedRaidUtilities;

// Calculate RAID-Z2 parity
var dataBlocks = new byte[][] { block1, block2, block3 };
var parityBlocks = ReedSolomonHelper.GenerateParityBlocks(dataBlocks, 2);

// Reconstruct failed blocks
var failedIndices = new[] { 1, 2 };
ReedSolomonHelper.ReconstructFailures(dataBlocks, failedIndices, parityBlocks);

// Check minimum devices
if (deviceCount < RaidConstants.RaidZ2MinDevices)
{
    throw new InvalidOperationException("Need more devices");
}
```

## Migration Guide

To migrate a RAID plugin to use SharedRaidUtilities:

### 1. Add Project Reference

Update your plugin's `.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\DataWarehouse.Plugins.SharedRaidUtilities\DataWarehouse.Plugins.SharedRaidUtilities.csproj" />
</ItemGroup>
```

### 2. Add Using Statement

```csharp
using DataWarehouse.Plugins.SharedRaidUtilities;
```

### 3. Replace GaloisField

**Before:**
```csharp
private readonly GaloisField _galoisField;  // Internal class
_galoisField = new GaloisField();
```

**After:**
```csharp
private readonly SharedRaidUtilities.GaloisField _galoisField;  // Shared class
_galoisField = new SharedRaidUtilities.GaloisField();
```

### 4. Remove Duplicate Code

Delete your plugin's internal:
- `GaloisField` class
- `ReedSolomon` helper methods (if any)
- RAID constants (if any)

### 5. Update Method Calls

Most method signatures are identical. Key changes:

**Parity Calculation:**
```csharp
// Old (if you had custom methods)
var parity = CalculateZ1Parity(dataBlocks);

// New
var parity = ReedSolomonHelper.CalculateZ1Parity(dataBlocks);
// OR use GaloisField directly
var parity = _galoisField.CalculatePParity(dataBlocks);
```

**Constants:**
```csharp
// Old
const int Raid5MinDevices = 3;

// New
RaidConstants.Raid5MinDevices
```

### 6. Build and Test

```bash
dotnet build DataWarehouse.slnx
dotnet test
```

## Migrated Plugins

- ✅ **ZfsRaid** - Reference implementation (completed)
- ⏳ **Raid** - Pending
- ⏳ **StandardRaid** - Pending
- ⏳ **AdvancedRaid** - Pending
- ⏳ **EnhancedRaid** - Pending
- ⏳ **NestedRaid** - Pending
- ⏳ **VendorSpecificRaid** - Pending
- ⏳ **SelfHealingRaid** - Pending
- ⏳ **AdaptiveEc** - Pending
- ⏳ **IsalEc** - Pending
- ⏳ **ErasureCoding** - Pending

## Benefits

1. **Code Reduction**: Eliminates ~1,450 lines of duplicated code
2. **Consistency**: All plugins use identical, well-tested algorithms
3. **Maintainability**: Bug fixes and optimizations benefit all plugins
4. **Performance**: Single precomputed lookup table shared across plugins
5. **Documentation**: Comprehensive XML docs in one place

## Technical Details

### Galois Field Operations

The shared GaloisField implementation uses:
- **Irreducible Polynomial**: 0x11D (x^8 + x^4 + x^3 + x^2 + 1)
- **Generator**: 2 (primitive element)
- **Field Size**: 256 (GF(2^8))
- **Optimization**: Precomputed exp/log/multiply/inverse tables

### Parity Algorithms

- **P Parity (Z1)**: XOR of all data blocks
- **Q Parity (Z2)**: Σ(data[i] × 2^i) in GF(2^8)
- **R Parity (Z3)**: Σ(data[i] × 4^i) in GF(2^8)

### Reconstruction

- **1 Failure**: XOR reconstruction using P parity
- **2 Failures**: Matrix solving using P+Q parity
- **3 Failures**: 3×3 matrix inversion using P+Q+R parity

## License

Same as parent DataWarehouse project.
