# TEST-10 PumpNDump has no backtest signal provider — zero quantitative validation of strategy viability

**Status:** Open  
**Category:** Tests  
**Size:** M  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Backtest\Strategies\` (new file), `src\TopStepTrader.Services\Backtest\StrategySignalProviderFactory.vb`, `src\TopStepTrader.Core\Enums\StrategyConditionType.vb`

## Problem

Every other live strategy in this solution has a corresponding backtest signal provider
(e.g. `TripleEmaCascadeSignalProvider`, `MultiConfluenceSignalProvider`) that allows
historical performance evaluation before live deployment.

`PumpNDumpExecutionEngine` has no equivalent. There is:
- No `PumpNDump` entry in `StrategyConditionType` enum.
- No `PumpNDumpSignalProvider` in `Services\Backtest\Strategies\`.
- No registration in `StrategySignalProviderFactory`.
- No entry in `StrategyDefaults.Defaults`.

Without a backtest, there is no quantitative evidence for:
- Win rate and average R:R on any instrument.
- How often 3-consecutive-bar signals appear per session.
- Whether the default SL=8t / TP=80t (ES) or SL=20t / TP=200t (NQ) produce positive
  expectancy at realistic fill prices.
- How the momentum-fade TP tighten and free-ride heuristics affect overall profitability.

## Proposed Fix

1. Add `PumpNDump = 16` to `StrategyConditionType` enum with a doc comment matching the
   signal logic: "3 consecutive 1-minute bars all closing in the same direction".

2. Create `PumpNDumpSignalProvider.vb` in `Services\Backtest\Strategies\` implementing
   `IStrategySignalProvider`. The signal logic must exactly mirror `DoCheckAsync`'s FLAT
   entry path (lines 511–536 of the live engine):
   - Input: ordered list of `MarketBar`.
   - Signal: `Buy` when the last 3 bars all have `Close > Open`; `Sell` when all have
     `Close < Open`; no signal otherwise.
   - SL/TP: configurable via `BacktestConfiguration.SlAtrMultiple` / `TpAtrMultiple`
     (ATR-based), falling back to fixed tick counts.

3. Register in `StrategySignalProviderFactory` under `StrategyConditionType.PumpNDump`.

4. Add `"Pump-n-Dump"` to `StrategyDefaults.Defaults` with `Qty = "1"` and no fixed dollar
   overrides (ATR-tier sizing).

5. Add at least two xUnit tests in `TopStepTrader.Tests`:
   - `PumpNDump_3GreenBars_ReturnsBuySignal`
   - `PumpNDump_3RedBars_ReturnsSellSignal`
   - `PumpNDump_MixedBars_ReturnsNoSignal`
   - `PumpNDump_FewerThan3Bars_ReturnsNoSignal`

## Acceptance Criteria

- [ ] `StrategyConditionType.PumpNDump = 16` exists.
- [ ] `PumpNDumpSignalProvider` implements `IStrategySignalProvider` correctly.
- [ ] Registered in `StrategySignalProviderFactory`.
- [ ] "Pump-n-Dump" appears in `StrategyDefaults.Defaults`.
- [ ] All four unit tests pass.
- [ ] Strategy can be selected in the Backtest UI and produces a result without error.
- [ ] Build passes; all tests still pass (360+ passing).
