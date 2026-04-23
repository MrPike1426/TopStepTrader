# [OBS-02] Emit `FailedConditions` list from `MultiConfluenceStrategy.Evaluate`

**Status:** Open  
**Category:** Observability  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb`, `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:913-930`

## Problem

`MultiConfluenceStrategy.Evaluate` returns a `MultiConfluenceResult` with `Side`, `StatusLine`, and `ConditionsMetCount`. When the strategy returns no signal (e.g., 6/9 conditions met), the engine logs only:

```
Bar checked — Multi-Confluence: 6/9 conditions met | 4m 12s remaining
```

There is no indication of *which* 3 conditions failed. Diagnosing why a signal was withheld — especially during a live UAT or post-trade review — requires manually correlating OHLCV bar data against each condition's formula. This costs 20–40 minutes per bar of interest.

In the UAT session the ~09:00 uptrend bars were skipped silently. Knowing that "Chikou failed, DMI failed" vs "MACD failed, Volume failed" would immediately narrow the root cause.

## Change

**1. Add `FailedConditions` to `MultiConfluenceResult`:**

```vb
Public Class MultiConfluenceResult
    Public Property Side As TradeDirection?
    Public Property StatusLine As String
    Public Property ConditionsMetCount As Integer
    Public Property FailedConditions As List(Of String) = New List(Of String)()
End Class
```

**2. Populate `FailedConditions` in `Evaluate`:**

Each of the 9 condition checks already sets a `Boolean` result variable (e.g., `ichimokuOk`, `emaOk`, `macdOk`). After all checks, collect failures:

```vb
Dim labels = {"Ichimoku", "EMA21", "EMA50", "TenkanKijun", "Chikou", "MACD", "StochRSI", "DMI/ADX", "Volume"}
Dim flags  = {ichimokuOk, ema21Ok, ema50Ok, tenkKijOk, chikouOk, macdOk, stochRsiOk, dmiAdxOk, volumeOk}

For i = 0 To labels.Length - 1
    If Not flags(i) Then result.FailedConditions.Add(labels(i))
Next
```

**3. Log in `StrategyExecutionEngine` when signal is absent:**

```vb
If mcResult.FailedConditions.Count > 0 Then
    Log($"  ↳ Failed: {String.Join(", ", mcResult.FailedConditions)}")
End If
```

This single extra log line appears only on no-signal bars, adding zero noise on signal bars.

## Acceptance Criteria

- [ ] `MultiConfluenceResult.FailedConditions` is populated on every `Evaluate` call (empty list when all 9 pass)
- [ ] When `ConditionsMetCount < 9` and no signal is returned, the engine logs `↳ Failed: [condition names]` on the line immediately after the status line
- [ ] When all 9 conditions pass and a signal is returned, `FailedConditions` is empty and the extra log line is suppressed
- [ ] Partial-signal path (8/9 via STRAT-16) still works; `FailedConditions` reflects the one failing condition
- [ ] Unit test: assert `FailedConditions` contains `"MACD"` when MACD histogram is below the minimum ATR fraction
- [ ] Build passes; all tests still pass
