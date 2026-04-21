# [ARCH-01d] Extract QuantLab Signal Providers

**Status:** ✅ Complete  
**Category:** Architecture

## Summary
Extracted ConnorsRsi2, SuperTrend, DonchianBreakout, BbRsiReversion into provider classes. Factory now resolves all 11 strategies.

**Files created:**
- `src/TopStepTrader.Services/Backtest/Strategies/ConnorsRsi2SignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/SuperTrendSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/DonchianBreakoutSignalProvider.vb`
- `src/TopStepTrader.Services/Backtest/Strategies/BbRsiReversionSignalProvider.vb`
