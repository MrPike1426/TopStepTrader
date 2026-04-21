# [STRAT-07] Fix Asymmetric StochRSI Short Gate

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** XS  

## Summary
Changed `sc7` in `MultiConfluenceStrategy.vb` from `stochKNow > 0.3F` to `stochKNow < 0.4F` to mirror the symmetric long gate (`lc7: K < 0.7`). Also applied the same fix to `MultiConfluenceSignalProvider.vb` (backtest path, previously `> 0.2F`). Added two unit tests: `MultiConfluence_ShortWithStochKOverbought_ReturnsNothing` (K=0.85 → no signal) and `MultiConfluence_ShortWithStochKTurningFromOverbought_ReturnsSell` (K=0.35 → Sell). Updated existing `AllShortConditions` test StochRsiK from 0.7F to 0.35F. Build: 338 passed, 0 failed.
