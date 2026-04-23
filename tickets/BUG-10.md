# BUG-10 MC live Confidence computed when Side = Nothing

**Status:** Open  
**Category:** Bugs  
**Size:** XS  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:271–284`

## Problem
`result.Confidence` is set unconditionally. The XML doc on line 43–44 promises "0 when no signal fires". UI confidence indicators (Hydra/AssetBassett tiles, `ConfidenceUpdated` event) may show non-zero values on no-signal bars, undermining the documented contract and any downstream confidence-threshold gating.

## Proposed Fix
Guard the entire confidence block: `If result.Side IsNot Nothing Then … End If`.

## Acceptance Criteria
- [ ] Unit test: call `Evaluate` on a no-signal bar → assert `result.Confidence = 0F`
- [ ] Build passes; all tests pass

## Open Questions
- Does any consumer rely on non-zero confidence for a "near-miss" UI indicator?
