# [TEST-04] `BarCollectionService` — Staleness and Dedup Tests

**Status:** ✅ Complete  
**Category:** Test Coverage

## Summary
≥ 3 tests: cache hit when bar < 24 hrs old and span ≥ 80%; cache miss when stale; duplicate bars do not double-count. Uses fake repository.

**Files modified:** `src/TopStepTrader.Tests/Backtest/BarCollectionServiceTests.vb`
