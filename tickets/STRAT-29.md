# STRAT-29 PumpNDump free-ride drops effective heat to zero — scale-in cap bypass after activation

**Status:** Open  
**Category:** Strategy  
**Size:** S  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:462–493, 935–946`

## Problem

`ApplyFreeRideAsync` moves all bracket SLs to `_averageEntry` (breakeven). After this runs,
`CalculateCurrentHeat()` returns near-zero because heat is computed as the sum of
`(entryPrice − currentSlPrice) × qty` across all brackets (lines 938–944), and all SLs are
now at `_averageEntry ≈ entryPrice`.

On the next poll, the scale-in check fires (lines 474–493). The heat guard at line 595
(`CalculateCurrentHeat() + newLotHeat > _maxRiskHeatTicks`) now sees near-zero accumulated
heat, so any new scale-in lot passes the check regardless of how many contracts are already
open or how extended the price is.

The intended purpose of `_maxRiskHeatTicks` is to cap total portfolio heat-at-risk. Free-ride
moves SLs to breakeven, not off the books — the open contracts still represent real exposure
if price reverses sharply through entry. The heat guard should reflect the *behavioural* risk
(how far price must move against us to close the position), not just the *dollar* distance of
the current SL from entry.

Specifically: after free-ride, the remaining risk is that price reverses through the average
entry back to wherever the trailing SL ends up. The scale-in logic should use
`_stopLossTicks * newLotQty` as the incremental heat for any new scale-in lot, and compare
that against a post-free-ride sub-limit rather than the full `_maxRiskHeatTicks`.

## Proposed Fix

After `_freeRideActive` becomes True, block further scale-ins entirely unless there is a
configurable `AllowScaleInAfterFreeRide` flag (default `False`). This is the safest default —
once the trade is at breakeven-or-better, the natural management mode is to trail the existing
size to the exit, not to add more contracts.

In `DoCheckAsync`, before the `ScaleInAsync` call (line 486):

```vb
If _freeRideActive Then
    Log($"⏸  Scale-in suppressed — free-ride is active (position at BE, no new heat)")
    ' skip scale-in entirely
Else
    ' existing priceMovedEnough check + ScaleInAsync call
End If
```

Alternatively, if the ability to scale in after free-ride is intentional, document that intent
explicitly and add a log line that makes it obvious:
```
Log($"📈 Scale-in post-free-ride — heat guard bypassed (SLs at BE; new lot heat only)")
```
and compute heat as `newLotHeat` alone (not `CalculateCurrentHeat() + newLotHeat`).

The decision between the two approaches is a strategy design choice; the current silent
bypass is the defect regardless of which approach is chosen.

## Acceptance Criteria

- [ ] After `_freeRideActive = True`, `ScaleInAsync` is either blocked with a log message
  OR the heat calculation explicitly accounts for only the new lot's heat with a log that
  makes the intent clear.
- [ ] `CalculateCurrentHeat()` is NOT used as the heat accumulator after free-ride (it
  returns near-zero and defeats the cap).
- [ ] Existing scale-in behaviour before free-ride activation is unchanged.
- [ ] Build passes; all tests still pass.
