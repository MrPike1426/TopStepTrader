# [BUG-01] Replace 11 Pending-State Locals with a `PendingEntry` Record

**Status:** ✅ Complete  
**Category:** Bugs

## Summary
`PendingEntry` class defined and used in `BacktestEngine`. All 15 pending-state local variables removed. All 286 tests passed.

**Files modified:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`
