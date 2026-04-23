# OBS-06 PumpNDump missing DiagnosticLogger integration — bracket changes and trade events unobservable

**Status:** Open  
**Category:** Observability  
**Size:** L  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb`, `src\TopStepTrader.Services\ServicesExtensions.vb`

## Problem

`PumpNDumpExecutionEngine` produces no structured diagnostic log records. All output is
UI-only string events raised via `RaiseEvent LogMessage` — ephemeral, not persisted, and
invisible to post-trade UAT review.

The following events are completely unobservable from log files:
- Initial bracket placement (what TP/SL prices were actually submitted to the broker).
- Free-ride activation (when it triggered, what `_averageEntry` was at the time).
- Trail SL moves (new SL price, tick advance, which bracket).
- TP tighten events (old TP, new TP, ATR and range values that triggered the fade).
- Close events (exit reason TP/SL/Emergency, fill price, P&L per bracket).
- Scale-in events (new contract count, heat at entry, average entry after scale-in).
- Entry rejections (why no entry fired on a given signal evaluation).

When reviewing UAT logs to answer "why did no bracket changes happen?" or "why was the SL
not where I expected?" there is currently no evidence to inspect.

`StrategyExecutionEngine` solves this with `DiagnosticLogger` (injected via DI), which writes
structured JSONL entries for every significant event. `PumpNDumpExecutionEngine` should use
the same logger.

## Proposed Fix

1. **Inject `DiagnosticLogger`** as a constructor dependency in `PumpNDumpExecutionEngine`.
   Register via DI in `ServicesExtensions.vb` (where `PumpNDumpExecutionEngine` is already
   registered as Transient).

2. **Call `_diagLogger.StartSession` / `CloseSession`** in `Start()` and `StopAsync()`.

3. **Add structured log calls** at each key event, reusing `DiagnosticLogEntry` fields
   where applicable:

   | Event | Entry type / fields |
   |---|---|
   | Engine started | `EventType = "ENGINE_START"`, config params as `Why` |
   | 3-bar signal fired | `EventType = "SIGNAL"`, direction, bar values in `Why` |
   | Entry order placed | `EventType = "ENTRY"`, side, qty, estimated price |
   | Bracket placed | `EventType = "BRACKET_PLACED"`, TP price, SL price, bracket qty |
   | Free-ride activated | `EventType = "FREE_RIDE"`, avg entry, PnL at activation |
   | Trail SL moved | `EventType = "TRAIL_SL"`, old SL, new SL, tick advance, current price |
   | TP tightened | `EventType = "TP_TIGHTEN"`, old TP, new TP, ATR, avg range |
   | Scale-in | `EventType = "SCALE_IN"`, new qty, heat, avg entry after |
   | Bracket closed (TP/SL) | `EventType = "CLOSED"`, exit reason, P&L |
   | Emergency close | `EventType = "EMERGENCY_CLOSE"`, reason, estimated P&L |
   | Entry suppressed | `EventType = "REJECT"`, reason (stale bar, trading hours, heat cap) |

4. **Add `WritePositionSnapshot`** calls (same cadence as SEE: first tick then every 60 s)
   inside `DoCheckAsync` when a position is open.

## Acceptance Criteria

- [ ] `DiagnosticLogger` is injected and non-null when the engine runs.
- [ ] A JSONL log file is produced for each PumpNDump session.
- [ ] Log contains `SIGNAL`, `ENTRY`, `BRACKET_PLACED`, `FREE_RIDE`, `TRAIL_SL`,
  `TP_TIGHTEN`, `SCALE_IN`, `CLOSED` / `EMERGENCY_CLOSE` entries for a full trade lifecycle.
- [ ] `REJECT` entries appear whenever an entry signal was suppressed (stale bar, hours,
  heat, or position already open after BUG-21 fix).
- [ ] `ENGINE_START` entry contains the effective TP/SL ticks and all session parameters.
- [ ] Build passes; all tests still pass.
