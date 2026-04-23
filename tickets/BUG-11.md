# BUG-11 MC lc2 missing Single.IsNaN(ema21Now) guard

**Status:** Open  
**Category:** Bugs  
**Size:** XS  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:195–196`

## Problem
`lc2 = (lastClose > CDec(ema21Now))` has no NaN guard. `lc2b` at line 196 correctly guards with `Not Single.IsNaN(ema50Now)`. `CDec(NaN)` throws `OverflowException`. A data gap or cache-stitch boundary could leave EMA21 NaN momentarily.

## Proposed Fix
`lc2 = (Not Single.IsNaN(ema21Now) AndAlso lastClose > CDec(ema21Now))`

## Acceptance Criteria
- [ ] Unit test: `ema21Arr` last element is `Single.NaN` → expect `Side = Nothing`, no exception
- [ ] Build passes; all tests pass
