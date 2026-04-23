# STRAT-28 PumpNDump missing trading hours and stale-bar entry suppression

**Status:** Open  
**Category:** Strategy  
**Size:** M  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:508–537`

## Problem

`PumpNDumpExecutionEngine.DoCheckAsync` has no time-of-day guard and no stale-bar check.
The FLAT entry path fires whenever a 3-consecutive-bar signal appears, regardless of:

- Whether the market is in the overnight thin session (e.g. 00:00–06:00 UTC for CME futures).
- Whether the most recent 1-minute bar is stale (market closed, data feed interrupted).

Practical consequences:
- A 3-bar signal formed during the CME daily maintenance window (21:00–22:00 UTC) or the
  thin overnight session can trigger a 5-contract pyramid at $100 SL into a 1-tick-wide market.
  Slippage alone can exceed the entire SL before the stop order reaches the exchange.
- If `BarIngestionService` returns cached bars from the prior session after market close,
  the engine sees 3 completed bars and enters. The entry order is rejected by the broker
  (market closed), but the engine has already incremented `_currentQty` and may enter a
  broken state if rejection handling is incomplete.

`StrategyExecutionEngine` uses three complementary guards (all absent in PumpNDump):
- `IsInsideTradingHours()` — UTC hour window (default 06:00–21:00).
- `IsInsideTradingWindow()` — optional minute-precision sub-window.
- Stale-bar guard — suppresses entry when `barAgeMins > TimeframeMinutes × 3`.

## Proposed Fix

1. **Stale-bar guard**: at the top of the FLAT entry section (before line 508), add:
   ```vb
   Dim barAgeMins = (DateTimeOffset.UtcNow - lastBar.Timestamp).TotalMinutes
   If barAgeMins > 5.0 Then   ' 5 min = 5× the 1-min timeframe
       Log($"⏸  Stale bar ({barAgeMins:F0} min old) — entry suppressed")
       Return
   End If
   ```

2. **Trading hours guard**: add two new parameters to `IPumpNDumpExecutionEngine.Start()`:
   `tradingStartHourUtc As Integer` (default 6) and `tradingEndHourUtc As Integer`
   (default 21). Store as `_tradingStartHour` / `_tradingEndHour`. Before the FLAT entry
   block, check:
   ```vb
   Dim utcHour = DateTimeOffset.UtcNow.Hour
   If _tradingEndHour > 0 AndAlso (utcHour < _tradingStartHour OrElse utcHour >= _tradingEndHour) Then
       Log($"⏸  Outside trading hours (UTC {utcHour:00}:xx, window={_tradingStartHour:00}–{_tradingEndHour:00}h) — entry suppressed")
       Return
   End If
   ```
   Set both to 0 to disable. Position management (free-ride, trail, scale-in) continues
   outside hours — only the FLAT→entry path is gated.

3. Update `IPumpNDumpExecutionEngine.Start()` signature, the ViewModel `ExecuteStart()`, and
   the ViewModel fields/UI defaults (6 and 21) accordingly.

## Acceptance Criteria

- [ ] A 3-bar signal at 03:00 UTC does NOT trigger an entry order when `tradingStartHour = 6`.
- [ ] A bar with `Timestamp` more than 5 minutes old suppresses entry and logs a stale-bar warning.
- [ ] Position management (trail, free-ride, scale-in) continues normally outside trading hours.
- [ ] Setting `tradingStartHour = 0` AND `tradingEndHour = 0` disables the hours filter entirely.
- [ ] ViewModel passes `6` and `21` as defaults; fields are editable in the UI.
- [ ] Build passes; all tests still pass.
