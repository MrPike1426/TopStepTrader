# [BUG-04] Add `TickSize > 0` Guard in `CalculatePnL` and `MaxScaleIns ≥ 0` Guard

**Status:** ✅ Complete  
**Category:** Bugs

## Summary
`CalculatePnL` throws `InvalidOperationException` (not `DivideByZeroException`) when `TickSize = 0`. `BacktestConfiguration` rejects negative `MaxScaleIns`. 6 new test facts added. All 292 tests passed.

**Files modified:** `src/TopStepTrader.Services/Backtest/BacktestMetrics.vb`, `Core/Interfaces/IBacktestService.vb`
