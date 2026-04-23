# STRAT-24 Externalise MC hard-coded thresholds to config/persona

**Status:** Open  
**Category:** Strategy  
**Size:** M  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:74–77,160,177,183,189,200,203,207,217,220,255–268`

## Problem
Ten thresholds are hardcoded constants — Ichimoku periods, MACD 8/17/9, `MinDiSpread = 2.0`, `chikouMinGap = lastClose * 0.001`, `macdMinMag = ATR * 0.05`, ADX threshold, StochRSI 0.7/0.3, volume 1.2×, SL 1.5×ATR, TP 2×R. Personas cannot tune these, and Maximum-Effort runs cannot explore this space.

## Proposed Fix
Add `MultiConfluenceConfig` record in `Core/Settings/`, bound from `appsettings.json "Strategies:MultiConfluence"`, with per-persona override support through `PersonaProfile`. Implement alongside ARCH-04.

## Acceptance Criteria
- [ ] Snapshot test: default config produces identical behaviour to current hardcoded constants
- [ ] Maximum-Effort sweep works with two alternative configs
- [ ] Build passes; all tests pass
