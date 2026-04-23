# STRAT-20 Clarify/rename min(1.5×ATR, cloud edge) SL selection — picks tighter stop

**Status:** Open  
**Category:** Strategy  
**Size:** S  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:131–141`, `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:255–268`

## Problem
Comment says "min(1.5×ATR, cloud edge)" but the long-side code is `If mcCloudBottom > mcAtrSlLevel Then mcCloudBottom Else mcAtrSlLevel` — it picks the higher (closer-to-entry) SL price, which is the *tighter* stop. When the cloud floor is just inside the ATR band the position gets whipsawed before it can develop.

## Proposed Fix
Either invert the selection (use the wider/safer of the two SLs) or rename + re-document to "tightest viable SL" and add a configurable `MinSlAtrFloor` (e.g. `Max(0.5 × ATR, chosenSl)`). **Requires trader decision before implementation.**

## Acceptance Criteria
- [ ] Backtest before/after on Damian/OIL/5-min and Lewis/Gold/15-min; measure win-rate, average loss, largest-loss tail
- [ ] Chosen behaviour is documented in a comment at the SL selection site
- [ ] Build passes; all tests pass

## Open Questions
- Trader intent: is "tight SL" deliberate (small-risk scalper) or a bug (intent was "wider of the two for safety")?
