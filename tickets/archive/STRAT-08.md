# [STRAT-08] Raise FreeRoll Activation to 66–75% of Initial TP

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** S  
**Files:** `src/TopStepTrader.Services/Trading/StrategyExecutionEngine.vb` — FreeRoll activation logic (~line 164)

## Problem
FreeRoll activates at 50% of the initial TP distance, moves SL to break-even+buffer, **cancels the TP**, and switches to pure ATR trailing. On a genuine Gold trend with all 7/8 Multi-Confluence conditions aligned, a Damian TP of 2.5× ATR represents a $10–20 target. Activating at 50% (1.25× ATR) exits at half the intended reward and caps trades that should run further. The "Extend TP on close" feature partially mitigates this but only fires on bar-close, not intrabar.

## Change
Raise the activation multiplier from 0.5 to 0.67 (two-thirds). Expose as a named constant:
```vb
Private Const FreeRollActivationFraction As Decimal = 0.67D
' Was: initialTpTicks × 50%
' Fix: initialTpTicks × 67%
_freeRollActivationPrice = _lastEntryPrice +
    (CDec(_initialTpTicks) * _strategy.TickSize * FreeRollActivationFraction * entryDirectionSign)
```

## Acceptance Criteria
- [ ] `FreeRollActivationFraction` constant defined at 0.67
- [ ] `_freeRollActivationPrice` calculated using the new constant
- [ ] Existing FreeRoll tests updated for the new threshold
- [ ] Build passes; all tests still pass
