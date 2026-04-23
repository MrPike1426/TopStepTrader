# [OBS-05] Emit bracket state-transition entries to `DiagnosticLogger`

**Status:** Open  
**Category:** Observability  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:2254, 2296, 2342`

## Problem

`ManagePositionAsync` (1-second loop) transitions the position through `TradePhase.HardStop → FreeRoll → Closing` and raises `TurtleBracketChanged` events. These events drive UI updates but are **not** written to `DiagnosticLogger`. Consequently:

- The post-session export has no record of when the SL was trailed, when Free Roll activated, or at what price the TP was hit
- In the UAT review, the BUG-14 finding (TP silently shrunk from WIDE-tier to `DefaultTpTicks`) would have been immediately visible if the diag log had recorded both the entry TP and the first trailed TP side by side
- Bracket phase transitions cannot be correlated with OHLCV bars or P&L snapshots

The three key transition points are:
1. First trail tick in `TrailHardStopBracketAsync` — SL moves, TP confirmed (or buggy — see BUG-14)
2. Free Roll activation in `ActivateFreeRollAsync` — SL moves to BE+buffer, TP cancelled
3. Position close in `OnPositionClosedAsync` — final SL/TP at close

## Change

At each transition, add a `DiagnosticLogger.Record` call alongside the existing `TurtleBracketChanged` raise:

**1. First trail tick (inside `TrailHardStopBracketAsync`, after `EditPositionSlTpAsync` succeeds):**

```vb
_diagLogger.Record(New DiagEntry With {
    .Timestamp = DateTimeOffset.UtcNow,
    .EventType = "BRACKET_TRAIL",
    .Side = _currentTrendSide.ToString(),
    .Price = currentPrice,
    .StopLoss = newSl,
    .TakeProfit = newTp,
    .StatusNote = $"SL trailed to {newSl:F4}  TP at {newTp:F4}"
})
```

**2. Free Roll activation (inside `ActivateFreeRollAsync`, after SL edit succeeds):**

```vb
_diagLogger.Record(New DiagEntry With {
    .Timestamp = DateTimeOffset.UtcNow,
    .EventType = "FREE_ROLL_ON",
    .Side = _currentTrendSide.ToString(),
    .Price = currentPrice,
    .StopLoss = newSl,
    .StatusNote = $"Free Roll activated — SL moved to BE+buffer {newSl:F4}"
})
```

**3. Position close (inside `OnPositionClosedAsync`):**

```vb
_diagLogger.Record(New DiagEntry With {
    .Timestamp = DateTimeOffset.UtcNow,
    .EventType = "POSITION_CLOSED",
    .Side = _currentTrendSide.ToString(),
    .Price = closePrice,
    .Pnl = realisedPnl,
    .StatusNote = $"Position closed at {closePrice:F4}  P&L ${realisedPnl:F2}"
})
```

Only add `DiagEntry` fields (`StopLoss`, `TakeProfit`, `Pnl`) if they do not already exist in the schema; otherwise map to the nearest existing field.

## Acceptance Criteria

- [ ] Every SL trail tick produces a `BRACKET_TRAIL` diag entry with current SL and TP values
- [ ] Free Roll activation produces a `FREE_ROLL_ON` diag entry with the new BE-buffer SL
- [ ] Position close produces a `POSITION_CLOSED` entry with close price and realised P&L
- [ ] Diag entries appear in the CSV / SQLite export with correct timestamps
- [ ] No duplicate entries — if `TrailHardStopBracketAsync` fires 60 times during a position, exactly 60 `BRACKET_TRAIL` rows appear (one per second tick that moves the SL)
- [ ] When no position is open, no bracket diag entries are emitted
- [ ] Build passes; all tests still pass
