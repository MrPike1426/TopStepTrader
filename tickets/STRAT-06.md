# [STRAT-06] Volume Confirmation Gate in MultiConfluenceStrategy

**Status:** Open  
**Category:** Strategy Improvements  
**Size:** M  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`

## Problem
Gold volume is fetched by `StrategyExecutionEngine` and passed as `volumes` to the bar data, but `MultiConfluenceStrategy.Evaluate` only accepts `highs`, `lows`, `closes`. Volume is not used as a signal gate. Low-participation bars — common in pre-market and Asian session — frequently satisfy all 7 Ichimoku/EMA/MACD conditions on minimal movement, producing false breakout signals.

## Change
Add a `volumes` parameter to `Evaluate` and compute a volume ratio gate:
```vb
' Volume gate: current bar volume must be ≥ 1.2× the 20-bar average
Dim volAvg = volumes.Skip(n - 21).Take(20).Average()
Dim lc8 = (volAvg > 0 AndAlso volumes(n - 1) >= volAvg * 1.2D)
Dim sc8 = lc8   ' same gate for shorts
```
This becomes condition 8 (or 9 after STRAT-03). All callers (`StrategyExecutionEngine`, backtest `MultiConfluenceSignalProvider`) must pass `volumes`.

## Acceptance Criteria
- [ ] `Evaluate` signature includes `volumes As IList(Of Decimal)`
- [ ] Volume gate implemented as `lc8` / `sc8`
- [ ] All callers updated to pass `volumes`
- [ ] Unit test: all other conditions pass but volume below threshold → no signal
- [ ] Build passes; all tests still pass
