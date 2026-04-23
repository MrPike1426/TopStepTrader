# BUG-07 Live partial-signal (8/9) missing hard-gate guard

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:259–269`, `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:147–164`

## Problem
Backtest only fires `IsPartialSignal` when `longHits = 8 AndAlso lcl1 AndAlso lcl4 AndAlso lcl8` (cloud direction, Chikou-vs-cloud, volume) — and for shorts additionally requires `scl7` (StochRSI gate). Live fires `result.IsPartialSignal = True` whenever `longCount = 8` / `shortCount = 8` with no hard-gate check. Live therefore takes partial-conviction half-size trades the backtest never validated — direct backtest→live drift on the most permissive entry.

## Proposed Fix
Lift the hard-gate guard from `MultiConfluenceSignalProvider.vb:151,157` into the live `MultiConfluenceStrategy` partial branches. Preferred path: via ARCH-04.

## Acceptance Criteria
- [ ] Unit test: fabricate 8/9 with cloud condition false → expect `Side = Nothing`
- [ ] Maximum-Effort sweep before/after: live partial entries per session drop visibly in `DiagnosticLogger`
- [ ] Build passes; all tests pass

## Open Questions
- Confirm STRAT-16 intent was to ship hard-gates in both live and backtest paths. Why did it land only in backtest?
