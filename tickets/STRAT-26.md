# STRAT-26 Clamp backtest SL to broker minimum ticks (parity with live)

**Status:** Open  
**Category:** Strategy  
**Size:** S  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:189`

## Problem
Backtest sets `StopDelta = Math.Abs(...)` without clamping to broker minimums. The live path applies `TopStepXInstrumentCatalog.ClampStopTicksAsync`. Backtest can fabricate SLs tighter than the exchange minimum — those orders would be rejected or widened live, causing P&L divergence on exactly the bars where MC's tight-SL preference (STRAT-20) bites hardest.

## Proposed Fix
Inject `IInstrumentCatalog` (or a min-stop-ticks lookup) into `BacktestConfiguration` and clamp `StopDelta` after the cloud/ATR pick.

## Acceptance Criteria
- [ ] Backtest with synthetic min-stop = 20 ticks on M6E: trade count and worst-case loss change as expected
- [ ] Build passes; all tests pass
