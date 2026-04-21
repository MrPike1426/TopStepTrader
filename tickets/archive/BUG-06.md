# [BUG-06] `DonchianBreakout` Exit De-bounce

**Status:** ✅ Complete  
**Category:** Bugs

## Summary
After a Donchian mid-cross exit, no new Donchian entry allowed for 3 bars. Test with synthetic oscillating-around-mid series verifies trade count does not grow unboundedly.

**Files modified:** `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`
