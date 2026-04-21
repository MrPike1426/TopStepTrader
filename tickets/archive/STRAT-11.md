# [STRAT-11] Add volume confirmation gate to EMA/RSI strategy

**Status:** ✅ Complete  
**Category:** Strategy  
**Size:** S  

## Summary
Added Signal 7 (+10 pts) to both bull and bear scoring in `EmaRsiSignalProvider` and `StrategyExecutionEngine`. Fires when bar volume > 20-bar average × 1.1. Zero/absent volume is gracefully skipped (no penalty). `VolumeRatio As Single` added to `ConfidenceUpdatedEventArgs` for UI telemetry. Two unit tests added.
