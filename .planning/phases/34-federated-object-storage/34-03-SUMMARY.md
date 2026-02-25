# Phase 34-03: Permission-Aware Routing (FOS-03) - COMPLETE

**Phase**: 34 - Federated Object Storage
**Plan**: 03 - Permission-Aware Routing
**Wave**: 2
**Status**: ✅ Complete
**Date**: 2026-02-17

---

## Overview

Implemented permission-aware routing layer that integrates access control checks into the federated storage routing pipeline. The router enforces deny-early pattern: requests are checked against UltimateAccessControl before reaching storage nodes, with cached permissions reducing message bus overhead.

---

## Deliverables

### 1. Permission Cache Interface and Models

**Files Created:**
- `DataWarehouse.SDK/Federation/Authorization/IPermissionCache.cs` (141 lines)
- `DataWarehouse.SDK/Federation/Authorization/PermissionCheckResult.cs` (90 lines)
- `DataWarehouse.SDK/Federation/Authorization/PermissionCacheEntry.cs` (73 lines)

**Key Features:**
- `IPermissionCache` interface with TryGet, Set, Invalidate, GetStatistics operations
- `PermissionCheckResult` models ACL outcomes with cache metadata (FromCache, CacheAge)
- `PermissionCacheEntry` internal storage model with TTL and expiration tracking
- `PermissionCacheStatistics` for observability (hit rate, entry count)

### 2. In-Memory Permission Cache

**File Created:**
- `DataWarehouse.SDK/Federation/Authorization/InMemoryPermissionCache.cs` (227 lines)

**Key Features:**
- Thread-safe storage using `ConcurrentDictionary<string, PermissionCacheEntry>`
- Bounded cache: max 10,000 entries (configurable), evicts oldest when full
- Background cleanup: `PeriodicTimer` runs every 1 minute to prune expired entries
- Atomic statistics tracking with `Interlocked` counters (TotalRequests, CacheHits, CacheMisses)
- IDisposable implementation for cleanup task lifecycle

**Configuration:**
- `PermissionCacheConfiguration`: MaxEntries (default 10K), DefaultTtl (default 5 minutes)

### 3. Permission-Aware Router

**File Created:**
- `DataWarehouse.SDK/Federation/Authorization/PermissionAwareRouter.cs` (327 lines)

**Key Features:**
- Decorator pattern over `IStorageRouter` (_innerRouter field)
- Pre-flight ACL checks before routing to storage nodes
- Cache-first lookup: TryGet before message bus round-trip
- Message bus integration:
  - Topic: `accesscontrol.check`
  - Request payload: `{ userId, resource, operation, metadata }`
  - Response expected: `{ allowed: bool, reason: string }`
  - Uses `IMessageBus.SendAsync` for synchronous request-response
- Fail-secure: exceptions during ACL check default to DENY
- Permission denials logged to `logging.security.denied` topic
- GetCacheStatistics() method for observability

**Configuration:**
- `PermissionRouterConfiguration`: RequireUserId (default false), CacheTtl (default 5 minutes)

---

## Architecture

### Deny-Early Pattern

```
Request → PermissionAwareRouter
  ├─ Check if UserId present
  │   ├─ No UserId + RequireUserId=true → DENY (403)
  │   └─ No UserId + RequireUserId=false → route to inner router (skip ACL)
  ├─ Cache lookup (IPermissionCache.TryGet)
  │   ├─ Cache HIT → use cached result
  │   └─ Cache MISS → call UltimateAccessControl via message bus
  ├─ Permission DENIED → log to logging.security.denied, return 403
  └─ Permission GRANTED → delegate to inner router
```

### Decorator Chain

```
LocationAwareRouter (FOS-04)
  → PermissionAwareRouter (FOS-03)
    → DualHeadRouter (FOS-01)
      → ObjectPipeline / FilePathPipeline
```

Routers can be composed in any order. Typical composition:
1. Permission check (security enforcement)
2. Location selection (performance optimization)
3. Language routing (dual-head dispatch)

---

## Performance

### Cache Hit Rate
- **Target**: >95% for repeated access patterns
- **Actual**: Depends on workload; same user accessing same resources repeatedly will see ~99% hit rate

### Cache Eviction
- **Bound**: 10,000 entries
- **Policy**: Evict oldest entry (by CachedAt timestamp) when full
- **Expiration**: Background cleanup every 1 minute

### Message Bus Overhead
- **Cache HIT**: Zero message bus calls
- **Cache MISS**: 1 round-trip to UltimateAccessControl (`accesscontrol.check`)
- **Cache TTL**: 5 minutes (configurable)

---

## Security

### Fail-Secure Default
If ACL check fails (message bus timeout, UltimateAccessControl unavailable, exception), permission is **DENIED**. This prevents authorization bypass via error injection.

### Denial Logging
All permission denials are logged to `logging.security.denied` with:
- userId
- resource (storage address)
- operation (read, write, delete, etc.)
- reason (why denied)
- fromCache (whether result came from cache)
- timestampUtc

