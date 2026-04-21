# [STRAT-01] Raise Re-Entry Cooldown to 2 Full Bars

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb:150`

## Problem
`ReEntryCooldownSeconds = 60` allows re-entry 1 minute after a SL hit. On a 15-min bar strategy (Multi-Confluence/Gold), all 7 confluence conditions can still be satisfied immediately after a stop — the cloud, EMA stack, and Tenkan/Kijun don't unwind in 60 seconds. This causes consecutive losses on the same degraded signal within a single bar period.

## Change
Replace the constant with a bar-count-aware computed property:
```vb
Private ReadOnly Property ReEntryCooldownSeconds As Integer
    Get
        Return _strategy.TimeframeMinutes * 2 * 60   ' 2 full bars in seconds
    End Get
End Property
```
Remove the `Private Const ReEntryCooldownSeconds As Integer = 60` declaration.

## Acceptance Criteria
- [ ] Cooldown is `TimeframeMinutes × 2 × 60` seconds for all strategy types
- [ ] Existing re-entry cooldown tests (if any) updated for new formula
- [ ] Build passes; all tests still pass
