# BUG-16 PumpNDump bracket close detection has no miss tolerance — single API hiccup falsely declares position flat

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:377–438`

## Problem

`DoCheckAsync` detects a closed bracket by checking whether a bracket's TP or SL order ID is
absent from the `GetLiveWorkingOrdersAsync` response in a single 15-second poll (lines 378–382).

A single transient API response — partial order list, rate-limit throttle, network blip,
momentary broker-side delay — causes:
1. The engine to declare the bracket closed.
2. All position state (`_currentQty`, `_averageEntry`, `_brackets`, `_freeRideActive`) to be
   reset immediately.
3. `TradeClosed` to fire, incrementing Win/Loss counters.
4. The FLAT entry path to become active on the very next poll — the engine can re-enter into the
   still-live broker position, doubling size without intent.

`StrategyExecutionEngine` solves the identical problem with `_syncMissCount` and
`SyncMissThreshold = 3` (lines 191–192, 1476–1513): three consecutive misses are required
before a position is declared closed. PumpNDump has no equivalent.

## Proposed Fix

1. Add a `MissCount As Integer = 0` field to the `BracketPair` inner class.
2. Define a module-level constant `BracketMissThreshold As Integer = 3`.
3. In the bracket-close detection loop, when `tpMissing OrElse slMissing` is True:
   - Increment `bracket.MissCount`.
   - Log a warning on the first miss: `"⚠️  Bracket miss {MissCount}/3 — order(s) not visible; will retry."`.
   - Only proceed to orphan-leg cancellation, P&L calc, `bracketsToRemove.Add`, and raising
     `TradeClosed` when `bracket.MissCount >= BracketMissThreshold`.
4. In the `Else` branch (both order IDs present), reset `bracket.MissCount = 0` so a transient
   miss that self-heals does not accumulate toward the threshold.

All existing behaviour after the threshold is reached (orphan cancel, zombie list, P&L, events,
state reset) remains unchanged.

## Acceptance Criteria

- [ ] `BracketPair` has `MissCount As Integer = 0`.
- [ ] Constant `BracketMissThreshold = 3` defined at class level.
- [ ] A single `GetLiveWorkingOrdersAsync` miss does NOT fire `TradeClosed` or reset position state.
- [ ] A miss warning is logged on miss 1 and miss 2 (not on miss 3, which triggers the close).
- [ ] Three consecutive misses DO fire `TradeClosed` and reset state (existing close path unchanged).
- [ ] `MissCount` resets to 0 when both order IDs reappear after a transient miss.
- [ ] Build passes; all tests still pass.
