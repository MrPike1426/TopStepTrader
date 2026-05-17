# Trade Management System Review

**Date:** 2026-05-17
**Branch:** `clean-start`
**Scope:** SuperTrend+ live trading: entry detection → SL management → exit → postmortem persistence → replay sufficiency → ML training inputs.
**Method:** Source-code audit only. Per project workflow I did not run `dotnet build` or `dotnet test` — the existing test suite (`ExitSignalEngineTests`) was read for behaviour confirmation but not executed.

---

## TL;DR — what's working and what isn't

| Area | Verdict | Notes |
|---|---|---|
| (a) Entry detection on a 15-min bar across favourites | ✅ Correct | Default timeframe is `"15min"` across the full SuperTrend+ watchlist (MES, MNQ, M2K, MBT, MGC, M6E, MCL). All instruments share the user-selected TF; the per-instrument `MultiConfluenceTimeframeMinutes` on `FavouriteContract` (5/10) belongs to a different strategy and is not consumed by the SuperTrend+ engine. |
| (b1) SL placed at entry & ratchet logic | ✅ Correct | Initial SL placed via TopStepX `StopLossBracket` (type=4). A "naked-entry guard" flattens the position 7 s after entry if the resting Stop is missing; a per-tick `BracketMissingTickCount` re-checks every monitoring cycle and flattens after 2 consecutive misses. Ratchet only — stop never retreats. |
| (b2) Every SL change recorded | ✅ Correct, with one minor caveat | `TradeStopAdjustments` table receives an `Initial` row at trade open and a row for every phase advance. Dedupe key is `(recordId:slotIndex)` held in-memory (BUG-62); on a mid-trade app restart the first post-restart move will re-log, which is harmless. |
| (b3) Exit when conditions degrade | ⚠️ Works, but documented spec disagrees with code | E1–E9 score engine + phased ratchet operate on **closed strategy-TF bars** (15-min by default), NOT closed 15-second bars as the XAML "How It Works" panel claims. The phase thresholds in the code disagree with both the XAML and the `StopPhase` enum docs. See **DOC-07** below. |
| (c1) SL trigger / app-close / TopStepX force-close all update DB | ✅ Mostly correct | `CloseTradeAsync` schedules `CaptureClosingSnapshotsAsync` which records broker Orders/Positions/Fills against the trade. The three exit paths converge on this. Edge case: force-closes that happen while the app is OFF only get reconciled on next startup via a 48-hour lookback — see **BUG-86**. |
| (c2) "Postmortem database" actually gets postmortem-grade data | ❌ Major gap | `TradeOutcomes`, `TradeSetupSnapshots`, `TradeLifespanRecords` entities + repositories exist and are wired into DI, but **no production code writes any of these tables.** See **FEAT-57** + **FEAT-58**. |
| Replay — step through a trade bar-by-bar after close | ❌ Conditional | Bar-level replay (15-s OHLCV / ST / ATR / ADX / SL / MFE / MAE / phase) exists only in `debug_trades.db` and only when the Debug Capture toggle was ON during the trade. Default toggle is OFF; retention is 30 days / 100 trades. See **FEAT-59**. |
| ML training input | ⚠️ Wired but disconnected | `SignalModelTrainer.TrainAndSave` accepts an `outcomeLabels` dictionary, but its only source (`TradeOutcomeRepository`) is never populated, so training silently falls back to its look-ahead heuristic. Fixed by **FEAT-57** + **FEAT-58**. |

Six tickets opened: **DOC-07, FEAT-57, FEAT-58, FEAT-59, BUG-86, BUG-87**.

---

## (a) Entry detection — 15-min default across all favourite contracts

**Confirmed:**
- `SuperTrendPlusViewModel.SelectedTimeframe` defaults to `"15min"` (its private backing field is initialised to that literal). The `Timeframes` combo offers `{"5min", "15min", "1hr"}`.
- The selected timeframe is passed uniformly to every monitored instrument: the bar fetcher is called per slot with the same `tf`, so all of MES, MNQ, M2K, MBT, MGC, M6E, MCL evaluate on the same closed-bar cadence.
- `FavouriteContract.MultiConfluenceTimeframeMinutes` (5 for MES/MNQ/MCL/MBT/M2K, 10 for MGC/M6E) is *not* read by the SuperTrend+ engine. It belongs to a separate `MultiConfluence` strategy. Worth confirming you're aware, but it's not a SuperTrend+ bug.
- The signal-detection rules in `SuperTrendPlusView.xaml` (ST direction + DI alignment + ADX gate + persona-specific minimum ADX) match the entry rules in the ViewModel.
- `FEAT-46` pre-entry exit-score gate uses the same `ExitSignalEngine` to block entries when degradation signals are already present at signal time. Smart safety net.
- Same-bar / same-tick double-entry is blocked at three layers: `SlotManager.TryOpenSlot` checks `EntryBarTime`, `_releasedThisTick` flag on the ViewModel, and `slot.IsEntryInFlight` straddles the broker round-trip.

