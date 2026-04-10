# While You're Up - Performance Fix

A performance-fixed fork of [While You're Up (1.6 patch)](https://github.com/cslaneyflett/zsbk.patch16.whileyoureup), originally by [CodeOptimist](https://github.com/CodeOptimist/rimworld-while-youre-up).

## The Problem

While You're Up is a great hauling mod, but it has critical performance bugs that cost **~20ms per tick** on modded games. The root cause: a storage cell cache that gets wiped and rebuilt from scratch dozens of times per tick, forcing an expensive search through every storage zone on the map for every item in every pawn's inventory.

On a 576-mod milsim load order, this was the **#1 most expensive mod** identified by [ModScope: Profiler](https://github.com/FluxxField/rimworld-modscope-profiler).

## Performance Fixes

### Cache Invalidation Bugs (4 fixes)

The mod maintains a `defHauls` cache mapping ThingDefs to storage cells. This cache should persist across ticks while a pawn is unloading, but four separate code paths were clearing it unnecessarily:

1. **`SetOrAddDetour`** — didn't early-return when the detour type was already `Puah`, causing `Deactivate()` to clear the cache on every `FirstUnloadableThing` call
2. **`WorkGiver_Scanner.HasJobOnThing` postfix** — unconditionally called `Deactivate()` on every work scan (runs 1,000+ times per tick across all pawns)
3. **`Pawn_JobTracker.ClearQueuedJobs` postfix** — unconditionally called `Deactivate()` ~17 times per tick during normal job transitions
4. **`SetOrAddDetour` Puah-family transitions** — switching between `Puah`, `PuahOpportunity`, and `PuahBeforeCarry` states called `Deactivate()` even though the cache is valid across all Puah sub-states

### Global Shared Storage Cache

Added a `GlobalStorageCache` — a shared `Dictionary<ThingDef, IntVec3>` across all pawns. When one colonist finds where steel should be stored, all other colonists reuse the answer instantly instead of each independently searching the entire map.

Three-layer lookup:
1. **Per-pawn detour cache** — persists across ticks for the same unload job
2. **Global shared cache** — persists until storage zones change
3. **Full storage search** — only runs on global cache miss (rare)

Cache invalidation: automatically clears when storage zones change (`Notify_SlotGroupChanged`) or storage priorities change (`set_Priority`).

### Reflection Elimination

Replaced 5 per-tick `Traverse` reflection calls with cached `FieldInfo`/`PropertyInfo`/`MethodInfo` lookups resolved once at startup:

- `CompHauledToInventory.GetHashSet` — called every `FirstUnloadableThing`
- `StorageSettings.HaulDestinationOwner` — called during priority cleanup
- `WorkGiver_ConstructDeliverResources.resourcesAvailable` — called during supply hauling
- `Pawn_JobTracker.pawn` — called during opportunistic job detection
- `CompHauledToInventory.takenToInventory` — called during inventory checks

### Linq Optimizations

- Eliminated duplicate Linq pass over inventory (two `GetDefHaul` passes → one materialized pass)
- Replaced `Linq.Intersect().Contains()` with direct `Contains()` check
- Replaced `.Any()` with `.Count == 0`

## Results

| Metric | Before | After |
|--------|--------|-------|
| `FirstUnloadableThing` cost | ~20ms/tick | <0.5ms/tick |
| Storage searches per tick | Hundreds (cache rebuilt every call) | Near zero (global cache hit) |
| Reflection calls per tick | 5 Traverse lookups | 0 (cached at startup) |
| Linq allocations per tick | Multiple iterators | Single materialized list |

## Installation

**Use this mod INSTEAD of the original "While You're Up (1.6 patch)", not alongside it.**

1. Unsubscribe/disable the original "While You're Up (1.6 patch)"
2. Subscribe to this mod
3. Place anywhere in your mod list after Harmony

Compatible with Pick Up And Haul (PUAH). All original features are preserved — only performance is changed.

## How This Was Found

These bugs were identified through runtime profiling on a 576-mod load order. The profiler showed While You're Up as the #1 most expensive mod at 9.43ms/tick, with `FirstUnloadableThing` accounting for 95% of the cost. Source code analysis revealed the cache invalidation chain, and four iterations of profiler-guided fixes reduced it to <0.5ms.

## Requirements

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) (required)
- RimWorld 1.6
- [Pick Up And Haul](https://steamcommunity.com/sharedfiles/filedetails/?id=1279012058) (optional, but recommended — enables PUAH+ features)

## License

AGPL-3.0 (inherited from original)

## Credits

- **CodeOptimist** — original While You're Up mod
- **cslaneyflett** — 1.6 port
- **FluxxField** — performance fixes
