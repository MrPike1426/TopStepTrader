# [BUG-14] `TrailHardStopBracketAsync` resets TP to `DefaultTpTicks`, silently discarding the WIDE-tier TP

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:2268-2300`

## Problem

`TrailHardStopBracketAsync` is the 1-second trail function that runs while a position is in `TradePhase.HardStop`. It correctly trails the SL using the same tick distance as at entry (`_initialSlTicks`). However, it calculates the **TP** using a completely different source:

```vb
' StrategyExecutionEngine.vb:2271
Dim tpTicks = Math.Max(1, _pxSettings.DefaultTpTicks)   ' ← appsettings.json default
```

`DefaultTpTicks` is a fixed config value — probably something like 10 or 20 ticks. For a **WIDE-tier** entry the OCO bracket was placed at `_initialTpTicks` (which was computed as `5.0 × ATR / tickSize` at entry — potentially 60–120 ticks for OIL). On the **very first profitable trail tick**, `TrailHardStopBracketAsync` calls `EditPositionSlTpAsync` with both the new SL and the new TP. The new TP is `DefaultTpTicks` from current price — far tighter than the intended WIDE target. The broker's native OCO TP is silently overwritten and shrunk.

Consequences observed in UAT:
- Position closed at ~$57 — unexpectedly small result for a WIDE 5.0×ATR TP bracket
- No `TurtleBracketChanged` advance events logged because the position may have hit the shrunken TP before the trail logic had a chance to fire
- The TP field in `_lastTpPrice` is also updated to the wrong value, making Free Roll activation distance calculations inaccurate

Compare the SL side which correctly uses `_initialSlTicks` (line 2270):

```vb
Dim slTicks = If(_initialSlTicks > 0, _initialSlTicks, Math.Max(1, _pxSettings.DefaultSlTicks))
```

The TP side must follow exactly the same pattern using `_initialTpTicks`.

## Change

Replace line 2271 with:

```vb
' BUG-14: use the tier-correct TP tick distance stored at entry, not the config default
Dim tpTicks = If(_initialTpTicks > 0, _initialTpTicks, Math.Max(1, _pxSettings.DefaultTpTicks))
```

`_initialTpTicks` is set in `PlaceBracketOrdersAsync` at line 1864:

```vb
_initialTpTicks = initialTpTicks
```

It is also set in the deferred bracket-init path for reattached orphan positions (line 1661). It is zeroed by `ResetTrailState` on position close. The field is already present and correctly maintained — it just was not used in `TrailHardStopBracketAsync`.

## Acceptance Criteria

- [ ] `TrailHardStopBracketAsync` uses `_initialTpTicks` (falling back to `DefaultTpTicks` when 0) for the TP leg
- [ ] The first trail tick for a WIDE-tier entry does not shrink the TP from the original 5.0×ATR distance
- [ ] `_lastTpPrice` after the first trail tick correctly reflects `entry ± initialTpTicks × tickSize` relative to the new trailed price, not the config default
- [ ] `TurtleBracketChanged` events emitted by `TrailHardStopBracketAsync` carry the correct TP value
- [ ] Add a unit test: create a WIDE bracket entry (`_initialTpTicks = 80`, `DefaultTpTicks = 10`), advance price by 1 tick into profit, assert `EditPositionSlTpAsync` is called with `newTp` derived from 80 ticks not 10
- [ ] Build passes; all tests still pass
