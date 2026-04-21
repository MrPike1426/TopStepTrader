# [STRAT-02] Time-of-Day Trading Window Gate

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** M

## Summary

Added nullable `TimeOnly?` properties `TradingWindowUtcStart` / `TradingWindowUtcEnd` to `StrategyDefinition`. Added `IsInsideTradingWindow()` helper to `StrategyExecutionEngine` and inserted the gate in both entry paths (DoCheckAsync non-EmaRsi branch and EvaluateConfidenceActionsAsync EmaRsi branch), each chained after the existing `IsInsideTradingHours()` check. `ApplyMultiConfluenceEngine()` in `HydraViewModel` pre-fills `08:00–17:00 UTC` on the template; `ExecuteStart` copies the window only to the Gold asset (`isGoldAsset` detection via symbol/contractId), leaving all other assets unrestricted. Null window = no restriction preserved for all other strategies.
