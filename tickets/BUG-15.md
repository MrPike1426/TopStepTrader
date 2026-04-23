# BUG-15 PumpNDump silently overrides user-provided TP/SL ticks with $100/$1000 fixed amounts

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:151–175`

## Problem

`PumpNDumpExecutionEngine.Start()` receives `takeProfitTicks` and `stopLossTicks` from the
caller (the ViewModel) but unconditionally discards both values and replaces them with a
fixed formula: `SL = Ceiling($100 / tickValue)`, `TP = Ceiling($1000 / tickValue)`.

The FavouriteContracts PX-resolution block (lines 165–178) then re-runs the same formula a
second time if a PX contract ID is found, again ignoring any user-provided tick counts.

Consequences:
- UI fields "Stop Loss Ticks" and "Take Profit Ticks" are completely non-functional.
- The `IPumpNDumpExecutionEngine.Start()` interface contract is a lie — parameters accepted
  but ignored with no warning.
- On NQ (tickValue = $5.00) this produces SL = 20 t / TP = 200 t regardless of what the user
  typed. On ES (tickValue = $12.50): SL = 8 t / TP = 80 t. These bear no relation to the
  default UI values of SL = 15 t / TP = 40 t.
- The startup log line prints the computed (wrong) values, giving a false sense that user input
  was respected.

## Proposed Fix

In `Start()`, honour the caller-provided values when they are positive; fall back to the
`$100/$1000` formula only when the caller passes `<= 0`:

```
_stopLossTicks  = If(stopLossTicks  > 0, stopLossTicks,  Math.Max(1, CInt(Math.Ceiling(100D  / tv))))
_takeProfitTicks = If(takeProfitTicks > 0, takeProfitTicks, Math.Max(1, CInt(Math.Ceiling(1000D / tv))))
```

Apply the same conditional to the PX-resolution re-derive block (lines ~173–175): only
recalculate when the original argument was `<= 0`.

No interface or ViewModel changes required. The existing startup log line
`Log($"   TP: {_takeProfitTicks}t  SL: {_stopLossTicks}t ...")` will now print the
correct user values automatically.

## Acceptance Criteria

- [ ] When `takeProfitTicks = 40` is passed, `_takeProfitTicks` is `40` after `Start()`.
- [ ] When `takeProfitTicks = 0` is passed, `_takeProfitTicks` falls back to `Ceiling($1000 / tickValue)`.
- [ ] Same behaviour symmetrically for `stopLossTicks` / `_stopLossTicks`.
- [ ] PX-resolution block does not re-override a user-supplied positive value.
- [ ] Startup log prints the value actually in use (auto-satisfied by existing log line).
- [ ] Build passes; all tests still pass.
