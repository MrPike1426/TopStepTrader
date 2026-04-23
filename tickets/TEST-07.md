# TEST-07 Live ↔ backtest MC evaluator parity tests

**Status:** Open  
**Category:** Tests  
**Size:** M  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Tests/Strategies/MultiConfluenceParityTests.vb` (new file)

## Problem
Without parity tests, BUG-07, BUG-08, BUG-09, BUG-10, BUG-11, and STRAT-21 will silently re-emerge after ARCH-04 and future changes.

## Proposed Fix
Build a fixture that fabricates `IList(Of Decimal)` price/volume series, derives `StrategyIndicators` for the backtest path, and asserts both evaluators return identical `Side`, `IsPartialSignal`, `Confidence` (within float tolerance), and SL/TP deltas. Cover at minimum: 9/9 long, 9/9 short, 8/9 with each hard-gate failing, NaN warm-up, `volAvg = 0`, `stochK = 0.75`, `DI spread = 1`.

## Acceptance Criteria
- [ ] All parity test cases pass against the unified evaluator (ARCH-04)
- [ ] Build passes; all tests pass
