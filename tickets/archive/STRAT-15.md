# [STRAT-15] Tune MACD Parameters for Intraday Bars (12,26,9 → 8,17,9)

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`, `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`

## Summary
Changed MACD from default (12,26,9) to intraday-tuned (8,17,9) in `MultiConfluenceStrategy.vb` (live path) and `BacktestEngine.vb` (backtest path, line 745 where `mcMacdHist` is computed). The backtest signal provider uses pre-computed `indicators.MacdHistogram` so only the engine-side computation needed updating. Updated docstrings in both strategy files. 352 tests pass.
