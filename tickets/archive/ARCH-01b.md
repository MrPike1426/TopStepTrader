# [ARCH-01b] Extract `EmaRsiSignalProvider` and `MultiConfluenceSignalProvider`

**Status:** ✅ Complete  
**Category:** Architecture

## Summary
Extracted signal evaluation logic for EmaRsi and MultiConfluence into provider classes. Factory updated. `BacktestEngine.vb` not modified.

**Files created:**
- `src/TopStepTrader.Services/Backtest/Strategies/EmaRsiSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb`
