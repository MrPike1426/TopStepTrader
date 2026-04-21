# [QUAL-01] Extract Duplicated Long/Short Exit Check to Helper Function

**Status:** ✅ Complete  
**Category:** Code Quality

## Summary
`CheckFixedExit` extracted to `BacktestMetrics`. Duplicate exit-check blocks in `BacktestEngine` replaced with single callsite. Unit tests added for all 5 paths (buy SL, buy TP, sell SL, sell TP, no-exit).

**Files modified:** `src/TopStepTrader.Services/Backtest/BacktestMetrics.vb`, `BacktestEngine.vb`
