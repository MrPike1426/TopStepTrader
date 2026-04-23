# BUG-09 MC DI-spread / Chikou-gap / MACD-mag filters absent in backtest

**Status:** Open  
**Category:** Bugs  
**Size:** M  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:177,183,189`, `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:97–101,121`

## Problem
Live applies three chop-rejection filters: `MinDiSpread = 2.0F`, `chikouMinGap = lastClose * 0.001D`, `macdMinMag = ATR * 0.05`. Backtest has none of these. Backtest overstates trade count and likely understates per-trade edge. Maximum-Effort comparisons are biased.

## Proposed Fix
Port all three thresholds into backtest (or via ARCH-04 into the shared evaluator). Make them part of `MultiConfluenceConfig`.

## Acceptance Criteria
- [ ] Re-run Damian/OIL/5-min baseline backtest; trade count drops 10–30%, average edge rises
- [ ] Build passes; all tests pass

## Open Questions
- Are `MinDiSpread`, `chikouMinGap`, `macdMinMag` the right defaults across MGC/MES/MNQ/MCL/M6E? Each instrument has different tick volatility.
