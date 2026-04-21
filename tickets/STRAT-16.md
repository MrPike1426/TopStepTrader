# [STRAT-16] Allow 7/8 Partial-Conviction Signal at Half-Size in MultiConfluence

**Status:** Open  
**Category:** Strategy Improvements  
**Size:** M  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`, `src/TopStepTrader.Core/Models/MultiConfluenceResult.vb` (or inline), `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb`, `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`

## Problem
The current 8/8 unanimity rule generates very few signals — often 1–3 per week on a 5-minute chart. `LongCount` and `ShortCount` are already computed and returned in `MultiConfluenceResult` but discarded at 7/8. A 7/8 score represents a strong trade setup where one lagging indicator (typically Chikou or MACD histogram) hasn't fully confirmed yet. These setups frequently resolve into the 8/8 direction within 1–2 bars.

## Change
Add a `PartialSignal` flag to `MultiConfluenceResult`:
```vb
''' <summary>True when 7/8 conditions align but not all 8. Entry at reduced size.</summary>
Public Property IsPartialSignal As Boolean = False
```

In `MultiConfluenceStrategy.Evaluate`, after the 8/8 check, add:
```vb
ElseIf longCount = 7 Then
    result.Side = OrderSide.Buy
    result.IsPartialSignal = True
    result.CloudEdgeSl = cloudBottom
ElseIf shortCount = 7 Then
    result.Side = OrderSide.Sell
    result.IsPartialSignal = True
    result.CloudEdgeSl = cloudTop
End If
```

In `StrategyExecutionEngine`, when `IsPartialSignal = True`, set `Quantity = Math.Max(1, definition.Quantity \ 2)` for the entry order (halving the position size). The live engine should also log the partial-conviction reason clearly.

In `BacktestEngine`, partial signals use `Quantity / 2` for P&L calculation. The results grid should indicate partial entries (a separate count column or flag).

## Acceptance Criteria
- [ ] `IsPartialSignal` property added to `MultiConfluenceResult`
- [ ] 7/8 long and 7/8 short fire `IsPartialSignal = True` with correct `Side` and `CloudEdgeSl`
- [ ] `StrategyExecutionEngine` halves quantity on partial signals
- [ ] `BacktestEngine` halves quantity on partial signals
- [ ] Unit test: 7/8 long → `Side = Buy, IsPartialSignal = True`
- [ ] Unit test: 7/8 short → `Side = Sell, IsPartialSignal = True`
- [ ] Unit test: 6/8 → no signal
- [ ] Build passes; all tests still pass
