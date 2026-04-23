# BUG-21 PumpNDump scale-in race condition — OnOrderFilled and timer can both enter DoCheckAsync concurrently

**Status:** Open  
**Category:** Bugs  
**Size:** M  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:243–313, 474–493, 586–636`

## Problem

`DoCheckAsync` is the single entry point for both the 15-second timer callback
(`TimerCallback`, line 294) and the order-filled event handler (`OnOrderFilled`, line 243).
Both paths are guarded by `Interlocked.CompareExchange(_callbackRunning, 1, 0)`.

However, `OnOrderFilled` first performs the cancel of the orphaned surviving bracket leg
(lines 257–276) *before* acquiring `_callbackRunning` (line 279). This cancel-then-acquire
sequence means:

1. Timer fires at T=0. `_callbackRunning` → 1. Enters `DoCheckAsync`.
2. A TP fill event arrives at T=0+ε. `OnOrderFilled` runs: attempts to cancel the SL (async),
   then tries to acquire `_callbackRunning` — it is already 1, so the inner `DoCheckAsync`
   is skipped. **This is fine.**

But consider the inverse:
1. Timer fires at T=0. `_callbackRunning` → 1.
2. `DoCheckAsync` has not yet reached the bracket-close detection block.
3. A fill event arrives. `OnOrderFilled` cancel runs (async, no guard). The inner
   `CompareExchange` correctly blocks the second `DoCheckAsync`.
4. Timer's `DoCheckAsync` now reaches the bracket-close block. Both TP and SL order IDs are
   missing (TP filled, SL cancel succeeded). It processes the close and resets `_currentQty = 0`.
5. Timer completes; `_callbackRunning` → 0.
6. OnOrderFilled's Task.Run re-acquires `_callbackRunning` and calls `DoCheckAsync` again.
   `_currentQty = 0`, so the FLAT entry path runs. The 3-bar condition is likely still True
   (same bars). A new entry order is placed immediately after the just-closed trade.

The consequence is an unintended immediate re-entry into the same signal on every TP fill, with
no cooldown between close and re-entry.

Additionally, `ScaleInAsync` increments `_currentQty` (line 628) and then calls
`PlaceBracketAsync` (line 635) which adds to `_brackets`. If two DoCheckAsync calls were
to run simultaneously (which the `_callbackRunning` guard is supposed to prevent but the
async gaps create windows for), `_currentQty` could exceed `_targetTotalSize` with orphaned
brackets.

## Proposed Fix

1. **Add a minimum re-entry cooldown after any close event.** After the bracket-close block
   resets state (lines 448–455), record a `_lastPositionClosedAt = DateTimeOffset.UtcNow`
   timestamp (add this field alongside `_freeRideActive`). At the top of the FLAT entry
   section (line 508), guard:

   ```vb
   Dim cooldownSecs = 30  ' one poll interval minimum
   If (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds < cooldownSecs Then
       Log($"⏸  Re-entry cooldown ({cooldownSecs}s) — skipping entry signal")
       Return
   End If
   ```

2. **Remove the re-triggering DoCheckAsync call from inside OnOrderFilled** (lines 279–287).
   The next 15-second timer tick will naturally pick up the new state. The fill-event's only
   responsibility should be to cancel the orphaned bracket leg and add to the zombie list if
   needed — not to trigger a full DoCheckAsync immediately.

## Acceptance Criteria

- [ ] After a TP or SL fill, a new entry is NOT placed within the same or the immediately
  following 15-second poll.
- [ ] `_lastPositionClosedAt` is set whenever `bracketsToRemove` is processed (TP, SL,
  or emergency close).
- [ ] `OnOrderFilled` no longer calls `DoCheckAsync` internally (leg cancellation and zombie
  handling are retained).
- [ ] Normal scale-in behaviour (price extends, scale-in fires) is unaffected.
- [ ] Build passes; all tests still pass.
