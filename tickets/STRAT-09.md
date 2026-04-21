# [STRAT-09] Fix EMA/RSI bear scoring to use independent bear signals

**Status:** Open  
**Category:** Strategy  
**Size:** M  
**Files:** `src/TopStepTrader.Services/Backtest/Strategies/EmaRsiSignalProvider.vb`, `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:762`

## Problem
`downPct = 100 - bullScore` means the bear score is the *absence* of bull signals, not the *presence* of bear signals. A neutral bar (EMA flat, RSI 50, mixed candles) produces bullScore ≈ 10, so downPct ≈ 90 — a strong short signal — even though nothing bearish is happening. This is the most likely source of false short entries.

## Change
Compute an independent `bearScore` using the mirror conditions:
1. EMA21 < EMA50 × 0.9995 — 25 pts
2. Close < EMA21 — 20 pts
3. Close < EMA50 — 15 pts
4. RSI in 30–45 range — 20 pts
5. EMA21 falling (ema21Now < ema21Prev) — 10 pts
6. ≥ 2 of last 3 candles bearish — 10 pts

Replace the `downPct = 100 - bullScore` line in both `EmaRsiSignalProvider` and `StrategyExecutionEngine`. Use `bearScore` for short entry decisions and for `ConfidenceUpdated` events.

## Acceptance Criteria
- [ ] `EmaRsiSignalProvider.Evaluate` computes independent `bearScore`
- [ ] `StrategyExecutionEngine` EmaRsiWeightedScore block computes independent `bearScore`
- [ ] `ConfidenceUpdated` event passes `bearScore` as `downPct` (not `100 - bullScore`)
- [ ] Neutral-zone exit logic updated: exit when both `bullScore` and `bearScore` are in 40–60
- [ ] Existing `SignalProviderTests` still pass; add at least 2 new tests for bear-signal cases
- [ ] Build passes; all tests still pass
