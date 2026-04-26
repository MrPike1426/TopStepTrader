# BackTest SME Review
**Date:** 2026-04-26  
**Scope:** `MaxEffortViewModel.vb`, `BacktestEngine.vb`, `BacktestMetrics.vb`, `BacktestViewModel.vb`  
**Session:** 1 — Static analysis only (no code changes in this review)

---

## Role 1 — Senior Test Engineer (Financial Systems)

### H1 ⚠️ All existing engine tests use monotonically rising bars — no strategy ever fires
**File:** `Tests/Backtest/BacktestEngineTests.vb`  
`MakeBars()` generates prices that rise by 1 pt/bar. EMA8 never crosses EMA21, RSI never reaches oversold. All three tests assert `Empty(result.Trades)`. A strategy that silently never fires (warm-up bug, NaN guard regression, threshold change) would pass every test.  
**Fix:** Add a crossover bar sequence (decline then rise) that produces ≥1 trade; assert `result.TotalTrades > 0`.  
**Tests written:** T1

### H2 ⚠️ `ExecuteMaximumEffort` bare `Catch` swallows all run failures silently
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
```vb
Catch ex As OperationCanceledException
    Exit For
Catch        ' ← swallows NullReferenceException, InvalidOperationException, etc.
End Try
```
A single bad bar sequence, NaN overflow, or DI misconfiguration fails silently. The grid fills with 1,079 results looking normal; one combination never appears. No test exercises the error path.  
**Fix:** Replace bare `Catch` with `Catch ex As Exception` → log + append a failed-row sentinel to the results grid.

### H3 ⚠️ `InitialCapital = 0D` in all MaxEffort runs — Calmar bias
**File:** `UI/ViewModels/MaxEffortViewModel.vb` line ~190  
```vb
.InitialCapital = 0D,
```
With no starting capital, `peakCapital` begins at 0. The first profitable trade sets the peak. Any run that never has a losing trade accumulates `MaxDrawdown = 0`, and `Calmar = TotalPnL` (raw dollars) rather than `TotalPnL / MaxDD`. A lucky 3-trade window with all wins will rank above a 300-trade strategy with a modest drawdown.  
**Fix:** Set `InitialCapital = config.InitialCapital` from the persona's capital, or apply a minimum `MaxDrawdown = 1D` guard.  
**Tests written:** T8

### H4 🟡 No minimum trade count filter — low-n results rank first
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
A 4-hr timeframe over 60 days produces ~360 bars with a 55-bar warm-up → ~305 actionable bars. A selective strategy may fire 3 times and win all 3. WinRate=1.0, Calmar=PnL. No filter exists to discard runs with `TotalTrades < 30`.  
**Fix:** Add a `MinTradesThreshold` constant (e.g. 30) and mark/dim rows below it on the results grid.  
**Tests written:** T10

### H5 🟡 Last-bar pending trade is silently dropped
**File:** `Services/Backtest/BacktestEngine.vb`  
A signal on the final bar sets `pending = New PendingEntry(...)` but the outer loop ends before the next iteration fills it. No end-of-data close path catches `pending IsNot Nothing`. The signal vanishes with no error, no trade, no log entry.  
**Fix:** After the bar loop exits, check `If pending IsNot Nothing` and append an orphan-signal warning to the result.  
**Tests written:** T3 (verifies all closed trades have ExitReason/ExitPrice — orphaned pending never surfaces as a closed trade)

### M1 🧪 Train/test split has no unit tests
**File:** `Services/Backtest/BacktestEngine.vb` + `UI/ViewModels/BacktestViewModel.vb`  
The 60/40 split is correct in `RunBacktestAsync` but floor-index arithmetic and temporal ordering have no coverage.  
**Tests written:** T2

### M2 ✅ `CheckFixedExit` SL-priority tie-break is undocumented
**File:** `Services/Backtest/BacktestMetrics.vb`  
When a bar sweeps both SL and TP, the function tests SL first. A developer changing the order would convert all double-hit bars from losses to wins without any failing test.  
**Tests written:** T4

### M3 🧪 Dynamic exit paths (trailing stop, break-even, extend-TP) have no unit tests
**File:** `Services/Backtest/BacktestMetrics.vb` — `UpdateDynamicExits`  
Three branches (trailing, break-even at 50% TP, extend-TP cap at 3×) are exercised only via full `RunReplay` integration. A regression in any one branch cannot be isolated.  
**Tests written:** T5, T6, T7

### M4 🟡 `Dispatcher.Invoke` (synchronous) for every result row — serial UI bottleneck
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
```vb
Application.Current.Dispatcher.Invoke(Sub() MaxEffortResults.Add(row))
```
840 `Invoke` calls from a background thread serialize each UI marshal through the WPF dispatcher message pump. On a fast machine this is imperceptible; on a slower machine or under test harness this can produce 5–10 s of dead time.  
**Fix:** Batch results in a local list and `Invoke` once per persona (3 batches of 280) — or use `InvokeAsync`.

