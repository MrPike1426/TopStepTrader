# [BUG-13] MultiConfluence same-direction scale-in blocked by `_positionOpen` guard

**Status:** Open  
**Category:** Bugs  
**Size:** M  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:1701-1806`

## Problem

Scale-in means adding contracts to the **same** running position in the **same direction** — not opening a second opposite position. The Damian persona defaults to `MaxScaleIns = 2`, and `PersonaProfile.MaxScaleIns` / `StrategyDefinition.MaxScaleIns` exist precisely to control this. `PlaceBracketOrdersAsync` correctly accumulates `_scaleInTradeCount`, `_totalDollarPerPoint`, and `_lastFinalAmount` across scale-ins. The infrastructure is fully wired.

However, for `MultiConfluence` (and every strategy except `EmaRsiWeightedScore`), the dispatch block takes the `Else` branch at line 1703, and the very first check inside that branch is:

```vb
If _positionOpen Then
    Log("⛔ Signal blocked — position already open ...")
    Return   ' <-- same-direction scale-in is silently killed here
```

This guard was written to prevent an opposite-direction signal from opening a second position in the wrong direction. It is correct for that purpose. But it does not distinguish between:

- **Same direction** (Long signal while Long open) → should place scale-in up to `MaxScaleIns`
- **Opposite direction** (Short signal while Long open) → should block (or route to reversal logic)

Consequence observed in UAT: the engine placed exactly one trade at ~14:40 and logged "position already open" for every subsequent confluence signal while the position was live. `_scaleInTradeCount` remained 0; Damian's two allowed scale-ins were never used.

## Change

Replace the flat `If _positionOpen Then → block` guard with a direction-aware split:

```vb
                    If _positionOpen Then
                        Dim isSameDirection = (_currentTrendSide.HasValue AndAlso side.Value = _currentTrendSide.Value)
                        If isSameDirection AndAlso _scaleInTradeCount < MaxScaleInTrades Then
                            ' Same-direction scale-in — allowed
                            If Not IsOrderingAllowed.Invoke() Then
                                Log($"⏸  Scale-in suppressed — market closed")
                            ElseIf _lastApiPnl < 0D Then
                                Log($"📊 Scale-in suppressed — position not profitable (P&L=${_lastApiPnl:F2})")
                            Else
                                Log($"📈 MC SCALE-IN #{_scaleInTradeCount + 1}/{MaxScaleInTrades} — {side.Value} signal while {_currentTrendSide.Value} position open | {mcResult.StatusLine}")
                                Await PlaceBracketOrdersAsync(side.Value, CDec(lastBar.Close), cloudSlArg)
                                _scaleInTradeCount += 1
                            End If
                        ElseIf isSameDirection Then
                            Log($"⛔ Scale-in cap reached ({_scaleInTradeCount}/{MaxScaleInTrades}) — signal: {side.Value}")
                        Else
                            Log($"⛔ Opposite-direction signal ({side.Value}) blocked — position open in {_currentTrendSide.Value} direction")
                        End If
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Position open — {If(isSameDirection, "scale-in suppressed", "opposite-direction blocked")}"
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
```

Key constraints to preserve:
- Profitability gate: `_lastApiPnl < 0D` suppresses scale-in (matches `EvaluateConfidenceActionsAsync:2712`)
- `IsOrderingAllowed` check: must pass (market hours guard)
- `MaxScaleInTrades` cap: enforced before placing
- Opposite-direction signals: still blocked (reversal confirmation logic unchanged)
- `_scaleInTradeCount` is incremented after a successful `PlaceBracketOrdersAsync` call (not before)
- The existing diag-entry `REJECT` path applies to blocked scale-ins

## Acceptance Criteria

- [ ] When a Long MultiConfluence signal fires while a Long position is open and `_scaleInTradeCount < MaxScaleInTrades`, a scale-in order is placed via `PlaceBracketOrdersAsync`
- [ ] `_scaleInTradeCount` increments correctly; after `MaxScaleInTrades` scale-ins are placed, further same-direction signals log the cap message and do not place orders
- [ ] When a Short signal fires while a Long position is open, `"⛔ Opposite-direction signal blocked"` is logged and no order is placed
- [ ] Profitability gate: if `_lastApiPnl < 0D`, scale-in is suppressed with log message (not placed)
- [ ] `IsOrderingAllowed = False` suppresses scale-in (no order, no crash)
- [ ] `EmaRsiWeightedScore` scale-in behaviour is unchanged (its path through `EvaluateConfidenceActionsAsync` is not touched)
- [ ] Add unit/integration tests: (a) same-direction signal while position open → scale-in placed; (b) opposite-direction signal → blocked; (c) cap reached → blocked; (d) losing position → suppressed
- [ ] Build passes; all tests still pass
