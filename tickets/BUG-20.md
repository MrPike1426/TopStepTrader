# BUG-20 PumpNDump TP tighten and SL trail can produce SL-above-TP price inversion

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:495–503, 872–931`

## Problem

On each 15-second poll `DoCheckAsync` runs two bracket-modification operations in sequence:

1. `TrailStopsAsync` (inside the free-ride block, line 471) — ratchets every bracket's SL
   forward by `_stopLossTicks` ticks behind the current price.
2. `TightenTakeProfitsAsync` (lines 495–503) — shrinks every bracket's TP toward current price
   when the 3-bar average range falls below `_momentumFadeAtrFraction × ATR`.

`TightenTakeProfitsAsync` guards against the new TP being too close to *current price* using
`minSafeDistance = 3 * tick` (line 875). However, it does not guard against the new TP being
at or below (long) / above (short) the **current SL price**.

On a fast momentum move where the SL has trailed aggressively and the momentum fade
tightener fires in the same poll:
- Long example: SL has advanced to price − 4 t. Fade tightener sets TP to price + 3 t.
  Net: SL = price − 4 t, TP = price + 3 t. Valid for now.
- Next poll: SL advances to price − 2 t. Fade tightener fires again: TP moves to price + 3 t
  (no change). Valid still.
- But if price is flat between polls: SL = price − 2 t, TP could be tightened to price + 1 t.
  Now: SL < TP by only 3 t, which is fine...
- However if `_tightenTicksPerBar` is large (e.g. 5) relative to `_stopLossTicks` and current
  price drift, it is possible for the computed `newTpPrice` to end up on the wrong side of or
  at the same level as `b.CurrentSlPrice`.

The existing guard (`If newTpPrice >= b.CurrentTpPrice Then Continue For`) only prevents
loosening; it does not prevent SL/TP inversion. An inverted bracket results in both the SL
and TP being live orders on the same side of the market — the first tick will fill whichever
executes first, potentially closing the position immediately at a loss.

## Proposed Fix

In `TightenTakeProfitsAsync`, after computing `newTpPrice` and before the cancel-replace cycle,
add an inversion guard:

```vb
' Long: TP must stay above SL by at least minSafeDistance
If _tradeSide = OrderSide.Buy AndAlso b.CurrentSlPrice > 0D Then
    If newTpPrice <= b.CurrentSlPrice + minSafeDistance Then Continue For
End If
' Short: TP must stay below SL by at least minSafeDistance
If _tradeSide = OrderSide.Sell AndAlso b.CurrentSlPrice > 0D Then
    If newTpPrice >= b.CurrentSlPrice - minSafeDistance Then Continue For
End If
```

Log a warning when the guard fires:
```
Log($"⚠️  TP tighten skipped — would invert bracket (SL={b.CurrentSlPrice:F2} TP={newTpPrice:F2})")
```

## Acceptance Criteria

- [ ] `TightenTakeProfitsAsync` does not cancel-replace TP when `newTpPrice` is within
  `minSafeDistance` ticks of `b.CurrentSlPrice`.
- [ ] A warning log is emitted when the guard fires.
- [ ] Existing tighten behaviour (moving TP toward price when momentum fades and no inversion
  risk exists) is unchanged.
- [ ] Build passes; all tests still pass.
