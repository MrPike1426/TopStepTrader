# BUG-08 MC StochRSI long threshold mismatch live(0.7) vs backtest(0.8)

**Status:** Open  
**Category:** Bugs  
**Size:** XS  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:203`, `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:102`

## Problem
Live uses `stochKNow < 0.7F`; backtest uses `mcStochK < 0.8F` for the same condition (lc7/lcl7). Backtest accepts long signals the live engine rejects. Same condition number, different threshold — direct backtest→live drift.

## Proposed Fix
Unify to `K < 0.7` (per STRAT-14 agreed semantic). Preferred path: via ARCH-04.

## Acceptance Criteria
- [ ] Parameterised test: feed `stochK = 0.75` → both evaluators must agree on Side
- [ ] Build passes; all tests pass

## Open Questions
- Was 0.8 a transcription error or a deliberate looser backtest gate?
