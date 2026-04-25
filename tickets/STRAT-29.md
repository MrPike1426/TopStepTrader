# STRAT-29 Implement Opening Range Breakout (ORB) strategy

**Status:** Open  
**Category:** Strategy  
**Priority:** P2  
**Size:** L  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Core/Enums/StrategyConditionType.vb`, `src/TopStepTrader.Services/Backtest/Strategies/`, `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb`, `src/TopStepTrader.Core/Trading/StrategyDefaults.vb`

## Problem

The current strategy lineup is entirely indicator-based (EMA, RSI, MACD, Ichimoku, etc.). None of the strategies use price structure or session context. Opening Range Breakout is one of the most consistently profitable documented intraday strategies — it is used by prop firms globally, has academic backing, and is specifically designed for the intraday-only constraint imposed by TopStepX's forced EOD close.

## Strategy Logic

**Setup:** The first 15–30 minutes of the regular session establish the "opening range" — the high and low of bars from session open to a configurable cutoff time (default: first 30 minutes).

**Entry:**
- Long when price closes a full bar **above** the opening range high, confirmed by a volume bar ≥ 1.2× the 20-bar average volume
- Short when price closes a full bar **below** the opening range low, confirmed by volume

**Stop Loss:** Opposite extreme of the opening range (e.g. long stop = opening range low). Expressed in ticks and clamped via `TopStepXInstrumentCatalog.ClampStopTicksAsync`.

**Take Profit:** 1.5× the width of the opening range in the breakout direction (minimum 1:1.5 R:R).

**No-trade filter:** Do not enter if the opening range width is > 2× the ATR(14) of the prior 20 bars (range too wide = risk too high). Do not enter after the session midpoint (instrument-specific cutoff, e.g. 1:00pm ET for Gold).

**Best instruments:** MNQ, MES (index futures with strong NY open momentum), and MGC (Gold, strong 8:20–10:30am ET window).

## Acceptance Criteria
- [ ] `StrategyConditionType.OpeningRangeBreakout` enum value added (next sequential integer, do not reuse any)
- [ ] `OrbSignalProvider` implementing `IStrategySignalProvider` with the logic above
- [ ] Registered in `StrategySignalProviderFactory.Create`
- [ ] Default parameters added to `StrategyDefaults`
- [ ] Live engine (`StrategyExecutionEngine`) recognises the strategy and evaluates it on each bar
- [ ] Backtest runs correctly for ORB including opening-range construction phase (bars before range is complete produce no signal)
- [ ] At least 5 unit tests covering: range construction, long breakout signal, short breakout signal, no-trade filter (wide range), no-trade filter (late session)
- [ ] Build passes; all tests still pass
