# [STRAT-10] Fix RSI zone boundary — include RSI > 70 as bullish

**Status:** ✅ Complete  
**Category:** Strategy  
**Size:** XS  

## Summary
Replaced the binary RSI zone check with a 3-tier system in both `EmaRsiSignalProvider` and `StrategyExecutionEngine`. Bull: 55–70=20pts, ≥70=12pts, 50–55=8pts. Bear: 30–45=20pts, <30=12pts, 45–50=8pts. RSI=72 no longer scores 0. Three unit tests added.