### M5 🟡 ForceClose settings read from sibling VM — action-at-a-distance
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
```vb
ForceClosePnL = _runVm.ForceClosePnL
```
`MaxEffortViewModel` reaches into `RunBacktestViewModel._runVm` to read form fields. If the Run tab is in an inconsistent state (user mid-edit), MaxEffort silently picks up stale values.  
**Fix:** Extract a `BacktestConfigSnapshot` record shared by both VMs, populated by the form on "Run/MaxEffort" click.

### M6 ✅ All closed trades have ExitReason, ExitPrice, and PnL
No trade should surface from `RunReplay` with a null exit — the end-of-data close path handles open legs.  
**Tests written:** T3

---

## Role 2 — Solution Architect (WPF / MVVM)

### A1 ⚠️ Business logic in ViewModel — 200-line `ExecuteMaximumEffort`
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
Combination enumeration, CSV I/O, Claude AI orchestration, result parsing and ranking all live inside a WPF ViewModel. This violates single-responsibility: the VM should coordinate, not implement. Testing requires the WPF dispatcher; parallelism is constrained by UI thread affinity.  
**Fix:** Extract an `IMaxEffortOrchestrator` service. VM becomes a thin coordinator that calls the service and data-binds results.

### A2 ⚠️ `_sut As New BacktestEngine(Nothing, Nothing, ...)` — shared mutable static in tests
**File:** `Tests/Backtest/BacktestEngineTests.vb`  
The static instance is shared across tests. `RunReplay` modifies no instance state (it's effectively pure), so the current tests pass. Any future stateful addition to `BacktestEngine` would produce intermittent, order-dependent test failures.  
**Fix:** Instantiate `_sut` in a `[SetUp]` / `New()` constructor per test class, or annotate the class with `[Collection("sequential")]`.

### A3 🟡 CSV file truncated at run start — partial file on cancel
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
```vb
File.WriteAllText(csvPath, header)   ' ← called before the loop starts
```
The header is written before any run executes. If the user cancels at run 10/840, the CSV contains 10 rows with a valid header — but the file name implies a complete run. Consumers (Excel, downstream scripts) may ingest partial data as complete.  
**Fix:** Write CSV atomically — buffer all rows in memory, write a `.tmp` file, `File.Move` to the final path on completion or cancellation with a `_partial` suffix.

### A4 🟡 Strategy enum ordinal used as DB discriminator — rename risk
**File:** `Core/Enums/StrategyConditionType.vb`  
Integer enum values are stored in `BacktestRunEntity.StrategyCondition`. Inserting a new strategy between two existing values (or reordering) silently changes what historical rows mean.  
**Note:** Documented in CLAUDE.md. The risk is acknowledged; no code change needed unless a rename occurs.

### A5 ✅ `IBarIngestionService` is the sole abstraction over bar data — no Yahoo coupling
Post-weed-out: `TopStepXBarIngestionService` is the only implementation. Constructor injection via DI. No concrete Yahoo reference remains in service or test layers.

---

## Role 3 — Experienced Quant / Backtesting Trader

### Q1 ⚠️ VWAP is rolling cumulative over full date range, not session-anchored
**File:** `ML/Features/TechnicalIndicators.vb` — `CalcVWAP`  
NakedTrader and VwapMeanReversion use VWAP as a session reference level. A session-anchored VWAP resets at each market open; the current implementation cumulates from the first bar of the dataset. For a 180-day dataset the VWAP drifts far from intraday price action and produces meaningless signals. Backtest results for these strategies are not representative of live performance.  
**Fix:** Add a `sessionAnchor: Boolean` parameter to `CalcVWAP`; when True, reset the cumulator whenever the bar date changes.

### Q2 ⚠️ `InitialCapital = 0` Calmar bias — zero-drawdown runs dominate leaderboard
*(See also H3 above)*  
A run with all-winning trades gets `Calmar = TotalPnL` (e.g. 500). A run with identical P&L but one small loss gets `Calmar = 500 / loss`. The first dominates by orders of magnitude. MaxEffort rankings are therefore unreliable for any combination that never hits its stop.  
**Tests written:** T8

### Q3 🟡 Sharpe uses √252 on per-trade returns — cross-timeframe incomparability
**File:** `Services/Backtest/BacktestMetrics.vb` — `CalculateSharpe`  
Sharpe is annualized using √252 (trading-day convention). But the inputs are per-trade P&L, not daily returns. A 1-min strategy firing 800 trades/year produces a very different denominator than a 4-hr strategy firing 40 trades/year. The reported Sharpe values are internally consistent within a single timeframe but cannot be compared across timeframes on the MaxEffort grid.  
**Fix:** Convert per-trade returns to daily returns before applying √252, or document the cross-timeframe caveat on the grid tooltip.

### Q4 🟡 No minimum trade count filter — statistical noise ranks first
*(See also H4 above)*  
Academic literature (Pardo, Aronson) requires ≥30 trades before reporting Sharpe, Calmar, or win rate as meaningful. MaxEffort has no such filter.  
**Tests written:** T10

### Q5 🟡 SpreadTicks = 0 for all 1,080 MaxEffort runs — overstated P&L
**File:** `UI/ViewModels/MaxEffortViewModel.vb`  
No spread cost is applied. For M6E (1-pip spread = $12.50/side = $25/round trip), 100 trades = $2,500 phantom P&L. OIL (0.01 spread = $10/side) = $2,000/100 trades. The MaxEffort leaderboard systematically overstates P&L for wide-spread instruments.  
**Fix:** Read `SpreadTicks` from `FavouriteContracts` per instrument and set it on each `BacktestConfiguration`.  
**Tests written:** T9

### Q6 🧠 OOS sentinel (train/test split) is correctly implemented but rarely used
The 60/40 split correctly partitions bars temporally and runs both halves independently. The `DegradationPct` column surfaces on the results grid. The mechanism is sound; adoption (turning it on by default for MaxEffort) would improve ranking reliability.  
**Recommendation:** Enable `TrainTestSplit = 0.6` by default on MaxEffort and rank by `TestPnL` rather than in-sample P&L.

### Q7 🧠 Dynamic exits (trailing stop + extend-TP) improve Sharpe but complicate OOS comparison
When both `TrailingStopEnabled` and `ExtendTpEnabled` are True, the strategy's risk profile changes mid-trade. The extend-TP cap (3×) prevents runaway, but comparing a run with dynamic exits to one without is comparing different strategies. MaxEffort should include the dynamic exit flags as dimensions (6 Boolean combinations × 840 base = 5,040 runs) or fix them at a single setting.

---

## Test Coverage Map

| Finding | Severity | Test(s) | File |
|---------|----------|---------|------|
| T1 — Strategies fire on crossover bars | H1 ⚠️ | `T1_EmaRsi_CrossoverBars_ProducesAtLeastOneTrade`, `T1_MultiConfluence_CrossoverBars_DoesNotThrow` | `BacktestSMEReviewTests.vb` |
| T2 — Train/test split partitioning | M1 🧪 | `T2_TrainTestSplit_FloorIndexIsCorrect`, `T2_*TrainTradesAllPrecedeFirstTestBar`, `T2_*BothHalvesRunWithoutException` | `BacktestSMEReviewTests.vb` |
| T3 — All trades have ExitReason/ExitPrice/PnL | H5/M6 | `T3_AllTrades_HaveNonNullExitReason`, `T3_AllTrades_HaveExitPrice`, `T3_AllTrades_HavePnL` | `BacktestSMEReviewTests.vb` |
| T4 — SL-priority tie-break | M2 ✅ | `T4_CheckFixedExit_Buy_BothLevelsHit_ReturnsSL`, `T4_*Sell_*`, `T4_*OnlyTP_Hit_*`, `T4_*NeitherLevel_*` | `BacktestSMEReviewTests.vb` |
| T5 — Trailing stop never retreats | M3 🧪 | `T5_TrailingStop_Long_*`, `T5_TrailingStop_Short_*` | `BacktestSMEReviewTests.vb` |
| T6 — Break-even fires at 50% TP | M3 🧪 | `T6_BreakEven_Long_*`, `T6_BreakEven_AlreadyAtEntry_*` | `BacktestSMEReviewTests.vb` |
| T7 — ExtendTp cap at 3× | M3 🧪 | `T7_ExtendTp_Long_ThreeAdvancesHitCap`, `T7_ExtendTp_Short_*` | `BacktestSMEReviewTests.vb` |
| T8 — Calmar zero-drawdown bias | H3/Q2 ⚠️ | `T8_Calmar_ZeroDrawdown_ReturnsRawPnL_NotRatio`, `T8_BuildResult_ZeroInitialCapital_*` | `BacktestSMEReviewTests.vb` |
| T9 — Spread reduces P&L | Q5 🟡 | `T9_SpreadTicks_PositiveSpread_ReducesPnLOnTradesThatFire`, `T9_SpreadTicks_TradeCount_NeverExceedsNoSpread` | `BacktestSMEReviewTests.vb` |
| T10 — Low trade count, valid but unreliable metrics | H4/Q4 🟡 | `T10_LowTradeCount_MetricsAreArithmeticallyValid`, `T10_LowTradeCount_CanProduceHundredPctWinRate` | `BacktestSMEReviewTests.vb` |

**Total new tests: 22** (added in `BacktestSMEReviewTests.vb`)  
**Suite total after addition: 500 passed, 0 failed**

---

## Priority Summary

| ID | Severity | Action Required |
|----|----------|----------------|
| H1 | ⚠️ | Add crossover-bar tests — done via T1 |
| H2 | ⚠️ | Replace bare `Catch` in `ExecuteMaximumEffort` |
| H3 | ⚠️ | Fix `InitialCapital = 0D` in MaxEffort |
| Q1 | ⚠️ | Session-anchor VWAP in `TechnicalIndicators` |
| Q2 | ⚠️ | Same as H3 |
| A1 | ⚠️ | Extract orchestration logic from ViewModel |
| M1–M6 | 🟡 | All covered by T2–T7; code fixes are backlog |
| Q3–Q7 | 🟡/🧠 | Quant improvements; no blocking defects |