### Cache Invalidation
Cache can be invalidated by:
- **User ID**: Clear all permissions for a specific user (e.g., user revoked)
- **Resource Key**: Clear all permissions for a specific resource (e.g., ACL updated)
- **Both**: Clear specific user+resource tuple
- **Neither (null, null)**: Clear entire cache

---

## Testing Checklist

- [x] All 5 files created under `DataWarehouse.SDK/Federation/Authorization/`
- [x] Zero new compilation errors
- [x] IPermissionCache interface defines TryGet, Set, Invalidate, GetStatistics
- [x] InMemoryPermissionCache implements bounded cache (max 10,000 entries)
- [x] PermissionAwareRouter implements IStorageRouter
- [x] Message bus integration uses `accesscontrol.check` topic
- [x] Cache statistics track hit rate
- [x] All async methods accept and propagate CancellationToken
- [x] All types have comprehensive XML documentation
- [x] Decorator pattern: _innerRouter field present
- [x] Cache-first lookup: _cache.TryGet used before message bus call
- [x] Thread-safe storage: ConcurrentDictionary used
- [x] Bounded behavior: MaxEntries enforced with eviction

---

## Integration Notes

### Message Bus Contract

**Request to UltimateAccessControl:**
```json
{
  "userId": "alice@example.com",
  "resource": "bucket/path/to/object",
  "operation": "read",
  "metadata": { "source": "api", "ip": "192.168.1.1" }
}
```

**Expected Response:**
```json
{
  "allowed": true,
  "reason": "User alice@example.com has read access to bucket/path/to/object"
}
```

If `response.Success` is false or `allowed` field is missing/false, permission is DENIED.

### Payload Parsing

PermissionAwareRouter includes helper methods to extract fields from dynamic payloads:
- `TryExtractBool`: Handles Dictionary, anonymous types, JsonElement
- `TryExtractString`: Same flexibility for string fields

This allows UltimateAccessControl to return responses as:
- Anonymous objects
- `Dictionary<string, object>`
- JSON via `System.Text.Json.JsonElement`

---

## Dependencies

**Zero new NuGet packages.** Uses existing SDK dependencies:
- `System.Collections.Concurrent` (ConcurrentDictionary)
- `System.Threading` (PeriodicTimer, SemaphoreSlim, Interlocked)
- `System.Text.Json` (JsonElement for payload parsing)

---

## Known Limitations

1. **In-Memory Only**: Cache is not distributed. Each router instance has its own cache. For multi-node deployments, consider Redis-backed implementation of IPermissionCache.

2. **No Cache Warming**: Cache starts cold. First N requests will experience cache misses and message bus latency.

3. **No Negative Cache Tuning**: Both "granted" and "denied" results use the same TTL. Some systems benefit from shorter TTL for denials (faster propagation of permission grants).

4. **No Metrics Export**: Cache statistics are retrievable via GetCacheStatistics() but not automatically exported to metrics systems. Consider integrating with UniversalObservability plugin.

---

## Future Enhancements

1. **Distributed Cache**: Implement IPermissionCache backed by Redis/Valkey for shared cache across router instances
2. **Metrics Integration**: Export cache hit rate, eviction count to Prometheus/OpenTelemetry
3. **Cache Warming**: Pre-populate cache with frequently accessed permissions at startup
4. **Adaptive TTL**: Shorter TTL for denials, longer TTL for grants
5. **Bulk Invalidation**: Invalidate by pattern (e.g., all objects in bucket X)

---

## Verification

```bash
# Compile SDK
dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj

# Check for Authorization files
find DataWarehouse.SDK/Federation/Authorization -type f -name "*.cs"

# Verify decorator pattern
grep -r "IStorageRouter.*_innerRouter" DataWarehouse.SDK/Federation/Authorization/

# Verify message bus integration
grep -r "accesscontrol.check" DataWarehouse.SDK/Federation/Authorization/

# Verify cache usage
grep -r "_cache.TryGet" DataWarehouse.SDK/Federation/Authorization/

# Verify thread safety
grep -r "ConcurrentDictionary" DataWarehouse.SDK/Federation/Authorization/

# Verify bounded behavior
grep -r "MaxEntries" DataWarehouse.SDK/Federation/Authorization/
```

**Result**: ✅ All checks pass, zero new compilation errors.

---

## Phase Completion

Phase 34-03 is **COMPLETE**. All requirements from `34-03-PLAN.md` have been met:
- ✅ Permission cache interface and entry types
- ✅ In-memory cache with bounded size and automatic expiration
- ✅ Permission-aware router with deny-early pattern
- ✅ Message bus integration with UltimateAccessControl
- ✅ Cache-first lookup with >95% hit rate potential
- ✅ All permission denials logged
- ✅ Zero new NuGet dependencies
- ✅ Zero new build errors

**Ready for**:
- Phase 34-04 (Location-Aware Routing) ✅ ALSO COMPLETE
- Integration with UltimateAccessControl plugin (requires `accesscontrol.check` message handler)
- Production deployment with monitoring of cache hit rates
