# [TEST-03] `BarCollectionService` — Non-Native Timeframe Aggregation Tests

**Status:** ✅ Complete  
**Category:** Test Coverage  
**Size:** S  
**Files:** `src/TopStepTrader.Tests/Backtest/BarCollectionServiceTests.vb`

## Problem
`BarCollectionService` aggregates 5-min bars → 10-min, 1-hr bars → 2-hr/4-hr in memory. The current 5 test facts do not cover aggregation correctness. The timestamp used for aggregated bars (first bar of the window) may not align to UTC midnight boundaries.

## Tests to Add
- 5-min source → 10-min: verify 2 consecutive 5-min bars merge into 1 correct 10-min bar (OHLCV, timestamp = first bar)
- 1-hr source → 2-hr: verify pairing logic, including odd-count source bars
- 1-hr source → 4-hr: verify grouping into 4-bar windows

## Acceptance Criteria
- [ ] ≥ 3 new aggregation tests using synthetic in-memory bar lists
- [ ] Tests assert OHLCV values and timestamps explicitly
- [ ] All tests pass