**No tickets** in this area. The 15-min default is honest across the watchlist.

---

## (b) Trade management — SL placement, movement, exit

### What the code actually does (source of truth: `ExitSignalEngine.ComputePhasedStop` + tests)

| Profit (R-multiples) | Phase | New stop |
|---|---|---|
| `profit < 1R` | `Initial` | trail the closed-bar SuperTrend line (ratchet only) |
| `profit >= 1R` | `Breakeven` | `entry ± 0.5R` |
| `profit >= 1.5R` | `ProfitTrail` | `currentPrice ± 1×ATR` |
| `profit >= 2R` | `Harvest` | `entry ± 1.5R` |
| `profit >= 3R` | `FreeRide` | `entry ± 2R` |

`R` is initial risk, floored at the entry-bar ATR per BUG-75 / ARCH-15 to prevent ratcheting on trivially small dollar moves (a SuperTrend flip places the SL within 1–3 ticks of close, which would otherwise mean a one-tick move = "1R reached").

Cadence: `ExitSignalEngine.Evaluate` is invoked once per monitoring tick (≈15 s) per open slot, but the bars handed in are the strategy-TF bars (15-min by default). So the ratchet *re-evaluates* every 15 s, but the **input only changes on each closed 15-min bar**. The XAML's promise of "ratchets on closed 15s bars" is wrong — see **DOC-07**.

### Exit decision

`ExitSignalEngine.Evaluate` produces a composite score over signals E1–E9:
- **E1 SuperTrend flip (weight 8, ImmediateExit)** — opposite-direction ST direction on the latest bar
- E2 SuperTrend momentum slowing (3) — distance-to-ST contracts over 3 bars by > 0.5×ATR
- E3 ADX declining (2) — 3-bar fall AND 10 pts below entry ADX
- E4 DI compression (2) — spread narrows ≥15% in one bar AND below an ADX-scaled ceiling
- E5 DI crossover warning (4) — opposite-side DI crosses on latest bar
- E6 Price rejection bar (2) — upper wick > 2× body and close below midpoint (long; mirrored for short)
- E7 ATR contraction (1) — 3-bar fall AND < 0.8× entry ATR
- E8 VWAP cross (2) — close crosses opposite side of VWAP
- E9 RSI hidden divergence (3)

Score thresholds: `0–2` healthy, `3–5` warning, `6+` exiting. The exit only fires when `ConsecutiveExitBars >= 2` (or `ImmediateExit` from E1 fires immediately), preventing single-bar high-trend consolidation from closing a healthy position.

**Strategy critique (per your "audit AND critique" answer):**

1. **The phased ladder thresholds may be too generous.** Breakeven only arms at +1R, not +0.5R as the docs claim. That means a trade that goes +0.9R then reverses to SL eats a full -1R. The docs' +0.5R BE is much safer for a trend system where the median win is small. Consider whether the docs were the older, better spec and the code drifted, or whether the +1R BE was a deliberate tightening that hasn't been documented. (Suggest tracking this decision somewhere explicit.)
2. **E1 (ST flip, weight 8) is the only true forced exit.** Everything else needs `ConsecutiveExitBars >= 2`. On a 15-min strategy that's 30 minutes of degraded health before exit. For sharp reversals (DI crossover + RSI divergence on the same bar = score 7, but only 1 bar of evidence) you can give back several R before exiting. Worth considering: score-based ImmediateExit threshold (e.g. `score >= 10` → exit on bar 1).
3. **The `ProfitLock` phase declared in the `StopPhase` enum is unreachable code** — `ComputePhasedStop` never returns it. Either implement it (between Breakeven and ProfitTrail per the XML doc) or delete it. Captured in **DOC-07**.
4. **`FavouriteContract.PhasedTrailMinInitialStopTicks` / `PhasedTrailMaxInitialStopTicks` / `PhasedTrailBreakevenMinTicks` are dead config** — declared on each instrument with sensible-looking values but never read by the engine. Either wire them into the initial-stop sizing path or delete. Captured in **BUG-87**.

### SL persistence — every move recorded?

