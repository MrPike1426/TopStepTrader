# [BUG-05] NaN Propagation Guard Before Every Indicator Access in `BacktestEngine`

**Status:** ✅ Complete  
**Category:** Bugs

## Summary
Every strategy branch in `RunBacktestAsync` has a NaN guard at entry that skips the bar if any required indicator is NaN. All 298 tests passed.

**Files modified:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`
