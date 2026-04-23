# OBS-01 Enrich MC StatusLine with diagnostic signal components

**Status:** Open  
**Category:** Observability  
**Size:** XS  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Trading/MultiConfluenceStrategy.vb:244–250`

## Problem
When live takes or misses a trade, the current `StatusLine` does not show the volume value, Chikou gap, MACD magnitude vs floor, DI spread, or the four confidence components. STRAT-16 and STRAT-19 features are live but invisible in logs.

## Proposed Fix
Append to the status line: `Vol={curVol}/{volAvg*1.2} | ChikouGap={…} | MACDmag={…}/floor={…} | DIspread={…} | Conf=adx{…}/di{…}/macd{…}/stoch{…}`. Keep on one line — `DiagnosticLogger` writes per-bar.

## Acceptance Criteria
- [ ] Snapshot test on `StatusLine` formatting
- [ ] Spot-checked against live log output during next UAT session
- [ ] Build passes; all tests pass