**Yes, with one observation.** The flow:
- `TradeRecordService.OpenTradeAsync` inserts the `Initial` row with `OldStop = NewStop = InitialStopPrice`.
- `ExitSignalEngine.Evaluate` calls `tradeService.LogStopAdjustmentAsync` whenever `phasedStop.NewStop != slot.StopPrice`, with `TriggerReason = phase.ToString()` ("Breakeven", "ProfitTrail", "Harvest", "FreeRide").
- BUG-62 dedupe (`_lastLoggedStop` keyed by `recordId:slotIndex`) prevents the same Old→New from being re-logged on subsequent ticks before the broker confirms.

**Observation:** Only the *application's* SL moves are logged. If the broker rejects a modify (`ModifyOrderAsync` returns `Success=false`), `EditPositionSlTpAsync` logs a warning but does NOT roll back the `TradeStopAdjustments` row that `ExitSignalEngine` already wrote. So the DB can show a stop at +0.5R while the broker still has the stop at entry. This is rare (rejections are usually "price too close" or "too late" — both retried next tick), but worth thinking about. Not opening a ticket — it's edge-case data drift, not a correctness bug.

---

## (c) Exit paths and postmortem persistence

### The three exit paths

| Path | Detection | Update flow |
|---|---|---|
| **SL trigger on broker** | `OnHubPositionUpdated` (NetPos→0) or `GetLivePositionSnapshotAsync` returns Nothing → `slot.MissCount++` → `ReleaseSlotAsync("Closed by Broker")` after `SyncMissThreshold` ticks. | `CloseTradeAsync` → `LiveTradeRecord.IsOpen=false`, exit data set, then fire-and-forget `CaptureClosingSnapshotsAsync` pulls Orders/Positions/Fills from TopStepX. |
| **App-initiated close** | `ReleaseSlotAsync(reason)` → `ProjectXOrderService.FlattenContractAsync` cancels both bracket legs (type=4 SL + type=1 TP) then `CloseContractAsync`. | Same as above. |
| **TopStepX force-close** (daily-loss limit, EOD flat, drawdown rule) | Same as broker SL — appears to the app as the broker zeroing the position. | Same as above. |

All three converge on `CloseTradeAsync` → snapshot capture. The persisted exit data is:
- `LiveTradeRecords` row updated: `ExitTime`, `ExitPrice`, `PnL`, `ExitReason`, `IsOpen=false`.
- `TradeOrderSnapshots` / `TradePositionSnapshots` / `TradeFillSnapshots` capture broker state ±1 minute around close (FEAT-50).
- `TradeStopAdjustments` already has the full per-phase ladder.

**Edge case → BUG-86:** if the app is OFF when a force-close happens, the LiveTradeRecord stays `IsOpen=true`. `RecoverOpenTradesAsync` runs once at startup with a 48-hour lookback into `/api/Trade/search`. So a weekend force-close while the app was off for ≥48 h leaves the trade stuck in "open" state. The right fix is a periodic reconciler + a wider startup lookback. See ticket.

### The hidden gap — what's NOT recorded

The user's brief says "we update the postmortem database accordingly". There are in fact **two** databases:

1. `TradeHistory.db` — the live production trade history. Always written. Captures everything described above.
2. `debug_trades.db` — separate diagnostic DB, opt-in via the Debug Capture toggle.

And then there are *three* unused tables in `AppDbContext` (the broader app DB, NOT `TradeHistory.db`):
- `TradeOutcomes` — winner/loser label, P&L, exit reason, with FK back to `Signals`
- `TradeSetupSnapshots` — full indicator state at signal time (Tenkan/Kijun/EMA9/21/50/MACD/Stoch/DMI/ADX/RSI/VIDYA/CMO + signal bar OHLCV + session/window)
- `TradeLifespanRecords` — MAE/MFE in $ and ticks, SL ratchet count, FreeRide activation, duration, R-multiple

A grep across the whole codebase finds **no production caller** writing these tables. The repos exist, are registered in DI, but nothing invokes `SaveOutcomeAsync` / `SaveAsync` / etc. in live trading.

This is the gap that breaks the "step through a trade" + "train the engine" goals. Two tickets:
- **FEAT-57** — Persist `TradeOutcome` per live trade
- **FEAT-58** — Persist `TradeSetupSnapshot` (at entry) and `TradeLifespanRecord` (at close)

---

## Replay sufficiency — can we walk a trade bar-by-bar after the fact?

