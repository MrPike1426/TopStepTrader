# [OBS-04] Write AI pre-trade veto outcome to `DiagnosticLogger`

**Status:** Open  
**Category:** Observability  
**Size:** XS  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:1764-1797`

## Problem

When `UsePreTradeAiCheck` is enabled, the engine calls the AI service and either proceeds with the trade or vetoes it. The veto decision is logged to the UI log with a single line:

```
🤖 AI veto — confidence 0.61 below threshold 0.85. Trade blocked.
```

This line is visible in the on-screen log but is **not** written to `DiagnosticLogger`. As a result:
- Post-session diagnostic exports (CSV / SQLite diag table) contain no record of AI vetoes
- It is impossible to answer "how many trades were blocked by AI today?" without manually scanning log text
- There is no way to correlate a veto timestamp with bar data or P&L outcomes to validate whether the AI is helping or hurting

## Change

After the existing `Log(...)` call for each AI outcome, add a `DiagnosticLogger.Record` call:

**Veto path:**

```vb
_diagLogger.Record(New DiagEntry With {
    .Timestamp = DateTimeOffset.UtcNow,
    .EventType = "AI_VETO",
    .Side = side.ToString(),
    .Confidence = aiConfidence,
    .Threshold = _riskSettings.MinSignalConfidence,
    .StatusNote = $"AI vetoed {side} entry — confidence {aiConfidence:F2} < {_riskSettings.MinSignalConfidence:F2}"
})
```

**Pass-through path:**

```vb
_diagLogger.Record(New DiagEntry With {
    .Timestamp = DateTimeOffset.UtcNow,
    .EventType = "AI_PASS",
    .Side = side.ToString(),
    .Confidence = aiConfidence,
    .Threshold = _riskSettings.MinSignalConfidence,
    .StatusNote = $"AI approved {side} entry — confidence {aiConfidence:F2}"
})
```

Use whatever `DiagEntry` fields are already mapped in the schema; add `Confidence` and `Threshold` only if they do not already exist.

## Acceptance Criteria

- [ ] Every AI veto produces a `DiagEntry` with `EventType = "AI_VETO"` in the diagnostic log
- [ ] Every AI pass-through produces a `DiagEntry` with `EventType = "AI_PASS"`
- [ ] `Confidence` and `Threshold` values are recorded accurately
- [ ] When `UsePreTradeAiCheck = False`, no AI diag entries are emitted (no null-ref)
- [ ] Diag entries appear in the CSV / SQLite export alongside trade entries
- [ ] Build passes; all tests still pass
