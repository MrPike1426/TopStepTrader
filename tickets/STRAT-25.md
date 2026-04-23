# STRAT-25 Add spread / slippage / commission to backtest fills

**Status:** Open  
**Category:** Strategy  
**Size:** M  
**Source:** Code-Review  
**Files:** `src/TopStepTrader.Services/Backtest/Strategies/MultiConfluenceSignalProvider.vb:184`, `src/TopStepTrader.Services/Backtest/BacktestEngine.vb`

## Problem
Backtest fills re-anchor to `nextBar.Open` but include no spread, stop slippage, or commission. Live fills include ~1-tick spread, occasional 1-tick slippage on stops, and ~$4–$5 round-turn commission per micro. With 2:1 R:R and ~50% win rate, cost drag erodes meaningful edge. Maximum-Effort top-rankings can shuffle when costs are applied.

## Proposed Fix
Add `BacktestConfiguration.SpreadTicks`, `SlippageTicksOnStop`, `CommissionPerSide`. Apply in `BacktestEngine` fill block.

## Acceptance Criteria
- [ ] Re-run Damian/OIL/5-min with spread=1 tick, slip=1 tick, comm=$2.50/side; P&L drops and ranking shuffles as expected
- [ ] Build passes; all tests pass

## Open Questions
- What are the actual TopStepX commission rates per micro contract? Confirm with ProjectX docs.
