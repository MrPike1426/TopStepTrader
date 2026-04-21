# [STRAT-13] Make RSI scoring use configurable period (not hardcoded 14)

**Status:** ✅ Complete  
**Category:** Strategy  
**Size:** S  

## Summary
Added `IndicatorPeriod As Integer = 14` to `BacktestConfiguration`. `BacktestEngine` now has a `Case EmaRsiWeightedScore` block that computes `RSI(allCloses, config.IndicatorPeriod)` (reuses `rsi14Series` when period=14 for zero overhead). `StrategyExecutionEngine` uses `_strategy.IndicatorPeriod` for the live RSI computation. Default remains 14 — no behaviour change for existing users. One unit test confirms RSI(9) and RSI(14) diverge.
