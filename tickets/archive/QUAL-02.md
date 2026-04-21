# [QUAL-02] Rename `InitialSlAmount` / `InitialTpAmount` to Clarify Semantics

**Status:** Open  
**Category:** Code Quality  
**Size:** M  
**Files:** Wherever `BacktestConfiguration` is defined (likely `Core/Models/` or `Services/Backtest/`)

## Problem
`InitialSlAmount` and `InitialTpAmount` are dollar-delta brackets, not absolute prices. The name "Amount" is ambiguous — it could mean price, dollars, or ticks. Callers reading the engine must track the naming through 3 layers to confirm semantics.

## Change
Rename to `SlDollarBracket` and `TpDollarBracket`. Update all references.

## Acceptance Criteria
- [ ] `InitialSlAmount` and `InitialTpAmount` no longer exist
- [ ] `SlDollarBracket` and `TpDollarBracket` used consistently in engine, ViewModels, and tests
- [ ] Build passes; all 221 tests still pass
- [ ] CLAUDE.md `StrategyDefaults` table updated to reflect new names if referenced there
