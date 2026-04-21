# [STRAT-14] Correct StochRSI Short Gate Semantics in MultiConfluence

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** XS  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`, `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb`

## Summary
Changed `sc7` in both the live strategy and backtest signal provider from `K < 0.4F` to `K > 0.3F AND K < K[prev]`. This correctly implements "not oversold AND momentum falling" — symmetric with the long gate `K < 0.7` (not overbought). Added `stochKPrev` via `PreviousValid()` in the live strategy and `indicators.StochRsiK(barIndex - 1)` in the signal provider. Updated two existing short tests to use a falling K (prev=0.55, now=0.45). Added 2 new tests: sc7 fails when K is rising; sc7 fails when K ≤ 0.3. 352 tests pass.
