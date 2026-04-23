# BUG-19 PumpNDump average entry derived from bar close, not broker-confirmed fill price

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:573–578, 629–631`

## Problem

`PlaceInitialEntryAsync` records the position's entry price as `entryPrice`, which is
`_lastClose` — the close of the most recent 1-minute bar at the time of signal evaluation
(lines 575–577). `ScaleInAsync` does the same using `currentPrice = _lastClose` (line 631).

Market orders on CME Globex for 1-minute momentum scalps frequently slip 2–5+ ticks from the
bar close on which the signal fired, especially because the 3-consecutive-bar signal fires
*after* the momentum move has already occurred. This means:

- `_averageEntry` is wrong for the entire life of the trade.
- The free-ride P&L threshold comparison (`UnrealisedPnl >= _freeRidePnlThreshold`) can
  trigger too early (if actual fill was worse than bar close) or not at all (if fill was
  better but threshold is tight).
- Per-bracket SL P&L estimates on close are inaccurate.
- The unrealised P&L displayed to the user is misleading.

`StrategyExecutionEngine` solves this by correcting `_lastEntryPrice` from
`snapshot.OpenRate` on the first broker sync after entry (lines 1428–1433), using a
one-tick-minimum drift guard before applying the correction.

## Proposed Fix

After each `PlaceOrderAsync` call in `PlaceInitialEntryAsync` and `ScaleInAsync`, poll
`GetLivePositionSnapshotAsync` (up to 3 attempts × 750 ms, mirroring SEE's pattern) to
retrieve the broker-confirmed `OpenRate`. If found and the drift exceeds one tick, correct
`_averageEntry` and `_lastEntryPrice` and log the adjustment:

```
Log($"📌 Fill corrected {_averageEntry:F2} → {snapshot.OpenRate:F2} (Δ={drift:F2})")
```

Recalculate `_averageEntry` after the correction using the updated `_entryPrices` list.

## Acceptance Criteria

- [ ] After initial entry, `_lastEntryPrice` and `_averageEntry` reflect the broker `OpenRate`
  when the fill drifts by more than one tick from `_lastClose`.
- [ ] Same correction applied after each scale-in.
- [ ] Fill correction is logged with `📌` prefix showing before/after values.
- [ ] If the snapshot call fails or returns no position, the bar-close estimate is retained
  (existing fallback).
- [ ] Build passes; all tests still pass.
