# [STRAT-12] Replace single-bar EMA21 momentum check with 3-bar slope

**Status:** ✅ Complete  
**Category:** Strategy  
**Size:** XS  

## Summary
Signal 5 ("EMA21 momentum") now uses a 3-bar slope check (slope > 0.03% for bull, < −0.03% for bear) in both `EmaRsiSignalProvider` and `StrategyExecutionEngine`. Bounds guard prevents out-of-range access when barIndex < 3. Two unit tests added.
