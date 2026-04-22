# [STRAT-05] Confluence Dissolution Exit

**Status:** Open  
**Category:** Strategy Improvements  
**Size:** M  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb` — MultiConfluence branch of `DoCheckAsync`

## Problem
Once in a Multi-Confluence trade, the only exits are hard SL, TP, FreeRoll trail, or manual flatten. There is no check for **signal decay** — if only 3 of the original 7 (or 8 after STRAT-03) conditions remain active, the trade is still held purely on bracket levels. This keeps the position open through significant adverse moves that haven't yet breached the hard SL.

## Change
After each bar check, when a position is open and the strategy is `MultiConfluence`, re-evaluate the condition count and exit if conviction has decayed:
```vb
Private Const DissolutionThreshold As Integer = 3

If _positionOpen AndAlso _strategy.Condition = StrategyConditionType.MultiConfluence Then
    Dim dissolution = MultiConfluenceStrategy.Evaluate(highs, lows, closes, _strategy.AdxThreshold)
    Dim activeCount = If(_lastEntrySide = OrderSide.Buy, dissolution.LongCount, dissolution.ShortCount)
    If activeCount < DissolutionThreshold Then
        Log($"⚠️  Confluence dissolved ({activeCount}/7 conditions active) — flattening position")
        Await FlattenAndCloseAsync("Confluence dissolved")
        Return
    End If
End If
```

## Acceptance Criteria
- [ ] Mid-trade confluence count evaluated on every bar check when `_positionOpen` and strategy = MultiConfluence
- [ ] Position flattened when count < `DissolutionThreshold` (3)
- [ ] Exit is logged with condition count
- [ ] Hard SL and FreeRoll trail remain in place — this is an additional early exit, not a replacement
- [ ] Build passes; all tests still pass
