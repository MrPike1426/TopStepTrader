# [ARCH-01e] Gut `BacktestEngine` Main Loop

**Status:** ✅ Complete  
**Category:** Architecture

## Summary
Removed the ~900-line If/ElseIf strategy chain from `RunBacktestAsync`. Replaced with `provider.Evaluate()` call. `BacktestEngine.RunBacktestAsync` reduced to ~525 lines. `BuildIndicators` helper extracted. All 281 tests passed.

**Files modified:**
- `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`