**Only when the Debug Capture toggle was ON during the trade.** That's the only path that persists per-15s OHLCV + indicator values + current SL + phase + MAE/MFE during the trade itself, into `debug_trades.db.DebugSnapshots`. The `DebugTradeActions` table also records authoritative actions (OrderPlaced / EntryFilled / StopLossPlaced / StopLossModified / Closed / etc.).

What this means in practice:
- **Live trades with Debug Capture OFF:** the only persisted decision-time data is the `TradeStopAdjustments` row per phase advance. To replay, you must re-fetch bars from TopStepX history (60-day lookback ceiling for 5m / 15m bars per `TopStepXBarIngestionService.MaxLookbackDays`) and recompute SuperTrend/DMI/ADX/ATR from scratch. Trades older than 60 days are unreplayable.
- **Live trades with Debug Capture ON:** full per-tick playback for 30 days, after which the purge sweeps them. Max 100 retained trades.

`tools/postmortem/postmortem.py` exists and reads both DBs to render a markdown report. I have not audited that script — it's outside the .NET solution. Worth a spot-check that it survives missing rows in the unused tables.

**Ticket FEAT-59** proposes either defaulting Debug Capture ON for live mode (with a longer / configurable retention) OR — preferably — relocating its key fields into `TradeHistory.db` so they're never optional.

---

## ML training pipeline — does the engine learn from real outcomes?

**Not currently — it falls back to the look-ahead heuristic.** Specifically:

- `SignalModelTrainer.TrainAndSave` accepts an optional `outcomeLabels As Dictionary(Of DateTimeOffset, Boolean)` parameter. When provided, real P&L labels override the look-ahead heuristic on a per-bar basis.
- That dictionary's only producer would be code that queries `TradeOutcomeRepository.GetResolvedOutcomesAsync(...)` and keys by `EntryTime`.
- No such caller exists in the codebase. So `outcomeLabels` is always `Nothing`, and training silently uses the synthetic heuristic ("close > entry within N bars").

This is the single biggest gap relative to your goal of "train the engine using this data so it improves over time". Fixing **FEAT-57** unblocks it. Adding **FEAT-58** then enriches the training set with the full indicator state at signal time, which is what allows the model to learn *which conditions produced winners*.

A secondary observation: `BarFeatureVector` (the ML input shape) defines 21 features. Several of these (`StochRsiK`, `CmoValue`, `VidyaValue`) appear in `TradeSetupSnapshotEntity` but not in the live engine. Confirm the trainer's input column list is the source of truth for what feature snapshot should capture.

---

## Tickets opened

1. **`DOC-07`** — Reconcile phased-stop documentation with code. Update XAML "How It Works", `StopPhase` enum XML docs, and remove unreachable `StopPhase.ProfitLock`.
2. **`FEAT-57`** — Persist `TradeOutcome` row per live trade so the ML feedback loop has real labels.
3. **`FEAT-58`** — Persist `TradeSetupSnapshot` at entry and `TradeLifespanRecord` at close. Required for step-through replay and richer ML training features.
4. **`FEAT-59`** — Make trade replay data unconditional for live trades (don't gate on the Debug Capture toggle).
5. **`BUG-86`** — Periodic reconciliation for broker-side closes the app missed (force-close, drawdown EOD flat). Today the 48-hour startup lookback is the only safety net.
6. **`BUG-87`** — `FavouriteContract.PhasedTrail*` properties are dead config. Either wire them into the initial-stop sizing path or remove.

These are independent enough to be picked up in any order. I'd suggest **FEAT-57 + FEAT-58 first** (they directly unblock training and replay), then **DOC-07 + BUG-87** (cheap cleanup), then **FEAT-59 + BUG-86** (require more thought about retention/cadence).

---

## What I did NOT verify

Honest list of things this review did NOT cover:
- I did not run `dotnet test` — the `ExitSignalEngineTests` assertions were read, not executed.
- I did not open `tools/postmortem/postmortem.py` to check whether the existing python report renderer is impacted by FEAT-57/FEAT-58 changes.
- I did not audit `SuperTrendPlusViewModel` in full — it's ~3.5k lines. I read the orchestration entry-point + management tick + the parts grep flagged for `ExitSignalEngine`, but I haven't read every helper.
- I did not verify the actual broker-side behaviour for force-closes (TopStepX's daily-loss flat) — only the code paths that *should* handle it. Worth manually testing once during a sim-account stop-out.
- I did not audit the entry order sizing / persona resolution paths (`Lewis`/`Damian`/`Joe` selection). Question was scoped to management and exit.

If any of these are load-bearing for the decisions you make from this review, flag and I'll go deeper.
