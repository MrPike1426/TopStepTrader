# [ARCH-02a] Extract `BacktestRunViewModel`

**Status:** ✅ Complete  
**Category:** Architecture

## Summary
All Tab 1 logic (contract selection, date range, strategy selector, capital/SL/TP inputs, persona selector, `ExecuteRun`, elapsed timer) moved to `BacktestRunViewModel`. Shell `BacktestViewModel` delegates.

**Files created/modified:**
- `src/TopStepTrader.UI/ViewModels/BacktestRunViewModel.vb` (created)
- `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb` (modified)
