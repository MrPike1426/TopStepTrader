# STRAT-21 Volume gate fails opposite ways — fail-open in backtest, fail-closed in live

**Status:** Open  
**Category:** Strategy  
**Size:** S  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:106–110`, `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:206–207`

## Problem
Backtest: `mcVolMa = 0D OrElse mcCurVol >= mcVolMa * 1.2D` — gate **passes** when volume data is missing. Live: `lc8 = (volAvg > 0D AndAlso volumes(n - 1) >= volAvg * 1.2D)` — gate **fails** when volume data is missing. Yahoo Finance frequently returns 0 volume for futures intraday bars (MES/MNQ/M6E micros). Live rejects all signals; backtest happily fires. Largest single source of live-vs-backtest divergence after BUG-07/08/09.

## Proposed Fix
Pick one semantic (recommend fail-closed = live behaviour; a zero-volume bar is suspect). Unify via ARCH-04. If volume data is structurally unreliable, resolve via STRAT-22 first.

## Acceptance Criteria
- [ ] Both evaluators return identical Side when injected with `volAvg = 0`
- [ ] Build passes; all tests pass
