# ARCH-04 Consolidate live + backtest Multi-Confluence evaluators

**Status:** Open  
**Category:** Architecture  
**Size:** L  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:98–287`, `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:30–193`

## Problem
Two parallel hand-maintained implementations of the same 9-condition rule set exist. BUG-07, BUG-08, BUG-09, BUG-10, and STRAT-21 all stem from drift between these two files. Backtest results cannot be trusted to predict live behaviour while the evaluators diverge.

## Proposed Fix
Extract a single `IMultiConfluenceEvaluator` (in `Core` or `ML`) that consumes pre-computed indicator arrays + a `MultiConfluenceConfig` record and returns a `MultiConfluenceResult`. Have the live `MultiConfluenceStrategy.Evaluate` build the arrays per-bar and delegate; have `MultiConfluenceSignalProvider.Evaluate` slice arrays out of `StrategyIndicators` and delegate. No condition logic should live in two places.

## Acceptance Criteria
- [ ] Single evaluator used by both live and backtest paths
- [ ] TEST-07 parity suite passes
- [ ] Maximum-Effort row counts and Damian/OIL/5-min P&L unchanged before/after
- [ ] Build passes; all tests pass

## Open Questions
- Is `StrategyIndicators.AllBars` populated identically (volume, timestamps) to what the live engine consumes from `IBarIngestionService`?
