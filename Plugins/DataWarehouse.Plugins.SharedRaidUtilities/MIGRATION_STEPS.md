# SharedRaidUtilities Migration Steps

## Remaining Plugins to Migrate

The following plugins still contain embedded GaloisField implementations and should be migrated:

### Priority 1: Core RAID Plugins (9 plugins)

1. **DataWarehouse.Plugins.Raid** (`RaidPlugin.cs`, line 2454)
   - Embedded GaloisField at line 2454
   - Used for RAID 0/1/2/3/4/5/6/10

2. **DataWarehouse.Plugins.StandardRaid** (`StandardRaidPlugin.cs`, line 1826)
   - Embedded GaloisField at line 1826
   - Used for RAID 0/1/5/6/10

3. **DataWarehouse.Plugins.AdvancedRaid** (`AdvancedRaidPlugin.cs`, line 1985)
   - Embedded GaloisField at line 1985
   - Used for advanced RAID configurations

4. **DataWarehouse.Plugins.EnhancedRaid** (`EnhancedRaidPlugin.cs`, line 2090)
   - Embedded GaloisField at line 2090
   - Used for enhanced RAID features

5. **DataWarehouse.Plugins.NestedRaid** (`NestedRaidPlugin.cs`, line 2052)
   - Embedded GaloisField at line 2052
   - Used for RAID 50/60/100

6. **DataWarehouse.Plugins.VendorSpecificRaid** (likely embedded)
   - Check for GaloisField implementation
   - Used for vendor-specific RAID

7. **DataWarehouse.Plugins.SelfHealingRaid** (`SelfHealingRaidPlugin.cs`, line 2919)
   - Embedded GaloisField at line 2919
   - Used for self-healing RAID arrays

8. **DataWarehouse.Plugins.ExtendedRaid** (likely embedded)
   - Check for GaloisField implementation
   - Used for extended RAID features

9. **DataWarehouse.Plugins.AutoRaid** (likely embedded)
   - Check for GaloisField implementation
   - Used for automatic RAID level selection

### Priority 2: Erasure Coding Plugins (3 plugins)

10. **DataWarehouse.Plugins.ErasureCoding** (`ErasureCodingPlugin.cs`, line 802)
    - Embedded GaloisField at line 802
    - Used for generic erasure coding

11. **DataWarehouse.Plugins.IsalEc** (`IsalEcPlugin.cs`, line 1071)
    - Embedded GaloisField at line 1071
    - Used for Intel ISA-L optimized erasure coding

12. **DataWarehouse.Plugins.AdaptiveEc** (`AdaptiveEcPlugin.cs`, line 983)
    - Embedded GaloisField at line 983
    - Used for adaptive erasure coding

## Migration Process

For each plugin listed above:

### Step 1: Update .csproj
```xml
<ItemGroup>
  <ProjectReference Include="..\DataWarehouse.Plugins.SharedRaidUtilities\DataWarehouse.Plugins.SharedRaidUtilities.csproj" />
</ItemGroup>
```

### Step 2: Add Using Statement
```csharp
using DataWarehouse.Plugins.SharedRaidUtilities;
```

### Step 3: Replace Field Declaration
Find the GaloisField field (search for `_galoisField` or similar):
```csharp
// OLD
private readonly GaloisField _galoisField;

// NEW
private readonly SharedRaidUtilities.GaloisField _galoisField;
```

### Step 4: Update Initialization
```csharp
// OLD
_galoisField = new GaloisField();

// NEW
_galoisField = new SharedRaidUtilities.GaloisField();
```

### Step 5: Remove Embedded GaloisField Class
Delete the entire `class GaloisField { ... }` block from the plugin file.

### Step 6: Update Constant References (if applicable)
Replace hardcoded constants with RaidConstants:
```csharp
// OLD
const int Raid5MinDevices = 3;
const int Raid6MinDevices = 4;

// NEW
RaidConstants.Raid5MinDevices
RaidConstants.Raid6MinDevices
```

### Step 7: Update Helper Method Calls (if applicable)
If the plugin has custom Reed-Solomon helper methods, consider replacing them:
```csharp
// OLD (custom methods)
var parity = CalculateZ1Parity(dataBlocks);
var parity = CalculateZ2Parity(dataBlocks);

// NEW (use ReedSolomonHelper)
var parity = ReedSolomonHelper.CalculateZ1Parity(dataBlocks);
var parity = ReedSolomonHelper.CalculateZ2Parity(dataBlocks);

// OR continue using GaloisField directly if preferred
var parity = _galoisField.CalculatePParity(dataBlocks);
var parity = _galoisField.CalculateQParity(dataBlocks);
```

### Step 8: Build and Test
```bash
cd C:\Temp\DataWarehouse\DataWarehouse
dotnet build "Plugins\DataWarehouse.Plugins.{PluginName}\DataWarehouse.Plugins.{PluginName}.csproj"
dotnet test
```

### Step 9: Verify No Behavioral Changes
- Ensure all tests pass
- Verify parity calculations produce identical results
- Check that reconstruction algorithms work correctly

## Expected Code Reduction

- **Per Plugin**: ~200-650 lines removed (GaloisField class)
- **Total**: ~1,450 lines removed across all plugins
- **Binary Size**: Reduced due to shared compilation

## Verification Checklist

After migrating each plugin:

- [ ] .csproj includes SharedRaidUtilities reference
- [ ] Using statement added
- [ ] GaloisField field type updated
- [ ] Embedded GaloisField class removed
- [ ] Plugin builds without errors
- [ ] All tests pass
- [ ] No warnings introduced
- [ ] Parity calculations verified

## Notes

1. **Method Compatibility**: The SharedRaidUtilities.GaloisField has identical method signatures to most embedded implementations, so most migrations should be straightforward.

2. **Performance**: No performance degradation expected. The shared implementation uses the same precomputed lookup tables.

3. **Thread Safety**: GaloisField is thread-safe. Multiple plugins can share the same instance if needed (though each typically creates its own).

4. **ZfsGaloisField vs GaloisField**: The ZfsRaid plugin originally used `ZfsGaloisField` which was renamed to just `GaloisField` in SharedRaidUtilities. The implementation is identical.

## Reference Implementation

See **DataWarehouse.Plugins.ZfsRaid** for a complete example of a successfully migrated plugin.

Key changes in ZfsRaid:
- Added SharedRaidUtilities project reference
- Changed `ZfsGaloisField` to `GaloisField`
- Removed `GaloisField.cs` file
- Build successful, no behavioral changes

## Estimated Time per Migration

- Simple plugins (straightforward GaloisField usage): 15-30 minutes
- Complex plugins (custom helper methods): 30-60 minutes
