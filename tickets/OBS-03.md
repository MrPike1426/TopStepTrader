# [OBS-03] Log `StrategyDefinition` config snapshot at engine `Start()`

**Status:** Open  
**Category:** Observability  
**Size:** XS  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:285-330`

## Problem

When `StrategyExecutionEngine.StartAsync` is called, the engine logs a brief startup banner (instrument, timeframe, persona name) but does not print the resolved `StrategyDefinition` values. Every configurable threshold — `SlMultipleOfN`, `TpMultipleOfN`, `MaxScaleIns`, `AdxThreshold`, `TradingStartHourUtc`/`End`, `UsePreTradeAiCheck`, `AtrBracketTier`, `MaxScaleIns` — is hidden until something goes wrong.

In the UAT session, to verify whether the WIDE ATR tier was active required reading `appsettings.json` offline. If the engine had printed the resolved values at startup, the analyst could confirm or refute the config in the log itself.

## Change

After the existing startup banner in `StartAsync`, add a compact config snapshot block:

```vb
Log("── Strategy Config ─────────────────────────────")
Log($"  Condition       : {_strategy.Condition}")
Log($"  Instrument      : {_strategy.Instrument}  TF: {_strategy.TimeframeMinutes}m")
Log($"  Persona         : {_persona.Name}  ADX≥{_persona.AdxThreshold}")
Log($"  ATR Bracket     : {_strategy.AtrBracketTier}  SL×{_strategy.SlMultipleOfN}N  TP×{_strategy.TpMultipleOfN}N")
Log($"  MaxScaleIns     : {_strategy.MaxScaleIns}")
Log($"  Trading Hours   : {_strategy.TradingStartHourUtc:D2}:00–{_strategy.TradingEndHourUtc:D2}:00 UTC")
Log($"  Pre-trade AI    : {If(_strategy.UsePreTradeAiCheck, "ON", "OFF")}")
Log($"  Re-entry cool   : {_reEntryCooldownSeconds}s")
Log("────────────────────────────────────────────────")
```

No new fields are required — all properties already exist on `StrategyDefinition` and `PersonaProfile`.

## Acceptance Criteria

- [ ] On every call to `StartAsync`, the config snapshot block is logged before the first bar-check tick fires
- [ ] All 8 fields listed above appear in the log
- [ ] Log output is human-readable (no JSON blobs, no raw enum integers)
- [ ] The block is suppressed / does not throw if any property is at its default zero value (e.g., `TradingStartHourUtc = 0`)
- [ ] Build passes; all tests still pass
