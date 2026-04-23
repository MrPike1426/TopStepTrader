# [BUG-12] `fetchCount` (70) is less than `MultiConfluenceStrategy.MinBarsRequired` (80) — perpetual warm-up on fresh sessions

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:501-502`, `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:84`

## Problem

Every 30-second bar-check tick the engine calls `GetBarsForMLAsync` with `fetchCount`, then passes the result to `MultiConfluenceStrategy.Evaluate`. The strategy immediately checks `n < MinBarsRequired` (80) and returns `"Warming up"` with `Side = Nothing`.

`fetchCount` is computed as:

```vb
Dim minBars = _strategy.IndicatorPeriod + 5          ' = 20 + 5 = 25 for MultiConfluence
Dim fetchCount = Math.Max(minBars + 15, 70)           ' = max(40, 70) = 70
```

`IndicatorPeriod` defaults to 20 on a `StrategyDefinition` — a legacy field that has no meaning for MultiConfluence, which hardcodes its own 80-bar requirement independently in `MinBarsRequired`.

Consequence: `GetBarsForMLAsync` is called with `maxBars = 70`. Even if the SQLite DB has 200 bars of history, only 70 are returned. `Evaluate` receives 70, sees `n < 80`, and returns empty — silently, forever, regardless of how much history is stored. On the UAT day this caused the engine to miss the ~09:00 uptrend entirely; warm-up should have completed within the first few ticks but never did.

The outer engine never logs why the strategy returned no signal — the warm-up message is produced inside `Evaluate` and stored in `StatusLine`, but the calling code at line 913–914 logs it as a generic "no signal" line with no visibility that the strategy is stuck in warm-up.

## Change

Replace the `fetchCount` calculation so it always satisfies `MultiConfluenceStrategy.MinBarsRequired` when the condition is `MultiConfluence`:

```vb
Dim minBars = _strategy.IndicatorPeriod + 5
Dim fetchCount = Math.Max(minBars + 15, 70)

' BUGFIX BUG-12: MultiConfluence needs MinBarsRequired=80 regardless of IndicatorPeriod
If _strategy.Condition = StrategyConditionType.MultiConfluence Then
    fetchCount = Math.Max(fetchCount, MultiConfluenceStrategy.MinBarsRequired + 15)
End If
```

The `+ 15` buffer above `MinBarsRequired` mirrors the existing pattern and ensures one full cold-start fill without sitting at exactly the boundary.

Additionally, when `DoCheckAsync` logs the MultiConfluence status line and `mcResult.Side` is Nothing *and* the status line contains `"Warming up"`, emit a distinct log prefix so the operator can see warm-up vs. a genuine no-signal bar:

```vb
If mcResult.StatusLine.Contains("Warming up") Then
    Log($"⏳ Multi-Confluence warming up — {mcResult.StatusLine} | {remStr}")
Else
    Log($"Bar checked — Multi-Confluence: {mcResult.StatusLine} | {remStr}")
End If
```

## Acceptance Criteria

- [ ] `fetchCount` for `MultiConfluence` condition is always ≥ `MultiConfluenceStrategy.MinBarsRequired + 15` (≥ 95)
- [ ] `GetBarsForMLAsync` is called with the correct count; on cold-start the DB receives 95+ bars on the first ingest
- [ ] A fresh session on a 15-min MultiConfluence strategy logs `"⏳ Multi-Confluence warming up"` only for the first tick(s), then transitions to normal signal evaluation
- [ ] Warm-up message is visually distinguishable from a genuine no-signal bar in the log
- [ ] Existing MultiConfluence unit tests still pass
- [ ] Build passes; all tests still pass
