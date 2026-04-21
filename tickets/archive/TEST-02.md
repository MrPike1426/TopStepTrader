# [TEST-02] Scale-In Multi-Leg Exit Tests

**Status:** ✅ Complete  
**Category:** Test Coverage  
**Size:** S  
**Files:** `src/TopStepTrader.Tests/Backtest/BacktestMetricsTests.vb`

## Problem
Scale-in creates multiple `BacktestTrade` legs in `openLegs`. When exit fires, all legs receive the same `exitPrice`. There are no tests confirming that:
- P&L is summed correctly across legs with different entry prices
- A 2-leg position hitting SL produces the correct aggregate loss
- `PositionGroupId` is consistent across all legs of the same group

## Acceptance Criteria
- [ ] ≥ 3 new tests for 2-leg scale-in scenarios (profitable, losing, break-even)
- [ ] Tests verify aggregate P&L and individual leg P&L
- [ ] All tests pass
